using System.Collections.Frozen;

namespace RoslynMcp;

/// <summary>
///     The single answer to "which directories does the server stay out of?" for one workspace
///     root. Before #309 that answer lived in three separate name lists and was missing from the
///     walks that needed it most, so a <c>node_modules</c> or <c>.git</c> tree was enumerated — or
///     watched — in full.
/// </summary>
/// <remarks>
///     Three tiers, each a superset of the one before, because the cost of a wrong skip differs:
///     <list type="bullet">
///         <item>
///             <see cref="IsNeverInput"/> — directories that cannot hold a compilation input:
///             the built-in tooling folders plus the configured ignore list. Safe for the file
///             watcher to stay out of entirely.
///         </item>
///         <item>
///             <see cref="IsExcluded"/> — adds <c>bin</c>/<c>obj</c>. Not for the watcher:
///             SDK-style projects keep generated documents under <c>obj</c> that must still
///             receive text updates.
///         </item>
///         <item>
///             <see cref="IsPrunedFromListing"/> — adds the root <c>.gitignore</c>. Only for
///             walks that list or look up files. A gitignored directory can still hold compiled
///             sources (generated code usually is ignored), so it never drives the watcher or
///             document discovery.
///         </item>
///     </list>
/// </remarks>
internal sealed class IgnoreRules
{
	/// <summary>Upper bound on configured names — a committed file should not be able to grow this without limit.</summary>
	public const int MaxConfiguredNames = 64;
	
	static readonly string[] neverInputNames  = ["node_modules", ".git", ".vs", "packages"];
	static readonly string[] buildOutputNames = ["bin", "obj"];
	
	// Git matches case-insensitively only where the file system does.
	static readonly StringComparer gitComparer = OperatingSystem.IsLinux()
		? StringComparer.Ordinal
		: StringComparer.OrdinalIgnoreCase;
	
	/// <summary>Rules with no configured names and no <c>.gitignore</c> — the built-in lists only.</summary>
	public static readonly IgnoreRules BuiltIn = new([], [], []);
	
	// One entry per workspace root, revalidated against the .gitignore stamp on every lookup.
	static readonly Dictionary<string, (IgnoreRules Rules, string[] Configured, long GitIgnoreStamp)> cache = new(StringComparer.OrdinalIgnoreCase);
	static readonly Lock cacheLock = new();
	
	readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> neverInput;
	readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> excluded;
	readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> gitNames;
	readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> gitPaths;
	readonly bool                                                  hasGitPaths;
	
	IgnoreRules(string[] configuredNames, string[] gitIgnoredNames, string[] gitIgnoredPaths)
	{
		neverInput  = Lookup([..neverInputNames, ..configuredNames], StringComparer.OrdinalIgnoreCase);
		excluded    = Lookup([..neverInputNames, ..buildOutputNames, ..configuredNames], StringComparer.OrdinalIgnoreCase);
		gitNames    = Lookup(gitIgnoredNames, gitComparer);
		gitPaths    = Lookup(gitIgnoredPaths, gitComparer);
		hasGitPaths = gitIgnoredPaths.Length > 0;
	}
	
	static FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Lookup(string[] names, StringComparer comparer) =>
		names.ToFrozenSet(comparer).GetAlternateLookup<ReadOnlySpan<char>>();
	
	/// <summary>
	///     The rules for the workspace rooted at <paramref name="rootPath"/>: built-in names, the
	///     configured ignore list (<c>--ignore</c> / <c>ROSLYNMCP_IGNORE</c>, else the project file's
	///     <c>ignore</c>), and the root <c>.gitignore</c>. Never throws — an unreadable
	///     <c>.gitignore</c> contributes nothing.
	/// </summary>
	public static IgnoreRules ForRoot(string rootPath, FileLogger logger)
	{
		var configured    = ProjectConfig.EffectiveIgnore(rootPath, logger);
		var gitIgnorePath = Path.Combine(rootPath, ".gitignore");
		var stamp         = GitIgnoreStamp(gitIgnorePath);
		
		lock(cacheLock)
			if(cache.TryGetValue(rootPath, out var cached)
			   && cached.GitIgnoreStamp == stamp
			   && ReferenceEquals(cached.Configured, configured))
				
				return cached.Rules;
		
		var (names, paths) = stamp == 0 ? (Names: [], Paths: []) : ReadGitIgnore(gitIgnorePath);
		var rules          = new IgnoreRules(configured, names, paths);
		
		lock(cacheLock)
			cache[rootPath] = (rules, configured, stamp);
		
		return rules;
	}
	
	// Last-write ticks mixed with the length; 0 means "no usable .gitignore". A stat per lookup
	// is what keeps an edited .gitignore from needing a server restart.
	static long GitIgnoreStamp(string path)
	{
		try {
			
			var info = new FileInfo(path);
			
			// A symlinked .gitignore could point anywhere; the walks it steers stay inside the root.
			if(!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > 256 * 1024)
				
				return 0;
			
			return info.LastWriteTimeUtc.Ticks ^ info.Length;
		}
		catch(Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return 0;
		}
	}
	
