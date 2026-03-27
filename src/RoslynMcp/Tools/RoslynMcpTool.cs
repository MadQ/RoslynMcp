using System.ComponentModel;
using Microsoft.CodeAnalysis;

namespace RoslynMcp.Tools;

/// <summary>
///     Base class for RoslynMcp tools. Provides common functionality for project path resolution,
///     workspace access, structured error handling, and file logging.
/// </summary>
internal abstract partial class RoslynMcpTool
{
	protected readonly WorkspaceResolver workspace;
	protected readonly FileLogger         logger;
	
	// Tracks the in-flight scope so TryGetCompilation/TryGetProject can set workspace mode without
	// requiring callers to thread the scope through as a parameter.
	[ThreadStatic]
	private static ToolScope? activeScope;
	
	protected RoslynMcpTool(WorkspaceResolver workspace, FileLogger logger)
	{
		this.workspace = workspace;
		this.logger    = logger;
	}
	
	/// <summary>
	///     Starts a timed tool scope. Dispose the returned handle to log the outcome.
	///     Usage: <c>using var scope = BeginTool("roslyn_foo", subject);</c>
	///     Call <c>scope.Failed("reason")</c> on error paths, <c>scope.Outcome("detail")</c> on notable
	///     success, or <c>scope.Record("note")</c> for neutral mid-scope annotations.
	/// </summary>
	protected ToolScope BeginTool(string name, string? subject = null)
	{
		var scope  = new ToolScope(name, subject, logger, () => activeScope = null);
		activeScope = scope;
		
		return scope;
	}
	
	// Static cache for path inference: maps relative/bare paths to resolved full paths.
	// Enabled by default; disable via ROSLYNMCP_DISABLE_PATH_CACHE=true env var.
	static readonly Dictionary<string, string> pathCache = new(StringComparer.OrdinalIgnoreCase);
	static readonly object pathCacheLock = new();
	static readonly bool pathCacheEnabled = !string.Equals(
		Environment.GetEnvironmentVariable("ROSLYNMCP_DISABLE_PATH_CACHE"),
		"true",
		StringComparison.OrdinalIgnoreCase
	);
	
