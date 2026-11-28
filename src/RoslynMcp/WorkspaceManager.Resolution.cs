using RoslynMcp.Tools;

namespace RoslynMcp;

internal sealed partial class WorkspaceManager
{
	// ── Path resolution ──────────────────────────────────────────────────────
	
	/// <summary>
	///     Resolves a project path using smart inference:
	///     - .sln/.slnx file → use directly
	///     - .csproj file → use directly
	///     - directory → search for .csproj
	///     - source file → walk up to find .csproj
	///     - bare filename → scan cached MSBuild workspaces
	/// </summary>
	public (string Path, ResolutionKind Kind) ResolveProjectPath(string inputPath)
	{
		// Deliberately not the default workspace: only tools whose projectPath is optional opt in to it,
		// explicitly, via ResolveDefaultWorkspacePath. An empty string sent to a tool that requires
		// projectPath must fail as missing rather than silently land on the default solution.
		if(string.IsNullOrWhiteSpace(inputPath))
			throw new ArgumentException("projectPath is required by this tool and cannot be empty.", nameof(inputPath));
		
		// Short (8.3) names expanded before anything routes by extension: SAME~1.CSP is Same.csproj (#386).
		var fullPath = FileIdentity.LongPath(RootedPath(inputPath, out _));
		
		if(SecurityBoundary.IsDangerousProjectPath(fullPath))
			throw new InvalidProjectPathException(fullPath, "Path is not a valid project location");
		
		if(IsSolutionPath(fullPath)) {
			
			if(!File.Exists(fullPath))
				throw new InvalidProjectPathException(fullPath, "File does not exist");
			
			return (fullPath, ResolutionKind.Solution);
		}
		
		// Already a .csproj file — explicit, no inference needed.
		if(fullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) {
			
			if(!File.Exists(fullPath))
				throw new InvalidProjectPathException(fullPath, "File does not exist");
			
			return (fullPath, ResolutionKind.Explicit);
		}
		
		// Directory — search for .csproj.
		if(Directory.Exists(fullPath)) {
			
			var csprojPath = FindProjectInDirectory(fullPath);
			
			if(csprojPath is not null)
				
				return (csprojPath, ResolutionKind.Directory);
			
			// The root of the session's solution set is no directory of loose sources (#501): as
			// an AdhocWorkspace it would be every source file of every solution below it.
			if(IsSolutionSetRoot(fullPath))
				
				return (Path.TrimEndingDirectorySeparator(fullPath), ResolutionKind.SolutionSet);
			
			return (fullPath, ResolutionKind.Adhoc);
		}
		
		// File path — walk up to find .csproj.
		if(File.Exists(fullPath))
			
			return (FindProjectFileUpwards(fullPath), ResolutionKind.FileWalkUp);
		
		// Last resort: bare filename — scan cached MSBuild workspaces.
		var inferred = TryInferWorkspaceFromFileName(Path.GetFileName(fullPath))
		;
		
		if(inferred is not null)
			
			return (inferred, ResolutionKind.InferredFromCache);
		
		throw new InvalidProjectPathException(fullPath, "Path does not exist");
	}
	
	/// <summary>
	///     Makes a projectPath absolute. A relative one is taken against the directory of the default
	///     workspace first, when there is one and the path names something in it, and against the
	///     working directory otherwise (#298). Tools report paths relative to the workspace root, so
	///     that is what an agent passes back, and a client often starts the server somewhere else,
	///     which is why <c>--root</c> exists. The working directory stays the fallback, so every
	///     path that resolved before still resolves; with no default workspace nothing changes.
	///     One thing does change: a path that names something under both directories ("." or "src",
	///     with the server started in another checkout) now means the one under the default root.
	///     The root is what the user pointed the server at, and it is what the paths in tool
	///     results are relative to. Only the startup inputs and the file system decide, never which
	///     workspaces are loaded.
	/// </summary>
	public string RootedPath(string inputPath, out bool underDefaultRoot)
	{
		underDefaultRoot = false;
		
		// An empty path is left for the caller to reject.
		if(string.IsNullOrWhiteSpace(inputPath))
			
			return inputPath;
		
		if(Path.IsPathFullyQualified(inputPath))
			
			return Path.GetFullPath(inputPath);
		
		var underWorkingDirectory = Path.GetFullPath(inputPath);
		
		if(DefaultWorkspaceDirectory() is not { } root)
			
			return underWorkingDirectory;
		
		var underRoot = Path.GetFullPath(inputPath, root);
		
		// The same place either way, as when the server was started in the repository.
		if(SecurityBoundary.PathComparer.Equals(underRoot, underWorkingDirectory) || !(File.Exists(underRoot) || Directory.Exists(underRoot)))
			
			return underWorkingDirectory;
		
		underDefaultRoot = true;
		
		return underRoot;
	}
	
