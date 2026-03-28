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
	protected readonly PaginationCache    paginationCache;

	// Tracks the in-flight scope so TryGetCompilation/TryGetProject can set workspace mode without
	// requiring callers to thread the scope through as a parameter.
	[ThreadStatic]
	private static ToolScope? activeScope;

	protected RoslynMcpTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache)
	{
		this.workspace       = workspace;
		this.logger          = logger;
		this.paginationCache = paginationCache;
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
	///     Returns a safe page slice from an array. Clamps <paramref name="skip"/> to
	///     <c>[0, items.Length]</c> so callers never hit <see cref="ArgumentOutOfRangeException"/>.
	/// </summary>
	protected static T[] Paginate<T>(T[] items, ref int skip, int take)
	{
		skip = Math.Clamp(skip, 0, items.Length);

		return items.AsSpan(skip, Math.Min(take, items.Length - skip)).ToArray();
	}

	/// <summary>ReadOnlyMemory overload — zero-copy slice from cache.</summary>
	protected static T[] Paginate<T>(ReadOnlyMemory<T> items, ref int skip, int take)
	{
		skip = Math.Clamp(skip, 0, items.Length);

		return items.Slice(skip, Math.Min(take, items.Length - skip)).ToArray();
	}

	/// <summary>
	///     Cache-hit path: if the token is valid, returns a standardized paged response directly.
	///     Returns null on miss — caller should compute results and call <see cref="PaginateAndStore{T}"/>.
	///     Subsequent-page responses use a common shape (items/total/page_token/has_more);
	///     tool-specific metadata is only included in the first-page response.
	/// </summary>
	protected object? TryServeCachedPage<T>(ToolScope scope, string? pageToken, ref int skip, int take)
	{
		if(pageToken is null || !paginationCache.TryGet<T>(pageToken, out var cached))
			return null;

		var page = Paginate(cached, ref skip, take);

		return scope.Outcome($"{page.Length}/{cached.Length}", new {
			items      = page,
			total      = cached.Length,
			skip,
			take,
			page_token = pageToken,
			has_more   = skip + page.Length < cached.Length
		});
	}

	/// <summary>
	///     Cache-miss path: stores <paramref name="allResults"/> in the pagination cache and
	///     returns a paginated slice with token for subsequent pages.
	/// </summary>
	protected PaginatedResult<T> PaginateAndStore<T>(T[] allResults, ref int skip, int take)
	{
		var token   = paginationCache.Store(allResults);
		var page    = Paginate(allResults, ref skip, take);
		var hasMore = skip + page.Length < allResults.Length;

		return new PaginatedResult<T>(page, allResults.Length, skip, take, token, hasMore);
	}

	/// <summary>
	///     Resolves a relative file path to a full path. Tries <paramref name="rootPath"/> first;
	///     if the file isn't found there, walks subdirectories looking for a suffix match.
	///     Returns null if the file can't be found.
	/// </summary>
	protected static string? ResolveFilePath(string filePath, string rootPath)
	{
		try {

			if(Path.IsPathRooted(filePath))
				return File.Exists(filePath) ? filePath : null;

			var normalized = NormalizePath(filePath);
			var direct     = Path.GetFullPath(Path.Combine(rootPath, normalized));

			if(File.Exists(direct))
				return direct;

			// Fallback: suffix match — handles agents passing project-relative paths
			// when rootPath is the solution directory.
			var suffix = Path.DirectorySeparatorChar + normalized;

			foreach(var candidate in Directory.EnumerateFiles(rootPath, Path.GetFileName(normalized), SearchOption.AllDirectories)) {

				if(candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(Path.GetFileName(candidate), Path.GetFileName(normalized), StringComparison.OrdinalIgnoreCase))
					return candidate;
			}
		}
		catch(Exception ex) when(ex is ArgumentException or IOException or UnauthorizedAccessException) { }

		return null;
	}

	/// <summary>
	///     Formats a symbol's modifiers (access, static, abstract, virtual, override, sealed)
	///     as a space-separated prefix string. Shared by FileOutlineTool, GetSymbolDefinitionTool,
	///     and TypeMembersTool.
	/// </summary>
	protected static string FormatModifiers(ISymbol symbol)
	{
		var parts = new List<string>();

		if(symbol.IsStatic)
			parts.Add("static");
		if(symbol.IsAbstract && symbol.ContainingType?.TypeKind != TypeKind.Interface)
			parts.Add("abstract");
		if(symbol.IsVirtual)
			parts.Add("virtual");
		if(symbol.IsOverride)
			parts.Add("override");
		if(symbol.IsSealed && symbol.Kind != SymbolKind.NamedType)
			parts.Add("sealed");

		var access = symbol.DeclaredAccessibility switch {
			Accessibility.Public               => "public",
			Accessibility.Private              => "private",
			Accessibility.Protected            => "protected",
			Accessibility.Internal             => "internal",
			Accessibility.ProtectedOrInternal  => "protected internal",
			Accessibility.ProtectedAndInternal => "private protected",
			_                                  => null
		};

		if(access is not null)
			parts.Insert(0, access);

		return parts.Count > 0 ? string.Join(" ", parts) + " " : string.Empty;
	}

	/// <summary>
	///     Normalizes a file path for cross-platform compatibility by converting forward slashes
	///     to the platform directory separator. Agents commonly supply Unix-style paths; this
	///     ensures suffix matching against Roslyn's <see cref="SyntaxTree.FilePath"/> works on Windows.
	/// </summary>
	protected static string NormalizePath(string filePath)
		=> filePath.Replace('/', Path.DirectorySeparatorChar);

	/// <summary>
	///     Resolves a symbol by name from a compilation. When <paramref name="containingType"/> is
	///     provided, searches that type's members. Otherwise tries type-first lookup (metadata name →
	///     SimpleNameFinder) before falling back to AnySymbolFinder for members.
	/// </summary>
	protected static ISymbol? FindSymbol(Compilation compilation, string name, string? containingType)
	{
		if(containingType is not null) {

			var type = compilation.GetTypeByMetadataName(containingType)
				?? compilation.GlobalNamespace.Accept(new SimpleNameFinder<INamedTypeSymbol>(containingType));

			return type?.GetMembers(name).FirstOrDefault();
		}

		var typeSymbol = compilation.GetTypeByMetadataName(name)
			?? compilation.GlobalNamespace.Accept(new SimpleNameFinder<INamedTypeSymbol>(name));

		if(typeSymbol is not null)
			return typeSymbol;

		return compilation.GlobalNamespace.Accept(new AnySymbolFinder(name));
	}

	/// <summary>
	///     Formats a symbol's display name: fully qualified for types, ContainingType.Name for members.
	/// </summary>
	protected static string FormatSymbolName(ISymbol symbol)
	{
		if(symbol is INamedTypeSymbol)
			return symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

		var ct = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

		return ct is not null ? $"{ct}.{symbol.Name}" : symbol.Name;
	}
}

internal sealed record PaginatedResult<T>(
	T[]    Items,
	int    Total,
	int    Skip,
	int    Take,
	string PageToken,
	bool   HasMore
);
