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
		
		// Set when a new file is detected that requires a full workspace reload.
		private volatile bool reloadNeeded;
		
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
					
					BuildProjectMap();
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
					BuildProjectMap();
					
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
		
		void BuildProjectMap()
		{
			foreach(var p in workspace.CurrentSolution.Projects)
				if(p.FilePath is not null)
					projectMap[Path.GetFullPath(p.FilePath)] = p.Id;
		}
		
		// ── Properties ───────────────────────────────────────────────────────
		
		public string              RootPath     => rootPath;
		public bool                IsMSBuild    => isMSBuild;
		public IEnumerable<string> ProjectPaths => projectMap.Keys;
		
		// ── Queries ──────────────────────────────────────────────────────────
		
		public Solution GetSolution()
		{
			ReloadIfNeeded();
			
			return workspace.CurrentSolution;
		}
		
		public Compilation GetCompilation(string? csprojPath = null)
		{
			ReloadIfNeeded();
			var projectId = ResolveProjectId(csprojPath);
			
			@lock.EnterReadLock();
			
			try {
				if(compilationCache.TryGetValue(projectId, out var cached))
					return cached;
			}
			finally {
				@lock.ExitReadLock();
			}
			
			return RebuildCompilation(projectId);
		}
		
		public Project GetProject(string? csprojPath = null)
		{
			ReloadIfNeeded();
			
			var projectId = ResolveProjectId(csprojPath);
			
			return workspace.CurrentSolution.GetProject(projectId)
				?? throw new InvalidOperationException($"Project '{projectId}' not found in current solution.");
		}
		
		ProjectId ResolveProjectId(string? csprojPath)
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
			if(projectMap.ContainsKey(normalizedPath))
				return normalizedPath;
			
			return projectMap.Keys.FirstOrDefault();
		}
		
		/// <summary>Searches all projects for a document matching a file suffix. Returns the .csproj of the first match.</summary>
		public string? FindCsprojForFileSuffix(string suffix, string fileName)
		{
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
		
		// ── File invalidation ────────────────────────────────────────────────
		
		public void InvalidateFile(string fullPath)
		{
			if(isMSBuild) {
				
				var docIds = workspace.CurrentSolution.GetDocumentIdsWithFilePath(fullPath);
				
				if(docIds.Length > 0) {
					
					try {
						
						using var stream = File.OpenRead(fullPath);
						var newText = SourceText.From(stream, FileWriter.Utf8NoBom);
						var newSolution = workspace.CurrentSolution;
						
						foreach(var id in docIds)
							newSolution = newSolution.WithDocumentText(id, newText);
						
						// If TryApplyChanges fails (rare — workspace conflict or unsupported kind),
					// flag for full reload so the next GetCompilation picks up the new content.
					if(!ApplyChangesWithFswSuppressed(newSolution))
						reloadNeeded = true;
					}
					catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
					{ }
				}
				else {
					reloadNeeded = true;
					InvalidateCompilation();
				}
			}
			
			else if(workspace is AdhocWorkspace adhoc)
				try {
					AddOrUpdateDocument(adhoc, defaultProjectId, fullPath);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
				{ }
		}
		
		public void Dispose()
		{
			watcher?.Dispose();
			
			lock(debounceLock) {
				debounceTimer?.Dispose();
				rmOwnedWriteSizes.Clear();
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
			// Collect changed document paths before TryApplyChanges overwrites them
			// (MSBuild only — Adhoc.TryApplyChanges is in-memory only, no disk write).
			// We record expected post-write sizes so FlushMSBuild can skip reloading
			// files that are already up to date from our own writes.
			string[] ownedPaths = [];
			
			if(isMSBuild) {
				
				ownedPaths = newSolution.GetChanges(workspace.CurrentSolution)
					.GetProjectChanges()
					.SelectMany(p => p.GetChangedDocuments())
					.Select(id => newSolution.GetDocument(id)?.FilePath)
					.Where(p => p is not null)
					.Cast<string>()
					.ToArray()
				;
			}
			
			if(watcher is not null)
				watcher.EnableRaisingEvents = false;
			
			bool applied;
			
			try {
				applied = workspace.TryApplyChanges(newSolution);
			}
			finally {
				if(watcher is not null)
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
		internal bool TryApplyTextChange(string fullPath, SourceText text)
		{
			if(!isMSBuild)
				return false;
			
			var docIds = workspace.CurrentSolution.GetDocumentIdsWithFilePath(fullPath);
			
			if(docIds.Length == 0)
				return false;
			
			var newSolution = workspace.CurrentSolution;
			
			foreach(var id in docIds)
				newSolution = newSolution.WithDocumentText(id, text);
			
			return ApplyChangesWithFswSuppressed(newSolution);
		}
		
		// For untracked and Adhoc .cs files: suppresses the per-file FSW event during
		// the write, then calls InvalidateFile to sync in-memory workspace state.
		// No double write — Adhoc.InvalidateFile calls AddOrUpdateDocument (in-memory
		// only); MSBuild-untracked InvalidateFile sets reloadNeeded (no TryApplyChanges).
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
				
				else if(workspace is AdhocWorkspace adhoc)
					FlushAdhoc(adhoc, changed, deleted);
			}
			catch(Exception) {
				// Swallow — best effort. Next FSW event or explicit InvalidateFile will retry.
			}
		}
		
		void FlushMSBuild(string[] changed, string[] deleted)
		{
			var newSolution = workspace.CurrentSolution;
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
						
						reloadNeeded = true;
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
			
			if(modified)
				ApplyChangesWithFswSuppressed(newSolution);
		}
		
		void FlushAdhoc(AdhocWorkspace adhoc, string[] changed, string[] deleted)
		{
			foreach(var path in deleted)
				RemoveDocument(adhoc, defaultProjectId, path);
			
			foreach(var path in changed)
				try {
					AddOrUpdateDocument(adhoc, defaultProjectId, path);
				}
				catch(FileNotFoundException) {
					
					RemoveDocument(adhoc, defaultProjectId, path);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
		
		}
			
			
		// ── Workspace reload ────────────────────────────────────────────────
		
		void ReloadIfNeeded()
		{
			if(!reloadNeeded)
				return;
			
			logger.LogInfo("Reload", $"Reloading workspace ({loadMode}: {loadPath})");
			
			var sw = System.Diagnostics.Stopwatch.StartNew();
			
			@lock.EnterWriteLock();
			
			try {
				
				if(!reloadNeeded)
					return;
				
				var oldWorkspace = workspace;
				
				switch(loadMode) {
					
					case LoadMode.Solution:
						workspace = LoadSolution(loadPath);
						projectMap.Clear();
						BuildProjectMap();
						defaultProjectId = workspace.CurrentSolution.Projects
							.FirstOrDefault()?.Id
							?? defaultProjectId;
						break;
					
					case LoadMode.Project:
						var (ws, projectId) = LoadMSBuildWorkspace(loadPath, logger);
						workspace        = ws;
						defaultProjectId = projectId;
						projectMap.Clear();
						BuildProjectMap();
						break;
					
					default:
						reloadNeeded = false;
						return;
				}
				
				compilationCache.Clear();
				
				reloadNeeded = false;
				
				oldWorkspace.Dispose();
				
				logger.LogInfo("Reload", $"Workspace reloaded in {sw.ElapsedMilliseconds}ms ({workspace.CurrentSolution.Projects.Count()} projects)");
			}
			finally {
				@lock.ExitWriteLock();
			}
		}
	
	// ── Compilation cache ────────────────────────────────────────────────
		
		Compilation RebuildCompilation(ProjectId projectId)
		{
			// Compile outside any lock — GetCompilationAsync can take seconds and
			// holding the write lock for the full duration blocks all concurrent readers.
			var project = workspace.CurrentSolution.GetProject(projectId)!;
			
			var compilation = project.GetCompilationAsync().GetAwaiter().GetResult() ?? CSharpCompilation.Create("empty");
			
			// Briefly take the write lock only to cache the result.
			// A concurrent thread may have compiled and stored first — prefer theirs
			// to avoid caching a redundant result.
			@lock.EnterWriteLock();
			
			try {
				if(compilationCache.TryGetValue(projectId, out var concurrent))
					return concurrent;
				
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
