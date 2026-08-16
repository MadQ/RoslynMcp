using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

/// <summary>
///     Base class for RoslynMcp tools. Provides common functionality for project path resolution,
///     workspace access, structured error handling, and file logging.
/// </summary>
internal abstract partial class RoslynMcpTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache)
{
	protected readonly WorkspaceResolver	workspace		= workspace;
	protected readonly FileLogger			logger			= logger;
	protected readonly PaginationCache		paginationCache	= paginationCache;
	
	// Tracks the in-flight scope so TryGetCompilation/TryGetProject can set workspace mode without
	// requiring callers to thread the scope through as a parameter.
	// AsyncLocal flows across await continuations, unlike [ThreadStatic] which is bound to a
	// single thread and would be null if TryGetCompilation ran after an await on a different thread.
	private static readonly AsyncLocal<ToolScope?> activeScope = new()
	;
	
	// 0 = not shown, 1 = shown. Interlocked.CompareExchange ensures exactly one thread injects the note.
	internal static int sessionNoteShown
	;
	
	// Cached once per process — hooks don't change while the server is running.
	static bool? hooksInstalled
	;
	
	internal static bool HooksInstalled()
	{
		if(hooksInstalled.HasValue)
			
			return hooksInstalled.Value;
		
		// Check global Claude Code settings for any reference to roslynmcp.
		var claudeSettings = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			".claude.json"
		);
		
		if(File.Exists(claudeSettings)) {
			
			try {
				
				using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(claudeSettings));
				
				if(ContainsRoslynMcpValue(doc.RootElement))
					
					return (hooksInstalled = true).Value;
			}
			catch { }
		}
		
		// Walk up from CWD looking for a Copilot CLI hook file written by setup-project.
		var dir = Environment.CurrentDirectory
		;
		
		while(true) {
			
			if(File.Exists(Path.Combine(dir, ".github", "roslynmcp.json")))
				
				return (hooksInstalled = true).Value;
			
			var parent = Directory.GetParent(dir);
			
			if(parent is null || parent.FullName == dir)
				break;
			
			dir = parent.FullName;
		}
		
		return (hooksInstalled = false).Value;
	}
	
	// Recursively check whether any JSON string value in the element contains "roslynmcp".
	// Scanning values (not keys) avoids false positives from structural JSON content
	// while still detecting both mcpServers entries and hooks entries.
	static bool ContainsRoslynMcpValue(System.Text.Json.JsonElement element)
	{
		return element.ValueKind switch {
			
			System.Text.Json.JsonValueKind.String  => element.GetString()?.Contains("roslynmcp", StringComparison.OrdinalIgnoreCase) == true,
			System.Text.Json.JsonValueKind.Object  => element.EnumerateObject().Any(p => ContainsRoslynMcpValue(p.Value)),
			System.Text.Json.JsonValueKind.Array   => element.EnumerateArray().Any(ContainsRoslynMcpValue),
			_                                      => false
		};
	}
	
	/// <summary>
	///     Starts a timed tool scope. Dispose the returned handle to log the outcome.
	///     Usage: <c>using var scope = BeginTool("roslyn_foo", subject);</c>
	///     Call <c>scope.Failed("reason")</c> on error paths, <c>scope.Outcome("detail")</c> on notable
	///     success, or <c>scope.Record("note")</c> for neutral mid-scope annotations.
	/// </summary>
	protected ToolScope BeginTool(string name, string? subject = null)
	{
		ServerHeartbeat.Touch();
		
		var scope  = new ToolScope(name, subject, logger, () => activeScope.Value = null, paginationCache);
		activeScope.Value = scope;
		
		return scope;
	}
	
	/// <summary>Convenience overload that starts a timed tool scope and immediately records the tool's input arguments.
	/// Args are serialized to compact JSON and stored on the scope for log emission on dispose.</summary>
	protected ToolScope BeginTool<T>(string name, string? subject, T args)
	{
		var scope = BeginTool(name, subject);
		
		scope.SetArgs(args);
		
		return scope;
	}
	
	// Static cache for project path inference: maps relative/bare paths to resolved full paths.
	// Enabled by default; disable via ROSLYNMCP_DISABLE_PATH_CACHE=true env var.
	// Entries evicted above 500 to prevent unbounded growth in long-running server sessions.
	const int pathCacheMaxSize = 500
	;
	
	static readonly Dictionary<string, string> pathCache = new(StringComparer.OrdinalIgnoreCase);

	// Static — must guard the static pathCache across ALL tool instances. A per-instance lock
	// would let different tool types write the shared Dictionary concurrently and corrupt it.
#if NET9_0_OR_GREATER
	static readonly Lock              pathCacheLock   = new();
#else
	static readonly object            pathCacheLock   = new();
