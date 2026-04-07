using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using System.Collections.Concurrent;

namespace RoslynMcp;

internal sealed partial class WorkspaceManager
{
	/// <summary>
	///     Encapsulates a single Roslyn workspace — loaded from a solution, a single .csproj,
	///     or a directory (AdhocWorkspace). Supports multi-project solutions with per-project
	///     compilation caching.
	/// </summary>
	sealed class WorkspaceInstance : IDisposable
	{
		enum LoadMode { Solution, Project, Adhoc }
		
		private Workspace            workspace;
		private readonly string      rootPath;
		private readonly bool        isMSBuild;
		private ProjectId            defaultProjectId;
		private readonly ReaderWriterLockSlim @lock = new();
		private          FileSystemWatcher?   watcher;
		
		// Maps normalized .csproj paths → ProjectIds for all projects in the workspace.
		private readonly Dictionary<string, ProjectId> projectMap = new(StringComparer.OrdinalIgnoreCase);
		
		// Per-project compilation cache — cleared on any file change.
		private readonly Dictionary<ProjectId, Compilation> compilationCache = new();
		
		// Incremented on each file-system change that requires a reload; cleared after reload completes.
		// Using a generation counter rather than a bool so a change arriving during a load is not lost.
		private volatile int reloadVersion;
		
		// Set early in Dispose so the timer callback can exit cleanly before the workspace is torn down.
		private volatile bool disposed;
		
		// The path used to load this workspace (solution or csproj), for reloading.
		private readonly string     loadPath;
		private readonly LoadMode   loadMode;
		private readonly FileLogger logger;
		
		// Debounce: accumulate FSW events for 300ms before processing.
#if NET9_0_OR_GREATER
		private readonly Lock             debounceLock   = new();
#else
		private readonly object           debounceLock   = new();
#endif
		private readonly HashSet<string>  pendingChanges = new(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string>  pendingDeletes = new(StringComparer.OrdinalIgnoreCase);
		// Tracks file sizes written by RM's own TryApplyChanges calls so FlushMSBuild
		// can skip reloading files that are already up to date in the workspace.
		private          Dictionary<string, long> rmOwnedWriteSizes    = new(StringComparer.OrdinalIgnoreCase);
		private const    int                       MaxRmOwnedWriteSizes = 50;
		// Per-file FSW suppression — ref-counted for concurrent-write safety.
		// A path is added before each RM-owned write and decremented in the finally block.
		// ScheduleDebounced skips events where the count is > 0.
		private readonly ConcurrentDictionary<string, int> ignoredPaths = new(StringComparer.OrdinalIgnoreCase);
		private          Timer?           debounceTimer;
		private const    int              DebounceMs     = 300;
		// Ref-counted FSW suppression — multiple concurrent ApplyChangesWithFswSuppressed
		// calls each increment on entry and decrement on exit; EnableRaisingEvents is only
		// restored when the last suppressor finishes (count returns to 0). See issue #145 item 3.
		private          int              fswSuppressCount;
		
		// ── Factory methods ──────────────────────────────────────────────────
		
		public static WorkspaceInstance ForSolution(string solutionPath, FileLogger logger)	=> new(solutionPath, LoadMode.Solution, logger);
		public static WorkspaceInstance ForProject(string csprojPath, FileLogger logger)	=> new(csprojPath, LoadMode.Project, logger);
		public static WorkspaceInstance ForDirectory(string dir, FileLogger logger)			=> new(dir, LoadMode.Adhoc, logger);
		
		private WorkspaceInstance(string path, LoadMode mode, FileLogger logger)
		{
			loadPath    = path;
			loadMode    = mode;
			this.logger = logger;
			
			switch(mode) {
				
				case LoadMode.Solution:
					
					rootPath = Path.GetDirectoryName(path)!;
					AutoDetectAndBootstrap(path, logger);
					
					logger.LogInfo("Load", $"Loading solution: {path}");
					var slnSw = System.Diagnostics.Stopwatch.StartNew();
					workspace = LoadSolution(path, logger);
					isMSBuild = true;
					
					foreach(var kvp in BuildProjectMapFor(workspace))
						projectMap[kvp.Key] = kvp.Value;
					
					defaultProjectId = workspace.CurrentSolution.Projects
						.FirstOrDefault()?.Id
						?? throw new InvalidOperationException($"Solution '{path}' contains no projects.")
					;
					logger.LogInfo("Load", $"Solution loaded in {slnSw.ElapsedMilliseconds}ms ({workspace.CurrentSolution.Projects.Count()} projects)");
					StartWatcher();
					break;
				
				case LoadMode.Project:
					
					rootPath = Path.GetDirectoryName(path)!;
					AutoDetectAndBootstrap(path, logger);
					
					logger.LogInfo("Load", $"Loading project: {path}");
					var projSw = System.Diagnostics.Stopwatch.StartNew();
					
					var (msbuildWs, projectId) = LoadMSBuildWorkspace(path, logger);
					workspace        = msbuildWs;
					defaultProjectId = projectId;
					isMSBuild        = true;
					
					// OpenProjectAsync also loads referenced projects — map them all.
					foreach(var kvp in BuildProjectMapFor(workspace))
						projectMap[kvp.Key] = kvp.Value;
					
					logger.LogInfo("Load", $"Project loaded in {projSw.ElapsedMilliseconds}ms ({workspace.CurrentSolution.Projects.Count()} projects)");
					
					StartWatcher();
					break;
				
				case LoadMode.Adhoc:
					
					rootPath = path;
					var dirInfo = new DirectoryInfo(path);
					
					if(dirInfo.Parent is null)
						throw new InvalidOperationException($"Cannot create AdhocWorkspace for root directory '{path}'. Specify a subdirectory or use a .csproj file.");
					
					var (adhocWs, adhocPid) = LoadAdhocWorkspace();
					workspace        = adhocWs;
					defaultProjectId = adhocPid;
					isMSBuild        = false;
					
					LoadAllFiles((AdhocWorkspace) workspace, defaultProjectId);
					
					StartWatcher();
					break;
				
				default:
					throw new ArgumentOutOfRangeException(nameof(mode));
			}
		}
		
		static Dictionary<string, ProjectId> BuildProjectMapFor(Workspace ws)
		{
			var map = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);
			
			foreach(var p in ws.CurrentSolution.Projects)
				if(p.FilePath is not null)
					map[Path.GetFullPath(p.FilePath)] = p.Id;
			
			return map;
		}
		