	// When the search for a default workspace last came up empty. The search lists directories and
	// walks up to the drive root, and RootedPath asks several times per tool call, so an empty
	// answer is kept for a moment. Not for good: a solution may be created while the server runs,
	// and an omitted projectPath, which always searches, would find it. One created in the root
	// or above it is found by the next call; one created below a root that had none is found
	// within a minute, since the walk down is kept longer (NothingBelowRetryMs).
	const int NoDefaultRetryMs = 2_000;
	
	// Starts one interval before any tick count, so the first question always searches.
	long noDefaultSince = -NoDefaultRetryMs;
	int  lookupFailureLogged;
	
	/// <summary>
	///     The directory of the default workspace, which relative paths are tried against first;
	///     <see langword="null"/> when there is no default workspace.
	/// </summary>
	string? DefaultWorkspaceDirectory()
	{
		if(Volatile.Read(ref defaultWorkspacePath) is null && Environment.TickCount64 - Volatile.Read(ref noDefaultSince) < NoDefaultRetryMs)
			
			return null;
		
		try {
			
			var path = ResolveDefaultWorkspacePath();
			
			// A solution set is its own directory (#501); a solution or a project is a file in one.
			return IsSolutionSetPath(path) ? path : Path.GetDirectoryName(path);
		}
		catch(Exception ex) when(IsDefaultLookupFailure(ex)) {
			
			// "Nothing found" is the ordinary answer. Anything else is said once, since it is
			// swallowed here and the path quietly goes to the working directory.
			if(ex is not NoDefaultWorkspaceException && Interlocked.Exchange(ref lookupFailureLogged, 1) == 0)
				logger.LogInfo("Workspace", $"Default workspace lookup failed, relative paths resolve against the working directory: {ex.GetType().Name}: {ex.Message}");
			
			Volatile.Write(ref noDefaultSince, Environment.TickCount64);
			
			return null;
		}
	}
	
	/// <summary>
	///     What the search for a default workspace fails with: nothing found, several found, a
	///     directory that cannot be read, or a configured root that is not a path at all.
	/// </summary>
	static bool IsDefaultLookupFailure(Exception ex)
		=> ex is NoDefaultWorkspaceException or MultipleProjectsFoundException or IOException or UnauthorizedAccessException
			or ArgumentException or NotSupportedException or System.Security.SecurityException
	;
	
	/// <summary>
	///     Walks upward from <paramref name="startDir"/> looking for a single .sln or .slnx file.
	///     Returns null if none found or if multiple solutions exist in the same directory (ambiguous).
	///     Silently skips inaccessible directories.
	/// </summary>
	static string? FindSolutionFileUpwards(string startDir)
	{
		var dir = startDir;
		
		while(dir is not null) {
			
			var slnxFiles = GetFilesSafe(dir, "*.slnx");
			var slnFiles  = GetFilesSafe(dir, "*.sln");
			
			// Prefer .slnx (newer format) over .sln.
			if(slnxFiles.Length == 1 && slnFiles.Length == 0)
				
				return slnxFiles[0];
			
			if(slnFiles.Length == 1 && slnxFiles.Length == 0)
				
				return slnFiles[0];
			
			// Multiple solutions or both formats present — ambiguous, fall back to project-level.
			if(slnxFiles.Length + slnFiles.Length > 1)
				
				return null;
			
			dir = Directory.GetParent(dir)?.FullName;
		}
		
		return null;
	}
	
	/// <summary>
	///     Scans cached workspaces for a document whose file path ends with
	///     <paramref name="fileName"/>. Returns the .csproj path of the containing project.
	/// </summary>
	string? TryInferWorkspaceFromFileName(string fileName)
	{
		if(string.IsNullOrEmpty(fileName))
			
			return null;
		
		var suffix = Path.DirectorySeparatorChar + fileName;
		
		List<string>? matches = null;
		
		lock(cacheLock) {
			
			foreach(var (_, entry) in cache) {
				
				if(!entry.Instance.IsMSBuild)
					continue;
				
				var csproj = entry.Instance.FindCsprojForFileSuffix(suffix, fileName);
				
				if(csproj is null)
					continue;
				
				matches ??= [];
				matches.Add(csproj);
			}
		}
		
		if(matches is null)
			
			return null;
		
		if(matches.Count == 1)
			
			return matches[0];
		
		throw new AmbiguousFileException(fileName, matches.ToArray());
	}
	
