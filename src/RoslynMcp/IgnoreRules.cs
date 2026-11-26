using System.Collections.Frozen;
using System.IO.Enumeration;

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
///     Beside the tiers: the linked git worktrees below the root (<see cref="GitWorktrees"/>,
///     #511), each a second checkout of the whole tree. <see cref="IsPrunedFromListing"/> leaves
///     them out, and so does an AdhocWorkspace's own search for sources
///     (<see cref="IsLinkedWorktree"/>, <see cref="IsInLinkedWorktree"/>). They are not in
///     <see cref="IsNeverInput"/>: a project whose items reach into one does compile those
///     files, and what a project compiles is MSBuild's to say.
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
	public static readonly IgnoreRules BuiltIn = new([], [], [], "", []);
	
	// One entry per workspace root, revalidated against the .gitignore stamp and the worktree
	// records' stamp on every lookup.
	// Keyed with the file system's own case rule: on Linux /work/Foo and /work/foo are two roots.
	static readonly Dictionary<string, (IgnoreRules Rules, string[] Configured, long GitIgnoreStamp, long WorktreeStamp)> cache = new(gitComparer);
	static readonly Lock cacheLock = new();
	
	readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> neverInput;
	readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> excluded;
	readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> gitNames;
	readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> gitPaths;
	readonly bool                                                  hasGitPaths;
	
	// The linked worktrees below the root, twice: relative to the root with forward slashes,
	// as the listing walks ask, and as full paths in the root's own spelling, as the walks that
	// start elsewhere under the root ask.
	readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> worktreePaths;
	readonly string[]                                              worktreeRoots;
	
	IgnoreRules(string[] configuredNames, string[] gitIgnoredNames, string[] gitIgnoredPaths, string rootPath, string[] worktrees)
	{
		worktreePaths = Lookup([..worktrees.Select(relative => relative.Replace('\\', '/'))], gitComparer);
		worktreeRoots = [..worktrees.Select(relative => Path.Combine(rootPath, relative))];
		
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
	///     <c>ignore</c>), the root <c>.gitignore</c>, and the linked git worktrees below the
	///     root. Never throws — an unreadable <c>.gitignore</c> contributes nothing, and neither
	///     do worktree records that cannot be read.
	/// </summary>
	public static IgnoreRules ForRoot(string rootPath, FileLogger logger)
	{
		var configured    = ProjectConfig.EffectiveIgnore(rootPath, logger);
		var gitIgnorePath = Path.Combine(rootPath, ".gitignore");
		var stamp         = GitIgnoreStamp(gitIgnorePath);
		var records       = GitWorktrees.RecordsDirectory(rootPath);
		var worktreeStamp = GitWorktrees.Stamp(records);
		
		lock(cacheLock)
			if(cache.TryGetValue(rootPath, out var cached)
			   && cached.GitIgnoreStamp == stamp
			   && cached.WorktreeStamp == worktreeStamp
			   && ReferenceEquals(cached.Configured, configured))
				
				return cached.Rules;
		
		var (names, paths) = stamp == 0 ? (Names: [], Paths: []) : ReadGitIgnore(gitIgnorePath);
		var worktrees      = worktreeStamp == 0 ? [] : GitWorktrees.Below(rootPath, records!);
		var rules          = new IgnoreRules(configured, names, paths, rootPath, worktrees);
		
		lock(cacheLock)
			cache[rootPath] = (rules, configured, stamp, worktreeStamp);
		
		return rules;
	}
	
	// Checked on the length before the read and enforced again during it (a file can grow in
	// between): a .gitignore past this is not one anyone wrote by hand.
	const long maxGitIgnoreBytes = 256 * 1024;
	
	// Last-write ticks mixed with the length; 0 means "no usable .gitignore". A stat per lookup
	// is what keeps an edited .gitignore from needing a server restart.
	static long GitIgnoreStamp(string path)
	{
		try {
			
			var info = new FileInfo(path);
			
			// A symlinked .gitignore could point anywhere; the walks it steers stay inside the root.
			if(!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > maxGitIgnoreBytes)
				
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
			
			foreach(var raw in FileReader.ReadLines(path, maxGitIgnoreBytes)) {
				
				// Trailing whitespace only, as git does: a leading space is part of the name, and
				// trimming it would turn " dist/" into a rule for a different directory.
				var line = raw.AsSpan().TrimEnd();
				
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
			
			// A read that stopped partway — the file grew past the limit after the length check,
			// or any other I/O failure. The unread rest could hold a negation re-including
			// something read so far, so none of it is used: a directory walked that git ignores
			// is the safe side.
			return ([], []);
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
	///     <see cref="IsExcluded"/> plus the root <c>.gitignore</c> and the linked worktrees below
	///     the root, for walks that list or look up files. <paramref name="relativeDirectory"/> is the parent of the directory called
	///     <paramref name="name"/>, relative to the workspace root (empty for a top-level directory).
	/// </summary>
	public bool IsPrunedFromListing(ReadOnlySpan<char> relativeDirectory, ReadOnlySpan<char> name)
	{
		if(excluded.Contains(name) || gitNames.Contains(name))
			
			return true;
		
		if(!hasGitPaths && worktreeRoots.Length == 0)
			
			return false;
		
		// Anchored entries and worktrees are rare, so the root-relative path is only built when
		// one exists.
		var relative = relativeDirectory.IsEmpty
			? name.ToString()
			: string.Concat(relativeDirectory, "/", name).Replace('\\', '/');
		
		return gitPaths.Contains(relative) || worktreePaths.Contains(relative);
	}
	
	/// <summary>
	///     Whether the directory a walk has reached is the root of a linked worktree below the
	///     workspace root. For the walks that do not start at the root and so cannot ask by
	///     relative path. The full path is built only when there is a worktree to compare with.
	/// </summary>
	public bool IsLinkedWorktree(ref FileSystemEntry directory)
	{
		if(worktreeRoots.Length == 0)
			
			return false;
		
		var fullPath = directory.ToFullPath();
		
		foreach(var root in worktreeRoots)
			if(gitComparer.Equals(root, fullPath))
				return true;
		
		return false;
	}
	
	/// <summary>Whether <paramref name="fullPath"/> is a linked worktree below the workspace root, or lies in one.</summary>
	public bool IsInLinkedWorktree(string fullPath) => LinkedWorktreeOf(fullPath) is not null;
	
	/// <summary>The root of the linked worktree below the workspace root that <paramref name="fullPath"/> is, or lies in; null when it is in none.</summary>
	public string? LinkedWorktreeOf(string fullPath)
	{
		foreach(var root in worktreeRoots)
			if(gitComparer.Equals(root, fullPath) || SecurityBoundary.IsUnderDirectory(fullPath, root))
				return root;
		
		return null;
	}
}