		// ── Properties ───────────────────────────────────────────────────────
		
		public string              RootPath     => rootPath;
		public bool                IsMSBuild    => isMSBuild;
		public IEnumerable<string> ProjectPaths
		{
			get {
				@lock.EnterReadLock();
				
				try {
					return [..projectMap.Keys];
				}
				finally {
					@lock.ExitReadLock();
				}
			}
		}
		
		// ── Queries ──────────────────────────────────────────────────────────
		
		public Solution GetSolution()
		{
			ReloadIfNeeded();
			
			// Snapshot under read lock — prevents use-after-dispose if ReloadIfNeeded
			// swaps and disposes the old workspace on a concurrent thread.
			@lock.EnterReadLock();
			try {
				return workspace.CurrentSolution;
			}
			finally {
				@lock.ExitReadLock();
			}
		}
		
		public Compilation GetCompilation(string? csprojPath = null)
		{
			ReloadIfNeeded();
			
			ProjectId projectId;
			Solution  solution;
			int       gen;
			
			@lock.EnterReadLock();
			
			try {
				
				projectId = ResolveProjectId_NoLock(csprojPath);
				solution  = workspace.CurrentSolution;
				gen       = reloadVersion;
				
				if(compilationCache.TryGetValue(projectId, out var cached))
					return cached;
			}
			finally {
				@lock.ExitReadLock();
			}
			
			return RebuildCompilation(projectId, solution, gen);
		}
		
		public Project GetProject(string? csprojPath = null)
		{
			ReloadIfNeeded();
			
			@lock.EnterReadLock();
			
			try {
				
				var projectId = ResolveProjectId_NoLock(csprojPath);
				
				return workspace.CurrentSolution.GetProject(projectId)
					?? throw new InvalidOperationException($"Project '{projectId}' not found in current solution.");
			}
			finally {
				@lock.ExitReadLock();
			}
		}
		
		ProjectId ResolveProjectId_NoLock(string? csprojPath)
		{
			if(csprojPath is null)
				return defaultProjectId;
			
			if(projectMap.TryGetValue(csprojPath, out var projectId))
				return projectId;
			
			// Match by filename only (agent may pass a relative path that doesn't match fully).
			var fileName = Path.GetFileName(csprojPath);
			var match    = projectMap.FirstOrDefault(kvp =>
				string.Equals(Path.GetFileName(kvp.Key), fileName, StringComparison.OrdinalIgnoreCase)
			);
			
			if(match.Value is not null)
				return match.Value;
			
			return defaultProjectId;
		}
		
		/// <summary>Returns the .csproj path for a given resolved path, or the first known .csproj.</summary>
		public string? FindCsprojForPath(string normalizedPath)
		{
			@lock.EnterReadLock();
			
			try {
				
				if(projectMap.ContainsKey(normalizedPath))
					return normalizedPath;
				
				return projectMap.Keys.FirstOrDefault();
			}
			finally {
				@lock.ExitReadLock();
			}
		}
		
