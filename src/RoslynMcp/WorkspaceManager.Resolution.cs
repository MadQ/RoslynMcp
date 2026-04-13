using RoslynMcp.Tools;

namespace RoslynMcp;

internal sealed partial class WorkspaceManager
{
	// ── Path resolution ──────────────────────────────────────────────────────
	
	/// <summary>
	///     Resolves a project path using smart inference:
	///     - .csproj file → use directly
	///     - directory → search for .csproj
	///     - source file → walk up to find .csproj
	///     - bare filename → scan cached MSBuild workspaces
	/// </summary>
	public (string Path, ResolutionKind Kind) ResolveProjectPath(string inputPath)
	{
		if(string.IsNullOrWhiteSpace(inputPath))
			throw new ArgumentException("Project path is required and cannot be empty. The agent must explicitly specify which project to operate on.", nameof(inputPath));
		
		var fullPath = Path.GetFullPath(inputPath);
		
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