	/// <summary>
	///     Reads the directory entries of a <c>.gitignore</c> that can be honored without
	///     implementing git's matcher: a plain name (<c>dist</c>, <c>dist/</c>) ignores a directory
	///     of that name at any depth, and an entry with a leading or inner slash
	///     (<c>/out</c>, <c>docs/generated</c>) ignores that one root-relative path. Globs and
	///     escapes are skipped, which is always safe: the cost is a directory walked that git would
	///     have ignored. Negations cannot be skipped — in git a later <c>!cache/</c> re-includes
	///     what <c>cache/</c> ignored — so a plain negation removes the entries it names and one
	///     with a glob discards everything read so far. Either way no file git tracks is hidden.
	/// </summary>
	internal static (string[] Names, string[] Paths) ReadGitIgnore(string path)
	{
		var names = new HashSet<string>(gitComparer);
		var paths = new HashSet<string>(gitComparer);
		
		try {
			
			foreach(var raw in File.ReadLines(path)) {
				
				var line = raw.AsSpan().Trim();
				
				if(line.IsEmpty || line[0] == '#')
					continue;
				
				var negated = line[0] == '!';
				
				if(negated)
					line = line[1..];
				
				var plain = line.IndexOfAny("*?[\\") < 0;
				
				// In git the last matching line wins, so a negation can re-include a directory an
				// earlier line ignored. One this reader cannot interpret might match anything
				// collected so far; forgetting all of it is the only safe reading.
				if(negated && !plain) {
					
					names.Clear();
					paths.Clear();
					
					continue;
				}
				
				if(!plain)
					continue;
				
				var anchored = line.Length > 0 && line[0] == '/';
				var entry    = line.Trim('/');
				
				// "." and ".." segments would escape the root or mean nothing; not worth resolving.
				if(entry.IsEmpty || entry is "." or ".." || entry.StartsWith("../") || entry.Contains("/../", StringComparison.Ordinal) || entry.EndsWith("/.."))
					continue;
				
				var isPath = anchored || entry.Contains('/');
				var text   = entry.ToString();
				
				if(!negated) {
					
					(isPath ? paths : names).Add(text);
					
					continue;
				}
				
				// A plain negation: drop what it re-includes. Wider than git on purpose — "!/out"
				// re-includes only the root-level directory, but a name entry cannot express
				// "everywhere except the root", so the name goes too. The cost is a directory
				// walked that git ignores, never one hidden that git tracks.
				var lastSegment = text[(text.LastIndexOf('/') + 1)..];
				
				names.Remove(lastSegment);
				paths.Remove(text);
				
				if(!isPath)
					paths.RemoveWhere(candidate => gitComparer.Equals(candidate[(candidate.LastIndexOf('/') + 1)..], text));
			}
		}
		catch(Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			// Best effort — fall back to whatever was read before the failure.
		}
		
		return ([..names], [..paths]);
	}
	
	/// <summary>
	///     Whether <paramref name="name"/> is acceptable as a configured ignore entry: a bare
	///     directory name. Paths and wildcards are rejected rather than half-supported.
	/// </summary>
	public static bool IsValidConfiguredName(string? name) =>
		!string.IsNullOrWhiteSpace(name)
		&& name is not ("." or "..")
		&& name.AsSpan().IndexOfAny("/\\*?") < 0
	;
	
	/// <summary>A directory that can never hold a compilation input — built-in tooling folders and configured names.</summary>
	public bool IsNeverInput(ReadOnlySpan<char> name) => neverInput.Contains(name);
	
	/// <summary><see cref="IsNeverInput"/> plus build output (<c>bin</c>, <c>obj</c>).</summary>
	public bool IsExcluded(ReadOnlySpan<char> name) => excluded.Contains(name);
	
	/// <summary>
	///     <see cref="IsExcluded"/> plus the root <c>.gitignore</c>, for walks that list or look up
	///     files. <paramref name="relativeDirectory"/> is the parent of the directory called
	///     <paramref name="name"/>, relative to the workspace root (empty for a top-level directory).
	/// </summary>
	public bool IsPrunedFromListing(ReadOnlySpan<char> relativeDirectory, ReadOnlySpan<char> name)
	{
		if(excluded.Contains(name) || gitNames.Contains(name))
			
			return true;
		
		if(!hasGitPaths)
			
			return false;
		
		// Anchored entries are rare, so the root-relative path is only built when one exists.
		var relative = relativeDirectory.IsEmpty
			? name.ToString()
			: string.Concat(relativeDirectory, "/", name).Replace('\\', '/');
		
		return gitPaths.Contains(relative);
	}
}