		/// <summary>Searches all projects for a document matching a file suffix. Returns the .csproj of the first match.</summary>
		public string? FindCsprojForFileSuffix(string suffix, string fileName)
		{
			@lock.EnterReadLock();
			
			try {
				
				foreach(var (csproj, projectId) in projectMap) {
					
					var project = workspace.CurrentSolution.GetProject(projectId);
					
					if(project is null)
						continue;
					
					var hasFile = project.Documents.Any(d =>
						d.FilePath is not null && (
							d.FilePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
							|| string.Equals(Path.GetFileName(d.FilePath), fileName, StringComparison.OrdinalIgnoreCase)
						)
					);
					
					if(hasFile)
						return csproj;
				}
				
				return null;
			}
			finally {
				@lock.ExitReadLock();
			}
		}
		
		// ── File invalidation ────────────────────────────────────────────────
		
		public void InvalidateFile(string fullPath)
		{
			Workspace ws;
			Solution  currentSolution;
			ProjectId adhocProjectId;
			
			@lock.EnterReadLock();
			
			try {
				ws              = workspace;
				currentSolution = workspace.CurrentSolution;
				adhocProjectId  = defaultProjectId;
			}
			finally {
				@lock.ExitReadLock();
			}
			
			if(isMSBuild) {
				
				var docIds = currentSolution.GetDocumentIdsWithFilePath(fullPath);
				
				if(docIds.Length > 0) {
					
					try {
						
						using var stream = File.OpenRead(fullPath);
						var newText = SourceText.From(stream, FileWriter.Utf8NoBom);
						var newSolution = currentSolution;
						
						foreach(var id in docIds)
							newSolution = newSolution.WithDocumentText(id, newText);
						
						// If TryApplyChanges fails (rare — workspace conflict or unsupported kind),
						// flag for full reload so the next GetCompilation picks up the new content.
						if(!ApplyChangesWithFswSuppressed(newSolution, currentSolution, ws))
							Interlocked.Increment(ref reloadVersion);
					}
					catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
					{ }
				}
				else {
					Interlocked.Increment(ref reloadVersion);
					InvalidateCompilation();
				}
			}
			
			else if(ws is AdhocWorkspace adhoc)
				try {
					AddOrUpdateDocument(adhoc, adhocProjectId, fullPath);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
				{ }
		}
		
		public void Dispose()
		{
			// Signal FlushPendingChanges to bail early on any in-flight or pending callbacks.
			disposed = true;
			
			watcher?.Dispose();
			
			// Quiesce the timer: wait for any in-flight callback to complete before
			// tearing down @lock and workspace, which the callback accesses.
			Timer? timerToQuiesce;
			
			lock(debounceLock) {
				timerToQuiesce = debounceTimer;
				debounceTimer  = null;
				rmOwnedWriteSizes.Clear();
			}
			
			if(timerToQuiesce is not null) {
				using var done = new ManualResetEventSlim(false);
				timerToQuiesce.Dispose(done.WaitHandle);
				done.Wait();
			}
			
			@lock.Dispose();
			workspace.Dispose();
		}
		
		// ── Workspace loading ────────────────────────────────────────────────
		
		
		/// <summary>
		///     When workspace mode is Auto, peeks at the project to detect SDK vs Framework style,
		///     then calls EnsureReady with the detected mode. Logs the result.
		/// </summary>
		static void AutoDetectAndBootstrap(string path, FileLogger logger)
		{
			// Start from the user's explicit choice; auto-detect only if not specified.
			var mode = ServerArgs.Current.WorkspaceMode;
			
			if(mode == WorkspaceMode.Auto) {
				
				// Find a .csproj to peek at — either the path itself, or first .csproj in the directory.
				var csprojToCheck = path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
					? path
					: MSBuildBootstrap.FindFirstCsproj(Path.GetDirectoryName(path) ?? path);
				
				if(csprojToCheck is not null) {
					var detected = MSBuildBootstrap.DetectProjectStyle(csprojToCheck);
					
					logger.LogInfo("Workspace", $"auto-detected {detected} from {Path.GetFileName(csprojToCheck)}");
					
					mode = detected;
				}
			}
			
			WarnIfLargeSolution(path, mode, logger);
			MSBuildBootstrap.EnsureReady(mode);
			logger.LogInfo("MSBuild", MSBuildBootstrap.DiscoveryMethod);
		}
		
		
		
		const int LargeSolutionThreshold = 30;
		
		/// <summary>
		///     Counts .csproj files under the solution directory. If over the threshold,
		///     logs a warning before the potentially long MSBuild load.
		/// </summary>
		static void WarnIfLargeSolution(string path, WorkspaceMode mode, FileLogger logger)
		{
			if(mode == WorkspaceMode.Adhoc)
				return;
			
			var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
			
			if(dir is null)
				return;
			
			try {
				var count = Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories).Count();
				
				if(count > LargeSolutionThreshold)
					logger.LogInfo("Workspace",
						$"Large solution detected: ~{count} projects. " +
						$"MSBuild loading may take several minutes. " +
						$"For faster startup, use --workspace adhoc or set ROSLYNMCP_WORKSPACE=adhoc.");
			}
			catch { }
		}
		
		
		static Workspace LoadSolution(string solutionPath)
			=> LoadSolution(solutionPath, null);
		
