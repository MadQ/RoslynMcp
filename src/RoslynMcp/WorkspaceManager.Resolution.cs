using RoslynMcp.Tools;

namespace RoslynMcp;

internal sealed partial class WorkspaceManager
{
	// ── Path resolution ──────────────────────────────────────────────────────
	
	/// <summary>
	///     Resolves a project path using smart inference:
	///     - empty → the session default workspace (see <see cref="ResolveDefaultWorkspace"/>)
	///     - .sln/.slnx file → use directly
	///     - .csproj file → use directly
	///     - directory → search for .csproj
	///     - source file → walk up to find .csproj
	///     - bare filename → scan cached MSBuild workspaces
	/// </summary>
	public (string Path, ResolutionKind Kind) ResolveProjectPath(string inputPath)
	{
		if(string.IsNullOrWhiteSpace(inputPath))
			
			return ResolveDefaultWorkspace();
		
		var fullPath = Path.GetFullPath(inputPath);
		
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
	///     Throws <see cref="NoDefaultWorkspaceException"/> when nothing usable is found.
	/// </summary>
	(string Path, ResolutionKind Kind) ResolveDefaultWorkspace()
	{
		if(Volatile.Read(ref defaultWorkspacePath) is not { } path) {
			
			var configured = ServerArgs.Current.Root;
			
			var (root, source) = configured is not null
				? (Path.GetFullPath(configured), "configured root")
				: (Environment.CurrentDirectory, "server working directory")
			;
			
			var found = FindDefaultWorkspace(root, source, logger);
			
			if(Interlocked.CompareExchange(ref defaultWorkspacePath, found, null) is null)
				logger.LogInfo("Workspace", $"Default workspace: {found} (from {source} '{root}')");
			
			path = defaultWorkspacePath!;
		}
		
		return (path, IsSolutionPath(path) ? ResolutionKind.Solution : ResolutionKind.Explicit);
	}
	
	/// <summary>
	///     Picks the default workspace for <paramref name="root"/>: a file root names it outright;
	///     a directory root prefers a <c>solution</c> pinned in <see cref="ProjectConfig"/>, then the one
	///     solution in the directory, then the nearest solution above it, then the one .csproj in it.
	///     Never searches downward — a root is a place the user pointed at, not a tree to crawl.
	/// </summary>
	static string FindDefaultWorkspace(string root, string source, FileLogger logger)
	{
		if(File.Exists(root))
			
			return IsSolutionPath(root) || root.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
				? root
				: throw new NoDefaultWorkspaceException(root, source, "it is a file, but not a .sln, .slnx, or .csproj")
			;
		
		if(!Directory.Exists(root))
			throw new NoDefaultWorkspaceException(root, source, "the directory does not exist");
		
		if(SecurityBoundary.IsDangerousProjectPath(root))
			throw new NoDefaultWorkspaceException(root, source, "it is a drive root or system directory");
		
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
		
		return FindProjectInDirectory(root)
			?? throw new NoDefaultWorkspaceException(root, source, "no .sln, .slnx, or .csproj was found in it, and no solution above it")
		;
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
