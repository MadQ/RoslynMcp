using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Collections.Frozen;
using System.IO.Enumeration;
using System.Xml.Linq;

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
		private          WorkspaceWatchSet?   watchSet;
		
		// Which directories this workspace stays out of. Fixed for the life of the instance, like
		// the rest of the project configuration: read once the root is known, before any walk.
		private          IgnoreRules          ignoreRules = IgnoreRules.BuiltIn;
		
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
					ignoreRules = IgnoreRules.ForRoot(rootPath, logger);
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
					
					ignoreRules = IgnoreRules.ForRoot(rootPath, logger);
					
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
				
				var impact = ClassifyEditImpact(currentSolution, fullPath, out var info, out var reason);
				
				if(impact is WorkspaceEditImpact.Incremental) {
					
					try {
						
						using var stream = File.OpenRead(fullPath);
						var newText     = SourceText.From(stream, FileWriter.Utf8NoBom);
						var newSolution = info.WithText(currentSolution, newText);
						
						// If TryApplyChanges fails (rare — workspace conflict or unsupported kind),
						// flag for full reload so the next GetCompilation picks up the new content.
						if(!ApplyChangesWithFswSuppressed(newSolution, currentSolution, ws)) {
							
							logger.LogInfo("Reload", $"Flagged (incremental apply failed): {fullPath}");
							Interlocked.Increment(ref reloadVersion);
						}
					}
					catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
					{ }
					catch(InvalidOperationException ex) {
						
						// Defensive only — see WorkspaceTextDocumentInfo.WithText for the mixed-kind case.
						// Flag a reload instead of letting Solution's kind-mismatch exception propagate out
						// of the FSW debounce timer (#276).
						logger.LogInfo("Reload", $"Flagged (incremental apply threw {ex.GetType().Name}): {fullPath}");
						Interlocked.Increment(ref reloadVersion);
					}
				}
				else if(impact is WorkspaceEditImpact.Reload) {
					
					// Unconditional bump, unlike FlagReload's == 0 guard: a genuinely new input must move
					// the generation so an in-flight reload that predates it is discarded. Also reached for
					// an existing analyzer-config document, which ClassifyEditImpact never reports as
					// Incremental — TryApplyChanges throws for ChangeAnalyzerConfigDocument, so
					// .editorconfig/.globalconfig edits always reload rather than apply incrementally (#276).
					logger.LogInfo("Reload", $"Flagged ({reason}): {fullPath}");
					Interlocked.Increment(ref reloadVersion);
					InvalidateCompilation();
				}
				
				// Otherwise WorkspaceEditImpact.None — not a compilation input (CHANGELOG.md, a .txt, an
				// unrelated .json). The workspace has nothing to invalidate; WorkspaceResolver still clears
				// the pagination cache.
			}
			
			// Adhoc needs no classification: AddOrUpdateDocument returns for non-.cs paths, and
			// ReloadIfNeeded clears any flag without loading in adhoc mode.
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
		///     Counts the projects a load is about to open — the ones a solution lists, or a bounded
		///     scan of a directory — and logs a warning before a potentially long MSBuild load when
		///     the count is over the threshold.
		/// </summary>
		static void WarnIfLargeSolution(string path, WorkspaceMode mode, FileLogger logger)
		{
			if(mode == WorkspaceMode.Adhoc)
				
				return;
			
			// A lone .csproj is one project by definition; counting its neighbours says nothing
			// about the load.
			if(path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
				
				return;
			
			var isSolution = path.EndsWith(".sln",  StringComparison.OrdinalIgnoreCase)
			              || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
			
			// Never an unbounded recursive enumeration: this runs on the first tool call, and a
			// walk through node_modules or .git on a network share takes longer than an MCP
			// client waits (#309). The solution's own project list is exact; the directory scan is
			// pruned and capped (just above the threshold), so its count is a lower bound.
			int count;
			
			// Advisory only: a warning that cannot be computed must never fail the load. Both
			// helpers swallow the I/O failures they expect; this covers whatever they do not.
			try {
				
				count = isSolution             ? MSBuildBootstrap.ReadSolutionProjects(path).Length
				      : Directory.Exists(path) ? MSBuildBootstrap.FindCsprojCandidates(path).Length
				      : 0;
			}
			catch(Exception) {
				return;
			}
			
			if(count > LargeSolutionThreshold)
				logger.LogInfo("Workspace",
					$"Large solution detected: {(isSolution ? "" : "at least ")}{count} projects. " +
					$"MSBuild loading may take several minutes. " +
					$"For a faster load, use --workspace adhoc or set ROSLYNMCP_WORKSPACE=adhoc.");
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
				
				return [];
			
			return WorkspaceWalker.EnumerateFiles(
				path,
				searchPattern,
				recursive: true,
				(ref FileSystemEntry directory) =>
					(directory.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0
					|| ignoreRules.IsExcluded(directory.FileName)
			);
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
		internal void MarkReloadNeeded(string? path = null)
		{
			logger.LogInfo("Reload", path is null ? "Flagged (ApplyChanges failed)" : $"Flagged (ApplyChanges failed): {path}");
			Interlocked.Increment(ref reloadVersion);
		}
		
		
		
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
				
				// Source documents and AdditionalFiles items: TryApplyChanges writes both kinds to
				// disk, and either can be a link whose target has to be brought up to date.
				var projectChanges = newSolution.GetChanges(baseSolution)
					.GetProjectChanges()
					.ToArray()
				;
				
				ownedPaths = projectChanges
					.SelectMany(p => p.GetChangedDocuments())
					.Select(id => newSolution.GetDocument(id)?.FilePath)
					.Concat(projectChanges
						.SelectMany(p => p.GetChangedAdditionalDocuments())
						.Select(id => newSolution.GetAdditionalDocument(id)?.FilePath))
					.OfType<string>()
					.ToArray()
				;
			}
			
			// TryApplyChanges writes each changed document at its own path. A document that is
			// reached through a link is thereby written into another file, and that file gets
			// the same treatment as in WriteAndInvalidate: captured before the write and
			// brought up to date afterwards (#338). It needs no entry in ignoredPaths for the
			// write itself — the watch set is suspended for that — only for the sync below.
			var linkedWrites = ownedPaths
				.Select(LinkedWriteFor)
				.OfType<LinkedWrite>()
				.ToArray()
			;
			
			// Ref-counted inside the watch set: events stop before TryApplyChanges and resume
			// only when the last concurrent suppressor finishes. See issue #145 item 3.
			watchSet?.Suspend();
			
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
				watchSet?.Resume();
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
			
			// Counted directly before the try that uncounts: nothing may come between the two,
			// or an exception leaves a file ignored for good.
			foreach(var linked in linkedWrites)
				ignoredPaths.AddOrUpdate(linked.Target, 1, (_, count) => count + 1);
			
			try {
				
				if(applied)
					foreach(var linked in linkedWrites)
						SyncLinkTarget(linked);
			}
			finally {
				
				foreach(var linked in linkedWrites)
					ignoredPaths.AddOrUpdate(linked.Target, 0, (_, count) => count - 1);
			}
			
			InvalidateCompilation();
			
			return applied;
		}
		
		
		// For MSBuild-tracked source/additional text documents: routes through TryApplyChanges as the single
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
			
			var info = WorkspaceTextDocumentInfo.Resolve(currentSolution, filePath);
			
			if(!info.SupportsIncrementalTextChange)
				
				return false;
			
			Solution newSolution;
			
			try {
				
				newSolution = info.WithText(currentSolution, newText);
			}
			catch(InvalidOperationException) {
				
				return false;
			}
			
			return ApplyChangesWithFswSuppressed(newSolution, currentSolution, ws);
		}
		
		
		// ── FSW-safe write entry point ────────────────────────────────────────
		//
		// ⚠ THIS CODE IS THE RESULT OF EXTENSIVE STAGED ANALYSIS (issue #179, Stages 1–5).
		//   DO NOT CHANGE WriteAndInvalidate, ScheduleDebounced, ownedDeletePaths, or
		//   the InvalidateFile placement, or the RequiresReload classification, without first reading:
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
			// A write through a symbolic link lands in the file the link leads to, and that is
			// the path the file system reports the change at. Worked out before the write: a
			// write may replace the link, and afterwards there is nothing left to follow (#338).
			var linked = LinkedWriteFor(fullPath);
			
			ignoredPaths.AddOrUpdate(fullPath, 1, (_, count) => count + 1);
			
			if(linked is { } beforeWrite)
				ignoredPaths.AddOrUpdate(beforeWrite.Target, 1, (_, count) => count + 1);
			
			if(movedFromPath != null)
				ownedDeletePaths.AddOrUpdate(movedFromPath, 1, (_, count) => count + 1);
			
			try {
				
				await write();
				
				// Bug #3 fix: InvalidateFile must run while ignoredPaths is still incremented.
				// Moving it after the finally decrement opened a race window where a late FSW
				// event fired unsuppressed between the decrement and the workspace invalidation.
				InvalidateFile(fullPath)
				;
				
				// Inside the same window, for the same reason as above.
				if(linked is { } afterWrite)
					SyncLinkTarget(afterWrite);
				
				MarkSynced();
			}
			finally {
				ignoredPaths.AddOrUpdate(fullPath, 0, (_, count) => count - 1);
				
				if(linked is { } released)
					ignoredPaths.AddOrUpdate(released.Target, 0, (_, count) => count - 1);
			}
		}
		
		/// <summary>
		///     A write that goes through a link, as it stood before the write: the path that was
		///     written, the file that path led to, and whether that file was there.
		/// </summary>
		readonly record struct LinkedWrite(string Path, string Target, bool TargetExisted);
		
		// Null for the ordinary case: the path leads nowhere else.
		LinkedWrite? LinkedWriteFor(string fullPath)
			=> LinkTargetOf(fullPath) is { } target
				? new LinkedWrite(fullPath, target, File.Exists(target))
				: null
		;
		
		/// <summary>
		///     The file a write to <paramref name="fullPath"/> lands in when that is a different
		///     file from the one the path names — the path is a symbolic link, or lies under a
		///     linked directory — and null when it is not, or when it cannot be worked out.
		///     <para>
		///         The result is spelled the way the watcher and the workspace's documents spell
		///         paths, not the way the file system resolves them. Resolving links gives the
		///         real location, and that differs from the path as it was opened whenever a
		///         directory above sits under a link — on macOS every temp directory does
		///         (<c>/var</c> is <c>/private/var</c>). A path in that spelling matches neither a
		///         watcher event nor a document. So the resolved path is re-spelled through the
		///         workspace root, or the nearest directory above the root that contains it: the
		///         part up to that directory is kept as written, the rest is the real location.
		///         For a path with no link below that directory this gives the path back, and the
		///         answer is null.
		///     </para>
		/// </summary>
		string? LinkTargetOf(string fullPath)
		{
			try {
				
				if(SecurityBoundary.ResolveLinks(fullPath) is not { } resolved)
					
					return null;
				
				// From the workspace root upwards — not from the written path. A link at or above
				// the root is where the user put the workspace and stays as written; a link
				// below it is what is being followed. Starting at the written path's own
				// directory would find a linked directory first and spell the target straight
				// back through it, as the path that was written.
				for(var above = (string?) rootPath; above is not null; above = Path.GetDirectoryName(above)) {
					
					if(SecurityBoundary.ResolveLinks(above) is not { } resolvedAbove
						|| !SecurityBoundary.IsUnderDirectory(resolved, resolvedAbove))
						continue;
					
					var respelled = Path.Combine(above, Path.GetRelativePath(resolvedAbove, resolved));
					
					// The same name the way the file system compares names, which on Linux is
					// to the letter: Foo.cs leading to foo.cs leads somewhere else.
					return SecurityBoundary.PathComparer.Equals(respelled, fullPath) ? null : respelled;
				}
				
				return SecurityBoundary.PathComparer.Equals(resolved, fullPath) ? null : resolved;
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
				
				return null;
			}
		}
		
		/// <summary>
		///     After a write through a link: brings the workspace up to date with the file the
		///     write landed in — if it landed there. A write can also replace the link where it
		///     sits (a link that leads out of the workspace, a restore that must not follow one),
		///     and then the file behind it was never touched: the path no longer leads to it, and
		///     nothing is done. The same when the link was dangling and still is.
		/// </summary>
		void SyncLinkTarget(LinkedWrite linked)
		{
			// Compared the way the file system compares names: on Linux a link Foo.cs to a
			// file foo.cs is a link to another file, not a path that leads to itself.
			if(LinkTargetOf(linked.Path) is not { } target || !SecurityBoundary.PathComparer.Equals(target, linked.Target))
				
				return;
			
			if(!linked.TargetExisted && !File.Exists(linked.Target))
				
				return;
			
			InvalidateLinkTarget(linked.Target, linked.TargetExisted);
			SyncOtherAliases(linked);
		}
		
		// Set while SyncOtherAliases is at work on this thread. Refreshing an alias writes it
		// through TryApplyChanges, which comes back here for that alias; without the mark every
		// alias would start a scan of its own.
		[ThreadStatic] static bool syncingAliases;
		
		/// <summary>
		///     After a write through a link: refreshes every other tracked document that is the
		///     same file on disk — a second link to it, or a link earlier in a chain. The write
		///     was reported under one name and, at most, seen by the watcher under the file's
		///     own; a document under a third name hears of it from nobody.
		///     <para>
		///         Done the plain way: every document path of the solution is resolved and
		///         compared. That is one file-system query per document plus one resolution per
		///         directory, paid only by a write that went through a link — an ordinary write
		///         never gets here. An index of documents by physical file would make it cheap
		///         and would also cover a write made straight to a file that has links to it,
		///         which this does not (#341).
		///     </para>
		/// </summary>
		void SyncOtherAliases(LinkedWrite linked)
		{
			if(syncingAliases)
				
				return;
			
			syncingAliases = true;
			
			try {
				
				Solution currentSolution;
				
				@lock.EnterReadLock();
				
				try {
					currentSolution = workspace.CurrentSolution;
				}
				finally {
					@lock.ExitReadLock();
				}
				
				var same        = SecurityBoundary.PathComparer;
				var directories = new Dictionary<string, string>(same);
				
				// Where a path really is. The directory is resolved once per directory; the file
				// itself only costs a full resolution when it is a link. A path that cannot be
				// examined stays as written — it is then simply not found to be an alias, and
				// the scan goes on to the next one.
				string Physical(string path)
				{
					try {
						
						if(Path.GetDirectoryName(path) is not { } directory)
							
							return path;
						
						if(!directories.TryGetValue(directory, out var resolved)) {
							
							resolved = SecurityBoundary.ResolveLinks(directory) ?? directory;
							directories.Add(directory, resolved);
						}
						
						var located = Path.Combine(resolved, Path.GetFileName(path));
						
						return FileWriter.IsLink(located) ? SecurityBoundary.ResolveLinks(located) ?? located : located;
					}
					catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
						
						return path;
					}
				}
				
				// Every kind of document a file can be: source, an AdditionalFiles item, an
				// analyzer-config file. InvalidateFile knows what to do with each.
				var written = Physical(linked.Target);
				var aliases = currentSolution.Projects
					.SelectMany(project => project.Documents
						.Concat<TextDocument>(project.AdditionalDocuments)
						.Concat(project.AnalyzerConfigDocuments))
					.Select(document => document.FilePath)
					.OfType<string>()
					.Distinct(same)
					.Where(path => !same.Equals(path, linked.Path) && !same.Equals(path, linked.Target) && same.Equals(Physical(path), written))
					.ToArray()
				;
				
				// One alias that cannot be refreshed must not cost the others their turn.
				foreach(var alias in aliases)
					try {
						InvalidateLinkTarget(alias, existedBefore: true);
					}
					catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
						_ = ex
						;
					}
			}
			finally {
				syncingAliases = false;
			}
		}
		
		/// <summary>
		///     Brings the workspace up to date with a file that was written through a link.
		///     <para>
		///         A file that existed before and is an unknown <c>.cs</c> is left alone. To
		///         <see cref="InvalidateFile"/> an unknown <c>.cs</c> is a new compilation input and
		///         costs a full reload, which is right for a file that has just appeared and wrong
		///         for one that was there all along and that no project compiles — a shared source
		///         kept outside the project and linked into it. Everything else goes through
		///         <see cref="InvalidateFile"/>: a tracked document gets its new text, an evaluation
		///         input flags its reload, and a file the write created is treated as new.
		///     </para>
		/// </summary>
		void InvalidateLinkTarget(string linkTarget, bool existedBefore)
		{
			if(existedBefore) {
				
				Solution currentSolution;
				
				@lock.EnterReadLock();
				
				try {
					currentSolution = workspace.CurrentSolution;
				}
				finally {
					@lock.ExitReadLock();
				}
				
				// An adhoc workspace takes any .cs it is handed for a document, so there the
				// question is simply whether it already has this one.
				var unknownSource = isMSBuild
					? ClassifyEditImpact(currentSolution, linkTarget, out _, out var reason) is WorkspaceEditImpact.Reload
						&& reason == ReasonNewDocument
					: currentSolution.GetDocumentIdsWithFilePath(linkTarget).IsEmpty
				;
				
				if(unknownSource) {
					
					// The watcher can report this write after the suppression window has
					// closed — its events arrive on their own time, and on Windows a replaced
					// file is reported more than once. The stamp lets the flush recognise every
					// such report as this write instead of taking it for a new document.
					lock(debounceLock) {
						
						if(ownedLinkTargets.Count >= MaxRmOwnedWriteSizes)
							ownedLinkTargets.Clear();
						
						try {
							
							var info = new FileInfo(linkTarget);
							
							ownedLinkTargets[linkTarget] = (info.Length, info.LastWriteTimeUtc, DateTime.UtcNow + OwnedLinkTargetWindow);
						}
						catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
							// Gone or locked — the flush handles the event the normal way.
							_ = ex
							;
						}
					}
					
					return;
				}
				
				// Already has this text: an edit applied through TryApplyChanges syncs the
				// target once on its own, and a second pass would only write the file again.
				if(HasCurrentText(currentSolution, linkTarget))
					
					return;
			}
			
			InvalidateFile(linkTarget);
		}
		
		// Whether the workspace's document at this path already holds what is on disk. False when
		// there is no such document, its text is not loaded, or the file cannot be read.
		static bool HasCurrentText(Solution solution, string path)
		{
			try {
				
				if(solution.GetDocumentIdsWithFilePath(path) is not [var id, ..]
					|| solution.GetDocument(id) is not { } document
					|| !document.TryGetText(out var held))
					
					return false;
				
				using var stream = File.OpenRead(path);
				
				return held.ContentEquals(SourceText.From(stream, FileWriter.Utf8NoBom));
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				
				return false;
			}
		}
		
		/// <summary>
		///     Whether a watcher report for <paramref name="path"/> is of a write RM itself made
		///     through a link, to a source no project compiles (see <see cref="ownedLinkTargets"/>).
		///     True while the file still has the stamp it was left with, and while another such
		///     write is in flight — that one is about to leave a new stamp, and judging the file
		///     against the old one in between would take RM's own write for someone else's.
		///     Otherwise the entry is forgotten: the file has been written by someone else.
		/// </summary>
		bool IsOwnLinkTargetWrite(string path)
		{
			lock(debounceLock) {
				
				if(!ownedLinkTargets.TryGetValue(path, out var stamp))
					
					return false;
				
				if(ignoredPaths.TryGetValue(path, out var writing) && writing > 0)
					
					return true;
				
				try {
					
					var info = new FileInfo(path);
					
					if(DateTime.UtcNow <= stamp.UntilUtc
						&& info.Exists && info.Length == stamp.Length && info.LastWriteTimeUtc == stamp.WriteUtc)
						
						return true;
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
					// Cannot tell — treat it as changed.
					_ = ex
					;
				}
				
				ownedLinkTargets.Remove(path);
				
				return false;
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
		///             <see cref="RequiresReload"/> consults it first, so an <em>unknown</em> file
		///             under one of them — a <c>.cs</c> build artifact, NuGet's generated
		///             <c>.props</c>/<c>.editorconfig</c> under <c>obj</c> — never forces a full
		///             reload, from the MSBuild flush or from <see cref="InvalidateFile"/>. Generated files under <c>obj</c> that really
		///             are compilation inputs (<c>*.AssemblyInfo.cs</c>,
		///             <c>*.GlobalUsings.g.cs</c>) already have document IDs, never reach that
		///             branch, and keep receiving ordinary text updates.
		///         </item>
		///     </list>
		///     For the watcher's pre-queue filter — which must not suppress those generated
		///     documents — see <see cref="IsNeverCompilationInput"/>.
		/// </summary>
		bool IsExcludedDirectoryName(string name) =>
			ignoreRules.IsExcluded(name);

		/// <summary>
		///     Directories that can never hold a compilation document, so a change under one is
		///     safe to drop before it is even queued. Deliberately excludes <c>bin</c>/<c>obj</c>:
		///     SDK-style projects put generated documents there (<c>*.AssemblyInfo.cs</c>,
		///     <c>*.GlobalUsings.g.cs</c>) which are real compilation inputs and must still receive
		///     text updates. Build output is filtered later instead — only from the decision to
		///     force a full reload. See <see cref="IsUnderExcludedDirectory"/>.
		/// </summary>
		bool IsNeverCompilationInput(string name) =>
			ignoreRules.IsNeverInput(name);

		// Reason literals double as the log vocabulary for "Reload — Flagged (<reason>)".
		const string ReasonNewDocument     = "new document";
		const string ReasonEvaluationInput = "evaluation input";
		const string ReasonAdditionalDoc   = "additional document";
		const string ReasonAnalyzerConfig  = "analyzer config document";
		
		// Files MSBuild reads while evaluating a project — a change to any of them can alter the
		// compilation even though Roslyn never sees them as documents. Directory.Build.props,
		// Directory.Build.targets and Directory.Packages.props are covered by extension.
		static readonly FrozenSet<string> EvaluationInputExtensions = new[] {
			".csproj", ".props", ".targets", ".sln", ".slnx", ".slnf",
			".editorconfig", ".globalconfig", ".ruleset", ".resx",
		}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
		
		static readonly FrozenSet<string> EvaluationInputNames = new[] {
			"global.json", "nuget.config", "packages.lock.json", "packages.config",
		}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
		
		/// <summary>
		///     For a path that is <em>not</em> a tracked source document: does a change to it alter the
		///     compilation, or the MSBuild evaluation that produced it? Shared by
		///     <see cref="InvalidateFile"/> (RM-owned writes) and <see cref="FlushMSBuild"/> (watcher
		///     batches) so both apply one rule (#273). Before this predicate existed, InvalidateFile
		///     treated every untracked path as a new compilation input, so editing CHANGELOG.md cost a
		///     full MSBuild reload on the next compilation-needing call.
		///     <para>
		///         Order matters: build output is dropped first, because <c>obj/</c> holds generated
		///         <c>*.nuget.g.props</c> and <c>*.GeneratedMSBuildEditorConfig.editorconfig</c> files
		///         that would otherwise match the evaluation-input rule. Then a new <c>.cs</c>, then
		///         evaluation inputs by extension or name, then anything the loaded projects list as an
		///         additional or analyzer-config document, then any path declared by an
		///         <c>&lt;AdditionalFiles Include="..." /&gt;</c> item even if the file did not exist
		///         when the workspace loaded. Everything else — a <c>.md</c>, a <c>.txt</c>, an
		///         unrelated <c>.json</c> — is a no-op for the workspace.
		///     </para>
		///     <para>
		///         Known limitation: MSBuild <c>EmbeddedResource</c> items are not exposed by
		///         <see cref="Project"/>, and MSBuildWorkspace discards the project instance after the
		///         design-time build, so a resource of arbitrary type cannot be recognized. Resources do
		///         not affect any Roslyn-served result — only <c>dotnet build</c>, which reads disk.
		///         <c>.resx</c> is matched by extension as the common case.
		///     </para>
		///     Paths outside <see cref="rootPath"/> are still classified — Directory.Build.props above a
		///     csproj-mode root is a legitimate input; only the excluded-directory walk is root-relative.
		/// </summary>
		bool RequiresReload(Solution solution, string fullPath, out string reason)
		{
			reason = "";
			
			// A .cs under bin/ or obj/ that is not already a document is a compiler artifact, and a
			// .props there is NuGet's. Neither is source.
			if(IsUnderExcludedDirectory(fullPath, IsExcludedDirectoryName))
				
				return false;
			
			var extension = Path.GetExtension(fullPath);
			
			if(extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)) {
				
				reason = ReasonNewDocument;
				
				return true;
			}
			
			if(EvaluationInputExtensions.Contains(extension) || EvaluationInputNames.Contains(Path.GetFileName(fullPath))) {
				
				reason = ReasonEvaluationInput;
				
				return true;
			}
			
			foreach(var project in solution.Projects) {
				
				if(project.AdditionalDocuments.Any(d => PathEquals(d.FilePath, fullPath))) {
					
					reason = ReasonAdditionalDoc;
					
					return true;
				}
				
				if(project.AnalyzerConfigDocuments.Any(d => PathEquals(d.FilePath, fullPath))) {
					
					reason = ReasonAnalyzerConfig;
					
					return true;
				}
				
				if(MatchesDeclaredAdditionalFile(project, fullPath)) {
					
					reason = ReasonAdditionalDoc;
					
					return true;
				}
			}
			
			return false;
		}
		
		static bool PathEquals(string? candidate, string fullPath) =>
			candidate is not null && string.Equals(candidate, fullPath, StringComparison.OrdinalIgnoreCase);
		
		bool MatchesDeclaredAdditionalFile(Project project, string fullPath)
		{
			if(project.FilePath is null)
				
				return false;
			
			try {
				
				var projectDir = Path.GetDirectoryName(project.FilePath);
				
				if(projectDir is null)
					return false;
				
				var relativePath = NormalizeProjectRelativePath(Path.GetRelativePath(projectDir, fullPath));
				var doc = XDocument.Load(project.FilePath);
				
				foreach(var include in doc
					.Descendants()
					.Where(e => e.Name.LocalName == "AdditionalFiles")
					.SelectMany(e => SplitMsbuildIncludeList((string?) e.Attribute("Include")))) {
					
					if(include.Contains("$(", StringComparison.Ordinal))
						continue;
					
					var normalizedInclude = NormalizeProjectRelativePath(include);
					
					if(normalizedInclude.IndexOfAny(['*', '?']) < 0) {
						
						var includePath = Path.GetFullPath(Path.Combine(projectDir, include));
						
						if(PathEquals(includePath, fullPath))
							return true;
						
						continue;
					}
					
					if(Regex.IsMatch(relativePath, BuildPathGlobRegex(normalizedInclude), RegexOptions.IgnoreCase))
						return true;
				}
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { }
			
			return false;
		}
		
		static IEnumerable<string> SplitMsbuildIncludeList(string? include) =>
			(include ?? "")
				.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		
		static string NormalizeProjectRelativePath(string path) =>
			path.Replace('\\', '/');
		
		static string BuildPathGlobRegex(string pattern)
		{
			var sb = new StringBuilder("^");
			var i  = 0;
			
			while(i < pattern.Length) {
				
				if(pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*') {
					
					sb.Append(".*");
					i += 2;
					
					if(i < pattern.Length && (pattern[i] == '/' || pattern[i] == '\\'))
						i++;
				}
				else if(pattern[i] == '*') {
					
					sb.Append(@"[^/\\]*");
					i++;
				}
				else if(pattern[i] == '?') {
					
					sb.Append('.');
					i++;
				}
				else {
					
					sb.Append(Regex.Escape(pattern[i].ToString()));
					i++;
				}
			}
			
			sb.Append('$');
			
			return sb.ToString();
		}

		/// <summary>
		///     What an edit to a given path means for the workspace (#288). The single decision point
		///     behind <see cref="InvalidateFile"/> and <see cref="FlushMSBuild"/>, which share the
		///     classification but not the flagging mechanics — InvalidateFile bumps
		///     <see cref="reloadVersion"/> unconditionally, FlushMSBuild goes through
		///     <see cref="FlagReload"/> with its <c>== 0</c> guard.
		/// </summary>
		enum WorkspaceEditImpact
		{
			/// <summary>Not a compilation or evaluation input — the workspace has nothing to do.</summary>
			None,
			
			/// <summary>Tracked as a source or additional document; apply the new text in place.</summary>
			Incremental,
			
			/// <summary>A compilation/evaluation input that cannot be updated in place — reload.</summary>
			Reload
		}
		
		/// <summary>
		///     Decides how <paramref name="fullPath"/> must be handled, and hands back the resolved
		///     <paramref name="info"/> so the caller can apply text without re-classifying.
		///     <paramref name="reason"/> is meaningful only for <see cref="WorkspaceEditImpact.Reload"/>.
		/// </summary>
		WorkspaceEditImpact ClassifyEditImpact(
			Solution                      solution,
			string                        fullPath,
			out WorkspaceTextDocumentInfo info,
			out string                    reason)
		{
			info   = WorkspaceTextDocumentInfo.Resolve(solution, fullPath);
			reason = "";
			
			if(info.SupportsIncrementalTextChange)
				
				return WorkspaceEditImpact.Incremental;
			
			return RequiresReload(solution, fullPath, out reason)
				? WorkspaceEditImpact.Reload
				: WorkspaceEditImpact.None;
		}

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
			watchSet = new WorkspaceWatchSet(rootPath, ignoreRules, (fullPath, deleted) => ScheduleDebounced(fullPath, deleted), ReconcileMissedChanges, logger);
			
			ApplyWatchPlan(workspace.CurrentSolution, constructedUtc);
		}
		
		/// <summary>
		///     Hands the watch set the directories <paramref name="solution"/> spans. It plans and
		///     starts the watchers on a background thread, and does nothing when the project
		///     directories are the ones it already planned for — so this is called after every
		///     reload, where a project may have been added or removed.
		/// </summary>
		void ApplyWatchPlan(Solution solution, DateTime sinceUtc)
		{
			// An AdhocWorkspace has no project files; its one project is the root itself.
			string[] projectDirectories = isMSBuild
				? [..solution.Projects
					.Select(project => Path.GetDirectoryName(project.FilePath))
					.OfType<string>()
					.Select(Path.GetFullPath)
					.Distinct(WatchPlanner.PathComparer)]
				: [rootPath];
			
			string[] documentDirectories =
			[
				..solution.Projects
					.SelectMany(project => project.Documents.Concat<TextDocument>(project.AdditionalDocuments).Concat(project.AnalyzerConfigDocuments))
					.Select(document => Path.GetDirectoryName(document.FilePath))
					.OfType<string>()
					.Distinct(WatchPlanner.PathComparer)
			];
			
			watchSet?.Apply(projectDirectories, documentDirectories, sinceUtc);
		}
		
		// File-system timestamp granularity — two seconds on FAT, the coarsest in common use, and the
		// same margin roslyn_check_drift allows. It does not cover a file server whose clock is
		// further off than this; nothing short of comparing content would.
		static readonly TimeSpan ReconcileTolerance = TimeSpan.FromSeconds(2);
		
		/// <summary>
		///     Runs once the watchers are live. The workspace was read from disk before they
		///     existed — during the load itself, then while they started in the background — so an
		///     edit made in that window raised no event. Any tracked document written since
		///     construction began is queued as if the watcher had reported it; an unchanged one is
		///     dropped by the content comparison in the flush.
		///     <para>
		///         Build output is left out: the design-time build writes generated documents under
		///         <c>obj</c> during the load, and reporting those would force a reload on every
		///         start. A missing file is left out too — a generated document that has never been
		///         built is tracked but not on disk, which is not a deletion.
		///     </para>
		/// </summary>
		void ReconcileMissedChanges(DateTime sinceUtc)
		{
			// Runs on a pool thread, possibly after Dispose: PeekSolution takes a lock that
			// Dispose tears down.
			try {
				
				if(disposed)
					
					return;
				
				var queued = 0;
				
				var paths = PeekSolution().Projects
					.SelectMany(project => project.Documents.Concat<TextDocument>(project.AdditionalDocuments).Concat(project.AnalyzerConfigDocuments))
					.Select(document => document.FilePath)
					.OfType<string>()
					.Distinct(WatchPlanner.PathComparer)
				;
				
				foreach(var path in paths) {
					
					if(IsUnderExcludedDirectory(path, IsExcludedDirectoryName))
						continue;
					
					try {
						
						// Deliberately generous: a file system with coarse timestamps can stamp a
						// write made just after sinceUtc as earlier. A document caught by the margin
						// alone is unchanged and is dropped by the content comparison in the flush.
						// A missing file reports the year 1601, so it never compares as newer.
						if(File.GetLastWriteTimeUtc(path) <= sinceUtc - ReconcileTolerance)
							continue;
					}
					catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
						continue;
					}
					
					ScheduleDebounced(path);
					queued++;
				}
				
				if(queued > 0)
					logger.LogInfo("Watch", $"{queued} document(s) changed on disk while the workspace was loading — queued for sync");
			}
			catch(Exception ex) when(ex is ObjectDisposedException or InvalidOperationException) {
				// Disposed mid-flight — nothing left to reconcile.
			}
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
		
		// Whether a readable file exists under exactly this name.
		// File.Exists comes first and settles the usual case of a file that is simply gone
		// without listing its directory — a checkout that removes a thousand files asks a
		// thousand times. It also says no to a symbolic link whose target is missing: such a
		// "file" cannot be read, and treating its delete report as a change would leave the
		// document's old text in place with nothing to correct it.
		// The exact name matters because File.Exists ignores case on Windows and macOS, and a
		// file renamed in case only (Foo.cs to foo.cs) is reported as a delete of the old name:
		// that one really is gone, and the workspace has to learn the new name.
		static bool ExistsAsNamed(string path)
		{
			try {
				
				if(!File.Exists(path))
					
					return false;
				
				var directory = Path.GetDirectoryName(path);
				var name      = Path.GetFileName(path);
				
				return directory is not null
					&& Directory.EnumerateFiles(directory, name).Any(found => Path.GetFileName(found) == name);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException) {
				
				return false;
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
				
				// A file reported as deleted that is there when the flush runs was replaced, not
				// removed. Replacing a file is how every atomic write ends, and on Windows the
				// watcher sometimes reports the old file's removal as a delete. Taken at its word
				// that is "tracked document deleted", a full reload, for a document that only
				// needs its text compared — and in an adhoc workspace the document is dropped
				// although its file exists. Such a report is a change.
				var replaced = deleted
					.Where(ExistsAsNamed)
					.ToArray()
				;
				
				// Compared the way the file system compares names: ignoring case here would, on
				// Linux, take the real delete of foo.cs along with a replaced Foo.cs.
				if(replaced.Any()) {
					
					deleted = [.. deleted.Except(replaced, SecurityBoundary.PathComparer)];
					changed = [.. changed.Union(replaced, SecurityBoundary.PathComparer)];
				}
				
				// Reports of RM's own writes through a link to a source no project compiles.
				// Dropped here, ahead of both flushes: to either of them such a file is an
				// unknown .cs — a reload in an MSBuild workspace, a new document in an adhoc one.
				changed = [.. changed.Where(path => !IsOwnLinkTargetWrite(path))];
				
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
			void FlagReload(string reason, string? path)
			{
				if(reloadFlagged)

					return;

				reloadFlagged = true;

				// Logged even when the version guard below skips the bump — the path that caused a
				// reload was otherwise invisible in the log (#273).
				logger.LogInfo("Reload", path is null ? $"Flagged ({reason})" : $"Flagged ({reason}): {path}");

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
			
			var newSolution     = currentSolution;
			var modified        = false;
			string? applyFailed = null;
			
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

				FlagReload("tracked document deleted", path);

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
					
					var impact = ClassifyEditImpact(newSolution, path, out var info, out var reason);

					// Not an incremental candidate — either no document tracks this path yet (MSBuildWorkspace
					// doesn't support AddDocument via TryApplyChanges: it modifies the .csproj, conflicting with
					// SDK-style implicit includes), or it is an analyzer-config document, which
					// MSBuildWorkspace.CanApplyChange(ChangeAnalyzerConfigDocument) reports false for as of
					// Roslyn 5.3.0 (verified). Both route to a full reload — unless RequiresReload says the path
					// is not a compilation input at all. Its first rule drops build output: a .cs under bin/ or
					// obj/ that is not already a document is a compiler artifact, not source, and reloading the
					// whole workspace for it is pure cost. Generated documents that ARE compilation inputs
					// (*.AssemblyInfo.cs, *.GlobalUsings.g.cs) are tracked and never reach this branch.
					if(impact is not WorkspaceEditImpact.Incremental) {

						if(impact is WorkspaceEditImpact.Reload)
							FlagReload(reason, path);

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
					var existingDoc = info.GetDocument(newSolution);

					if(existingDoc is not null
					   && existingDoc.TryGetText(out var currentText)
					   && currentText.ContentEquals(text))

						continue;

					newSolution = info.WithText(newSolution, text);

					applyFailed ??= path;
					modified = true;
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
				catch(InvalidOperationException) {
					
					// Defensive only — reachable if the same physical path is tracked with mixed
					// kinds across projects (e.g. a source document in one, an AdditionalFiles item
					// in another), so the kind of the first id does not describe every id in the
					// array. Without this, the exception would propagate out of the per-path try,
					// abandoning the rest of `changed` unprocessed for this flush cycle — mirrors the
					// same defensive catch in InvalidateFile (#276).
					FlagReload("incremental apply threw InvalidOperationException", path);
				}
			}
			
			if(modified && !ApplyChangesWithFswSuppressed(newSolution, currentSolution, ws))
				FlagReload("incremental apply failed", applyFailed);

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

			// Before the load reads anything: a file edited after the load read it, in a directory
			// the current watch plan does not cover, is only caught by reconciling from here.
			var reloadStartedUtc = DateTime.UtcNow;

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

			// A reload can add or remove projects; the watch set ignores this when the directories
			// it planned for are unchanged. When it does replace watchers there is a moment with
			// the old ones gone and the new ones not yet live, so edits since the reload began
			// are reconciled by timestamp once they are.
			ApplyWatchPlan(PeekSolution(), reloadStartedUtc);
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