		static Workspace LoadSolution(string solutionPath, FileLogger? log)
		{
			var msbuildWorkspace = MSBuildWorkspace.Create();
			msbuildWorkspace.RegisterWorkspaceFailedHandler(e =>
			{
				var level = e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure ? "ERROR" : "WARN";
				log?.LogInfo("WorkspaceFailed", $"[{level}] {e.Diagnostic.Message}");
			}, options: null);
			
			
			try {
				
				if(solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)) {
					
					// .slnx — parse XML and load each project into the same workspace.
					var doc    = System.Xml.Linq.XDocument.Load(solutionPath);
					var slnDir = Path.GetDirectoryName(solutionPath)!;
					
					var projectPaths = doc.Root!
						.Descendants("Project")
						.Select(e => e.Attribute("Path")?.Value)
						.Where(p => p is not null)
						.Select(p => Path.GetFullPath(Path.Combine(slnDir, p!)))
						.Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
					;
					
					foreach(var projectPath in projectPaths) {
						
						try {
							msbuildWorkspace.OpenProjectAsync(projectPath).GetAwaiter().GetResult();
						}
						catch(Exception ex) when(ex is not OperationCanceledException) {
							// Multi-TFM projects or transitive references may already be loaded
							// by a previous OpenProjectAsync call. Log and skip.
							log?.LogInfo("Load", $"Skipped {Path.GetFileName(projectPath)}: {ex.GetType().Name}: {ex.Message}");
						}
					}
				}
				
				else
					msbuildWorkspace.OpenSolutionAsync(solutionPath).GetAwaiter().GetResult();
				
				return msbuildWorkspace;
			}
			catch(Exception ex) when(ex is not OperationCanceledException) {
				log?.LogError("LoadSolution", $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
				msbuildWorkspace.Dispose();
				throw new InvalidOperationException($"Failed to load solution '{solutionPath}': {ex.Message}", ex);
			}
		}
		
		static (Workspace workspace, ProjectId projectId) LoadMSBuildWorkspace(string csprojPath, FileLogger? log)
		{
			try {
				var msbuildWorkspace = MSBuildWorkspace.Create();
				
				msbuildWorkspace.RegisterWorkspaceFailedHandler(e =>
				{
					var level = e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure ? "ERROR" : "WARN";
					log?.LogInfo("WorkspaceFailed", $"[{level}] {e.Diagnostic.Message}");
				}, options: null);
				
				var project = msbuildWorkspace.OpenProjectAsync(csprojPath).GetAwaiter().GetResult();
				
				return (msbuildWorkspace, project.Id);
			}
			catch(Exception ex) when(ex is not OperationCanceledException) {
				throw new InvalidOperationException($"Failed to load MSBuildWorkspace for '{csprojPath}': {ex.Message}", ex);
			}
		}
		
		(Workspace workspace, ProjectId projectId) LoadAdhocWorkspace()
		{
			var adhocWorkspace = new AdhocWorkspace();
			
			var projectInfo = ProjectInfo.Create(
				id:             ProjectId.CreateNewId(),
				version:        VersionStamp.Create(),
				name:           Path.GetFileName(rootPath),
				assemblyName:   Path.GetFileName(rootPath),
				language:       LanguageNames.CSharp,
				compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
				parseOptions:       new CSharpParseOptions(LanguageVersion.Preview)
			);
			
			adhocWorkspace.AddProject(projectInfo);
			
			return (adhocWorkspace, projectInfo.Id);
		}
		
		// ── AdhocWorkspace file management ───────────────────────────────────
		
		void LoadAllFiles(AdhocWorkspace adhocWorkspace, ProjectId projectId)
		{
			var files = EnumerateFilesWithErrorHandling(rootPath, "*.cs");
			
			foreach(var path in files)
				AddOrUpdateDocument(adhocWorkspace, projectId, path);
		}
		
