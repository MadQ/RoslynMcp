using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

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
	
	public string[] GetLoadWarnings(string projectPath)
	{
		var (resolved, _) = ResolveWithKind(projectPath);

		return manager.GetLoadWarnings(resolved);
	}

	public WorkspaceHealth GetHealth(string projectPath)
	{
		var (resolved, _) = ResolveWithKind(projectPath);

		return manager.GetHealth(resolved);
	}
	
	public DateTime GetLastSyncedUtc(string projectPath)
	{
		var (resolved, _) = ResolveWithKind(projectPath);
		
		return manager.GetLastSyncedUtc(resolved);
	}
	
	public Solution PeekSolution(string projectPath)
	{
		var (resolved, _) = ResolveWithKind(projectPath);
		
		return manager.PeekSolution(resolved);
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
	///     Returns the <see cref="SecurityBoundary"/> for the workspace, creating it if the workspace
	///     is not yet loaded. Use to validate file paths before accessing files outside the normal
	///     tool flow.
	/// </summary>
	public SecurityBoundary GetSecurityBoundary(string projectPath)
	{
		var (resolved, _) = ResolveWithKind(projectPath);
		
		return manager.GetSecurityBoundary(resolved);
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
	public bool ApplyChanges(string projectPath, Solution newSolution)
	{
		var (resolved, _) = ResolveWithKind(projectPath);
		
		return manager.ApplyChanges(resolved, newSolution);
	}
	
	
	public void InvalidateFile(string projectPath, string fullPath)
	{
		var (resolved, _) = ResolveWithKind(projectPath);
		
		manager.InvalidateFile(resolved, fullPath);
		paginationCache.InvalidateAll();
	}
	
	/// <summary>
	///     Writes a text change to a .cs file via the workspace-managed path.
	///     For MSBuild-tracked files: single write via TryApplyChanges (FSW-suppressed).
	///     For untracked/Adhoc: FileWriter write with per-file FSW suppression, then InvalidateFile.
	/// </summary>
	public async Task ApplyTextChange(string projectPath, string fullPath, SourceText text)
	{
		var (resolved, _) = ResolveWithKind(projectPath);
		
		if(!manager.TryApplyTextChange(resolved, fullPath, text))
			await manager.WriteAndInvalidate(resolved, fullPath,
				() => FileWriter.WriteAllTextAsync(fullPath, text.ToString()));
		
		paginationCache.InvalidateAll();
	}
	
	/// <summary>
	///     Writes to a file with per-file FSW suppression and syncs the workspace in-memory state.
	///     Use for .cs files where callers manage the write (e.g. atomic tmp→rename).
	/// </summary>
	public Task WriteAndInvalidate(string projectPath, string fullPath, Func<Task> write)
	=> WriteAndInvalidate(projectPath, fullPath, null, write);
	
	public Task WriteAndInvalidate(string projectPath, string fullPath, string? movedFromPath, Func<Task> write)
	{
		var (resolved, _) = ResolveWithKind(projectPath);
		
		paginationCache.InvalidateAll();
		
		return manager.WriteAndInvalidate(resolved, fullPath, movedFromPath, write);
	}
}
