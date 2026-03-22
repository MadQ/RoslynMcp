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
    ///     Resolves a project path and gets the compilation. Handles structured errors.
    /// </summary>
    protected object ExecuteWithProject(string? projectPath, Func<Compilation, object> execute)
    {
        try {

            var compilation = workspace.GetCompilation(projectPath);

            return execute(compilation);
        }
        catch(ProjectNotFoundException ex) {

            return new {
                error       = "project_not_found",
                message     = ex.Message,
                search_path = ex.SearchPath,
                hint        = "Provide a valid projectPath pointing to a directory containing a .csproj file, or the .csproj file itself."
            };
        }
        catch(MultipleProjectsFoundException ex) {

            return new {
                error          = "multiple_projects_found",
                message        = ex.Message,
                directory      = ex.Directory,
                found_projects = ex.ProjectFiles.Select(Path.GetFileName).ToArray(),
                hint           = "Specify the exact .csproj file path instead of the directory."
            };
        }
        catch(InvalidProjectPathException ex) {

            return new {
                error         = "invalid_project_path",
                message       = ex.Message,
                provided_path = ex.Path,
                hint          = "Ensure the path exists and contains a valid .csproj file."
            };
        }
        catch(Exception ex) {

            return new {
                error   = "unexpected_error",
                message = ex.Message,
                type    = ex.GetType().Name
            };
        }
    }

    /// <summary>
    ///     Common parameter description for projectPath across all tools.
    /// </summary>
    protected const string ProjectPathDescription =
        "Optional path to project directory, .csproj file, or source file. " +
        "If omitted, uses current working directory. " +
        "Supports smart resolution: directory → searches for .csproj; file → walks up to find .csproj.";
}
