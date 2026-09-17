using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using System.Collections.Concurrent;
using System.Diagnostics;

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
		private readonly Dictionary<string, ProjectId> projectMap = new(StringComparer.OrdinalIgnoreCase)
		;
		
		// Per-project compilation cache — cleared on any file change.
		private readonly Dictionary<ProjectId, Compilation> compilationCache = new()
		;
		
		// Incremented on each file-system change that requires a reload; cleared after reload completes.
		// Using a generation counter rather than a bool so a change arriving during a load is not lost.
		private volatile int reloadVersion
		;
		
		// Set early in Dispose so the timer callback can exit cleanly before the workspace is torn down.
		private volatile bool disposed
		;
		
		// The path used to load this workspace (solution or csproj), for reloading.
		private readonly string     loadPath
		;
		private readonly LoadMode   loadMode;
		private readonly FileLogger logger;
		
		// Load-health warnings: WorkspaceFailed failures captured during the MSBuild load, plus
		// post-load findings (projects whose metadata references were silently dropped by a
		// contended design-time build). Repopulated on every full load/reload.
		private readonly ConcurrentQueue<string> loadWarnings = new();
		
		/// <summary>Snapshot of load-health warnings, for surfacing in info tools.</summary>
		public string[] LoadWarnings => [..loadWarnings];

		// Most recent unhealthy load, retained across the healing reload that clears loadWarnings.
		// volatile: written by the reload thread, read by tool threads without the lock.
		private volatile string? lastUnhealthyLoad;

		/// <summary>Timestamped record of the last load that produced projects without metadata references.</summary>
		public string? LastUnhealthyLoad => lastUnhealthyLoad;

		// ── Reload scheduling ────────────────────────────────────────────────
		//
		// One loader at a time. Concurrent accessors join the in-flight reload instead of each
		// running their own full load and throwing all but one result away.
		private readonly SemaphoreSlim reloadGate = new(1, 1);

		// Consecutive reloads discarded for dropped references. Drives the deferral backoff and
		// resets to 0 on any successful promotion.
		private int consecutiveDiscards;

		// 1 while a deferred retry is scheduled. Accessors check this and return immediately
		// rather than blocking: the workspace is known to be unable to reload right now, so
		// waiting would burn a full load and still hand back stale text. Staleness is reported by
		// roslyn_check_drift as reload_pending — not as drift, which is a mtime comparison over
		// documents the workspace already has and therefore cannot see a file it has never loaded.
		private int deferredRetryScheduled;

#if NET9_0_OR_GREATER
		private readonly Lock             deferLock = new();
#else
		private readonly object           deferLock = new();