	static string? FindProjectInDirectory(string directory)
	{
		var csprojFiles = GetFilesSafe(directory, "*.csproj");
		
		if(csprojFiles.Length == 0)
			
			return null;
		
		if(csprojFiles.Length == 1)
			
			return csprojFiles[0];
		
		throw new MultipleProjectsFoundException(directory, csprojFiles);
	}
	
	static string FindProjectFileUpwards(string startPath)
	{
		var dir = File.Exists(startPath)
			? Path.GetDirectoryName(startPath)
			: startPath;
		
		if(dir is null)
			throw new ProjectNotFoundException(startPath);
		
		while(true) {
			
			var csprojFiles = GetFilesSafe(dir, "*.csproj");
			
			if(csprojFiles.Length == 1)
				
				return csprojFiles[0];
			
			if(csprojFiles.Length > 1)
				throw new MultipleProjectsFoundException(dir, csprojFiles);
			
			var parent = Directory.GetParent(dir);
			
			if(parent is null)
				throw new ProjectNotFoundException(startPath);
			
			dir = parent.FullName;
		}
	}
	
	// ── Default workspace ────────────────────────────────────────────────────
	
	// Resolved once, on the first call that omits projectPath. Its inputs — the configured root and
	// the process CWD — are fixed for the process lifetime, so a cached answer never goes stale.
	// Failures are not cached: they are cheap to recompute and carry the reason back to the caller.
	string? defaultWorkspacePath;
	
	/// <summary>
	///     The session default workspace — a .sln/.slnx or .csproj path — used whenever a tool is called
	///     without <c>projectPath</c>. Deterministic by construction: it depends only on startup inputs,
	///     never on which workspaces happen to be cached, which is what separates it from a guess.
	///     Throws <see cref="NoDefaultWorkspaceException"/> when nothing usable is found. Filesystem-only:
	///     never loads a workspace.
	/// </summary>
	public string ResolveDefaultWorkspacePath()
	{
		if(Volatile.Read(ref defaultWorkspacePath) is not { } path) {
			
			var (root, source) = DefaultWorkspaceRoot();
			
			var found = FindDefaultWorkspace(root, source, logger);
			
			if(Interlocked.CompareExchange(ref defaultWorkspacePath, found, null) is null)
				logger.LogInfo("Workspace", IsSolutionSetPath(found)
					? $"Default workspace: none, {found} is a root of several solutions; each call lands in the solution above the file it names (from {source} '{root}')"
					: $"Default workspace: {found} (from {source} '{root}')");
			
			path = defaultWorkspacePath!;
		}
		
		return path;
	}
	
	/// <summary>
	///     The directory or file the default workspace is looked for in, and what named it. Both are
	///     fixed at startup.
	/// </summary>
	static (string Root, string Source) DefaultWorkspaceRoot()
		=> ServerArgs.Current.Root is { } configured
			? (Path.GetFullPath(configured), "configured root")
			: (Environment.CurrentDirectory, "server working directory")
	;
	
	/// <summary>
	///     The default workspace as <c>roslyn_info</c> reports it (#298): the path an omitted
	///     <c>projectPath</c> stands for, or the reason there is none, with the root it was looked
	///     for in and the setting that named the root. The same cached answer as
	///     <see cref="ResolveDefaultWorkspacePath"/>, and like it filesystem-only: asking never
	///     loads a workspace.
	/// </summary>
	public DefaultWorkspaceInfo DescribeDefaultWorkspace()
	{
		var source = ServerArgs.Current.RootSource ?? "server working directory";
		
		// The root as it was given, until it has been made a full path: that step fails for a
		// root that is not a path, and the answer must still name it.
		var root = ServerArgs.Current.Root ?? Environment.CurrentDirectory;
		
		try {
			
			(root, _) = DefaultWorkspaceRoot();
			
			var path = ResolveDefaultWorkspacePath();
			
			if(SolutionSetRoot is not { } set)
				
				return new(path, root, source, null);
			
			// A solution set (#501): no path an omitted projectPath stands for, and the solutions a
			// call can land in, each with whether the server holds it now.
			return new(null, root, source, null) {
				Solutions = [..SolutionSet.Find(set, logger).Select(solution => new SolutionSetMember(Path.GetRelativePath(set, solution), IsLoaded(solution)))]
			};
		}
		catch(Exception ex) when(IsDefaultLookupFailure(ex)) {
			
			return new(null, root, source, ex.Message);
		}
	}
	
