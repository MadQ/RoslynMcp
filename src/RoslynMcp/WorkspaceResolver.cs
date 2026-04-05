using Microsoft.CodeAnalysis;

namespace RoslynMcp;

/// <summary>
///     Helper class that encapsulates project path resolution and workspace access.
///     Provides a clean API for tools to get compilations without directly managing resolution logic.
/// </summary>
internal sealed class WorkspaceResolver
{
	readonly WorkspaceManager  manager;
	readonly PaginationCache   paginationCache;
	
	public WorkspaceResolver(WorkspaceManager manager, PaginationCache paginationCache)
	{
		this.manager         = manager;
		this.paginationCache = paginationCache;
	}
	
	/// <summary>
	///     Resolves a project path and returns both the resolved path and how it was resolved.
	/// </summary>
	private (string Resolved, ResolutionKind Kind) ResolveWithKind(string projectPath)
	{
		return manager.ResolveProjectPath(projectPath);
	}
	
	/// <summary>
	///     Returns how the given projectPath would be resolved (explicit, directory, file walk-up, inferred, or adhoc).
	///     Tools use this to annotate log entries when non-obvious resolution occurred.
	/// </summary>
	public ResolutionKind GetResolutionKind(string projectPath)
	{
		var (_, kind) = ResolveWithKind(projectPath);
		
		return kind;
	}
	
	/// <summary>
	///     Resolves the project path and returns the compilation.
	/// </summary>
	public Compilation GetCompilation(string projectPath)
	{
		var (resolved, _) = ResolveWithKind(projectPath);
		
		return manager.GetCompilation(resolved);
	}
	
	/// <summary>
	///     Resolves the project path and returns the solution.
	/// </summary>
	public Solution GetSolution(string projectPath)
	{
		var (resolved, _) = ResolveWithKind(projectPath);
		
		return manager.GetSolution(resolved);
	}
	
	/// <summary>
	///     Gets the root path for a resolved project.
	///     Used for computing relative paths in tool responses.
	/// </summary>
	public string GetRootPath(string projectPath)
	{
		var (rootPath, _, _) = GetWorkspaceInfo(projectPath);
		
		return rootPath;
	}
	
	/// <summary>
	///     Resolves the project path and returns the project.
	/// </summary>
	public Project GetProject(string projectPath)
	{
		var (resolved, _) = ResolveWithKind(projectPath);
		
		return manager.GetProject(resolved);
	}
	
	/// <summary>
	///     Gets workspace metadata for the resolved project (root path, MSBuild flag, .csproj path).
	/// </summary>
	public (string RootPath, bool IsMSBuild, string? CsprojPath) GetWorkspaceInfo(string projectPath)
	{
		var (resolved, _) = ResolveWithKind(projectPath);
		
		return manager.GetWorkspaceInfo(resolved);
	}
	
	/// <summary>
	///     Returns true when the resolved workspace is an AdhocWorkspace (no .csproj found).
	///     Tools use this to attach a caution to success responses.
	/// </summary>
	public bool IsAdhoc(string projectPath)
	{
		var (_, isMSBuild, _) = GetWorkspaceInfo(projectPath);
		
		return !isMSBuild;
	}
	
	/// <summary>
	///     Invalidates the cached compilation for a file after edits.
	///     Tools that modify files should call this to ensure fresh diagnostics on subsequent queries.
	/// </summary>
	/// <summary>
	///     Applies an updated solution and writes changed documents to disk (MSBuild only).
	///     Prefer this over direct file I/O + <see cref="InvalidateFile"/> when the caller
	///     already holds the updated <see cref="Solution"/> in memory.
	/// </summary>
	public void ApplyChanges(string projectPath, Solution newSolution)
	{
		var (resolved, _) = ResolveWithKind(projectPath);
		manager.ApplyChanges(resolved, newSolution);
	}
	

	public void InvalidateFile(string projectPath, string fullPath)
	{
		var (resolved, _) = ResolveWithKind(projectPath);
		
		manager.InvalidateFile(resolved, fullPath);
		paginationCache.InvalidateAll();
	}
}
