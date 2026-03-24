using System.ComponentModel;
using System.Diagnostics;
using Microsoft.CodeAnalysis;

namespace RoslynMcp.Tools;

/// <summary>
///     Base class for RoslynMcp tools. Provides common functionality for project path resolution,
///     workspace access, structured error handling, and file logging.
/// </summary>
internal abstract class RoslynMcpTool
{
    protected readonly WorkspaceResolver workspace;
    readonly           FileLogger         logger;

    protected RoslynMcpTool(WorkspaceResolver workspace, FileLogger logger)
    {
        this.workspace = workspace;
        this.logger    = logger;
    }

    /// <summary>
    ///     Starts a timed tool scope. Dispose the returned handle to log the outcome.
    ///     Usage: <c>using var _ = BeginTool("roslyn_foo");</c>
    /// </summary>
    protected ToolScope BeginTool(string name) => new(name, logger);

    /// <summary>
    ///     Disposable scope that logs a tool invocation with elapsed time on dispose.
    ///     Mark <see cref="Failed"/> before dispose to log an ERROR outcome.
    /// </summary>
    protected sealed class ToolScope : IDisposable
    {
        readonly string      name;
        readonly FileLogger  log;
        readonly Stopwatch   sw = Stopwatch.StartNew();

        public bool   Failed  { get; set; }
        public string? Detail { get; set; }

        internal ToolScope(string name, FileLogger log)
        {
            this.name = name;
            this.log  = log;
        }

        public void Dispose() => log.LogTool(name, sw.ElapsedMilliseconds, !Failed, Detail);
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
            logger.LogError("TryGetCompilation", ex.Message);

            return false;
        }
        catch(MultipleProjectsFoundException ex) {

            error = MultipleProjectsError(ex);
            logger.LogError("TryGetCompilation", ex.Message);

            return false;
        }
        catch(InvalidProjectPathException ex) {

            error = InvalidPathError(ex);
            logger.LogError("TryGetCompilation", ex.Message);

            return false;
        }
        catch(Exception ex) {

            error = UnexpectedError(ex);
            logger.LogError("TryGetCompilation", $"{ex.GetType().Name}: {ex.Message}");

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
            logger.LogError("TryGetProject", ex.Message);

            return false;
        }
        catch(MultipleProjectsFoundException ex) {

            error = MultipleProjectsError(ex);
            logger.LogError("TryGetProject", ex.Message);

            return false;
        }
        catch(InvalidProjectPathException ex) {

            error = InvalidPathError(ex);
            logger.LogError("TryGetProject", ex.Message);

            return false;
        }
        catch(Exception ex) {

            error = UnexpectedError(ex);
            logger.LogError("TryGetProject", $"{ex.GetType().Name}: {ex.Message}");

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
