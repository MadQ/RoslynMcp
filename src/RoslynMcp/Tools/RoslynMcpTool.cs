using System.ComponentModel;
using System.Text.RegularExpressions;
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
		[System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out ToolErrorResult? error)
	{
		error		= null;
		compilation	= null;
		
		if(pathCacheEnabled && !Path.IsPathRooted(projectPath))
			lock(pathCacheLock)
				if(pathCache.TryGetValue(projectPath, out var cached)) {
					logger.LogInfo("TryGetCompilation", $"Path cache hit: '{projectPath}' → '{cached}'");
					projectPath = cached;
				}
		
		try {
			// Defensive check: if path doesn't exist, return helpful error.
			if(!Path.IsPathRooted(projectPath) || (!File.Exists(projectPath) && !Directory.Exists(projectPath))) {
				
				error = new PathErrorResult(
					"invalid_project_path",
					$"Path '{projectPath}' does not exist or is not rooted.",
					ProvidedPath: projectPath,
					Hint: "Use an absolute path (e.g., 'J:\\Projects\\MyProject') or ensure the relative path exists. If you have a valid full path, provide it and the server will cache the association."
				);
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
				
				lock(pathCacheLock)
					if(!pathCache.ContainsKey(originalPath)) {
						
						pathCache[originalPath] = resolvedFull;
						logger.LogInfo("TryGetCompilation", $"Cached path: '{originalPath}' → '{resolvedFull}'");
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
			
			error = new PathErrorResult(
				"missing_project_path",
				ex.Message,
				Hint: "projectPath is required. Pass the .csproj file path or a directory containing one."
			);
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
		[System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out ToolErrorResult? error)
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
			
			error = new PathErrorResult(
				"missing_project_path",
				ex.Message,
				Hint: "projectPath is required. Pass the .csproj file path or a directory containing one."
			);
			logger.LogError("TryGetProject", ex.Message);
			
			return false;
		}
		catch(Exception ex) {
			
			error = UnexpectedError(ex);
			logger.LogError("TryGetProject", $"{ex.GetType().Name}: {ex.Message}");
			
			return false;
		}
	}
	
	private static ToolErrorResult ProjectNotFoundError(ProjectNotFoundException ex)
		=> new PathErrorResult(
			"project_not_found",
			ex.Message,
			SearchPath: ex.SearchPath,
			Hint: "Provide a valid projectPath pointing to a directory containing a .csproj file, or the .csproj file itself."
		);
	
	private static ToolErrorResult MultipleProjectsError(MultipleProjectsFoundException ex)
	{
		string[] foundProjects = [.. ex.ProjectFiles.Select(Path.GetFileName).Where(f => f is not null)!];
		
		return new PathErrorResult(
			"multiple_projects_found",
			ex.Message,
			Directory: ex.Directory,
			FoundProjects: foundProjects,
			Hint: "Specify the exact .csproj file path instead of the directory."
		);
	}
	
	private static ToolErrorResult AmbiguousFileError(AmbiguousFileException ex)
		=> new PathErrorResult(
			"ambiguous_file",
			ex.Message,
			FileName: ex.FileName,
			FoundIn: ex.CsprojPaths,
			Hint: "This file exists in multiple loaded projects. Specify which .csproj to use as projectPath."
		);
	
	private static ToolErrorResult InvalidPathError(InvalidProjectPathException ex)
		=> new PathErrorResult(
			"invalid_project_path",
			ex.Message,
			ProvidedPath: ex.Path,
			Hint: "Ensure the path exists and contains a valid .csproj file."
		);
	
	private static ToolErrorResult UnexpectedError(Exception ex)
		=> new UnexpectedErrorResult(
			"unexpected_error",
			ex.Message,
			ex.GetType().Name
		);
	
	/// <summary>
	///     Returns a caution string when the resolved workspace is AdhocWorkspace (no .csproj).
	///     Include in success responses so agents know to re-invoke with a .csproj path for full functionality.
	///     Returns null for MSBuildWorkspace — callers can use null-conditional to omit cleanly.
	/// </summary>
	protected string? AdhocCaution(string projectPath)
		=> workspace.IsAdhoc(projectPath)
			? "AdhocWorkspace in use — pass the .csproj path directly for full MSBuild support (complete type info, references, diagnostics)."
			: null
	;
	
	/// <summary>
	///     Common parameter description for projectPath across all tools.
	/// </summary>
	protected const string ProjectPathDescription =
		"Path to project directory, .csproj file, or source file. REQUIRED - must be explicitly specified. " +
		"Supports smart resolution: directory → searches for .csproj; file → walks up to find .csproj. " +
		"NOTE: a directory or file path that cannot locate a .csproj falls back to AdhocWorkspace (no MSBuild, " +
		"reduced functionality). Prefer passing the .csproj path directly for full MSBuild support."
	;
	
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
	protected object? TryServeCachedPage<T>(ToolScope scope, string? pageToken, ref int skip, ref int take, int maxTake)
	{
		take = Math.Clamp(take, 1, maxTake);
		
		if(pageToken is null || !paginationCache.TryGet<T>(pageToken, out var cached))
			return null;
		
		scope.SetCacheTag(hit: true);
		
		var page = Paginate(cached, ref skip, take);
		
		return scope.Outcome($"{page.Length}/{cached.Length}", new CachedPageResult<T>(
			Items:      page,
			Total:      cached.Length,
			Skip:       skip,
			Take:       take,
			PageToken: pageToken!,
			HasMore:   skip + page.Length < cached.Length
		));
	}
	
	/// <summary>
	///     Cache-miss path: stores <paramref name="allResults"/> in the pagination cache and
	///     returns a paginated slice with token for subsequent pages.
	/// </summary>
	protected PaginatedResult<T> PaginateAndStore<T>(T[] allResults, ref int skip, int take)
	{
		activeScope?.SetCacheTag(hit: false);
		
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
				
				if(candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(candidate), Path.GetFileName(normalized), StringComparison.OrdinalIgnoreCase))
					return candidate;
			}
		}
		catch(Exception ex) when(ex is ArgumentException or IOException or UnauthorizedAccessException) { }
		
		return null;
	}
		
		/// <summary>
	///     Resolves where a file <em>would</em> be created — does not require the file to exist.
	///     Returns false with an error message if the path escapes the root or is otherwise invalid.
	/// </summary>
	protected static bool TryResolveTargetPath(
		string filePath,
		string rootPath,
		[System.Diagnostics.CodeAnalysis.NotNullWhen(true)]  out string? fullPath,
		[System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
	{
		fullPath = null;
		error    = null;
		
		try {
			
			if(Path.IsPathRooted(filePath)) {
				
				// Absolute path — verify it stays under root.
				if(!filePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) {
					
					error = $"Path '{filePath}' is outside the project root.";
					
					return false;
				}
				
				fullPath = filePath;
				
				return true;
			}
			
			var normalized = NormalizePath(filePath);
			var candidate  = Path.GetFullPath(Path.Combine(rootPath, normalized));
			
			// Under-root guard — prevent path traversal (e.g. ../../etc/passwd).
			if(!candidate.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) ||
				(candidate.Length > rootPath.Length && candidate[rootPath.Length] is not '\\' and not '/')) {
				
				error = $"Path '{filePath}' resolves outside the project root.";
				
				return false;
			}
			
			fullPath = candidate;
			
			return true;
		}
		catch(Exception ex) when(ex is ArgumentException or IOException) {
			
			error = $"Invalid path '{filePath}': {ex.Message}";
			
			return false;
		}
	}
	
	protected static string FormatModifiers(ISymbol symbol)
		=> SymbolFormatter.FormatModifiers(symbol);
	
	/// <summary>
	///     Finds a SyntaxTree in the compilation by relative file path suffix match.
	///     Handles path normalization (forward slashes → platform separator).
	/// </summary>
	protected static SyntaxTree? FindSyntaxTree(Compilation compilation, string filePath)
	{
		var normalized = NormalizePath(filePath);
		
		return compilation.SyntaxTrees
			.FirstOrDefault(t => t.FilePath.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
		;
	}
	
	/// <summary>
	///     Normalizes a file path for cross-platform compatibility by converting forward slashes
	///     to the platform directory separator. Agents commonly supply Unix-style paths; this
	///     ensures suffix matching against Roslyn's <see cref="SyntaxTree.FilePath"/> works on Windows.
	/// </summary>
	
	/// <summary>
	///     Builds a compiled regex for literal pattern matching, with CRLF-agnostic newline handling.
	///     Literal newlines in the pattern match both <c>\n</c> and <c>\r\n</c> in the file content,
	///     preventing silent match failures on Windows files.
	/// </summary>
	protected static Regex BuildLiteralRegex(string pattern, bool caseSensitive = true)
	{
		var escaped = Regex.Escape(pattern).Replace(@"\n", @"\r?\n");
		var options = RegexOptions.Compiled;
		
		if(!caseSensitive)
			options |= RegexOptions.IgnoreCase;
		
		return new Regex(escaped, options);
	}
	
	/// <summary>
	///     Detects the dominant line ending in a string and normalizes the replacement text to match.
	///     Returns the replacement unchanged if the file uses LF or the replacement already matches.
	/// </summary>
	protected static string NormalizeLineEndings(string replacement, string fileContent)
	{
		var hasCrlf = fileContent.Contains("\r\n");
		
		if(hasCrlf && !replacement.Contains("\r\n"))
			return replacement.Replace("\n", "\r\n");
		
		return replacement;
	}
	
	protected static string NormalizePath(string filePath)
		=> filePath.Replace('/', Path.DirectorySeparatorChar);
	
	/// <summary>Returns a predicate for <see cref="Diagnostic"/> based on the severity string from a tool parameter.</summary>
	protected static Func<Diagnostic, bool> GetSeverityFilter(string? severity) =>
		severity?.ToLowerInvariant() switch {
			
			"errors"   => static d => d.Severity == DiagnosticSeverity.Error,
			"warnings" => static d => d.Severity == DiagnosticSeverity.Warning,
			
			// Info and Hidden are deferred — see #111 and #110.
			_          => static d => d.Severity >= DiagnosticSeverity.Warning
		};
	
	/// <summary>
	///     Returns a project-relative path. Falls back to the original absolute path when
	///     relativization fails (e.g. paths on different drives on Windows).
	/// </summary>
	protected static string? TryMakeRelative(string? path, string rootPath)
	{
		if(string.IsNullOrEmpty(path))
			return null;
		
		try {
			return Path.GetRelativePath(rootPath, path);
		}
		catch(ArgumentException) {
			
			return path;
		}
	}
	
	// Filters out diagnostics from files outside the project root — prevents external files
	// open in the IDE (e.g. HTML, CSS from unrelated directories) from polluting results.
	// Diagnostics with no file path (global/assembly-level) always pass through.
	//
	// Known gaps (tracked in issue #114):
	//   - Path prefix collision: "J:\Foo" incorrectly matches "J:\FooBar\file.cs".
	//   - Non-C# files physically inside the root (e.g. wwwroot\index.html) pass through.
	//   A future improvement can combine LocationKind filtering with a path separator guard.
	protected static bool IsUnderRoot(Diagnostic diagnostic, string rootPath)
	{
		var filePath = diagnostic.Location.SourceTree?.FilePath;
		
		if(string.IsNullOrEmpty(filePath))
			return true;
		
		return filePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase);
	}
	
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