	/// <summary>
	///     Tries to resolve a project path and get the compilation. Returns structured errors on failure.
	/// </summary>
	/// <param name="projectPath">Project path (directory, .csproj, or source file). REQUIRED.</param>
	/// <param name="compilation">The resolved compilation if successful.</param>
	/// <param name="error">Structured error response if resolution failed.</param>
	/// <returns>True if compilation was successfully resolved; false otherwise.</returns>
	protected bool TryGetCompilation(
		string projectPath,
		[System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Compilation? compilation,
		[System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out object? error)
	{
		error = null;
		compilation = null;

		if(pathCacheEnabled && !Path.IsPathRooted(projectPath)) {
			
			lock(pathCacheLock) {
				
				if(pathCache.TryGetValue(projectPath, out var cached)) {
					
					logger.LogInfo("TryGetCompilation", $"Path cache hit: '{projectPath}' → '{cached}'");
					projectPath = cached;
				}
			}
		}
		
		try {
			
			// Defensive check: if path doesn't exist, return helpful error.
			if(!Path.IsPathRooted(projectPath) || (!File.Exists(projectPath) && !Directory.Exists(projectPath))) {
				
				error = new {
					
					error = "invalid_project_path",
					message = $"Path '{projectPath}' does not exist or is not rooted.",
					provided_path = projectPath,
					hint = "Use an absolute path (e.g., 'J:\\Projects\\MyProject') or ensure the relative path exists. If you have a valid full path, provide it and the server will cache the association."
				};
				logger.LogError("TryGetCompilation", $"Path does not exist: '{projectPath}'");
				
				return false;
			}
			
			var originalPath = projectPath;
			compilation = workspace.GetCompilation(projectPath);
			activeScope?.SetWorkspaceMode(workspace.IsAdhoc(projectPath) is false);
			
			// Annotate when non-obvious resolution occurred.
			var kind = workspace.GetResolutionKind(projectPath);
			activeScope?.Record(kind switch {
				
				ResolutionKind.Directory          => "dir→.csproj",
				ResolutionKind.FileWalkUp         => "file→.csproj",
				ResolutionKind.InferredFromCache  => $"inferred from '{Path.GetFileName(projectPath)}'",
				ResolutionKind.Adhoc              => "adhoc (no .csproj)",
				_                                 => null!
			});

			if(pathCacheEnabled && !Path.IsPathRooted(originalPath)) {
				
				var resolvedFull = workspace.GetWorkspaceInfo(projectPath).RootPath;
				
				lock(pathCacheLock) {
					
					if(!pathCache.ContainsKey(originalPath)) {
						
						pathCache[originalPath] = resolvedFull;
						logger.LogInfo("TryGetCompilation", $"Cached path: '{originalPath}' → '{resolvedFull}'");
					}
				}
			}
			
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
		catch(AmbiguousFileException ex) {
			
			error = AmbiguousFileError(ex);
			logger.LogError("TryGetCompilation", ex.Message);
			
			return false;
		}
		catch(ArgumentException ex) {
			
			error = new {
				
				error   = "missing_project_path",
				message = ex.Message,
				hint    = "projectPath is required. Pass the .csproj file path or a directory containing one."
			};
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
		string projectPath,
		[System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Project? project,
		[System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out object? error)
	{
		error = null;
		project = null;
		
		try {
			
			project = workspace.GetProject(projectPath);
			activeScope?.SetWorkspaceMode(workspace.IsAdhoc(projectPath) is false);
			
			// Annotate when non-obvious resolution occurred.
			var kind = workspace.GetResolutionKind(projectPath);
			activeScope?.Record(kind switch {
				
				ResolutionKind.Directory          => "dir→.csproj",
				ResolutionKind.FileWalkUp         => "file→.csproj",
				ResolutionKind.InferredFromCache  => $"inferred from '{Path.GetFileName(projectPath)}'",
				ResolutionKind.Adhoc              => "adhoc (no .csproj)",
				_                                 => null!
			});
			
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
		catch(AmbiguousFileException ex) {
			
			error = AmbiguousFileError(ex);
			logger.LogError("TryGetProject", ex.Message);
			
			return false;
		}
		catch(ArgumentException ex) {
			
			error = new {
				
				error   = "missing_project_path",
				message = ex.Message,
				hint    = "projectPath is required. Pass the .csproj file path or a directory containing one."
			};
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
	{
		string[] foundProjects = [.. ex.ProjectFiles.Select(Path.GetFileName).Where(f => f is not null)!];

		return new {

			error          = "multiple_projects_found",
			message        = ex.Message,
			directory      = ex.Directory,
			found_projects = foundProjects,
			hint           = "Specify the exact .csproj file path instead of the directory."
		};
	}
	
	private static object AmbiguousFileError(AmbiguousFileException ex)
		=> new {
			
			error          = "ambiguous_file",
			message        = ex.Message,
			file_name      = ex.FileName,
			found_in       = ex.CsprojPaths,
			hint           = "This file exists in multiple loaded projects. Specify which .csproj to use as projectPath."
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
	///     Returns a caution string when the resolved workspace is AdhocWorkspace (no .csproj).
	///     Include in success responses so agents know to re-invoke with a .csproj path for full functionality.
	///     Returns null for MSBuildWorkspace — callers can use null-conditional to omit cleanly.
	/// </summary>
	protected string? AdhocCaution(string projectPath)
		=> workspace.IsAdhoc(projectPath)
			? "AdhocWorkspace in use — pass the .csproj path directly for full MSBuild support (complete type info, references, diagnostics)."
			: null;
	
	/// <summary>
	///     Common parameter description for projectPath across all tools.
	/// </summary>
	protected const string ProjectPathDescription =
		"Path to project directory, .csproj file, or source file. REQUIRED - must be explicitly specified. " +
		"Supports smart resolution: directory → searches for .csproj; file → walks up to find .csproj. " +
		"NOTE: a directory or file path that cannot locate a .csproj falls back to AdhocWorkspace (no MSBuild, " +
		"reduced functionality). Prefer passing the .csproj path directly for full MSBuild support.";

	/// <summary>
	///     Normalizes a file path for cross-platform compatibility by converting forward slashes
	///     to the platform directory separator. Agents commonly supply Unix-style paths; this
	///     ensures suffix matching against Roslyn's <see cref="SyntaxTree.FilePath"/> works on Windows.
	/// </summary>
	protected static string NormalizePath(string filePath)
		=> filePath.Replace('/', Path.DirectorySeparatorChar);
}
