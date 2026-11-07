using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynMcp;

internal sealed partial class WorkspaceManager
{
	sealed partial class WorkspaceInstance
	{
		// ── Workspace reload ────────────────────────────────────────────────
		
		/// <summary>
		///     Loads a fresh workspace for this instance's load mode, resetting the per-load warning
		///     queue first. Never called for <see cref="LoadMode.Adhoc"/>, which cannot reload.
		/// </summary>
		(Workspace Workspace, ProjectId ProjectId) LoadForMode()
		{
			loadWarnings.Clear();

			if(loadMode is LoadMode.Solution) {

				var solutionWorkspace = LoadSolution(loadPath, logger, loadWarnings, disposing.Token)
				;

				return (
					solutionWorkspace,
					solutionWorkspace.CurrentSolution.Projects.FirstOrDefault()?.Id ?? defaultProjectId
				);
			}

			return LoadMSBuildWorkspace(loadPath, logger, loadWarnings, disposing.Token);
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
			catch(OperationCanceledException) when(disposing.IsCancellationRequested) {

				// Queued on MSBuildGate when disposal began: the reload is abandoned, and this caller
				// reads the workspace it already had, as any call racing a disposal does.
				logger.LogInfo("Reload", $"Reload abandoned: the workspace is being released ({loadPath})");
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
			catch(OperationCanceledException) when(disposing.IsCancellationRequested) {
				logger.LogInfo("Reload", $"Deferred reload retry abandoned: the workspace is being released ({loadPath})");
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
			var newLoadFailed = LoadFailedProjects(newWorkspace);

			// The projects the live workspace is already unhealthy on. Stable for this attempt: only a
			// reload replaces the workspace, and this one holds reloadGate.
			string[] liveUnhealthy = [..ProjectsWithoutReferences(PeekSolution()).Union(projectsWithLoadFailures)];

			// Contended design-time builds can silently drop a project's references, or fail on a
			// project file and leave its options at defaults (#300) — results would be wrong, not
			// failed. The initial load retries the same way; the reload path is the likelier victim,
			// since it is often triggered by the very file writes that cause the contention.
			// Contention comes and goes, so a retry is for a project the live workspace loaded well. One
			// it is unhealthy on too is failing for good — an import that is not there, a target that
			// always fails in a design-time build — and another load would take seconds to say so again.
			var retry = 0;

			while(retry < ReloadRetries
			      && UnhealthyProjects(newWorkspace, newLoadFailed).Except(liveUnhealthy).Any()) {

				var retryDelayMs = Backoff.DelayMs(retry, Backoff.DefaultSeedMs, MaxRetryDelayMs)
				;

				logger.LogInfo("Reload", $"Unhealthy reload ({UnhealthyReason(newWorkspace, newLoadFailed)}) — attempt={retry + 2} delay_ms={retryDelayMs}");

				newWorkspace.Dispose();
				Thread.Sleep(retryDelayMs);

				(newWorkspace, newProjectId) = LoadForMode();
				newLoadFailed                = LoadFailedProjects(newWorkspace);
				retry++;
			}

			// Never replace a healthy workspace with an unhealthy one. A stale-but-correct
			// compilation beats a fresh-but-wrong one: dropped references and default options
			// produce plausible-looking results rather than visible failures, so promoting this
			// would silently corrupt every subsequent query. If the live workspace is broken too
			// there is nothing to protect, so promote regardless — otherwise a bad first load could
			// never recover. And not forever: past MaxConsecutiveDiscards the failure is taken to be
			// the project's own (an edit that broke it, a new project that never loads), which no
			// retry will clear. Discarding would then keep a workspace that ignores the edit, and
			// reload the whole solution every half minute for as long as it lasts; promoting shows
			// the edit, and the workspace says it is unhealthy — and keeps retrying a while longer
			// (see the healing retries below), since contention can outlast the discards too.
			var stillUnhealthy  = UnhealthyProjects(newWorkspace, newLoadFailed);
			var newIsWorse      = stillUnhealthy.Length > 0 && liveUnhealthy.Length == 0;
			var promotedPastCap = newIsWorse && consecutiveDiscards >= MaxConsecutiveDiscards;
			var discardedBefore = consecutiveDiscards;

			if(promotedPastCap)
				logger.LogInfo("Reload", $"Promoting an unhealthy reload ({UnhealthyReason(newWorkspace, newLoadFailed)}) after {discardedBefore} discarded in a row");

			if(newIsWorse && !promotedPastCap) {

				// Seed = what this attempt actually cost, so the retry rate self-scales to the
				// workspace instead of relying on a constant that fits neither big nor small.
				var deferMs = Backoff.DelayMs(
					consecutiveDiscards,
					(int) Math.Min(sw.ElapsedMilliseconds, MaxDeferMs),
					MaxDeferMs
				);

				consecutiveDiscards++;

				var reason = UnhealthyReason(newWorkspace, newLoadFailed);

				newWorkspace.Dispose();

				RecordUnhealthyLoad(
					$"Reload discarded — the reloaded workspace had {reason}. "
					+ "Keeping the previous workspace, whose text may now be one edit stale. "
					+ "Call roslyn_respawn if symbol results look outdated."
				);

				logger.LogInfo("Reload", $"Discarded reload — still {reason}; keeping previous workspace, retry attempt={consecutiveDiscards} delay_ms={deferMs}");

				// reloadVersion stays set: the deferred retry picks up the newest generation.
				ScheduleDeferredRetry(deferMs);

				return;
			}

			WarnIfUnhealthy(newWorkspace, newLoadFailed);

			// After WarnIfUnhealthy, so last_unhealthy_load says why an unhealthy workspace replaced a healthy one.
			if(promotedPastCap)
				RecordUnhealthyLoad(
					$"Reload promoted although it had {UnhealthyReason(newWorkspace, newLoadFailed)}: {discardedBefore} reloads in a row "
					+ "were discarded for it, so it is taken to be the project's own state rather than contention. "
					+ $"Retrying every {MaxDeferMs / 1000} s up to {MaxHealingRetries} more times; call roslyn_respawn once the cause is "
					+ "fixed if the workspace does not recover by then."
				);

			consecutiveDiscards = 0;

			var newProjectMap = BuildProjectMapFor(newWorkspace);
			
			// Looks at the file system, so it is done out here and only swapped in under the lock.
			var newPhysicalIndex = BuildPhysicalIndex(newWorkspace.CurrentSolution);

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

				// Swapped in with the solution it was built from, under the same lock.
				physicalIndex            = newPhysicalIndex;
				projectsWithLoadFailures = newLoadFailed;

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

			// Healing retries. A workspace promoted past the cap is unhealthy, and nothing else would reload
			// it until the next edit: contention that outlasted the discards — a long build outside the
			// server — would leave it broken although it is over. So it is retried at the longest deferral,
			// a bounded number of times: a failure that is the project's own costs that many loads, then
			// stops. A healthy promotion, whatever triggered it, ends the run.
			if(stillUnhealthy.Length == 0)
				healingRetriesLeft = 0;
			else if(promotedPastCap)
				healingRetriesLeft = MaxHealingRetries;

			if(stillUnhealthy.Length > 0 && healingRetriesLeft > 0) {

				healingRetriesLeft--;

				// Pending, as it is: the deferred retry only runs while a reload is.
				Interlocked.Increment(ref reloadVersion);

				logger.LogInfo("Reload", $"Still unhealthy ({UnhealthyReason(workspace, projectsWithLoadFailures)}); healing retry in {MaxDeferMs} ms, {healingRetriesLeft} left after it");

				ScheduleDeferredRetry(MaxDeferMs);
			}

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