		IEnumerable<string> EnumerateFilesWithErrorHandling(string path, string searchPattern)
		{
			var pathInfo = new DirectoryInfo(path);
			if(pathInfo.Attributes.HasFlag(FileAttributes.System) || pathInfo.Parent is null)
				yield break;
			
			IEnumerable<string> files;
			try {
				files = Directory.EnumerateFiles(path, searchPattern, SearchOption.TopDirectoryOnly);
			}
			catch(Exception ex) when(ex is UnauthorizedAccessException or DirectoryNotFoundException) {
				yield break;
			}
			
			foreach(var file in files)
				yield return file;
			
			IEnumerable<string> directories;
			try {
				directories = Directory.EnumerateDirectories(path);
			}
			catch(Exception ex) when(ex is UnauthorizedAccessException or DirectoryNotFoundException) {
				yield break;
			}
			
			foreach(var directory in directories) {
				
				var dirInfo = new DirectoryInfo(directory);
				if(dirInfo.Attributes.HasFlag(FileAttributes.Hidden) ||
				   dirInfo.Attributes.HasFlag(FileAttributes.System) ||
				   dirInfo.Name is "node_modules" or "bin" or "obj" or ".git" or ".vs" or "packages")
					continue;
				
				foreach(var file in EnumerateFilesWithErrorHandling(directory, searchPattern))
					yield return file;
			}
		}
		