	/// <summary>
	///     Picks the default workspace for <paramref name="root"/>: a file root names it outright;
	///     a directory root prefers a <c>solution</c> pinned in <see cref="ProjectConfig"/>, then the one
	///     solution in the directory, then the nearest solution above it, then the one .csproj in it.
	///     Last, and only below a root the user named, the solutions a few levels down (#501): one is
	///     the default, several make the root a <see cref="SolutionSet"/>, which is returned as the
	///     directory itself. The working directory is never searched downward: it is wherever a
	///     client started the server, not a place the user pointed at (#295).
	/// </summary>
	static string FindDefaultWorkspace(string root, string source, FileLogger logger)
	{
		// Checked before anything else, so a file root inside a sensitive tree (~/.ssh/X.slnx) is
		// rejected exactly like the directory itself would be.
		if(SecurityBoundary.IsDangerousProjectPath(root))
			throw new NoDefaultWorkspaceException(root, source, "it is, or lies inside, a drive root or system directory");
		
		var found = FindDefaultWorkspaceCandidate(root, source, logger);
		
		// The upward walk can land on a solution whose own directory is dangerous — a .sln at a drive
		// root. Loading it would scope the workspace, its watcher, and its security boundary to that
		// whole tree, so the result is held to the same rule as the root.
		// A solution set is the root itself, which was checked above.
		if(!IsSolutionSetPath(found) && SecurityBoundary.IsDangerousProjectPath(Path.GetDirectoryName(found)!))
			throw new NoDefaultWorkspaceException(root, source, $"it resolves to '{found}', whose directory is a drive root or system directory");
		
		return found;
	}
	
	// The root last walked for solutions below it without finding one, and when (see the walk).
	// A reference, so that it is read and written whole.
	sealed record NothingBelow(string Root, long At);
	
	static volatile NothingBelow? nothingBelow;
	
	const int NothingBelowRetryMs = 60_000;
	
	static string FindDefaultWorkspaceCandidate(string root, string source, FileLogger logger)
	{
		if(File.Exists(root))
			
			return IsSolutionPath(root) || root.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
				? root
				: throw new NoDefaultWorkspaceException(root, source, "it is a file, but not a .sln, .slnx, or .csproj")
			;
		
		if(!Directory.Exists(root))
			throw new NoDefaultWorkspaceException(root, source, "the directory does not exist");
		
		if(ProjectConfig.ForPath(root, logger).Solution is { } pinned)
			
			return pinned;
		
		var solutions = SolutionFilesIn(root);
		
		if(solutions.Length == 1)
			
			return solutions[0];
		
		if(solutions.Length > 1)
			throw new NoDefaultWorkspaceException(root, source,
				$"it holds {solutions.Length} solutions ({string.Join(", ", solutions.Select(Path.GetFileName))}) — " +
				$"pin one with \"solution\" in {ProjectConfig.FileName}, or pass a solution path as --root");
		
		if(Directory.GetParent(root) is { } parent && FindSolutionFileUpwards(parent.FullName) is { } above)
			
			return above;
		
		if(FindProjectInDirectory(root) is { } project)
			
			return project;
		
		// Looked for below the root only where the user pointed the server at it (#501). The
		// working directory is wherever a client happened to start the server, often a home
		// directory, and a search downward from there is the crawl #295 ended: it would make
		// every repository under a home directory a solution set, with the home directory as
		// its boundary. A home directory is not searched even when it is named, and neither is
		// a directory above one (C:\Users, /home): a root given as "." is a named root, and is
		// still wherever the client started the server.
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		
		if(ServerArgs.Current.Root is null || (home.Length > 0 && SecurityBoundary.StartsWithPath(home, Path.TrimEndingDirectorySeparator(root))))
			throw new NoDefaultWorkspaceException(root, source, "no .sln, .slnx, or .csproj was found in it, and no solution above it");
		
		// A root with nothing below it is not walked again for a minute. The lookup is repeated
		// every two seconds while there is no default, by whichever tool call comes next, and the
		// walk is what costs: the steps above list a few directories, this one a tree.
		if(nothingBelow is (var walked, var at) && SecurityBoundary.PathComparer.Equals(walked, root) && Environment.TickCount64 - at < NothingBelowRetryMs)
			throw new NoDefaultWorkspaceException(root, source, $"no .sln, .slnx, or .csproj was found in it, no solution above it, and none in the {SolutionSet.Depth} directory levels below it (looked for below it less than a minute ago, and again once a minute)");
		
		var below = SolutionSet.Find(root, logger);
		
		if(below.Length == 0)
			nothingBelow = new(root, Environment.TickCount64);
		
		// Nothing in the root and nothing above it: the solutions below it. One of them is
		// the default as if it were in the root. Several make the root a solution set, which is
		// answered as a directory: no workspace, and each call lands in one of them.
		return below switch {
			
			[]         => throw new NoDefaultWorkspaceException(root, source, $"no .sln, .slnx, or .csproj was found in it, no solution above it, and none in the {SolutionSet.Depth} directory levels below it"),
			[var only] => only,
			_          => Path.TrimEndingDirectorySeparator(root),
		};
	}
	
