using Microsoft.CodeAnalysis;
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
	sealed partial class WorkspaceInstance : IDisposable
	{
		enum LoadMode { Solution, Project, Adhoc }
		
		private Workspace            workspace;
		private readonly string      rootPath;
		private readonly bool        isMSBuild;
		private ProjectId            defaultProjectId;
		private readonly ReaderWriterLockSlim @lock = new();
		private          WorkspaceWatchSet?   watchSet;
		
		// Which directories this workspace stays out of. Fixed for the life of the instance, like
		// the rest of the project configuration: read once the root is known, before any walk.
		private          IgnoreRules          ignoreRules = IgnoreRules.BuiltIn;
		
		// Two parts of the work that need none of this instance's state, only what is fixed at
		// construction, and are nested types for that reason: what a changed path means for the
		// workspace, and where a path leads when links are followed. Each is given what it
		// needs and cannot reach back into the instance.
		private readonly EditClassifier       edits;
		private readonly LinkResolver         links;
		
		// When construction began. Changes made on disk after this and before the watchers are
		// live produced no event; ReconcileMissedChanges picks them up by timestamp.
		private readonly DateTime             constructedUtc = DateTime.UtcNow;
		
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
		// Files RM wrote through a symbolic link that are sources no project compiles, with the
		// size and write time they were left with (#338). The watcher reports such a write at
		// the link's target, any number of times and on its own schedule; FlushMSBuild drops
		// those reports for as long as the file still has this stamp, and forgets the entry the
		// moment it does not — someone else has written the file since. Guarded by debounceLock.
		// An entry also lapses on its own after OwnedLinkTargetWindow: long enough for any report
		// of the write to arrive, short enough that a stamp cannot go on vouching for a file
		// that someone has since rewritten to the same length and the same write time.
		// Keyed the way the file system compares names: on Linux Foo.cs and foo.cs are two files
		// and must not share a stamp.
		private readonly Dictionary<string, (long Length, DateTime WriteUtc, DateTime UntilUtc)> ownedLinkTargets = new(SecurityBoundary.PathComparer)
		;
		private static readonly TimeSpan OwnedLinkTargetWindow = TimeSpan.FromSeconds(30)
		;
		// The tracked documents that are another file's name (#341). In an MSBuild workspace it
		// is built by a load and swapped in with the solution it was built from, under the
		// workspace write lock, and never changed in between (see FlagLinkChanged for what
		// that leaves open). An adhoc workspace never reloads and updates it in place,
		// under indexLock. Either way an instance is immutable and read without a lock.
		private volatile PhysicalFileIndex physicalIndex = PhysicalFileIndex.Empty;
		private readonly object            indexLock     = new();
		// Serialises SyncAdhocDocument: it looks whether a file is already a document under
		// another name and then adds one, and a tool's write and the watcher flush both get there.
		private readonly object            adhocSyncLock = new();
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
					ignoreRules = IgnoreRules.ForRoot(rootPath, logger);
					edits       = new(rootPath, ignoreRules);
					links       = new(rootPath);
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
					// Before the watcher starts: its flush updates the index, and an update made
					// while the index was still being built would be overwritten by it.
					physicalIndex = BuildPhysicalIndex(workspace.CurrentSolution);
					StartWatcher();
					break;
				
				case LoadMode.Project:
					
					rootPath = Path.GetDirectoryName(path)!;
					ignoreRules = IgnoreRules.ForRoot(rootPath, logger);
					edits       = new(rootPath, ignoreRules);
					links       = new(rootPath);
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
					
					// Before the watcher starts: its flush updates the index, and an update made
					// while the index was still being built would be overwritten by it.
					physicalIndex = BuildPhysicalIndex(workspace.CurrentSolution);
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
					
					ignoreRules = IgnoreRules.ForRoot(rootPath, logger);
					edits       = new(rootPath, ignoreRules);
					links       = new(rootPath);
					
					var (adhocWs, adhocPid) = LoadAdhocWorkspace();
					workspace        = adhocWs;
					defaultProjectId = adhocPid;
					isMSBuild        = false;
					
					LoadAllFiles((AdhocWorkspace) workspace, defaultProjectId);
					
					// Before the watcher starts: its flush updates the index, and an update made
					// while the index was still being built would be overwritten by it.
					physicalIndex = BuildPhysicalIndex(workspace.CurrentSolution);
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
		
		public void Dispose()
		{
			var started = Stopwatch.GetTimestamp();
			
			// Signal FlushPendingChanges to bail early on any in-flight or pending callbacks.
			disposed = true
			;
			
			watchSet?.Dispose();
			
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
	}
}
