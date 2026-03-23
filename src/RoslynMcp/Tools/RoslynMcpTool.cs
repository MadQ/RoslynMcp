using System.ComponentModel;
using Microsoft.CodeAnalysis;

namespace RoslynMcp.Tools;

/// <summary>
///     Base class for RoslynMcp tools. Provides common functionality for project path resolution,
///     workspace access, and structured error handling.
/// </summary>
internal abstract class RoslynMcpTool
{
    protected readonly WorkspaceResolver workspace;

    protected RoslynMcpTool(WorkspaceResolver workspace)
    {
        this.workspace = workspace;
    }

    /// <summary>
    ///     Tries to resolve a project path and get the compilation. Returns structured errors on failure.
    /// </summary>
    /// <param name="projectPath">Optional project path (directory, .csproj, or source file).</param>
    /// <param name="compilation">The resolved compilation if successful.</param>
    /// <param name="error">Structured error response if resolution failed.</param>
    /// <returns>True if compilation was successfully resolved; false otherwise.</returns>
    protected bool TryGetCompilation(
        string? projectPath,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Compilation? compilation,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out object? error)
    {
        error = null;
        compilation = null;

        try {

            compilation = workspace.GetCompilation(projectPath);

            return true;
        }
        catch(ProjectNotFoundException ex) {

            error = ProjectNotFoundError(ex);

            return false;
        }
        catch(MultipleProjectsFoundException ex) {

            error = MultipleProjectsError(ex);

            return false;
        }
        catch(InvalidProjectPathException ex) {

            error = InvalidPathError(ex);

            return false;
        }
        catch(Exception ex) {

            error = UnexpectedError(ex);

            return false;
        }
    }

    /// <summary>
    ///     Tries to resolve a project path and get the project instance. Returns structured errors on failure.
    ///     Use this for tools that need Project-level metadata (ProjectInfoTool, etc.).
    /// </summary>
    protected bool TryGetProject(
        string? projectPath,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Project? project,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out object? error)
    {
        error = null;
        project = null;

        try {

            project = workspace.GetProject(projectPath);

            return true;
        }
        catch(ProjectNotFoundException ex) {

            error = ProjectNotFoundError(ex);

            return false;
        }
        catch(MultipleProjectsFoundException ex) {

            error = MultipleProjectsError(ex);

            return false;
        }
        catch(InvalidProjectPathException ex) {

            error = InvalidPathError(ex);

            return false;
        }
        catch(Exception ex) {

            error = UnexpectedError(ex);

            return false;
        }
    }

    private static object ProjectNotFoundError(ProjectNotFoundException ex)
        => new {
            error       = "project_not_found",
            message     = ex.Message,
            search_path = ex.SearchPath,
            hint        = "Provide a valid projectPath pointing to a directory containing a .csproj file, or the .csproj file itself."
        };

    private static object MultipleProjectsError(MultipleProjectsFoundException ex)
        => new {
            error          = "multiple_projects_found",
            message        = ex.Message,
            directory      = ex.Directory,
            found_projects = ex.ProjectFiles.Select(Path.GetFileName).ToArray(),
            hint           = "Specify the exact .csproj file path instead of the directory."
        };

    private static object InvalidPathError(InvalidProjectPathException ex)
        => new {
            error         = "invalid_project_path",
            message       = ex.Message,
            provided_path = ex.Path,
            hint          = "Ensure the path exists and contains a valid .csproj file."
        };

    private static object UnexpectedError(Exception ex)
        => new {
            error   = "unexpected_error",
            message = ex.Message,
            type    = ex.GetType().Name
        };

    /// <summary>
    ///     Common parameter description for projectPath across all tools.
    /// </summary>
    protected const string ProjectPathDescription =
        "Optional path to project directory, .csproj file, or source file. " +
        "If omitted, uses current working directory. " +
        "Supports smart resolution: directory → searches for .csproj; file → walks up to find .csproj.";
}