		void AddOrUpdateDocument(AdhocWorkspace adhocWorkspace, ProjectId projectId, string path)
		{
			if(!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
				return;
			
			using var stream = File.OpenRead(path);
			
			var text = SourceText.From(stream, FileWriter.Utf8NoBom);
			var name = Path.GetRelativePath(rootPath, path);
			
			var project  = adhocWorkspace.CurrentSolution.GetProject(projectId)!;
			var existing = project.Documents.FirstOrDefault(d => d.Name == name);
			
			Solution newSolution;
			
			if(existing is not null)
				newSolution = adhocWorkspace.CurrentSolution.WithDocumentText(existing.Id, text);
			else
				newSolution = adhocWorkspace.CurrentSolution.AddDocument(
					DocumentId.CreateNewId(projectId), name, text, filePath: path
				);
			
			adhocWorkspace.TryApplyChanges(newSolution);
			InvalidateCompilation();
		}
		
		void RemoveDocument(AdhocWorkspace adhocWorkspace, ProjectId projectId, string path)
		{
			var name     = Path.GetRelativePath(rootPath, path);
			var project  = adhocWorkspace.CurrentSolution.GetProject(projectId)!;
			var existing = project.Documents.FirstOrDefault(d => d.Name == name);
			
			if(existing is null)
				return;
			
			adhocWorkspace.TryApplyChanges(adhocWorkspace.CurrentSolution.RemoveDocument(existing.Id));
			
			InvalidateCompilation();
		}
		
		// ── FileSystemWatcher ────────────────────────────────────────────────
		
		/// <summary>
		///     Applies solution changes with FSW suppressed to prevent a feedback loop.
		///     MSBuildWorkspace.TryApplyChanges writes text back to disk, which would
		///     re-trigger the FSW. See issue #49.
		/// </summary>
		internal bool ApplyChangesWithFswSuppressed(Solution newSolution)
		{
			Workspace ws;
			Solution  baseSolution;
			
			@lock.EnterReadLock();
			
			try {
				ws           = workspace;
				baseSolution = workspace.CurrentSolution;
			}
			finally {
				@lock.ExitReadLock();
			}
			
			return ApplyChangesWithFswSuppressed(newSolution, baseSolution, ws);
		}
		
		// Triggers a full workspace reload on the next GetCompilation call. Used by
		// callers (WorkspaceManager.ApplyChanges) that cannot retry the apply themselves.
		internal void MarkReloadNeeded() => Interlocked.Increment(ref reloadVersion);
		

		
		// Carries the caller's immutable Solution snapshot for diff computation and the expected
		// Workspace reference for a best-effort staleness check before TryApplyChanges.
		// A very narrow race between the staleness check and TryApplyChanges still exists, but
		// TryApplyChanges returns false gracefully on a disposed workspace — so this is safe.
		internal bool ApplyChangesWithFswSuppressed(Solution newSolution, Solution baseSolution, Workspace expectedWs)
		{
			// Best-effort staleness guard: if another thread completed a reload and replaced
			// the workspace since the caller snapshotted it, discard rather than apply stale edits.
			@lock.EnterReadLock();
			Workspace ws;
			
			try {
				ws = workspace;
				
				if(!ReferenceEquals(ws, expectedWs))
					return false;
			}
			finally {
				@lock.ExitReadLock();
			}
			
			// Collect changed document paths before TryApplyChanges overwrites them
			// (MSBuild only — Adhoc.TryApplyChanges is in-memory only, no disk write).
			// Use caller's baseSolution snapshot — not ws.CurrentSolution post-unlock —
			// so the diff reflects exactly what changed relative to the caller's view.
			string[] ownedPaths = [];
			
			if(isMSBuild) {
				
				ownedPaths = newSolution.GetChanges(baseSolution)
					.GetProjectChanges()
					.SelectMany(p => p.GetChangedDocuments())
					.Select(id => newSolution.GetDocument(id)?.FilePath)
					.Where(p => p is not null)
					.Cast<string>()
					.ToArray()
				;
			}
			
			// Ref-counted suppression: always disable before TryApplyChanges (idempotent),
			// only re-enable when the last concurrent suppressor finishes.
			if(watcher is not null) {
				Interlocked.Increment(ref fswSuppressCount);
				watcher.EnableRaisingEvents = false;
			}
			
			bool applied;
			
			try {
				applied = ws.TryApplyChanges(newSolution);
			}
			catch(Exception ex) when(ex is ObjectDisposedException or InvalidOperationException) {
				// Workspace was disposed by a concurrent ReloadIfNeeded between the staleness
				// check and TryApplyChanges — treat the same as a false return.
				_ = ex;
				applied = false;
			}
			finally {
				if(watcher is not null && Interlocked.Decrement(ref fswSuppressCount) == 0)
					watcher.EnableRaisingEvents = true;
			}
			
			// Record sizes only when TryApplyChanges succeeded and actually wrote files.
			if(applied && ownedPaths.Length > 0) {
				
				lock(debounceLock) {
					
					// Safety cap — clear rather than evict individual entries to keep it simple.
					// These entries are short-lived (consumed by the next FSW flush), so this
					// branch is only reached if FSW events are being lost or unusually delayed.
					if(rmOwnedWriteSizes.Count + ownedPaths.Length > MaxRmOwnedWriteSizes)
						rmOwnedWriteSizes.Clear();
					
					foreach(var path in ownedPaths) {
						
						try {
							rmOwnedWriteSizes[path] = new FileInfo(path).Length;
						}
						catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
							// File gone or locked — skip recording; FlushMSBuild will reload normally.
							_ = ex;
						}
					}
				}
			}
			
			InvalidateCompilation();
			return applied;
		}

		
		// For MSBuild-tracked .cs files: routes through TryApplyChanges as the single
		// disk write (FSW-suppressed via ApplyChangesWithFswSuppressed). Returns false
		// for Adhoc workspaces (TryApplyChanges is in-memory only) or for files not
		// tracked by the workspace — callers fall back to WriteAndInvalidate.
		internal bool TryApplyTextChange(string filePath, SourceText newText)
		{
			// Adhoc workspace TryApplyChanges is in-memory only — no disk write.
			// Return false so the caller's WriteAndInvalidate path handles disk persistence.
			if(!isMSBuild)
				return false;
			
			Workspace ws;
			Solution  currentSolution;
			
			@lock.EnterReadLock();
			
			try {
				ws              = workspace;
				currentSolution = workspace.CurrentSolution;
			}
			finally {
				@lock.ExitReadLock();
			}
			
			var docIds = currentSolution.GetDocumentIdsWithFilePath(filePath);
			
			if(docIds.IsEmpty)
				return false;
			
			var newSolution = currentSolution;
			
			foreach(var id in docIds)
				newSolution = newSolution.WithDocumentText(id, newText);
			
			return ApplyChangesWithFswSuppressed(newSolution, currentSolution, ws);
		}
		
		// For untracked and Adhoc .cs files: suppresses the per-file FSW event during
		// the write, then calls InvalidateFile to sync in-memory workspace state.
		// No double write — Adhoc.InvalidateFile calls AddOrUpdateDocument (in-memory
		// only); MSBuild-untracked InvalidateFile increments reloadVersion (no TryApplyChanges).
		internal async Task WriteAndInvalidate(string fullPath, Func<Task> write)
		{
			ignoredPaths.AddOrUpdate(fullPath, 1, (_, count) => count + 1);
			
			try {
				await write();
			}
			finally {
				ignoredPaths.AddOrUpdate(fullPath, 0, (_, count) => count - 1);
			}
			
			InvalidateFile(fullPath);
		}
		