#endif
	
	// Computed on every access so the read is guaranteed to happen after ServerArgs.Initialize().
	static bool PathCacheEnabled => !ServerArgs.Current.DisablePathCache
	;
	
	/// <summary>
	///     Load-health snapshot for the workspace serving <paramref name="projectPath"/>, or
	///     <see langword="null"/> if it cannot be determined. Never throws: health is diagnostic
	///     colour on a result the caller already has, so a failure here must not turn a successful
	///     tool call into an error.
	/// </summary>
	protected WorkspaceHealth? TryGetHealth(string projectPath)
	{
		try {
			return workspace.GetHealth(projectPath);
		}
		catch {
			return null;
		}
	}

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
		[System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out ToolResult? error)
	{
		error		= null;
		compilation	= null;
		
		if(PathCacheEnabled && !Path.IsPathRooted(projectPath))
			lock(pathCacheLock)
				if(pathCache.TryGetValue(projectPath, out var cached)) {
					
					logger.LogInfo("TryGetCompilation", $"Path cache hit: '{projectPath}' → '{cached}'");
					projectPath = cached;
				}
		
		try {
			
			var originalPath = projectPath;
			
			compilation = workspace.GetCompilation(projectPath);
			
			activeScope.Value?.SetWorkspaceMode(workspace.IsAdhoc(projectPath) is false);
			
			// Annotate when non-obvious resolution occurred.
			var kind = workspace.GetResolutionKind(projectPath)
			;
			
			activeScope.Value?.Record(kind switch {
				
				ResolutionKind.Directory          => "dir→.csproj",
				ResolutionKind.FileWalkUp         => "file→.csproj",
				ResolutionKind.InferredFromCache  => $"inferred from '{Path.GetFileName(projectPath)}'",
				ResolutionKind.Adhoc              => "adhoc (no .csproj)",
				_                                 => null!
			});
			
			if(PathCacheEnabled && !Path.IsPathRooted(originalPath)) {
				
				// Cache the absolute .csproj path, not the workspace root directory.
				// GetWorkspaceInfo().RootPath is the solution root (e.g. J:\Projects\RoslynMcp),
				// which when passed back on the next call would load an AdhocWorkspace with no
				// BCL references. Path.GetFullPath resolves relative to CWD — the same resolution
				// that WorkspaceManager.ResolveProjectPath performs.
				var resolvedFull = Path.GetFullPath(originalPath)
				;
				
				lock(pathCacheLock) {
					
					if(pathCache.Count >= pathCacheMaxSize)
						pathCache.Clear();
					
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
			
			error = new PathErrorResult(ex.Message)
			{
				Error = "missing_project_path",
				Hint  = "projectPath is required. Pass the .csproj file path or a directory containing one."
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
		[System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out ToolResult? error)
	{
		error = null;
		project = null;
		
		try {
			
			project = workspace.GetProject(projectPath);
			
			activeScope.Value?.SetWorkspaceMode(workspace.IsAdhoc(projectPath) is false);
			
			// Annotate when non-obvious resolution occurred.
			var kind = workspace.GetResolutionKind(projectPath)
			;
			
			activeScope.Value?.Record(kind switch {
				
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
			
			error = new PathErrorResult(ex.Message)
			{
				Error = "missing_project_path",
				Hint  = "projectPath is required. Pass the .csproj file path or a directory containing one."
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
	
	/// <summary>
	///     Resolves the workspace root path and security boundary for an editing tool, guarding against
	///     transient mid-reload failures and deterministic path-resolution errors. Editing tools call the
	///     resolver directly (unlike analysis tools, which funnel through <see cref="TryGetCompilation"/> /
	///     <see cref="TryGetProject"/>), so without this guard a call landing while the workspace reloads
	///     throws unhandled and surfaces as an opaque MCP invocation error. On failure returns a structured
	///     <see cref="ToolResult"/>: deterministic path problems map to their usual errors; any other
	///     exception becomes a retryable <see cref="TransientWorkspaceError"/>. The exception type and
	///     message are always logged.
	/// </summary>
	protected bool TryResolveEditContext(
		string projectPath,
		[System.Diagnostics.CodeAnalysis.NotNullWhen(true)]  out string?           rootPath,
		[System.Diagnostics.CodeAnalysis.NotNullWhen(true)]  out SecurityBoundary? boundary,
		[System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out ToolResult?       error)
	{
		rootPath = null;
		boundary = null;
		error    = null;
		
		try {
			
			var (rPath, isMSBuild, _) = workspace.GetWorkspaceInfo(projectPath);
			rootPath = rPath;
			boundary = workspace.GetSecurityBoundary(projectPath);
			
			activeScope.Value?.SetWorkspaceMode(isMSBuild);
			
			return true;
		}
		catch(Exception ex) {
			
			error = MapEditWorkspaceFault("TryResolveEditContext", ex);
			
			return false;
		}
	}
	
	/// <summary>
	///     Reads the current <see cref="Solution"/> for an editing tool, guarding the same transient
	///     mid-reload and path-resolution failures as <see cref="TryResolveEditContext"/>.
	/// </summary>
	protected bool TryGetEditSolution(
		string projectPath,
		[System.Diagnostics.CodeAnalysis.NotNullWhen(true)]  out Solution?   solution,
		[System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out ToolResult? error)
	{
		solution = null;
		error    = null;
		
		try {
			
			solution = workspace.GetSolution(projectPath);
			
			return true;
		}
		catch(Exception ex) {
			
			error = MapEditWorkspaceFault("TryGetEditSolution", ex);
			
			return false;
		}
	}
	
	/// <summary>
	///     Applies a changed <see cref="Solution"/> to the workspace, guarding the same transient
	///     mid-reload and path-resolution failures as <see cref="TryResolveEditContext"/>. On success the
	///     workspace has accepted (or scheduled a reload for) the change; on failure <paramref name="error"/>
	///     carries a structured, retryable result.
	/// </summary>
	protected bool TryApplyEdit(
		string projectPath,
		Solution newSolution,
		[System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out ToolResult? error)
	{
		error = null;
		
		try {
			
			if(!workspace.ApplyChanges(projectPath, newSolution)) {
				
				logger.LogError("TryApplyEdit", "ApplyChanges returned false — workspace rejected the solution change.");
				
				error = new TransientWorkspaceError("ApplyChanges returned false")
				{
					Error = "Workspace rejected the solution change — likely reloading after a prior edit.",
					Hint  = "Transient: retry the call in a moment. If it persists, the workspace may need roslyn_respawn."
				};
				
				return false;
			}
			
			return true;
		}
		catch(Exception ex) {
			
			error = MapEditWorkspaceFault("TryApplyEdit", ex);
			
			return false;
		}
	}
	
	/// <summary>
	///     Maps an exception thrown by a <see cref="WorkspaceResolver"/> call during an editing operation
	///     into a structured <see cref="ToolResult"/>, logging the exception type and message. Deterministic
	///     path-resolution failures map to their specific errors (mirroring <see cref="TryGetProject"/>);
	///     any other exception is treated as a transient fault — typically a workspace mid-reload — and
	///     returned as a retryable <see cref="TransientWorkspaceError"/>.
	/// </summary>
	private ToolResult MapEditWorkspaceFault(string op, Exception ex)
	{
		switch(ex) {
			
			case ProjectNotFoundException e:
				logger.LogError(op, e.Message);
				
				return ProjectNotFoundError(e);
			
			case MultipleProjectsFoundException e:
				logger.LogError(op, e.Message);
				
				return MultipleProjectsError(e);
			
			case InvalidProjectPathException e:
				logger.LogError(op, e.Message);
				
				return InvalidPathError(e);
			
			case AmbiguousFileException e:
				logger.LogError(op, e.Message);
				
				return AmbiguousFileError(e);
			
			case ArgumentException e:
				logger.LogError(op, e.Message);
				
				return new PathErrorResult(e.Message)
				{
					Error = "missing_project_path",
					Hint  = "projectPath is required. Pass the .csproj file path or a directory containing one."
				};
			
			default:
				// Any other exception is almost always the workspace resolving a project mid-reload: a prior
				// edit triggered an MSBuild reload and this call landed before it settled. Surface it as a
				// clearly retryable error instead of letting it propagate unhandled — an unhandled throw here
				// is what the MCP transport reports as an opaque "An error occurred invoking …".
				logger.LogError(op, $"{ex.GetType().Name}: {ex.Message}");
				
				return new TransientWorkspaceError($"{ex.GetType().Name}: {ex.Message}")
				{
					Error = $"Workspace temporarily unavailable ({ex.GetType().Name}) — likely reloading after a prior edit.",
					Hint  = "Transient: retry the call in a moment. If it persists, the workspace may need roslyn_respawn."
				};
		}
	}
	

	private static PathErrorResult ProjectNotFoundError(ProjectNotFoundException ex)
		=> new(ex.Message, SearchPath: ex.SearchPath)
		{
			Error = "project_not_found",
			Hint  = "Provide a valid projectPath pointing to a directory containing a .csproj file, or the .csproj file itself."
		};
	
	private static PathErrorResult MultipleProjectsError(MultipleProjectsFoundException ex)
	{
		string[] foundProjects = [.. ex.ProjectFiles.Select(Path.GetFileName).Where(f => f is not null)!];
		
		return new PathErrorResult(ex.Message, Directory: ex.Directory, FoundProjects: foundProjects)
		{
			Error = "multiple_projects_found",
			Hint  = "Specify the exact .csproj file path instead of the directory."
		};
	}
	
	private static PathErrorResult AmbiguousFileError(AmbiguousFileException ex)
		=> new(ex.Message, FileName: ex.FileName, FoundIn: ex.CsprojPaths)
		{
			Error = "ambiguous_file",
			Hint  = "This file exists in multiple loaded projects. Specify which .csproj to use as projectPath."
		};
	
	private static PathErrorResult InvalidPathError(InvalidProjectPathException ex)
		=> new(ex.Message, ProvidedPath: ex.Path)
		{
			Error = "invalid_project_path",
			Hint  = "Ensure the path exists and contains a valid .csproj file."
		};
	
	private static UnexpectedErrorResult UnexpectedError(Exception ex)
		=> new(ex.Message, ex.GetType().Name)
		{
			Error = "unexpected_error"
		};
	
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
		"Path to project directory, .csproj file, or source file — absolute or relative (relative paths are resolved against the server's working directory). " +
		"REQUIRED - must be explicitly specified. " +
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
	///     Cache-miss path: stores <paramref name="allResults"/> in the pagination cache and
	///     returns a paginated slice with token for subsequent pages.
	/// </summary>
	protected PaginatedResult<T> PaginateAndStore<T>(T[] allResults, ref int skip, int take)
	{
		activeScope.Value?.SetCacheTag(hit: false);
		
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
	protected static string? ResolveFilePath(string filePath, string rootPath, SecurityBoundary boundary)
	{
		try {
			
			if(Path.IsPathRooted(filePath)) {
				
				// Normalize first — prevents traversal via ".." in rooted paths.
				var full = Path.GetFullPath(filePath)
				;
				
				return boundary.IsPathAllowed(full) && File.Exists(full) ? full : null;
			}
			
			var normalized = NormalizePath(filePath);
			var direct     = Path.GetFullPath(Path.Combine(rootPath, normalized));
			
			if(File.Exists(direct) && boundary.IsPathAllowed(direct))
				
				return direct;
			
			// Fallback: suffix match — handles agents passing project-relative paths
			// when rootPath is the solution directory. Prefer strict suffix matches
			// over bare filename matches, and return null on ambiguity rather than
			// silently picking an arbitrary file.
			var suffix        = Path.DirectorySeparatorChar + normalized
			;
			var fileName      = Path.GetFileName(normalized);
			string? suffixHit = null;
			string? nameHit   = null;
			var     ambiguous = false;
			
			foreach(var candidate in Directory.EnumerateFiles(rootPath, fileName, SearchOption.AllDirectories)) {
				
				try {
					if(!boundary.IsPathAllowed(candidate))
						continue;
					
					if(candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
						
						if(suffixHit is not null) {
							
							ambiguous = true;
							break;
						}
						
						suffixHit = candidate;
					}
					else if(string.Equals(Path.GetFileName(candidate), fileName, StringComparison.OrdinalIgnoreCase)) {
						
						if(nameHit is not null)
							nameHit = null; // >1 filename match → discard
						else
							nameHit = candidate;
					}
				}
				catch(Exception ex) when(ex is ArgumentException or IOException or UnauthorizedAccessException) {
					// Skip this candidate and keep enumerating.
				}
			}
			
			if(!ambiguous && suffixHit is not null)
				
				return suffixHit;
			
			if(nameHit is not null)
				
				return nameHit;
		}
		catch(Exception ex) when(ex is ArgumentException or IOException or UnauthorizedAccessException) { }
		
		return null;
	}
	
	/// <summary>
	///     Returns a truncation <see cref="ErrorResult"/> if <paramref name="fullPath"/> is empty
	///     on disk after a write that was expected to produce <paramref name="expectedLength"/> bytes.
	///     Returns null if no truncation is detected or if <paramref name="expectedLength"/> is zero
	///     (empty-to-empty writes are non-events).
	/// </summary>
	protected static ErrorResult? CheckForTruncation(string filePath, string fullPath, int expectedLength) =>
		expectedLength > 0 && new FileInfo(fullPath).Length <= 4
			? new ErrorResult(
				$"Write appeared to succeed but '{filePath}' is empty on disk — filesystem or antivirus interference is suspected.",
				BackupRecoveryHint(filePath))
			: null;
	
	/// <summary>
	///     Checks whether a workspace-tracked write truncated <paramref name="fullPath"/> to 0 bytes
	///     and, if so, attempts transparent self-healing recovery: flushes the stale workspace state
	///     via <see cref="WorkspaceResolver.WriteAndInvalidate"/>, then atomically re-writes the
	///     intended content via temp-file rename. Returns null if no truncation occurred or recovery
	///     succeeded. Returns an <see cref="ErrorResult"/> only if the file is still empty after the
	///     recovery attempt.
	/// </summary>
	protected async Task<ErrorResult?> TryRecoverTruncation(
		string filePath, string fullPath, string projectPath, byte[] contentBytes)
	{
		// Skip if there's nothing to write, or if the file exists with content
		// (no truncation). Use FileInfo to check both existence and length in one
		// stat call — FileInfo.Length throws FileNotFoundException if the file is
		// absent, so existence must be checked first.
		var fi = new FileInfo(fullPath)
		;
		
		if(contentBytes.Length == 0 || (fi.Exists && fi.Length > 0))
			
			return null;
		
		var dir = Path.GetDirectoryName(fullPath)!;
		var tmp = Path.Combine(dir, $".roslynmcp_recover_{Guid.NewGuid():N}.tmp");
		
		try {
			// WriteAndInvalidate handles FSW suppression and workspace resync atomically.
			await workspace.WriteAndInvalidate(projectPath, fullPath, async () => {
				
				await FileWriter.WriteAllBytesAsync(tmp, contentBytes);
				FileWriter.Move(tmp, fullPath, overwrite: true);
			});
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			return new ErrorResult(
				$"Workspace write truncated '{filePath}' to 0 bytes and recovery also failed: {ex.Message}",
				BackupRecoveryHint(filePath));
		}
		
		return new FileInfo(fullPath).Length == 0
			? new ErrorResult(
				$"Workspace write truncated '{filePath}' to 0 bytes; self-healing also produced an empty file — filesystem or antivirus interference is suspected.",
				BackupRecoveryHint(filePath))
			: null;
	}
	
	// Returns recovery guidance that agents can act on when a write produces bad results.
	protected static string BackupRecoveryHint(string relPath) =>
		$"Both pre- and post-write snapshots were saved before the write. " +
		$"Use roslyn_local_history (action: \"list\", filePath: \"{relPath}\") to find tokens — " +
		"apply the \"post\" snapshot to restore the intended content, or the \"pre\" snapshot to roll back. " +
		$"As a last resort: git checkout -- {relPath}";
		
		
		/// <summary>
	///     Resolves where a file <em>would</em> be created — does not require the file to exist.
	///     Returns false with an error message if the path escapes the root or is otherwise invalid.
	/// </summary>
	protected static bool TryResolveTargetPath(
		string filePath,
		string rootPath,
		SecurityBoundary boundary,
		[System.Diagnostics.CodeAnalysis.NotNullWhen(true)]  out string? fullPath,
		[System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
	{
		fullPath = null;
		error    = null;
		
		try {
			
			if(Path.IsPathRooted(filePath)) {
				
				// Normalize first — prevents traversal via ".." embedded in rooted paths.
				var rootedFull = Path.GetFullPath(filePath)
				;
				
				if(!boundary.IsPathAllowed(rootedFull)) {
					
					error = "The specified path is not accessible.";
					
					return false;
				}
				
				fullPath = rootedFull;
				
				return true;
			}
			
			var normalized = NormalizePath(filePath);
			var candidate  = Path.GetFullPath(Path.Combine(rootPath, normalized));
			
			// Under-root guard — prevent path traversal (e.g. ../../etc/passwd).
			if(!boundary.IsPathAllowed(candidate)) {
				
				error = "The specified path is not accessible.";
				
				return false;
			}
			
			fullPath = candidate;
			
			return true;
		}
		catch(Exception ex) when(ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException) {
			
			error = "The specified path is not accessible.";
			
			return false;
		}
	}
	
	protected static string FormatModifiers(ISymbol symbol)
		=> SymbolFormatter.FormatModifiers(symbol);
	
	/// <summary>
	///     Returns true if <paramref name="path"/> is at or below <paramref name="root"/>.
	///     Expects both arguments to already be normalized via <see cref="Path.GetFullPath"/>.
	///     Uses a separator-aware comparison to prevent prefix collisions
	///     (e.g. root <c>D:\Foo</c> must not match <c>D:\FooBar\file.cs</c>).
	/// </summary>
	protected static bool IsPathUnderRoot(string path, string root)
	{
		var rootNorm = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		
		if(string.Equals(path, rootNorm, StringComparison.OrdinalIgnoreCase))
			
			return true;
		
		return path.StartsWith(rootNorm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
			|| path.StartsWith(rootNorm + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}
	
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
		
		return hasCrlf && !replacement.Contains("\r\n")
			? replacement.Replace("\n", "\r\n")
			: replacement
		;
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
		var location = diagnostic.Location;
		
		if(location.Kind == LocationKind.None)
			
			return true;
		
		// ExternalFile / MetadataFile / XmlFile — not project source; exclude them.
		if(location.Kind != LocationKind.SourceFile)
			
			return false;
		
		var filePath = location.SourceTree?.FilePath;
		
		if(string.IsNullOrEmpty(filePath))
			
			return true;
		
		if(!filePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
			
			return false;
		
		// Guard against prefix collisions (e.g. "Foo" matching "FooBar\file.cs").
		
		return filePath.Length == rootPath.Length || filePath[rootPath.Length] is '\\' or '/';
	}
	
	/// <summary>
	///     Resolves a type by CLR metadata name, tolerating cross-assembly ambiguity.
	///     <see cref="Compilation.GetTypeByMetadataName"/> returns null when several referenced
	///     assemblies define the same metadata name — even if all but one are inaccessible — so
	///     this falls back to <see cref="Compilation.GetTypesByMetadataName"/>, preferring the
	///     source assembly, then public metadata types.
	/// </summary>
	protected static INamedTypeSymbol? GetTypeByMetadataNameOrBest(Compilation compilation, string metadataName)
	{
		var direct = compilation.GetTypeByMetadataName(metadataName);
		
		if(direct is not null)
			
			return direct;
		
		var candidates = compilation.GetTypesByMetadataName(metadataName);
		
		if(candidates.IsEmpty)
			
			return null;
		
		return candidates.FirstOrDefault(t => SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, compilation.Assembly))
			?? candidates.FirstOrDefault(t => t.DeclaredAccessibility == Accessibility.Public)
			?? candidates[0];
	}
	
	/// <summary>
	///     Resolves a symbol by name from a compilation. When <paramref name="containingType"/> is
	///     provided, searches that type's members. Otherwise tries type-first lookup (metadata name →
	///     SimpleNameFinder) before falling back to AnySymbolFinder for members.
	/// </summary>
	protected static ISymbol? FindSymbol(Compilation compilation, string name, string? containingType)
	{
		if(containingType is not null) {
			
			var type = GetTypeByMetadataNameOrBest(compilation, containingType)
				?? compilation.GlobalNamespace.Accept(new SimpleNameFinder<INamedTypeSymbol>(containingType));
			
			return type?.GetMembers(name).FirstOrDefault();
		}
		
		var typeSymbol = GetTypeByMetadataNameOrBest(compilation, name)
			?? compilation.GlobalNamespace.Accept(new SimpleNameFinder<INamedTypeSymbol>(name));
		
		return typeSymbol is not null
			? typeSymbol
			: compilation.GlobalNamespace.Accept(new AnySymbolFinder(name))
		;
	}
	
	/// <summary>
	///     Finds all symbols with <paramref name="symbolName"/> in the compilation. When
	///     <paramref name="containingType"/> is provided, returns the single matching member (or empty
	///     if not found). Otherwise uses <see cref="AllSymbolsFinder"/> to collect every symbol that
	///     matches — prevents silently incomplete results when multiple types share a member name.
	/// </summary>
	protected static ISymbol[] FindSymbols(Compilation compilation, string symbolName, string? containingType)
	{
		if(containingType is not null) {
			
			var symbol = FindSymbol(compilation, symbolName, containingType);
			
			return symbol is not null ? [symbol] : [];
		}
		
		var finder = new AllSymbolsFinder(symbolName);
		finder.Visit(compilation.Assembly.GlobalNamespace);
		
		return [.. finder.Results];
	}
	
	/// <summary>
	///     Resolves the symbol at a 1-based line/column in a file of this compilation — the
	///     declared symbol at the position, or the referenced one. Position resolution pinpoints
	///     overloads, locals, and parameters that name-based lookup cannot distinguish. Works from
	///     the compilation (not a Document), so the result belongs to the same snapshot — required
	///     by Renamer — and Adhoc workspaces are covered. Returns null when the file, position, or
	///     symbol cannot be resolved.
	/// </summary>
	protected static async Task<ISymbol?> FindSymbolAtPosition(Compilation compilation, string filePath, int line, int column, CancellationToken cancellationToken)
	{
		if(FindSyntaxTree(compilation, filePath) is not { } tree)
			
			return null;
		
		var text     = await tree.GetTextAsync(cancellationToken);
		var position = GetPosition(text, line, column);
		
		if(position < 0)
			
			return null;
		
		var model = compilation.GetSemanticModel(tree);
		var token = (await tree.GetRootAsync(cancellationToken)).FindToken(position);
		
		for(var node = token.Parent; node is not null; node = node.Parent) {
			
			if(model.GetDeclaredSymbol(node, cancellationToken) is { } declared)
				
				return declared;
			
			var info = model.GetSymbolInfo(node, cancellationToken);
			
			if((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is { } referenced)
				
				return referenced;
		}
		
		return null;
	}
	
	/// <summary>Formats one ambiguous-symbol candidate as "kind Display — file:line".</summary>
	protected static string FormatSymbolCandidate(ISymbol symbol, string rootPath)
	{
		var span = symbol.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan();
		var file = span?.Path is { Length: > 0 } p ? TryMakeRelative(p, rootPath) ?? p : "?";
		var line = span is { } s ? s.StartLinePosition.Line + 1 : 0;
		
		return $"{symbol.Kind.ToString().ToLowerInvariant()} {symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} — {file}:{line}";
	}
	
	/// <summary>
	///     Builds the structured ambiguous-name failure: candidate list with the exact
	///     containingType / filePath+line values the agent needs for a self-recovering retry.
	///     This is the default ambiguity response — elicitation only runs when enabled via
	///     --elicit / ROSLYNMCP_ELICIT (see <see cref="ServerArgs.Elicit"/>) or a project-local
	///     config file (see <see cref="ProjectConfig.EffectiveElicit"/>).
	/// </summary>
	protected static AmbiguousSymbolResult AmbiguousSymbolError(ISymbol[] candidates, string symbolName, string rootPath)
	{
		var items = candidates.Select(s => {
			
			var span = s.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan();
			var file = span?.Path is { Length: > 0 } p ? TryMakeRelative(p, rootPath) ?? p : null;
			
			return new SymbolCandidate(
				s.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
				s.Kind.ToString().ToLowerInvariant(),
				s.ContainingType?.Name,
				file,
				span is { } sp ? sp.StartLinePosition.Line + 1 : 0);
		}).ToArray();
		
		return new AmbiguousSymbolResult(
			$"{candidates.Length} symbols match '{symbolName}'.",
			items,
			"Pick the intended symbol and re-call with its containingType (or filePath + line to pinpoint an overload, local, or parameter). " +
			"If the user's intent is not clear from the conversation, ask them which candidate they meant.");
	}
	
	/// <summary>
	///     Resolves an ambiguous name to one symbol by asking the user through MCP elicitation
	///     (single-select form). Returns null when the client lacks elicitation support, the user
	///     declined, or the answer did not resolve — callers then fail with the candidate list so
	///     the agent can retry with containingType or filePath+line. Elicitation-based
	///     disambiguation follows darylmcd/Roslyn-Backed-MCP's UX (no code reused).
	/// </summary>
	protected static async Task<ISymbol?> TryElicitSymbolChoice(McpServer server, ISymbol[] candidates, string symbolName, string rootPath, CancellationToken cancellationToken)
	{
		if(server.ClientCapabilities?.Elicitation is null)
			
			return null;
		
		// Const values must be unique — display strings can collide (e.g. partial types), so the
		// 1-based index is the round-tripped value and the display string is only the title.
		var options = candidates
			.Select((s, i) => new ElicitRequestParams.EnumSchemaOption {
				Const = $"{i + 1}",
				Title = FormatSymbolCandidate(s, rootPath),
			})
			.ToList();
		
		ElicitResult result;
		
		try {
			result = await server.ElicitAsync(
				new ElicitRequestParams {
					Message         = $"Multiple symbols match '{symbolName}'. Which one should be used?",
					RequestedSchema = new ElicitRequestParams.RequestSchema {
						Properties = { ["symbol"] = new ElicitRequestParams.TitledSingleSelectEnumSchema { OneOf = options } },
						Required   = ["symbol"],
					},
				},
				cancellationToken);
		}
		catch(Exception ex) when(ex is not OperationCanceledException) {
			// A client that advertises elicitation but fails the request must not break the tool —
			// fall through to the candidate-list failure path.
			return null;
		}
		
		if(!result.IsAccepted || result.Content is not { } content || !content.TryGetValue("symbol", out var chosen))
			
			return null;
		
		return int.TryParse(chosen.GetString(), out var index) && index >= 1 && index <= candidates.Length
			? candidates[index - 1]
			: null;
	}
	
	/// <summary>
	///     Returns a canonical "symbol not found" <see cref="ErrorResult"/> with standardized hint text.
	///     Callers must pass this to <c>scope.Failed("symbol not found", ...)</c> — the analyzer
	///     requires the scope terminal to appear directly at the return site.
	/// </summary>
	protected static ErrorResult SymbolNotFoundError(string symbolName) =>
		new(
			$"Symbol '{symbolName}' not found.",
			"Use roslyn_get_type_members or roslyn_find_references to verify the name.");
	
	/// <summary>
	///     Detects a VS Code Copilot chat symbol-reference prefix ("sym:" or "#sym:") that chat
	///     serialization prepends to backtick-quoted terms. Returns true when the prefix is
	///     present; <paramref name="stripped"/> is the trimmed remainder — possibly empty when
	///     the chat pipeline truncated the reference to the bare prefix.
	/// </summary>
	protected static bool HasChatSymbolRefPrefix(string value, out string stripped)
	{
		var trimmed = value.TrimStart();
		
		var rest =
			  trimmed.StartsWith("#sym:", StringComparison.Ordinal) ? trimmed["#sym:".Length..]
			: trimmed.StartsWith("sym:",  StringComparison.Ordinal) ? trimmed["sym:".Length..]
			: null
		;
		
		if(rest is null) {
			
			stripped = value;
			return false;
		}
		
		stripped = rest.Trim();
		return true;
	}
	
	/// <summary>
	///     Guard for symbol/type/method name arguments: strips a chat symbol-reference prefix in
	///     place — identifiers can never contain ':', so the prefix is unambiguous chat noise.
	///     Returns false with a populated <paramref name="error"/> when only the bare prefix
	///     arrived (the name was truncated upstream), so the agent re-sends the plain name.
	///     Call after BeginTool so the log keeps the original value.
	/// </summary>
	protected static bool TryStripChatSymbolRef(ref string value, out ErrorResult? error)
	{
		error = null;
		
		if(!HasChatSymbolRefPrefix(value, out var stripped))
			
			return true;
		
		if(stripped.Length == 0) {
			
			error = new ErrorResult(
				"The argument was a VS Code chat symbol reference whose name was lost ('sym:').",
				"Re-send the plain symbol name without the 'sym:' prefix.");
			
			return false;
		}
		
		value = stripped;
		return true;
	}
	
	/// <summary>
	///     Null-tolerant companion for optional name arguments (e.g. containingType). A separate
	///     name, not an overload — nullable annotations are erased from CLR signatures, so
	///     'ref string' and 'ref string?' overloads would collide (CS0111).
	/// </summary>
	protected static bool TryStripChatSymbolRefOptional(ref string? value, out ErrorResult? error)
	{
		error = null;
		
		if(value is null)
			
			return true;
		
		var name = value;
		var ok   = TryStripChatSymbolRef(ref name, out error);
		
		value = name;
		return ok;
	}
	
	/// <summary>Caution for a search that only matched after stripping a chat symbol-reference prefix.</summary>
	protected static string ChatRefFallbackCaution(string original, string stripped) =>
		$"No matches for '{original}'; interpreted it as a VS Code chat symbol reference and searched '{stripped}' instead.";
	
	/// <summary>Hint for a zero-match search whose pattern was a bare chat-reference prefix (name truncated upstream).</summary>
	protected const string ChatRefTruncatedHint =
		"0 matches. If this was a VS Code chat symbol reference whose name was truncated to 'sym:', re-send the plain symbol name.";
	
	/// <summary>Hint for a zero-match search where the verbatim pattern and its chat-reference-stripped form both found nothing.</summary>
	protected static string ChatRefBothTriedHint(string original, string stripped) =>
		$"0 matches for '{original}' (also tried '{stripped}' in case the pattern was a VS Code chat symbol reference).";
	
	/// <summary>Joins two optional caution strings, or null when both are null.</summary>
	protected static string? ComposeCautions(string? first, string? second) =>
		first is null ? second : second is null ? first : $"{first} {second}";
	
	/// <summary>
	///     Saves pre- and post-change backup snapshots, returning the pre-change token on success
	///     or a populated <see cref="ErrorResult"/> on failure. Pass <paramref name="skipPre"/>=true
	///     for new files (no existing content to snapshot). Pass <paramref name="fileState"/> to
	///     customize the "file was not {state}" phrase in the error message.
	/// </summary>
	protected static async Task<(string? PreToken, ErrorResult? Error)> SaveBackupsAsync(
		BackupStore backups, string fullPath, string projectPath, string toolName,
		byte[] postBytes, bool skipPre = false, string fileState = "modified")
	{
		string? preToken = null;
		var    preSaved = false;
		
		try {
			
			if(!skipPre) {
				
				preToken = await backups.SavePreAsync(fullPath, projectPath, toolName);
				preSaved = preToken is not null;
			}
			
			await backups.SavePostAsync(fullPath, projectPath, toolName, postBytes);
			
			return (preToken, null);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			var phase = preSaved ? "post" : "pre";
			var hint  = preSaved
				? "Resolve the issue and retry. The pre-change snapshot that was saved is not needed since the file was not touched."
				: "Resolve the issue (disk space or permissions) and retry.";
			
			return (null, new ErrorResult(
				$"Write aborted — could not save {phase}-change backup: {ex.Message}. The file was not {fileState}.",
				hint));
		}
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
	
	protected static int GetPosition(SourceText text, int line, int column)
	{
		if(line < 1 || line > text.Lines.Count || column < 1)
			
			return -1;
		
		var lineSpan = text.Lines[line - 1];
		
		return lineSpan.Start + Math.Min(column - 1, lineSpan.Span.Length);
	}
}

internal sealed record PaginatedResult<T>(
	T[]    Items,
	int    Total,
	int    Skip,
	int    Take,
	string? PageToken,
	bool    HasMore
);
