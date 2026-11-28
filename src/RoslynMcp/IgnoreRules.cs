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
///     Beside the tiers: the linked git worktrees below the root (<see cref="GitWorktrees"/>,
///     #511), each a second checkout of the whole tree. <see cref="IsPrunedFromListing"/> leaves
///     them out, as they were when the rules were asked for. A loaded workspace keeps its rules
///     while worktrees come and go, so it asks <see cref="GitWorktrees.For"/> itself. They are
///     not in <see cref="IsNeverInput"/>: a project whose items reach into one does compile
///     those files, and what a project compiles is MSBuild's to say.
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
	public static readonly IgnoreRules BuiltIn = new([], [], [], GitWorktrees.Below.None, [], "", "");
	
	// One entry per workspace root, revalidated against the .gitignore stamp and the worktrees
	// below the root on every lookup.
	// Keyed with the file system's own case rule: on Linux /work/Foo and /work/foo are two roots.
	static readonly Dictionary<string, (IgnoreRules Rules, string[] Configured, long GitIgnoreStamp, GitWorktrees.Below Worktrees)> cache = new(gitComparer);
	static readonly Lock cacheLock = new();
	
	readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> neverInput;
	readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> excluded;
	readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> gitNames;
	readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> gitPaths;
	readonly bool                                                  hasGitPaths;
	
	// The linked worktrees below the root, relative to it with forward slashes, as the listing
	// walks ask. Matched as the anchored .gitignore entries are, since both are paths under
	// the root that a walk from the root builds itself.
	readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> worktreePaths;
	readonly bool                                                  hasWorktrees;
	
	// The configured path entries (#543), as they apply below this root.
	readonly PathRules pathRules;
	
	IgnoreRules(string[] configuredNames, string[] gitIgnoredNames, string[] gitIgnoredPaths, GitWorktrees.Below worktrees, string[] pathEntries, string pathDirectory, string rootPath)
	{
		worktreePaths = Lookup(worktrees.RelativePaths, gitComparer);
		hasWorktrees  = worktrees.RelativePaths.Length > 0;
		
		neverInput  = Lookup([..neverInputNames, ..configuredNames], StringComparer.OrdinalIgnoreCase);
		excluded    = Lookup([..neverInputNames, ..buildOutputNames, ..configuredNames], StringComparer.OrdinalIgnoreCase);
		gitNames    = Lookup(gitIgnoredNames, gitComparer);
		gitPaths    = Lookup(gitIgnoredPaths, gitComparer);
		hasGitPaths = gitIgnoredPaths.Length > 0;
		
		pathRules   = pathEntries.Length == 0
			? PathRules.None
			: PathRules.Create(pathEntries, pathDirectory, rootPath, name => excluded.Contains(name) || gitNames.Contains(name));
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
		
		// From the same config file as the names, so the same instance while that file stands:
		// the cache below needs no key of its own for them.
		var (pathEntries, pathDirectory) = ProjectConfig.EffectiveIgnorePaths(rootPath, logger);
		
		var gitIgnorePath = Path.Combine(rootPath, ".gitignore");
		var stamp         = GitIgnoreStamp(gitIgnorePath);
		var worktrees     = GitWorktrees.For(rootPath, fresh: true);
		
		lock(cacheLock)
			if(cache.TryGetValue(rootPath, out var cached)
			   && cached.GitIgnoreStamp == stamp
			   && ReferenceEquals(cached.Worktrees, worktrees)
			   && ReferenceEquals(cached.Configured, configured))
				
				return cached.Rules;
		
		var (names, paths) = stamp == 0 ? (Names: [], Paths: []) : ReadGitIgnore(gitIgnorePath);
		var rules          = new IgnoreRules(configured, names, paths, worktrees, pathEntries, pathDirectory, rootPath);
		
		lock(cacheLock)
			cache[rootPath] = (rules, configured, stamp, worktrees);
		
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
	///     <see cref="IsExcluded"/> plus the linked worktrees below the root, and not the root
	///     <c>.gitignore</c>: for the top level of a solution set (#501), whose root often ignores
	///     the very repositories the set is made of, while a worktree of the root is still a
	///     second copy of it (#511).
	/// </summary>
	public bool IsExcludedOrWorktree(ReadOnlySpan<char> relativeDirectory, ReadOnlySpan<char> name)
	{
		if(pathRules.Decides(relativeDirectory, name, out var pruned))
			
			return pruned;
		
		if(excluded.Contains(name))
			
			return true;
		
		if(!hasWorktrees)
			
			return false;
		
		return worktreePaths.Contains(relativeDirectory.IsEmpty
			? name.ToString()
			: string.Concat(relativeDirectory, "/", name).Replace('\\', '/'));
	}
	
	/// <summary>
	///     <see cref="IsExcluded"/> plus the root <c>.gitignore</c> and the linked worktrees below
	///     the root, for walks that list or look up files. <paramref name="relativeDirectory"/> is the parent of the directory called
	///     <paramref name="name"/>, relative to the workspace root (empty for a top-level directory).
	/// </summary>
	public bool IsPrunedFromListing(ReadOnlySpan<char> relativeDirectory, ReadOnlySpan<char> name)
	{
		// The configured paths first (#543): an exception keeps a directory a name would hide.
		if(pathRules.Decides(relativeDirectory, name, out var pruned))
			
			return pruned;
		
		if(excluded.Contains(name) || gitNames.Contains(name))
			
			return true;
		
		if(!hasGitPaths && !hasWorktrees)
			
			return false;
		
		// Anchored entries and worktrees are rare, so the root-relative path is only built when
		// one exists.
		var relative = relativeDirectory.IsEmpty
			? name.ToString()
			: string.Concat(relativeDirectory, "/", name).Replace('\\', '/');
		
		return gitPaths.Contains(relative) || worktreePaths.Contains(relative);
	}
	
	// ── Configured paths (#543) ────────────────────────────────────────────────
	
	/// <summary>
	///     <see cref="IsExcluded"/> with the configured paths, for a walk that looks for what the
	///     server works on and not for what it lists (the solutions below a root): the root
	///     <c>.gitignore</c> has no say there, the configured paths do.
	/// </summary>
	public bool IsExcludedAt(ReadOnlySpan<char> relativeDirectory, ReadOnlySpan<char> name)
		=> pathRules.Decides(relativeDirectory, name, out var pruned) ? pruned : excluded.Contains(name)
	;
	
	/// <summary>
	///     Whether a directory, given relative to the root, is one the server stays out of or lies
	///     inside one: what a walk down from the root would never reach. For the way up from a
	///     file, which meets directories without having walked to them.
	/// </summary>
	public bool IsHidden(string relativeDirectory)
	{
		var path = relativeDirectory.Replace('\\', '/').Trim('/');
		
		for(var start = 0; start < path.Length; ) {
			
			var next = path.IndexOf('/', start);
			
			if(next < 0)
				next = path.Length;
			
			if(IsExcludedAt(start == 0 ? [] : path.AsSpan(0, start - 1), path.AsSpan(start, next - start)))
				
				return true;
			
			start = next + 1;
		}
		
		return false;
	}
	
	/// <summary>Whether an entry of a configured ignore list is a path and not a directory name: it has a separator, a leading <c>!</c>, or is a lone <c>*</c>.</summary>
	public static bool LooksLikePathEntry(string? entry)
	{
		if(entry is null)
			
			return false;
		
		var text = entry.Trim();
		
		return text.StartsWith('!') || text.Contains('/') || text.Contains('\\') || text == "*";
	}
	
	/// <summary>
	///     A path entry in the one form the rules keep it in, or false for one that is refused:
	///     parts joined by <c>/</c>, relative to the config file's directory, with a leading
	///     <c>!</c> for an exception. Refused: an empty part, <c>.</c> or <c>..</c>, a drive or
	///     any <c>:</c>, a wildcard other than a whole last part <c>*</c>, a wildcard in an
	///     exception, and a single plain name, which is a name and not a path. A glob that is
	///     only half supported is refused, not guessed at.
	/// </summary>
	public static bool TryNormalizePathEntry(string? entry, out string normalized)
	{
		normalized = "";
		
		if(string.IsNullOrWhiteSpace(entry))
			
			return false;
		
		var text    = entry.Trim();
		var negated = text.StartsWith('!');
		
		if(negated)
			text = text[1..];
		
		text = text.Replace('\\', '/').Trim('/');
		
		if(text.Length == 0 || text.Contains(':'))
			
			return false;
		
		var parts = text.Split('/');
		
		for(var index = 0; index < parts.Length; index++) {
			
			var part = parts[index];
			
			if(part.Length == 0 || part == "." || part == ".." || part != part.Trim())
				
				return false;
			
			var wild = part.Contains('*') || part.Contains('?');
			
			if(wild && (part != "*" || index != parts.Length - 1 || negated))
				
				return false;
		}
		
		if(!negated && parts.Length == 1 && parts[0] != "*")
			
			return false;
		
		normalized = (negated ? "!" : "") + string.Join('/', parts);
		
		return true;
	}
	
	/// <summary>
	///     The configured path entries as they apply below one root. Three kinds: a directory
	///     (<c>a/b</c>), every directory directly inside one (<c>a/*</c>), and an exception
	///     (<c>!a/b</c>). An exception is stronger than anything that would hide its directory, a
	///     built-in name included (<c>packages</c>), and it opens the way down to it: a directory
	///     above an exception is entered though it is ignored, and everything else inside that
	///     directory stays hidden, because its parent is. So <c>!Packages/One</c> alone shows one
	///     directory of a tree that is hidden whole.
	///     <para>
	///         The entries are relative to the config file's directory. For a root below it (a
	///         solution's own workspace under a root of several) they are taken relative to that
	///         root, and the ones that lie outside it are left out.
	///     </para>
	/// </summary>
	sealed class PathRules
	{
		public static readonly PathRules None = new([], [], [], false, static _ => false);
		
		readonly string[]           exceptions;
		readonly FrozenSet<string>  directories;
		readonly FrozenSet<string>  wildcardParents;
		readonly bool               rootInsideIgnored;
		readonly Func<string, bool> ignoredByName;
		readonly bool               any;
		
		PathRules(string[] exceptions, string[] directories, string[] wildcardParents, bool rootInsideIgnored, Func<string, bool> ignoredByName)
		{
			this.exceptions        = exceptions;
			this.directories       = directories.ToFrozenSet(gitComparer);
			this.wildcardParents   = wildcardParents.ToFrozenSet(gitComparer);
			this.rootInsideIgnored = rootInsideIgnored;
			this.ignoredByName     = ignoredByName;
			any                    = exceptions.Length + directories.Length + wildcardParents.Length > 0;
		}
		
		public static PathRules Create(string[] entries, string configDirectory, string rootPath, Func<string, bool> ignoredByName)
		{
			// As the config file has them, to judge the directories between it and the root.
			var atConfig = Split(entries, prefix: "", ignoredByName, rootInsideIgnored: false);
			
			var prefix = Path.GetRelativePath(configDirectory, rootPath).Replace('\\', '/').Trim('/');
			
			if(prefix == "." || prefix.Length == 0)
				
				return atConfig;
			
			// A root the config file is not above has no part in its paths.
			if(prefix.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(prefix))
				
				return None;
			
			// The root itself inside an ignored directory, of which an exception below the root
			// shows a part: everything else below the root is hidden as it is from above.
			var inside = false;
			
			for(var end = prefix.IndexOf('/'); ; end = prefix.IndexOf('/', end + 1)) {
				
				inside |= atConfig.Ignored(end < 0 ? prefix : prefix[..end]);
				
				if(end < 0)
					break;
			}
			
			var below = Split(entries, prefix, ignoredByName, rootInsideIgnored: false);
			
			return inside && below.exceptions.Length > 0
				? Split(entries, prefix, ignoredByName, rootInsideIgnored: true)
				: below;
		}
		
		// The entries below a prefix, relative to it; all of them for an empty prefix.
		static PathRules Split(string[] entries, string prefix, Func<string, bool> ignoredByName, bool rootInsideIgnored)
		{
			var exceptions  = new List<string>();
			var directories = new List<string>();
			var wildcards   = new List<string>();
			
			foreach(var entry in entries) {
				
				var negated = entry.StartsWith('!');
				var path    = negated ? entry[1..] : entry;
				
				if(prefix.Length > 0) {
					
					if(!Under(path, prefix))
						continue;
					
					path = path[(prefix.Length + 1)..];
				}
				
				if(negated)
					exceptions.Add(path);
				else if(path == "*")
					wildcards.Add("");
				else if(path.EndsWith("/*", StringComparison.Ordinal))
					wildcards.Add(path[..^2]);
				else
					directories.Add(path);
			}
			
			return new PathRules([..exceptions], [..directories], [..wildcards], rootInsideIgnored, ignoredByName);
		}
		
		// Whether 'path' lies inside 'directory', and is not the directory itself.
		static bool Under(string path, string directory)
			=> path.Length > directory.Length + 1 && path[directory.Length] == '/' && path.StartsWith(directory, GitComparison)
		;
		
		static StringComparison GitComparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		
		static string ParentOf(string path)
		{
			var cut = path.LastIndexOf('/');
			
			return cut < 0 ? "" : path[..cut];
		}
		
		static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];
		
		// Whether a directory is ignored by a name or a path entry, exceptions aside.
		bool Ignored(string path)
			=> ignoredByName(NameOf(path)) || directories.Contains(path) || wildcardParents.Contains(ParentOf(path))
		;
		
		/// <summary>
		///     Whether the path entries decide about a directory, and how: kept by an exception
		///     (itself, or on the way down to one), or pruned (named by an entry, or inside an
		///     ignored directory that is entered only for an exception). False where they say
		///     nothing, and the names decide as before.
		/// </summary>
		public bool Decides(ReadOnlySpan<char> relativeDirectory, ReadOnlySpan<char> name, out bool pruned)
		{
			pruned = false;
			
			if(!any)
				
				return false;
			
			var path = relativeDirectory.IsEmpty
				? name.ToString()
				: string.Concat(relativeDirectory, "/", name).Replace('\\', '/');
			
			var belowException = false;
			
			foreach(var exception in exceptions) {
				
				// The exception itself, or a directory on the way down to it.
				if(string.Equals(exception, path, GitComparison) || Under(exception, path))
					
					return true;
				
				belowException |= Under(path, exception);
			}
			
			if(!belowException) {
				
				if(rootInsideIgnored) {
					
					pruned = true;
					
					return true;
				}
				
				// Inside an ignored directory that was entered for an exception, and not on the
				// way to one: hidden, as everything in that directory is.
				foreach(var exception in exceptions)
					for(var end = exception.IndexOf('/'); end >= 0; end = exception.IndexOf('/', end + 1)) {
						
						var ancestor = exception[..end];
						
						if(Under(path, ancestor) && Ignored(ancestor)) {
							
							pruned = true;
							
							return true;
						}
					}
			}
			
			if(directories.Contains(path) || wildcardParents.Contains(ParentOf(path))) {
				
				pruned = true;
				
				return true;
			}
			
			return false;
		}
	}
}