		void StartWatcher()
		{
			watcher = new FileSystemWatcher(rootPath, "*.cs")
			{
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
			};
			
			watcher.Changed += (_, e) => ScheduleDebounced(e.FullPath);
			watcher.Created += (_, e) => ScheduleDebounced(e.FullPath);
			watcher.Deleted += (_, e) => ScheduleDebounced(e.FullPath, deleted: true);
			watcher.Renamed += (_, e) => {
				
				ScheduleDebounced(e.OldFullPath, deleted: true);
				ScheduleDebounced(e.FullPath);
			};
			
			watcher.EnableRaisingEvents = true;
		}
		
		void ScheduleDebounced(string fullPath, bool deleted = false)
		{
			// Skip FSW events for paths RM is currently writing — prevents spurious workspace
			// reloads from our own writes. Ref-counted for safety; normal usage is single-threaded.
			if(!deleted && ignoredPaths.TryGetValue(fullPath, out var count) && count > 0)
				return;
			
			lock(debounceLock) {
				
				// Guard against late-arriving FSW callbacks after Dispose has quiesced the timer.
				// Without this, a queued callback could create a new timer that never gets disposed.
				if(disposed)
					return;
				
				if(deleted)
					pendingDeletes.Add(fullPath);
				
				else
					pendingChanges.Add(fullPath);
				
				
				if(debounceTimer is null)
					debounceTimer = new Timer(FlushPendingChanges, null, DebounceMs, Timeout.Infinite);
				
				else
					debounceTimer.Change(DebounceMs, Timeout.Infinite);
			}
		}
		
		void FlushPendingChanges(object? _)
		{
			// Timer callbacks must never throw — unhandled exceptions crash the process.
			try {
				
				if(disposed)
					return;
				
				string[] changed;
				string[] deleted;
				
				lock(debounceLock) {
					
					changed = [.. pendingChanges];
					deleted = [.. pendingDeletes];
					pendingChanges.Clear();
					pendingDeletes.Clear();
				}
				
				if(isMSBuild)
					FlushMSBuild(changed, deleted);
				
				else {
					Workspace ws;
					ProjectId defId;
					
					@lock.EnterReadLock();
					
					try {
						ws    = workspace;
						defId = defaultProjectId;
					}
					finally {
						@lock.ExitReadLock();
					}
					
					if(ws is AdhocWorkspace adhoc)
						FlushAdhoc(adhoc, defId, changed, deleted);
				}
			}
			catch(Exception) {
				// Swallow — best effort. Next FSW event or explicit InvalidateFile will retry.
			}
		}
		
		void FlushMSBuild(string[] changed, string[] deleted)
		{
			Workspace ws;
			Solution  currentSolution;
			
			@lock.EnterReadLock();
			
			try {
				ws              = workspace;
				currentSolution = workspace.CurrentSolution;
			}
			finally {
				@lock.ExitReadLock();
			}
			
			var newSolution = currentSolution;
			var modified    = false;
			
			// MSBuildWorkspace.TryApplyChanges doesn't support RemoveDocument.
			// Clear the text instead — an empty file produces no diagnostics or types.
			foreach(var path in deleted) {
				
				var docIds = newSolution.GetDocumentIdsWithFilePath(path);
				
				foreach(var id in docIds) {
					
					newSolution = newSolution.WithDocumentText(id, SourceText.From(""));
					modified    = true;
				}
			}
			
			foreach(var path in changed) {
				
				// Skip reload if this is a write RM made itself — workspace is already up to date
				// from the TryApplyChanges call that triggered the FSW event.
				lock(debounceLock) {
					
					if(rmOwnedWriteSizes.TryGetValue(path, out var expected)) {
						
						rmOwnedWriteSizes.Remove(path);
						
						try {
							if(new FileInfo(path).Length == expected)
								continue;
						}
						catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
							// Can't read size — fall through to normal reload.
							_ = ex;
						}
					}
				}
				
				try {
					
					var docIds = newSolution.GetDocumentIdsWithFilePath(path);
					
					// MSBuildWorkspace doesn't support AddDocument via TryApplyChanges —
					// it modifies the .csproj, conflicting with SDK-style implicit includes.
					// Flag for full workspace reload on next tool call.
					if(docIds.Length == 0) {
						
						Interlocked.Increment(ref reloadVersion);
						continue;
					}
					
					using var stream = File.OpenRead(path);
					var text = SourceText.From(stream, FileWriter.Utf8NoBom);
					
					foreach(var id in docIds)
						newSolution = newSolution.WithDocumentText(id, text);
					
					modified = true;
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			}
			
			if(modified && !ApplyChangesWithFswSuppressed(newSolution, currentSolution, ws))
				Interlocked.Increment(ref reloadVersion);
		}
		