	// A default workspace path that is neither a solution nor a project is the root of a solution
	// set (#501): the one value says both, so the two cannot come apart.
	static bool IsSolutionSetPath(string defaultPath)
		=> !IsSolutionPath(defaultPath) && !defaultPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
	;
	
	/// <summary>
	///     The root of the session's solution set, or <see langword="null"/> when the default is a
	///     solution, a project, or nothing. Looks the default up when that has not happened yet:
	///     a call may name the root outright before any call has omitted its projectPath.
	/// </summary>
	public string? SolutionSetRoot {
		get {
			if(Volatile.Read(ref defaultWorkspacePath) is null)
				DefaultWorkspaceDirectory();
			
			return Volatile.Read(ref defaultWorkspacePath) is { } path && IsSolutionSetPath(path) ? path : null;
		}
	}
	
	bool IsSolutionSetRoot(string fullPath)
		=> SolutionSetRoot is { } root && SecurityBoundary.PathComparer.Equals(Path.TrimEndingDirectorySeparator(fullPath), root)
	;
	
	/// <summary>The solutions of the session's solution set as they are on disk now; empty without a set.</summary>
	public string[] SolutionSetMembers()
	{
		if(SolutionSetRoot is not { } root)
			
			return [];
		
		// Kept for a few seconds: an answer that names the other solutions of a project asks
		// with every call, and the walk is a fifth of a second on a root of twenty repositories.
		if(members is (var found, var at) && Environment.TickCount64 - at < 5_000)
			
			return found;
		
		var walked = SolutionSet.Find(root, logger);
		
		members = new(walked, Environment.TickCount64);
		
		return walked;
	}
	
	sealed record Members(string[] Found, long At);
	
	volatile Members? members;
	
	/// <summary>The solutions of the session's solution set whose workspace is loaded now.</summary>
	public string[] LoadedSetMembers()
	{
		if(SolutionSetRoot is not { } root)
			
			return [];
		
		lock(cacheLock)
			
			return [..cache.Keys.Where(key => IsSolutionPath(key) && SecurityBoundary.StartsWithPath(key, root)).Order(StringComparer.OrdinalIgnoreCase)];
	}
	
	/// <summary>Whether the workspace of a solution file is loaded, without loading it.</summary>
	public bool IsLoaded(string solutionPath)
	{
		lock(cacheLock)
			
			return cache.ContainsKey(Path.GetFullPath(solutionPath));
	}
	
	// The boundary of the set's root: every solution below it is inside. Made once.
	SecurityBoundary? solutionSetBoundary;
	
	/// <summary>
	///     The root and the boundary of the session's solution set, for the calls that only list,
	///     search or read files and so need no workspace; <see langword="null"/> when
	///     <paramref name="resolvedPath"/> is not that root.
	/// </summary>
	public (string Root, SecurityBoundary Boundary)? SolutionSetContext(string resolvedPath)
	{
		if(!IsSolutionSetRoot(Path.GetFullPath(resolvedPath)))
			
			return null;
		
		var root = SolutionSetRoot!;
		
		return (root, LazyInitializer.EnsureInitialized(ref solutionSetBoundary, () => new SecurityBoundary(root)));
	}
	
	/// <summary>True for a .sln or .slnx path.</summary>
	internal static bool IsSolutionPath(string path)
		=> path.EndsWith(".sln",  StringComparison.OrdinalIgnoreCase)
		|| path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
	;
	
	// Filtered on the exact extension rather than trusting the search pattern's extension matching.
	static string[] SolutionFilesIn(string directory)
		=> [..GetFilesSafe(directory, "*.sln*").Where(IsSolutionPath)];
	
	// ── IO helper ────────────────────────────────────────────────────────────
	
	/// <summary>
	///     <see cref="Directory.GetFiles"/> with filesystem exception guarding.
	///     Returns an empty array if the directory is inaccessible or missing.
	/// </summary>
	static string[] GetFilesSafe(string directory, string pattern)
	{
		try {
			return Directory.GetFiles(directory, pattern);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) {
			return [];
		}
	}
}
