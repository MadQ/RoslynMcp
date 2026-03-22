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
    ///     If projectPath is null, uses current working directory.
    /// </summary>
    public Compilation GetCompilation(string? projectPath)
    {
        var resolved = manager.ResolveProjectPath(projectPath);

        return manager.GetCompilation(resolved);
    }

    /// <summary>
    ///     Resolves the project path and returns the solution.
    ///     If projectPath is null, uses current working directory.
    /// </summary>
    public Solution GetSolution(string? projectPath)
    {
        var resolved = manager.ResolveProjectPath(projectPath);

        return manager.GetSolution(resolved);
    }

    /// <summary>
    ///     Gets the root path for a resolved project.
    ///     Used for computing relative paths in tool responses.
    /// </summary>
    public string GetRootPath(string? projectPath)
    {
        var resolved = manager.ResolveProjectPath(projectPath);

        // Root path is the directory containing the .csproj
        return Path.GetDirectoryName(resolved)!;
    }
}