#endif
		private          Timer?           deferredRetryTimer;

		// Up to three loads per attempt, matching FileWriter's default retry count, with the same
		// 50/100 ms backoff between them. Contention is usually held for milliseconds, so an
		// immediate retry tends to hit the same lock — the pause is what makes the retry worth
		// making, and it is negligible against a load measured in seconds.
		private const int ReloadRetries       = 2;
		private const int MaxRetryDelayMs     = 1_000;

		// Upper bound on the deferral. The lower bound is not a constant: it is the measured
		// duration of the attempt that just failed (see ReloadAttempt).
		private const int MaxDeferMs          = 30_000;

		// How long Dispose waits for an in-flight reload before giving up and leaking the
		// instance. Bounded so shutdown and LRU eviction cannot wedge behind a pathological load.
		private static readonly TimeSpan ReloadQuiesceTimeout = TimeSpan.FromSeconds(30);

		/// <summary>
		///     The workspace is behind disk: a change arrived that could not be applied
		///     incrementally, and the reload servicing it has not completed. Drift cannot report
		///     this on its own — a file that is not yet a document is not enumerated by
		///     <c>roslyn_check_drift</c>, and the sync clock says nothing about work still pending.
		/// </summary>
		public bool ReloadPending => reloadVersion != 0;

		/// <summary>
		///     Load-health snapshot: projects the live workspace holds with zero metadata references,
		///     the current load's warnings, the retained last-unhealthy-load record, and whether a
		///     reload is still pending. Peeks — never forces a pending reload.
		/// </summary>
		public WorkspaceHealth Health => new(
			// MSBuild only. Reference health is a property of the design-time build; an
			// AdhocWorkspace project is constructed with no metadata references at all
			// (see LoadAdhocWorkspace), so applying the same check would flag every adhoc
			// workspace as broken.
			isMSBuild ? ProjectsWithoutReferences(PeekSolution()) : [],
			[..loadWarnings],
			lastUnhealthyLoad,
			ReloadPending
		);
		
		// UTC ticks of the last event that synchronized this workspace with disk: initial load,
		// FSW debounce flush, workspace reload, or an RM-owned write. roslyn_check_drift compares
		// on-disk mtimes against this to detect FileSystemWatcher misses.
		private long lastSyncedUtcTicks = DateTime.UtcNow.Ticks;
		
		public DateTime LastSyncedUtc => new(Interlocked.Read(ref lastSyncedUtcTicks), DateTimeKind.Utc);
		
		private void MarkSynced() => Interlocked.Exchange(ref lastSyncedUtcTicks, DateTime.UtcNow.Ticks);
		
		/// <summary>
		///     Current solution snapshot WITHOUT running a pending reload. The drift probe must
		///     observe the workspace as-is — syncing first would hide exactly what it measures.
		/// </summary>
		public Solution PeekSolution()
		{
			@lock.EnterReadLock();
			
			try {
				return workspace.CurrentSolution;
			}
			finally {
				@lock.ExitReadLock();
			}
		}
		
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
		private          Dictionary<string, long> rmOwnedWriteSizes    = new(StringComparer.OrdinalIgnoreCase)
		;
		private const    int                       MaxRmOwnedWriteSizes = 50;
		// Per-file FSW suppression — ref-counted for concurrent-write safety.
		// A path is added before each RM-owned write and decremented in the finally block.
		// ScheduleDebounced skips non-Deleted events where the count is > 0 (Deleted events
		// use ownedDeletePaths below instead — they are intentionally excluded here).
		private readonly ConcurrentDictionary<string, int> ignoredPaths    = new(StringComparer.OrdinalIgnoreCase)
		;
		// Per-path owned-delete suppression for file rename operations. When RM moves a file
		// (old → new), the FSW fires Deleted(oldPath) which is excluded from ignoredPaths by
		// design. This set tracks the old paths whose delete events should be consumed rather
		// than dispatched. Ref-counted for concurrent-rename safety; consumed on first match in
		// ScheduleDebounced.
		private readonly ConcurrentDictionary<string, int> ownedDeletePaths = new(StringComparer.OrdinalIgnoreCase)
		;
		private          Timer?           debounceTimer;
		private const    int              DebounceMs     = 300;
		// Ref-counted FSW suppression — multiple concurrent ApplyChangesWithFswSuppressed
		// calls each increment on entry and decrement on exit; EnableRaisingEvents is only
		// restored when the last suppressor finishes (count returns to 0). See issue #145 item 3.
		private          int              fswSuppressCount
		;
		
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
					workspace = LoadSolution(path, logger, loadWarnings);
					
					// Contended design-time builds can silently drop a project's references —
					// results would be wrong, not failed. One fresh reload usually clears it.
					if(ProjectsWithoutReferences(workspace) is { Length: > 0 } droppedSln) {
						
						logger.LogInfo("Load", $"No metadata references on: {string.Join(", ", droppedSln)} — reloading once");
						workspace.Dispose();
						loadWarnings.Clear();
						workspace = LoadSolution(path, logger, loadWarnings);
					}
					
					WarnIfReferencesDropped(workspace);
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
					
					var (msbuildWs, projectId) = LoadMSBuildWorkspace(path, logger, loadWarnings);
					
					if(ProjectsWithoutReferences(msbuildWs) is { Length: > 0 } droppedProj) {
						
						logger.LogInfo("Load", $"No metadata references on: {string.Join(", ", droppedProj)} — reloading once");
						msbuildWs.Dispose();
						loadWarnings.Clear();
						(msbuildWs, projectId) = LoadMSBuildWorkspace(path, logger, loadWarnings);
					}
					
					WarnIfReferencesDropped(msbuildWs);
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
					
					// Deliberately no AutoDetectAndBootstrap/EnsureReady here: adhoc needs no
					// MSBuild, and EnsureReady is one-shot process-global — spending it on
					// "register nothing" would permanently lock MSBuild out, crashing a later
					// load of a different repo whose effective mode is Sdk/Vs (#229/#220).
					// Consequences: ResolvedMode stays Auto (routing in GetOrLoadInstance
					// checks the requested effective mode instead) and roslyn_info reports
					// MSBuild discovery as "not attempted", which is accurate.
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
			
			MarkSynced();
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
			@lock.EnterReadLock()
			;
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
			var fileName = Path.GetFileName(csprojPath)
			;
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
						var newSolution = currentSolution
						;
						
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
			var started = Stopwatch.GetTimestamp();
			
			// Signal FlushPendingChanges to bail early on any in-flight or pending callbacks.
			disposed = true
			;
			
			watcher?.Dispose();
			
			// Quiesce the timer: wait for any in-flight callback to complete before
			// tearing down @lock and workspace, which the callback accesses.
			Timer? timerToQuiesce
			;
			
			lock(debounceLock) {
				
				timerToQuiesce = debounceTimer;
				debounceTimer  = null;
				rmOwnedWriteSizes.Clear();
			}
			
			if(!QuiesceTimer(timerToQuiesce, "Debounce callback"))
				
				return;

			// Same treatment for the deferred reload retry — its callback touches @lock and
			// workspace, so it must be off the thread before either is torn down.
			Timer? deferToQuiesce
			;

			lock(deferLock) {

				deferToQuiesce     = deferredRetryTimer;
				deferredRetryTimer = null;
			}

			if(!QuiesceTimer(deferToQuiesce, "Deferred reload retry"))
				
				return;

			// Wait out an in-flight reload rather than pulling @lock and workspace from under it.
			// Bounded so a pathological load cannot wedge cache eviction or shutdown.
			//
			// On timeout, skip teardown entirely. A reload that is still running holds references
			// to @lock, workspace and reloadGate; disposing them under it converts a slow load into
			// an ObjectDisposedException on another thread — and @lock.Dispose() while a writer is
			// inside it is worse than that. Leaking one instance is the strictly safer outcome:
			// the process is either shutting down, or evicting a single LRU cache entry.
			if(!reloadGate.Wait(ReloadQuiesceTimeout)) {

				logger.LogError(
					"Dispose",
					$"Reload still in flight after {ReloadQuiesceTimeout.TotalSeconds:0}s for '{loadPath}' — "
					+ "skipping teardown rather than disposing state it is still using."
				);

				return;
			}

			// Deliberately not released: nothing may acquire the gate between here and Dispose.
			reloadGate.Dispose();

			@lock.Dispose();
			workspace.Dispose();
			
			// One line per teardown — eviction is rare — and the line a stalled watcher.Dispose()
			// or workspace.Dispose() would be missing from the log (#277).
			logger.LogInfo("Dispose", $"Released '{loadPath}' in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms");
		}
		
		/// <summary>
		///     Disposes <paramref name="timer"/> and waits, bounded by <see cref="ReloadQuiesceTimeout"/>,
		///     for an in-flight callback to leave. Returns false when it did not — the caller must
		///     then skip teardown, for the same reason as the reload branch in <see cref="Dispose"/>:
		///     the callback still holds @lock and workspace. Both waits used to be unbounded and ran
		///     under WorkspaceManager.cacheLock, so one wedged callback stopped every tool call in
		///     the process (#277).
		/// </summary>
		bool QuiesceTimer(Timer? timer, string what)
		{
			if(timer is null)
				
				return true;
			
			var done = new ManualResetEvent(false);
			
			// False means the timer was already disposed: nothing will ever signal the handle.
			if(!timer.Dispose(done)) {
				
				done.Dispose();
				
				return true;
			}
			
			if(done.WaitOne(ReloadQuiesceTimeout)) {
				
				done.Dispose();
				
				return true;
			}
			
			// Deliberately leaked on this path: the timer signals the handle when the callback
			// finally leaves, and a disposed handle would turn that into an ObjectDisposedException
			// on a pool thread.
			logger.LogError(
				"Dispose",
				$"{what} still running after {ReloadQuiesceTimeout.TotalSeconds:0}s for '{loadPath}' — "
				+ "skipping teardown rather than disposing state it is still using."
			);
			
			return false;
		}
		
		// ── Workspace loading ────────────────────────────────────────────────
		
		
		/// <summary>
		///     When workspace mode is Auto, peeks at the project to detect SDK vs Framework style,
		///     then calls EnsureReady with the detected mode. Logs the result.
		/// </summary>
		static void AutoDetectAndBootstrap(string path, FileLogger logger)
		{
			// Start from the user's explicit choice (CLI arg or env var), falling back to a
			// committed project-local config file; auto-detect only if neither specifies a mode.
			var mode = ProjectConfig.EffectiveWorkspaceMode(path, logger);
			
			if(mode != ServerArgs.Current.WorkspaceMode)
				logger.LogInfo("Workspace", $"mode={mode} (from {ProjectConfig.FileName})");
			
			// Same precedence for the Visual Studio version pin. It steers the .NET Framework
			// BuildHost in every mode but adhoc — see MSBuildBootstrap.EnsureReady.
			var vsVersion = ProjectConfig.EffectiveVsVersion(path, logger);
			
			if(vsVersion is not null && !ServerArgs.Current.VsVersionSpecified)
				logger.LogInfo("Workspace", $"vsVersion={vsVersion} (from {ProjectConfig.FileName})");
			
			if(mode == WorkspaceMode.Auto) {
				
				// A solution is judged by the projects it references — the first .csproj the file
				// system happens to enumerate may be a stale copy that is not even in the solution.
				var (detected, detail) = MSBuildBootstrap.DetectLoadStyle(path);
				
				logger.LogInfo("Workspace", $"auto-detected {detected}: {detail}");
				
				mode = detected;
			}
			
			WarnIfLargeSolution(path, mode, logger);
			
			var bootstrapFailure = MSBuildBootstrap.EnsureReady(mode, vsVersion);
			
			if(bootstrapFailure is not null)
				throw new InvalidOperationException($"MSBuild initialization failed: {bootstrapFailure}");
			
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
		
		
		/// <summary>
		///     Cancellation source bounding a single MSBuild workspace load. Fires after
		///     <see cref="ServerArgs.LoadTimeoutSeconds"/>; never fires when the timeout is disabled.
		/// </summary>
		static CancellationTokenSource CreateLoadTimeoutCts()
		{
			var cts     = new CancellationTokenSource();
			var seconds = ServerArgs.Current.LoadTimeoutSeconds;
			
			if(seconds > 0)
				cts.CancelAfter(TimeSpan.FromSeconds(seconds));
			
			return cts;
		}
		
		static InvalidOperationException LoadTimeout(string path) =>
			new(
				$"Loading '{path}' timed out after {ServerArgs.Current.LoadTimeoutSeconds}s. " +
				"The workspace may be too large or MSBuild may be stuck. Try loading a single .csproj " +
				"instead of the full solution, or raise ROSLYNMCP_LOAD_TIMEOUT_SECONDS.");
		
		/// <summary>
		///     Projects whose design-time build produced zero metadata references. A successfully
		///     built project always references at least the core library, so an empty set means the
		///     build silently dropped them and symbol queries over that project would return
		///     wrong-but-plausible results. The BuildHost-contention cause and the retry-on-fresh-
		///     workspace mitigation are undocumented MSBuild behavior, documented empirically by
		///     MarcelRoozekrans/roslyn-codelens-mcp (no code reused).
		/// </summary>
		static string[] ProjectsWithoutReferences(Solution solution) =>
			[..solution.Projects
				.Where(p => p.MetadataReferences.Count == 0)
				.Select(p => p.Name)
				.Distinct()];

		static string[] ProjectsWithoutReferences(Workspace ws) => ProjectsWithoutReferences(ws.CurrentSolution);

		void WarnIfReferencesDropped(Workspace ws)
		{
			if(ProjectsWithoutReferences(ws) is { Length: > 0 } dropped)
				RecordUnhealthyLoad($"Projects loaded without metadata references — symbol results may be incomplete: {string.Join(", ", dropped)}");
		}

		/// <summary>
		///     Records a load-health finding in both the per-load queue and the retained
		///     <see cref="LastUnhealthyLoad"/> slot. The queue is cleared by every subsequent load,
		///     so without the retained copy the evidence disappears at exactly the moment a healthy
		///     reload fixes the symptom — leaving the episode undiagnosable (issue #235).
		/// </summary>
		void RecordUnhealthyLoad(string message)
		{
			loadWarnings.Enqueue(message);
			lastUnhealthyLoad = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z] {message}";
		}
		
		static Workspace LoadSolution(string solutionPath, FileLogger? log, ConcurrentQueue<string> warnings)
		{
			using var loadCts = CreateLoadTimeoutCts();
			
			var msbuildWorkspace = MSBuildWorkspace.Create();
			msbuildWorkspace.RegisterWorkspaceFailedHandler(e =>
			{
				var level = e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure ? "ERROR" : "WARN";
				log?.LogInfo("WorkspaceFailed", $"[{level}] {e.Diagnostic.Message}");
				
				if(e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
					warnings.Enqueue(e.Diagnostic.Message);
			}, options: null);
			
			
			try {
				
				if(solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)) {
					
					// .slnx — parse XML and load each project into the same workspace.
					var doc    = System.Xml.Linq.XDocument.Load(solutionPath)
					;
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
							msbuildWorkspace.OpenProjectAsync(projectPath, cancellationToken: loadCts.Token).GetAwaiter().GetResult();
						}
						catch(Exception ex) when(ex is not OperationCanceledException) {
							// Multi-TFM projects or transitive references may already be loaded
							// by a previous OpenProjectAsync call. Log and skip.
							log?.LogInfo("Load", $"Skipped {Path.GetFileName(projectPath)}: {ex.GetType().Name}: {ex.Message}")
							;
						}
					}
				}
				
				else
					msbuildWorkspace.OpenSolutionAsync(solutionPath, cancellationToken: loadCts.Token).GetAwaiter().GetResult();
				
				return msbuildWorkspace;
			}
			catch(OperationCanceledException) when(loadCts.IsCancellationRequested) {
				
				log?.LogError("LoadSolution", $"Load timed out after {ServerArgs.Current.LoadTimeoutSeconds}s: {solutionPath}");
				msbuildWorkspace.Dispose();
				throw LoadTimeout(solutionPath);
			}
			catch(Exception ex) when(ex is not OperationCanceledException) {
				
				log?.LogError("LoadSolution", $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
				msbuildWorkspace.Dispose();
				throw new InvalidOperationException($"Failed to load solution '{solutionPath}': {ex.Message}", ex);
			}
		}
		
		static (Workspace workspace, ProjectId projectId) LoadMSBuildWorkspace(string csprojPath, FileLogger? log, ConcurrentQueue<string> warnings)
		{
			using var loadCts = CreateLoadTimeoutCts();
			
			var msbuildWorkspace = MSBuildWorkspace.Create();
			
			msbuildWorkspace.RegisterWorkspaceFailedHandler(e =>
			{
				var level = e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure ? "ERROR" : "WARN";
				log?.LogInfo("WorkspaceFailed", $"[{level}] {e.Diagnostic.Message}");
				
				if(e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
					warnings.Enqueue(e.Diagnostic.Message);
			}, options: null);
			
			try {
				
				var project = msbuildWorkspace.OpenProjectAsync(csprojPath, cancellationToken: loadCts.Token).GetAwaiter().GetResult();
				
				return (msbuildWorkspace, project.Id);
			}
			catch(OperationCanceledException) when(loadCts.IsCancellationRequested) {
				msbuildWorkspace.Dispose();
				throw LoadTimeout(csprojPath);
			}
			catch(Exception ex) when(ex is not OperationCanceledException) {
				msbuildWorkspace.Dispose();
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
				   IsExcludedDirectoryName(dirInfo.Name))
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
			var name = Path.GetRelativePath(rootPath, path)
			;
			
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
		internal void MarkReloadNeeded() => Interlocked.Increment(ref reloadVersion)
		;
		
		
		
		// Carries the caller's immutable Solution snapshot for diff computation and the expected
		// Workspace reference for a best-effort staleness check before TryApplyChanges.
		// A very narrow race between the staleness check and TryApplyChanges still exists, but
		// TryApplyChanges returns false gracefully on a disposed workspace — so this is safe.
		internal bool ApplyChangesWithFswSuppressed(Solution newSolution, Solution baseSolution, Workspace expectedWs)
		{
			// Best-effort staleness guard: if another thread completed a reload and replaced
			// the workspace since the caller snapshotted it, discard rather than apply stale edits.
			@lock.EnterReadLock()
			;
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
			string[] ownedPaths = []
			;
			
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
			catch(Exception ex) when(ex is ObjectDisposedException or InvalidOperationException or NotSupportedException) {
				// ObjectDisposedException/InvalidOperationException: workspace disposed by a concurrent
				// ReloadIfNeeded between the staleness check and TryApplyChanges.
				// NotSupportedException: TryApplyChanges throws for unsupported change kinds
				// (e.g. AddDocument/RemoveDocument on MSBuildWorkspace). Both treated as false return.
				_ = ex
				;
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
							_ = ex
							;
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
		
		
		// ── FSW-safe write entry point ────────────────────────────────────────
		//
		// ⚠ THIS CODE IS THE RESULT OF EXTENSIVE STAGED ANALYSIS (issue #179, Stages 1–5).
		//   DO NOT CHANGE WriteAndInvalidate, ScheduleDebounced, ownedDeletePaths, or
		//   the InvalidateFile placement without first reading:
		//   docs/development/WORKSPACE_SYNC.md
		//
		//   The ordering of InvalidateFile vs the finally-decrement is not arbitrary.
		//   The ownedDeletePaths pattern for rename suppression is not redundant.
		//   The FSW Deleted/Created split is intentional. Change any of it carefully.
		//
		// Forwarding overload — non-rename writes have no movedFromPath to suppress.
		internal Task WriteAndInvalidate(string fullPath, Func<Task> write)
			=> WriteAndInvalidate(fullPath, null, write);
		
		// movedFromPath: for file renames, the old path whose FSW Deleted event should be consumed
		// and not trigger a full reload — the new path's InvalidateFile handles workspace sync.
		internal async Task WriteAndInvalidate(string fullPath, string? movedFromPath, Func<Task> write)
		{
			ignoredPaths.AddOrUpdate(fullPath, 1, (_, count) => count + 1);
			
			if(movedFromPath != null)
				ownedDeletePaths.AddOrUpdate(movedFromPath, 1, (_, count) => count + 1);
			
			try {
				
				await write();
				
				// Bug #3 fix: InvalidateFile must run while ignoredPaths is still incremented.
				// Moving it after the finally decrement opened a race window where a late FSW
				// event fired unsuppressed between the decrement and the workspace invalidation.
				InvalidateFile(fullPath)
				;
				MarkSynced();
			}
			finally {
				ignoredPaths.AddOrUpdate(fullPath, 0, (_, count) => count - 1);
			}
		}
		
		
		/// <summary>
		///     Directories not worth walking or reloading for. Two distinct uses, and the
		///     <c>bin</c>/<c>obj</c> entries mean something different in each:
		///     <list type="bullet">
		///         <item>
		///             Adhoc enumeration skips them entirely when discovering source files.
		///         </item>
		///         <item>
		///             The MSBuild flush uses it only to decide that an <em>unknown</em>
		///             <c>.cs</c> under one of them is a build artifact, not new source, and so
		///             must not force a full reload. Generated files under <c>obj</c> that really
		///             are compilation inputs (<c>*.AssemblyInfo.cs</c>,
		///             <c>*.GlobalUsings.g.cs</c>) already have document IDs, never reach that
		///             branch, and keep receiving ordinary text updates.
		///         </item>
		///     </list>
		///     For the watcher's pre-queue filter — which must not suppress those generated
		///     documents — see <see cref="IsNeverCompilationInput"/>.
		/// </summary>
		static bool IsExcludedDirectoryName(string name) =>
			name is "node_modules" or "bin" or "obj" or ".git" or ".vs" or "packages";

		/// <summary>
		///     Directories that can never hold a compilation document, so a change under one is
		///     safe to drop before it is even queued. Deliberately excludes <c>bin</c>/<c>obj</c>:
		///     SDK-style projects put generated documents there (<c>*.AssemblyInfo.cs</c>,
		///     <c>*.GlobalUsings.g.cs</c>) which are real compilation inputs and must still receive
		///     text updates. Build output is filtered later instead — only from the decision to
		///     force a full reload. See <see cref="IsUnderExcludedDirectory"/>.
		/// </summary>
		static bool IsNeverCompilationInput(string name) =>
			name is "node_modules" or ".git" or ".vs" or "packages";

		/// <summary>
		///     True when any directory segment of <paramref name="fullPath"/> below
		///     <see cref="rootPath"/> matches <paramref name="excluded"/>. Name-based only — no
		///     <c>FileInfo</c> stat, because this runs on every FileSystemWatcher event.
		/// </summary>
		bool IsUnderExcludedDirectory(string fullPath, Func<string, bool> excluded)
		{
			string relative;

			try {
				relative = Path.GetRelativePath(rootPath, fullPath);
			}
			catch(ArgumentException) {
				return false;
			}

			var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
			;

			// Outside the root entirely — not ours to filter. The first segment must equal ".."
			// exactly: a prefix test would also catch a directory legitimately named "..config",
			// which is inside the root and must still be subject to the exclusions below. A rooted
			// result means GetRelativePath could not relativize at all (different volume).
			if(Path.IsPathRooted(relative) || segments[0] == "..")

				return false;

			// Last segment is the filename, not a directory.
			for(var i = 0; i < segments.Length - 1; i++)

				if(excluded(segments[i]))

					return true;

			return false;
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
			// Nothing under these can be a compilation document, so drop the event before it is
			// queued. Without this, a stray .cs anywhere below the root — a package cache, a
			// sample in .git, anything — reaches FlushMSBuild as an unknown document and forces a
			// full workspace reload, which is both slow and the operation that can drop metadata
			// references (issue #235).
			if(IsUnderExcludedDirectory(fullPath, IsNeverCompilationInput))

				return;

			// Skip FSW events for paths RM is currently writing — prevents spurious workspace
			// reloads from our own writes. Ref-counted for safety; normal usage is single-threaded.
			if(!deleted && ignoredPaths.TryGetValue(fullPath, out var count) && count > 0)
				
				return;
			
			// Consume owned-delete entry for file rename operations — the delete of the old path
			// is expected and should not trigger a reload (the rename's WriteAndInvalidate call
			// handles the workspace sync for the new path). See ownedDeletePaths field comment.
			if(deleted && ownedDeletePaths.TryGetValue(fullPath, out var delCount) && delCount > 0) {
				
				ownedDeletePaths.AddOrUpdate(fullPath, 0, (_, c) => Math.Max(0, c - 1));
				
				return;
			}
			
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
				
				// True when the flush could only flag a reload. The workspace is then behind disk
				// until that reload completes, so the sync clock must not advance — otherwise
				// roslyn_check_drift reports "in sync" for changes it has not applied, and a
				// reload that is later discarded becomes invisible.
				var reloadFlagged = false;

				if(isMSBuild)
					reloadFlagged = FlushMSBuild(changed, deleted);

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

				if(!reloadFlagged)
					MarkSynced();
			}
			catch(Exception) {
				// Swallow — best effort. Next FSW event or explicit InvalidateFile will retry.
			}
		}
		
		/// <summary>
		///     Applies a debounced batch of file changes to an MSBuild workspace.
		///     Returns <see langword="true"/> when it could only flag a full reload rather than
		///     apply the change — meaning the workspace is now behind disk, and the caller must
		///     not advance the sync clock.
		/// </summary>
		bool FlushMSBuild(string[] changed, string[] deleted)
		{
			// Set when this batch left the workspace behind disk.
			var reloadFlagged = false;

			// One generation bump per flush batch. Each bump moves reloadVersion, and an in-flight
			// reload discards its work when the generation no longer matches the one it captured —
			// so deleting several documents used to throw away a full load per extra file.
			// The inner guard is pre-existing: InvalidateFile may already have flagged this same
			// change, and re-flagging it would discard the reload that is servicing it.
			void FlagReload()
			{
				if(reloadFlagged)

					return;

				reloadFlagged = true;

				#pragma warning disable CS0420 // A reference to a volatile field will not be treated as volatile
				if(Volatile.Read(ref reloadVersion) == 0)
					Interlocked.Increment(ref reloadVersion);
				#pragma warning restore CS0420
			}

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
			
			// MSBuildWorkspace.TryApplyChanges doesn't support RemoveDocument, and clearing
			// document text then calling TryApplyChanges writes an empty file back to disk,
			// silently recreating the deleted file as a zero-byte ghost.
			// Flag for full workspace reload instead — symmetric with the new-file case below.
			// Flag once, not once per file: every increment moves the generation, and an in-flight
			// reload discards its work when reloadVersion no longer matches the gen it captured —
			// so deleting several files used to throw away one full load per extra file.
			foreach(var path in deleted) {

				if(newSolution.GetDocumentIdsWithFilePath(path).Length == 0)
					continue;

				FlagReload();

				break;
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
							_ = ex
							;
						}
					}
				}
				
				try {
					
					var docIds = newSolution.GetDocumentIdsWithFilePath(path);
					
					// MSBuildWorkspace doesn't support AddDocument via TryApplyChanges —
					// it modifies the .csproj, conflicting with SDK-style implicit includes.
					// Flag for full workspace reload on next tool call.
					if(docIds.Length == 0) {

						// ...unless it is build output. A .cs under bin/ or obj/ that is not
						// already a document is a compiler artifact, not source: reloading the
						// whole workspace for it is pure cost. Generated documents that ARE
						// compilation inputs (*.AssemblyInfo.cs, *.GlobalUsings.g.cs) have
						// docIds and never reach this branch, so they keep updating normally.
						if(IsUnderExcludedDirectory(path, IsExcludedDirectoryName))
							continue;

						FlagReload();

						continue;
					}

					using var stream = File.OpenRead(path);
					var text = SourceText.From(stream, FileWriter.Utf8NoBom);

					// Already current. Applying identical text is not free: it still runs a
					// TryApplyChanges, which writes to disk and flags a full reload if it fails.
					// This catches what the rmOwnedWriteSizes check above cannot — an edit that
					// leaves the file the same length, and any write RM did not make itself.
					// TryGetText deliberately: it reads already-materialized text rather than
					// forcing a load, and falls through to apply when none is available.
					if(newSolution.GetDocument(docIds[0]) is { } existingDoc
					   && existingDoc.TryGetText(out var existingText)
					   && existingText.ContentEquals(text))

						continue;

					foreach(var id in docIds)
						newSolution = newSolution.WithDocumentText(id, text);

					modified = true;
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			}
			
			if(modified && !ApplyChangesWithFswSuppressed(newSolution, currentSolution, ws))
				FlagReload();

			return reloadFlagged;
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
		
		/// <summary>
		///     Loads a fresh workspace for this instance's load mode, resetting the per-load warning
		///     queue first. Never called for <see cref="LoadMode.Adhoc"/>, which cannot reload.
		/// </summary>
		(Workspace Workspace, ProjectId ProjectId) LoadForMode()
		{
			loadWarnings.Clear();

			if(loadMode is LoadMode.Solution) {

				var solutionWorkspace = LoadSolution(loadPath, logger, loadWarnings)
				;

				return (
					solutionWorkspace,
					solutionWorkspace.CurrentSolution.Projects.FirstOrDefault()?.Id ?? defaultProjectId
				);
			}

			return LoadMSBuildWorkspace(loadPath, logger, loadWarnings);
		}

		void ReloadIfNeeded()
		{
			// Fast path — no pending reload.
			var gen = reloadVersion
			;

			if(gen == 0)

				return;

			if(loadMode is LoadMode.Adhoc) {

				// Adhoc workspaces don't support full reload — just clear the pending flag.
				Interlocked.CompareExchange(ref reloadVersion, 0, gen)
				;

				return;
			}

			// A retry is already scheduled: the last attempt could not resolve references, so
			// blocking here would pay a full load to arrive at the same stale answer.
			if(Volatile.Read(ref deferredRetryScheduled) != 0)

				return;

			// Single-flight. Waiting is the point — the caller needs the reloaded workspace, and
			// joining one load beats running a duplicate.
			reloadGate.Wait();

			try {

				// Re-read under the gate: another caller may have completed the reload while we
				// waited, or scheduled a deferral.
				if(reloadVersion == 0 || Volatile.Read(ref deferredRetryScheduled) != 0 || disposed)

					return;

				ReloadAttempt();
			}
			finally {
				reloadGate.Release();
			}
		}
	
		/// <summary>
		///     Fires after the deferral backoff. Runs on a timer thread, so it must never throw.
		///     <para>
		///         It does run a full workspace load, which takes seconds — what it must not do is
		///         <em>wait</em> on <see cref="reloadGate"/>. A held gate means a foreground reload
		///         is already doing this work, so the callback bails via <c>Wait(0)</c> instead of
		///         queueing a second attempt behind it.
		///     </para>
		/// </summary>
		void DeferredRetryCallback()
		{
			if(disposed)

				return;

			if(!reloadGate.Wait(0)) {

				// A foreground reload is in flight and will settle the pending generation.
				Volatile.Write(ref deferredRetryScheduled, 0);

				return;
			}

			try {

				// Cleared before the attempt so a fresh discard can schedule the next retry.
				Volatile.Write(ref deferredRetryScheduled, 0);

				if(disposed || reloadVersion == 0)

					return;

				ReloadAttempt();
			}
			catch(Exception ex) {
				// Nothing above this on a timer thread — an escape would take the process down.
				logger.LogError("Reload", $"Deferred reload retry failed: {ex.GetType().Name}: {ex.Message}");
			}
			finally {
				reloadGate.Release();
			}
		}

		/// <summary>
		///     Schedules the next retry. The delay is seeded with the measured cost of the attempt
		///     that just failed, so a retry never runs more often than it costs — bounding retry
		///     work to roughly a 50% duty cycle whether a load takes one second or twenty — and
		///     doubling while contention persists.
		/// </summary>
		void ScheduleDeferredRetry(int delayMs)
		{
			lock(deferLock) {

				if(disposed)

					return;

				Volatile.Write(ref deferredRetryScheduled, 1);

				deferredRetryTimer?.Dispose();
				deferredRetryTimer = new Timer(_ => DeferredRetryCallback(), null, delayMs, Timeout.Infinite);
			}
		}

		/// <summary>
		///     One reload attempt: load, retry on dropped references, then either promote or
		///     discard and defer. Callers must hold <see cref="reloadGate"/>.
		/// </summary>
		void ReloadAttempt()
		{
			var gen = reloadVersion
			;

			// Load workspace OUTSIDE the write lock — this can take seconds for large solutions
			// and would block every concurrent reader for the duration.
			logger.LogInfo("Reload", $"Reloading workspace ({loadMode}: {loadPath})")
			;

			var sw = System.Diagnostics.Stopwatch.StartNew();

			// Nullable so the write-lock block can hand off ownership by nulling it out.
			Workspace? newWorkspace;
			ProjectId  newProjectId;

			(newWorkspace, newProjectId) = LoadForMode();

			// Contended design-time builds can silently drop a project's references — results
			// would be wrong, not failed. The initial load retries the same way; the reload path
			// is the likelier victim, since it is often triggered by the very file writes that
			// cause the contention.
			var retry = 0;

			while(retry < ReloadRetries
			      && ProjectsWithoutReferences(newWorkspace) is { Length: > 0 } dropped) {

				var retryDelayMs = Backoff.DelayMs(retry, Backoff.DefaultSeedMs, MaxRetryDelayMs)
				;

				logger.LogInfo("Reload", $"No metadata references on: {string.Join(", ", dropped)} — attempt={retry + 2} delay_ms={retryDelayMs}");

				newWorkspace.Dispose();
				Thread.Sleep(retryDelayMs);

				(newWorkspace, newProjectId) = LoadForMode();
				retry++;
			}

			// Never replace a healthy workspace with a reference-less one. A stale-but-correct
			// compilation beats a fresh-but-wrong one: dropped references produce plausible-looking
			// symbol results rather than visible failures, so promoting this would silently corrupt
			// every subsequent query. If the live workspace is equally broken there is nothing to
			// protect, so promote regardless — otherwise a bad first load could never recover.
			if(ProjectsWithoutReferences(newWorkspace) is { Length: > 0 } stillDropped
			   && ProjectsWithoutReferences(PeekSolution()).Length == 0) {

				newWorkspace.Dispose();

				// Seed = what this attempt actually cost, so the retry rate self-scales to the
				// workspace instead of relying on a constant that fits neither big nor small.
				var deferMs = Backoff.DelayMs(
					consecutiveDiscards,
					(int) Math.Min(sw.ElapsedMilliseconds, MaxDeferMs),
					MaxDeferMs
				);

				consecutiveDiscards++;

				RecordUnhealthyLoad(
					$"Reload discarded — the reloaded workspace had no metadata references on: {string.Join(", ", stillDropped)}. "
					+ "Keeping the previous workspace, whose text may now be one edit stale. "
					+ "Call roslyn_respawn if symbol results look outdated."
				);

				logger.LogInfo("Reload", $"Discarded reload — still no metadata references on: {string.Join(", ", stillDropped)}; keeping previous workspace, retry attempt={consecutiveDiscards} delay_ms={deferMs}");

				// reloadVersion stays set: the deferred retry picks up the newest generation.
				ScheduleDeferredRetry(deferMs);

				return;
			}

			WarnIfReferencesDropped(newWorkspace);
			consecutiveDiscards = 0;

			var newProjectMap = BuildProjectMapFor(newWorkspace);

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

				foreach(var kvp in newProjectMap)
					projectMap[kvp.Key] = kvp.Value;

				compilationCache.Clear();

				// Only clear the version counter if no new invalidation arrived between
				// our load and the write-lock CAS — if one did, we'll reload again next call.
				Interlocked.CompareExchange(ref reloadVersion, 0, gen)
				;

				projectCount = workspace.CurrentSolution.Projects.Count();
			}
			finally {

				@lock.ExitWriteLock();

				// Dispose and log happen outside the write lock — readers are unblocked first.
				newWorkspace?.Dispose();    // only set if we lost the race
			}

			oldWorkspace?.Dispose();

			MarkSynced();
			logger.LogInfo("Reload", $"Workspace reloaded in {sw.ElapsedMilliseconds}ms ({projectCount} projects)");
		}


	// ── Compilation cache ────────────────────────────────────────────────
		
		Compilation RebuildCompilation(ProjectId projectId, Solution solution, int capturedGen)
		{
			var project = solution.GetProject(projectId)!;
			
			// GetCompilationAsync can take seconds — run outside the lock so concurrent
			// readers are not blocked.
			var compilation = project.GetCompilationAsync().GetAwaiter().GetResult() ?? CSharpCompilation.Create("empty")
			;
			
			// Briefly take the write lock only to cache the result.
			// A concurrent thread may have compiled and stored first — prefer theirs.
			// Only cache if the workspace generation hasn't changed — a concurrent reload
			// clears compilationCache and bumps reloadVersion; storing here would reinsert
			// a stale entry that callers would pick up before the next reload.
			@lock.EnterWriteLock()
			;
			
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
