namespace RoslynMcp;

/// <summary>
///     Enforces filesystem access boundaries for a workspace.
///     All path validation normalizes with <see cref="Path.GetFullPath"/> before comparison
///     to prevent directory traversal via <c>..</c> components or alternate path representations.
///     Symbolic links are denied to prevent escape via symlink redirection.
/// </summary>
internal sealed class SecurityBoundary
{
	readonly string[] allowedRoots;
	
	// Computed once at startup — SpecialFolder lookups involve platform invocation and filesystem access.
	static readonly string[] systemDirectories = BuildSystemDirectories()
	;
	
	
	/// <param name="workspaceRoot">The resolved root directory of the workspace (project or solution directory).</param>
	/// <param name="solutionRoot">The solution directory, if any, which adds an additional trusted root.</param>
	/// <param name="referencedProjectRoots">Roots of directly referenced projects; each becomes a trusted root.</param>
	public SecurityBoundary(string workspaceRoot, string? solutionRoot = null, IEnumerable<string>? referencedProjectRoots = null)
	{
		var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { workspaceRoot };
		
		if(solutionRoot is not null)
			roots.Add(solutionRoot);
		
		if(referencedProjectRoots is not null)
			foreach(var r in referencedProjectRoots)
				roots.Add(r);
		
		allowedRoots = [..roots];
	}
	
	/// <summary>
	///     Returns true if <paramref name="requestedPath"/> is accessible within this workspace's
	///     trusted roots. Normalizes the path with <see cref="Path.GetFullPath"/> and denies symbolic links.
	/// </summary>
	public bool IsPathAllowed(string requestedPath)
	{
		string normalized;
		
		try {
			normalized = Path.GetFullPath(requestedPath);
		}
		catch(Exception ex) when(ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) {
			
			return false;
		}
		
		// Deny symbolic links anywhere in the path chain — prevents escape via symlinked directories.
		try {
			
			var current = normalized;
			
			while(!string.IsNullOrEmpty(current)) {
				
				if((File.Exists(current) || Directory.Exists(current)) &&
					File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
					
					return false;
				
				var parent = Path.GetDirectoryName(current);
				
				if(parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
					break;
				
				current = parent;
			}
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
		
		foreach(var root in allowedRoots.AsSpan())
			if(IsUnderDirectory(normalized, root))
				
				return true;
		
		return false;
	}
	
	/// <summary>
	///     Returns true if <paramref name="fullPath"/> is a drive root or known system directory
	///     that must not be opened as a project. Used by <see cref="WorkspaceManager.ResolveProjectPath"/>
	///     before workspace creation to prevent arbitrary directory scanning.
	/// </summary>
	internal static bool IsDangerousProjectPath(string fullPath)
	{
		if(IsDriveRoot(fullPath))
			
			return true;
		
		foreach(var dir in systemDirectories.AsSpan()) {
			
			if(IsUnderDirectory(fullPath, dir))
				
				return true;
		}
		
		return false;
	}
	
	/// <summary>
	///     Returns true if <paramref name="path"/> is contained within <paramref name="root"/>,
	///     including the root directory itself. Uses separator-aware comparison to prevent prefix
	///     collisions (e.g. root <c>D:\Foo</c> incorrectly matching <c>D:\FooBar\file.cs</c>).
	/// </summary>
	internal static bool IsUnderDirectory(string path, string root)
	{
		if(string.IsNullOrEmpty(root))
			
			return false;
		
		var rootNorm = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		
		if(string.Equals(path, rootNorm, StringComparison.OrdinalIgnoreCase))
			
			return true;
		
		// Check both separator styles for cross-platform compatibility.
		
		return path.StartsWith(rootNorm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
			|| path.StartsWith(rootNorm + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}
	
	static bool IsDriveRoot(string fullPath)
	{
		var root = Path.GetPathRoot(fullPath);
		
		if(root is null)
			
			return false;
		
		// Strip trailing separators on both sides for consistent comparison.
		var normalizedFull = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
		;
		var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		
		return string.Equals(normalizedFull, normalizedRoot, StringComparison.OrdinalIgnoreCase);
	}
	
	static string[] BuildSystemDirectories()
	{
		var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		
		if(OperatingSystem.IsWindows()) {
			
			AddIfNotEmpty(dirs, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
			AddIfNotEmpty(dirs, Environment.GetFolderPath(Environment.SpecialFolder.System));
			AddIfNotEmpty(dirs, Environment.GetFolderPath(Environment.SpecialFolder.SystemX86));
		}
		else {
			
			dirs.Add("/etc");
			dirs.Add("/sys");
			dirs.Add("/proc");
			dirs.Add("/dev");
			dirs.Add("/bin");
			dirs.Add("/sbin");
			dirs.Add("/boot");
		}
		
		// User-sensitive directories common to both platforms.
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
		;
		
		if(!string.IsNullOrEmpty(home)) {
			
			AddIfNotEmpty(dirs, Path.Combine(home, ".ssh"));
			AddIfNotEmpty(dirs, Path.Combine(home, ".gnupg"));
			
			if(OperatingSystem.IsWindows()) {
				
				var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
				
				if(!string.IsNullOrEmpty(appData)) {
					
					AddIfNotEmpty(dirs, Path.Combine(appData, "Microsoft", "Credentials"));
					AddIfNotEmpty(dirs, Path.Combine(appData, "Microsoft", "Protect"));
				}
			}
		}
		
		return [..dirs];
	}
	
	static void AddIfNotEmpty(HashSet<string> set, string value)
	{
		if(!string.IsNullOrEmpty(value))
			set.Add(value);
	}
}