		void FlushAdhoc(AdhocWorkspace adhoc, ProjectId projectId, string[] changed, string[] deleted)
		{
			foreach(var path in deleted)
				RemoveDocument(adhoc, projectId, path);
			
			foreach(var path in changed)
				try {
					AddOrUpdateDocument(adhoc, projectId, path);
				}
				catch(FileNotFoundException) {
					
					RemoveDocument(adhoc, projectId, path);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
		
		}
			
			
		// ── Workspace reload ────────────────────────────────────────────────
		
		void ReloadIfNeeded()
		{
			// Fast path — no pending reload.
			var gen = reloadVersion;
			
			if(gen == 0)
				return;
			
			// Load workspace OUTSIDE the write lock — this can take seconds for large solutions
			// and would block every concurrent reader for the duration.
			logger.LogInfo("Reload", $"Reloading workspace ({loadMode}: {loadPath})");
			
			var sw = System.Diagnostics.Stopwatch.StartNew();
			
			Workspace? newWorkspace  = null;
			ProjectId  newProjectId  = defaultProjectId;
			Dictionary<string, ProjectId>? newProjectMap = null;
			
			switch(loadMode) {
				
				case LoadMode.Solution:
					newWorkspace  = LoadSolution(loadPath);
					newProjectMap = BuildProjectMapFor(newWorkspace);
					newProjectId  = newWorkspace.CurrentSolution.Projects.FirstOrDefault()?.Id ?? defaultProjectId;
					break;
				
				case LoadMode.Project:
					var (ws, projectId) = LoadMSBuildWorkspace(loadPath, logger);
					newWorkspace  = ws;
					newProjectId  = projectId;
					newProjectMap = BuildProjectMapFor(newWorkspace);
					break;
				
				default:
					// Adhoc workspaces don't support full reload — just clear the pending flag.
					Interlocked.CompareExchange(ref reloadVersion, 0, gen);
					return;
			}
			
			Workspace? oldWorkspace = null;
			int        projectCount = 0;
			
			@lock.EnterWriteLock();
			
			try {
				
				// Another thread may have loaded while we were outside the lock,
				// or another invalidation arrived — discard our load in both cases.
				// Leave newWorkspace non-null so the finally block disposes it.
				if(reloadVersion != gen)
					return;
				
				oldWorkspace = workspace;
				workspace    = newWorkspace;
				newWorkspace = null;     // ownership transferred; don't dispose in finally
				
				defaultProjectId = newProjectId;
				projectMap.Clear();
				
				foreach(var kvp in newProjectMap!)
					projectMap[kvp.Key] = kvp.Value;
				
				compilationCache.Clear();
				
				// Only clear the version counter if no new invalidation arrived between
				// our load and the write-lock CAS — if one did, we'll reload again next call.
				Interlocked.CompareExchange(ref reloadVersion, 0, gen);
				
				projectCount = workspace.CurrentSolution.Projects.Count();
			}
			finally {
				@lock.ExitWriteLock();
				
				// Dispose and log happen outside the write lock — readers are unblocked first.
				newWorkspace?.Dispose();    // only set if we lost the race
			}
			
			oldWorkspace?.Dispose();
			
			logger.LogInfo("Reload", $"Workspace reloaded in {sw.ElapsedMilliseconds}ms ({projectCount} projects)");
		}
	
	// ── Compilation cache ────────────────────────────────────────────────
		
		Compilation RebuildCompilation(ProjectId projectId, Solution solution, int capturedGen)
		{
			var project = solution.GetProject(projectId)!;
			
			// GetCompilationAsync can take seconds — run outside the lock so concurrent
			// readers are not blocked.
			var compilation = project.GetCompilationAsync().GetAwaiter().GetResult() ?? CSharpCompilation.Create("empty");
			
			// Briefly take the write lock only to cache the result.
			// A concurrent thread may have compiled and stored first — prefer theirs.
			// Only cache if the workspace generation hasn't changed — a concurrent reload
			// clears compilationCache and bumps reloadVersion; storing here would reinsert
			// a stale entry that callers would pick up before the next reload.
			@lock.EnterWriteLock();
			
			try {
				if(compilationCache.TryGetValue(projectId, out var concurrent))
					return concurrent;
				
				if(reloadVersion == capturedGen)
					compilationCache[projectId] = compilation;
				
				return compilation;
			}
			finally {
				@lock.ExitWriteLock();
			}
		}
		
		void InvalidateCompilation()
		{
			@lock.EnterWriteLock();
			
			try {
				compilationCache.Clear();
			}
			finally {
				@lock.ExitWriteLock();
			}
		}
	}
}
