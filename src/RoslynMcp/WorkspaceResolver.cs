using Microsoft.CodeAnalysis;

namespace RoslynMcp;

/// <summary>
///     Helper class that encapsulates project path resolution and workspace access.
///     Provides a clean API for tools to get compilations without directly managing resolution logic.
/// </summary>
internal sealed class WorkspaceResolver
{
	readonly WorkspaceManager manager;
	
	public WorkspaceResolver(WorkspaceManager manager)
	{
		this.manager = manager;
	}
	
	/// <summary>
	///     Resolves the project path and returns the compilation.
	/// </summary>
	public Compilation GetCompilation(string projectPath)
	{
		var resolved = manager.ResolveProjectPath(projectPath);
		
		return manager.GetCompilation(resolved);
	}
	
	/// <summary>
	///     Resolves the project path and returns the solution.
	/// </summary>
	public Solution GetSolution(string projectPath)
	{
		var resolved = manager.ResolveProjectPath(projectPath);
		
		return manager.GetSolution(resolved);
	}
	
	/// <summary>
	///     Gets the root path for a resolved project.
	///     Used for computing relative paths in tool responses.
	/// </summary>
	public string GetRootPath(string projectPath)
	{
		var resolved = manager.ResolveProjectPath(projectPath);
		
		// Root path is the directory containing the .csproj
		return Path.GetDirectoryName(resolved)!;
	}
	
	/// <summary>
	///     Resolves the project path and returns the project.
	/// </summary>
	public Project GetProject(string projectPath)
	{
		var resolved = manager.ResolveProjectPath(projectPath);
		
		return manager.GetProject(resolved);
	}
	
	/// <summary>
	///     Gets workspace metadata for the resolved project (root path, MSBuild flag, .csproj path).
	/// </summary>
	public (string RootPath, bool IsMSBuild, string? CsprojPath) GetWorkspaceInfo(string projectPath)
	{
		var resolved = manager.ResolveProjectPath(projectPath);
		
		return manager.GetWorkspaceInfo(resolved);
	}
	
	/// <summary>
	///     Invalidates the cached compilation for a file after edits.
	///     Tools that modify files should call this to ensure fresh diagnostics on subsequent queries.
	/// </summary>
	public void InvalidateFile(string projectPath, string fullPath)
	{
		var resolved = manager.ResolveProjectPath(projectPath);
		
		manager.InvalidateFile(resolved, fullPath);
	}
}
