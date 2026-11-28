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
	static readonly Dictionary<string, (IgnoreRules Rules, string[] Configured, string[] Paths, long GitIgnoreStamp, GitWorktrees.Below Worktrees)> cache = new(gitComparer);
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
			: PathRules.Create(pathEntries, pathDirectory, rootPath, name => excluded.Contains(name));
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
		
		// A key of the cache below in their own right: a list of paths alone has no names, and
		// every list without names is the one shared empty array.
		var (pathEntries, pathDirectory) = ProjectConfig.EffectiveIgnorePaths(rootPath, logger);
		
		var gitIgnorePath = Path.Combine(rootPath, ".gitignore");
		var stamp         = GitIgnoreStamp(gitIgnorePath);
		var worktrees     = GitWorktrees.For(rootPath, fresh: true);
		
		lock(cacheLock)
			if(cache.TryGetValue(rootPath, out var cached)
			   && cached.GitIgnoreStamp == stamp
			   && ReferenceEquals(cached.Worktrees, worktrees)
			   && ReferenceEquals(cached.Configured, configured)
			   && ReferenceEquals(cached.Paths, pathEntries))
				
				return cached.Rules;
		
		var (names, paths) = stamp == 0 ? (Names: [], Paths: []) : ReadGitIgnore(gitIgnorePath);
		var rules          = new IgnoreRules(configured, names, paths, worktrees, pathEntries, pathDirectory, rootPath);
		
		lock(cacheLock)
			cache[rootPath] = (rules, configured, pathEntries, stamp, worktrees);
		
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
		=> Pruned(Tier.ExcludedOrWorktree, relativeDirectory, name)
	;

	/// <summary>
	///     Which of the rules a walk asks for: each is what one of the three methods ignored by
	///     name before there were configured paths, and still is where there are none.
	/// </summary>
	internal enum Tier { Excluded, ExcludedOrWorktree, Listing }

	// What a tier ignores by a name, or by a path the root itself anchors (its .gitignore, its
	// linked worktrees): everything but the configured paths.
	bool IgnoredIn(Tier tier, ReadOnlySpan<char> relativeDirectory, ReadOnlySpan<char> name)
	{
		if(excluded.Contains(name))
			
			return true;
		
		if(tier == Tier.Excluded)
			
			return false;
		
		var listing = tier == Tier.Listing;
		
		if(listing && gitNames.Contains(name))
			
			return true;
		
		// Anchored entries and worktrees are rare, so the root-relative path is only built when
		// one exists.
		if(!hasWorktrees && !(listing && hasGitPaths))
			
			return false;
		
		var relative = relativeDirectory.IsEmpty
			? name.ToString()
			: string.Concat(relativeDirectory, "/", name).Replace('\\', '/');
		
		return (listing && gitPaths.Contains(relative)) || worktreePaths.Contains(relative);
	}

	/// <summary>
	///     <see cref="IsPrunedFromListing"/> by the names and the root's own anchored paths alone,
	///     the configured paths left aside: for a directory inside one that was asked for by
	///     name, which is walked as if nothing above it were ignored (#543).
	/// </summary>
	public bool IsPrunedByName(ReadOnlySpan<char> relativeDirectory, ReadOnlySpan<char> name)
		=> IgnoredIn(Tier.Listing, relativeDirectory, name)
	;
	
	// Whether a walk of a tier leaves a directory out. With configured paths (#543) that is the
	// directory's state, worked out from the root down; a passage is entered.
	bool Pruned(Tier tier, ReadOnlySpan<char> relativeDirectory, ReadOnlySpan<char> name)
		=> pathRules.Any
			? pathRules.StateOf(this, tier, PathRules.Join(relativeDirectory, name)) == PathState.Hidden
			: IgnoredIn(tier, relativeDirectory, name)
	;
	
	/// <summary>
	///     <see cref="IsExcluded"/> plus the root <c>.gitignore</c> and the linked worktrees below
	///     the root, for walks that list or look up files. <paramref name="relativeDirectory"/> is the parent of the directory called
	///     <paramref name="name"/>, relative to the workspace root (empty for a top-level directory).
	/// </summary>
	public bool IsPrunedFromListing(ReadOnlySpan<char> relativeDirectory, ReadOnlySpan<char> name)
		=> Pruned(Tier.Listing, relativeDirectory, name)
	;
	
	// ── Configured paths (#543) ────────────────────────────────────────────────
	
	/// <summary>
	///     <see cref="IsExcluded"/> with the configured paths, for a walk that looks for what the
	///     server works on and not for what it lists (the solutions below a root): the root
	///     <c>.gitignore</c> has no say there, the configured paths do.
	/// </summary>
	public bool IsExcludedAt(ReadOnlySpan<char> relativeDirectory, ReadOnlySpan<char> name)
		=> Pruned(Tier.Excluded, relativeDirectory, name)
	;

	/// <summary>
	///     What the configured paths alone say about a directory, for a walk that takes its names
	///     from other rules below the top level (<see cref="SolutionSet.Listing"/>, where each
	///     directory under the root has rules of its own): true for one that is hidden, false for
	///     an exception or a directory entered for one, and null where the paths say nothing.
	/// </summary>
	public bool? DecidedByPaths(ReadOnlySpan<char> relativeDirectory, ReadOnlySpan<char> name)
	{
		if(!pathRules.Any)
			
			return null;
		
		var path = PathRules.Join(relativeDirectory, name);
		
		return pathRules.StateOf(this, Tier.ExcludedOrWorktree, path) switch {
			
			PathState.Hidden  => true,
			PathState.Passage => false,
			_                 => pathRules.IsException(path) ? false : null,
		};
	}

	/// <summary>
	///     Whether the files directly in a directory are left out though the directory is entered:
	///     it is ignored and only walked through, on the way to an exception below it (#543). A walk
	///     prunes directories, so the files of such a one are the caller's to leave out, asked in
	///     the tier of the walk that listed them: a directory that walk took for visible must not
	///     lose its files to a rule the walk never applied.
	/// </summary>
	public bool HidesFilesOf(string? relativeDirectory)
		=> HidesFilesOf(relativeDirectory, Tier.Listing)
	;

	/// <inheritdoc cref="HidesFilesOf(string?)"/>
	internal bool HidesFilesOf(string? relativeDirectory, Tier tier)
	{
		if(!pathRules.Any || string.IsNullOrEmpty(relativeDirectory))
			
			return false;
		
		var path = relativeDirectory.Replace('\\', '/').Trim('/');
		
		return path.Length > 0 && path != "." && pathRules.StateOf(this, tier, path) == PathState.Passage;
	}
	
	/// <summary>
	///     Whether a directory, given relative to the root, is one the server stays out of, lies
	///     inside one, or is only walked through on the way to an exception: where a walk down from
	///     the root finds nothing of its own. For the way up from a file, which meets directories
	///     without having walked to them. The root itself is never hidden.
	/// </summary>
	public bool IsHidden(string relativeDirectory)
	{
		var path = relativeDirectory.Replace('\\', '/').Trim('/');
		
		if(path.Length == 0 || path == "." || path == ".." || path.StartsWith("../", StringComparison.Ordinal))
			
			return false;
		
		if(pathRules.Any)
			
			return pathRules.StateOf(this, Tier.Excluded, path) != PathState.Visible;
		
		foreach(var part in path.AsSpan().Split('/'))
			if(excluded.Contains(path.AsSpan(part)))
				
				return true;
		
		return false;
	}
	
	/// <summary>
	///     Whether an entry of a configured ignore list is a path and not a directory name: it has a
	///     separator inside it, a leading <c>!</c>, or is a lone <c>*</c>. A name written with a
	///     slash at its end, as a <c>.gitignore</c> has it (<c>dist/</c>), is still a name.
	/// </summary>
	public static bool LooksLikePathEntry(string? entry)
	{
		if(entry is null)
			
			return false;
		
		var text = entry.Trim();
		
		if(text.StartsWith('!') || text == "*")
			
			return true;
		
		var inner = text.TrimEnd('/', '\\');
		
		return inner.Contains('/') || inner.Contains('\\');
	}
	
	/// <summary>The most parts and characters a path entry may have: enough for any real tree, and a bound on what a walk works out per directory.</summary>
	public const int MaxPathParts = 16, MaxPathLength = 260;

	// The directories an exception may not bring back: tooling and build output, whose content a
	// search must not return (a .git/config can hold a token) and whose size the walks were made
	// to stay out of (#309). 'packages' is not among them: a repository of that name is real.
	static readonly string[] neverExcepted = [".git", ".vs", "node_modules", "bin", "obj"];

	/// <summary>
	///     A path entry in the one form the rules keep it in, or false for one that is refused:
	///     parts joined by <c>/</c>, relative to the config file's directory, with a leading
	///     <c>!</c> for an exception. Refused: a leading separator (a rooted path), an empty
	///     part, <c>.</c> or <c>..</c>, a drive or any <c>:</c>, a wildcard other than a whole last
	///     part <c>*</c>, a wildcard in an exception, an exception that names a tooling or build
	///     directory (<c>.git</c>, <c>.vs</c>, <c>node_modules</c>, <c>bin</c>, <c>obj</c>), more
	///     than <see cref="MaxPathParts"/> parts or <see cref="MaxPathLength"/> characters, and a
	///     single plain name, which is a name and not a path. A glob that is only half supported
	///     is refused, not guessed at.
	/// </summary>
	public static bool TryNormalizePathEntry(string? entry, out string normalized)
	{
		normalized = "";
		
		if(string.IsNullOrWhiteSpace(entry) || entry.Length > MaxPathLength)
			
			return false;
		
		var text    = entry.Trim();
		var negated = text.StartsWith('!');
		
		if(negated)
			text = text[1..];
		
		text = text.Replace('\\', '/');
		
		if(text.StartsWith('/'))
			
			return false;
		
		text = text.TrimEnd('/');
		
		if(text.Length == 0 || text.Contains(':'))
			
			return false;
		
		var parts = text.Split('/');
		
		if(parts.Length > MaxPathParts)
			
			return false;
		
		for(var index = 0; index < parts.Length; index++) {
			
			var part = parts[index];
			
			if(part.Length == 0 || part == "." || part == ".." || part != part.Trim())
				
				return false;
			
			var wild = part.Contains('*') || part.Contains('?');
			
			if(wild && (part != "*" || index != parts.Length - 1 || negated))
				
				return false;
			
			if(negated && neverExcepted.Contains(part, StringComparer.OrdinalIgnoreCase))
				
				return false;
		}
		
		if(!negated && parts.Length == 1 && parts[0] != "*")
			
			return false;
		
		normalized = (negated ? "!" : "") + string.Join('/', parts);
		
		return true;
	}
	
	/// <summary>What the configured paths and the names make of one directory, seen from the root down.</summary>
	internal enum PathState
	{
		/// <summary>Walked and listed.</summary>
		Visible,
		
		/// <summary>Ignored, and entered all the same because an exception lies below it: nothing of its own is shown.</summary>
		Passage,
		
		/// <summary>Not entered.</summary>
		Hidden,
	}

	/// <summary>
	///     The configured path entries (#543). Three kinds: a directory (<c>a/b</c>), every
	///     directory directly inside one (<c>a/*</c>), and an exception (<c>!a/b</c>).
	///     <para>
	///         One rule, applied from the root down, decides a directory's
	///         <see cref="PathState"/>. An exception is visible, whatever would hide it. Any other
	///         directory is ignored when a name or an entry says so, or when its parent is a
	///         passage. An ignored directory with an exception somewhere below it is a passage:
	///         entered, with nothing of its own shown. One with none below it is hidden, and so
	///         is everything in it. So <c>!Packages/One</c> alone shows one directory of a tree a
	///         built-in name hides whole, and the rule holds as it is for an exception inside an
	///         exception's directory.
	///     </para>
	///     <para>
	///         What "a name says so" means is the caller's: each walk has its own tier of names,
	///         and the root's <c>.gitignore</c> counts in one of them only. The entries are
	///         relative to the config file's directory and are kept that way; a root below that
	///         directory starts from what the rule makes of the root itself, a root that is
	///         hidden being taken as visible, since it was named outright.
	///     </para>
	/// </summary>
	sealed class PathRules
	{
		public static readonly PathRules None = new([], "", PathState.Visible);
		
		readonly FrozenSet<string> exceptions;
		readonly FrozenSet<string> aboveExceptions;
		readonly FrozenSet<string> directories;
		readonly FrozenSet<string> wildcardParents;
		
		// The root, relative to the config file's directory with forward slashes; empty when it is
		// that directory. And what the rule makes of the root itself.
		readonly string    prefix;
		readonly PathState rootState;
		
		// States already worked out, by tier and root-relative path: a walk asks for the children
		// of one directory after another, and each answer starts at the root.
		readonly System.Collections.Concurrent.ConcurrentDictionary<(Tier, string), PathState> known = new();
		
		const int MaxKnown = 8_192;
		
		public bool Any { get; }
		
		PathRules(string[] entries, string prefix, PathState rootState)
		{
			var excepted = new List<string>();
			var above    = new List<string>();
			var named    = new List<string>();
			var wild     = new List<string>();
			
			foreach(var entry in entries) {
				
				if(entry.StartsWith('!')) {
					
					var path = entry[1..];
					
					excepted.Add(path);
					
					for(var end = path.IndexOf('/'); end >= 0; end = path.IndexOf('/', end + 1))
						above.Add(path[..end]);
				}
				else if(entry == "*")
					wild.Add("");
				else if(entry.EndsWith("/*", StringComparison.Ordinal))
					wild.Add(entry[..^2]);
				else
					named.Add(entry);
			}
			
			exceptions      = excepted.ToFrozenSet(gitComparer);
			aboveExceptions = above.ToFrozenSet(gitComparer);
			directories     = named.ToFrozenSet(gitComparer);
			wildcardParents = wild.ToFrozenSet(gitComparer);
			
			this.prefix    = prefix;
			this.rootState = rootState;
			
			Any = entries.Length > 0;
		}
		
		/// <summary>
		///     The rules for a root, from entries relative to <paramref name="configDirectory"/>.
		///     <paramref name="excludedName"/> says which names are ignored everywhere, for the
		///     directories between the config file and the root.
		/// </summary>
		public static PathRules Create(string[] entries, string configDirectory, string rootPath, Func<string, bool> excludedName)
		{
			var prefix = Path.GetRelativePath(configDirectory, rootPath).Replace('\\', '/').Trim('/');
			
			if(prefix == "." || prefix.Length == 0)
				
				return new PathRules(entries, "", PathState.Visible);
			
			// A root the config file is not above has no part in its paths.
			if(prefix == ".." || prefix.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(prefix))
				
				return None;
			
			var atConfig = new PathRules(entries, "", PathState.Visible);
			var state    = atConfig.Walk(PathState.Visible, prefix, 0, (_, name) => excludedName(name.ToString()), stopWhenHidden: false);
			
			return new PathRules(entries, prefix, state == PathState.Hidden ? PathState.Visible : state);
		}
		
		/// <summary>A directory and a name under it as one path with forward slashes.</summary>
		public static string Join(ReadOnlySpan<char> relativeDirectory, ReadOnlySpan<char> name)
			=> relativeDirectory.IsEmpty
				? name.ToString()
				: string.Concat(relativeDirectory, "/", name).Replace('\\', '/')
		;
		
		/// <summary>Whether a root-relative path is itself an exception.</summary>
		public bool IsException(string path)
			=> exceptions.Contains(prefix.Length == 0 ? path : $"{prefix}/{path}")
		;
		
		/// <summary>The state of a directory given relative to the root, for one tier's names.</summary>
		public PathState StateOf(IgnoreRules owner, Tier tier, string path)
		{
			if(known.TryGetValue((tier, path), out var state))
				
				return state;
			
			state = Walk(rootState, path, prefix.Length == 0 ? 0 : prefix.Length + 1, (parent, name) => owner.IgnoredIn(tier, parent, name), stopWhenHidden: true);
			
			if(known.Count >= MaxKnown)
				known.Clear();
			
			known[(tier, path)] = state;
			
			return state;
		}
		
		delegate bool NameIgnored(ReadOnlySpan<char> parent, ReadOnlySpan<char> name);
		
		// The rule, one directory of 'path' after another from 'start'. 'skip' is how much of each
		// config-relative path is the prefix, which the names are not asked about.
		PathState Walk(PathState start, string path, int skip, NameIgnored nameIgnored, bool stopWhenHidden)
		{
			var state = start;
			var full  = skip == 0 ? path : $"{prefix}/{path}";
			
			for(var from = skip; from < full.Length; ) {
				
				var to = full.IndexOf('/', from);
				
				if(to < 0)
					to = full.Length;
				
				var here   = full[..to];
				var parent = from == 0 ? "" : full[..(from - 1)];
				
				if(exceptions.Contains(here))
					state = PathState.Visible;
				else if(state != PathState.Hidden) {
					
					var ignored = state == PathState.Passage
						|| directories.Contains(here)
						|| wildcardParents.Contains(parent)
						|| nameIgnored(from <= skip ? [] : full.AsSpan(skip, from - 1 - skip), full.AsSpan(from, to - from));
					
					state = !ignored ? PathState.Visible
						: aboveExceptions.Contains(here) ? PathState.Passage
						: PathState.Hidden;
				}
				
				// Nothing below a hidden directory is an exception, or it would be a passage.
				if(state == PathState.Hidden && stopWhenHidden)
					break;
				
				from = to + 1;
			}
			
			return state;
		}
	}
}
