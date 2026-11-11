using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp;

internal sealed partial class WorkspaceManager
{
	sealed partial class WorkspaceInstance
	{
		// ── File invalidation ────────────────────────────────────────────────
		
		/// <summary>
		///     Flags a full reload for a change no file the classifier sees stands for: a restore writes
		///     <c>project.assets.json</c> under obj\, which the watcher and <see cref="EditClassifier"/> ignore,
		///     and a project loaded before it has no references until it reloads (#399). The bump is
		///     unconditional, as for a new evaluation input: a reload already in flight read the state before.
		///     An AdhocWorkspace has no design-time build to repeat.
		/// </summary>
		public void RequestReload(string reason)
		{
			if(!isMSBuild)
				
				return;
			
			logger.LogInfo("Reload", $"Flagged ({reason}): {loadPath}");
			Interlocked.Increment(ref reloadVersion);
			InvalidateCompilation();
		}
		

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
				
				var impact = edits.Classify(currentSolution, fullPath, out var info, out var reason);
				
				if(impact is WorkspaceEditImpact.Incremental) {
					
					try {
						
						using var stream = FileReader.OpenUnbounded(fullPath);
						var newText     = SourceText.From(stream, FileWriter.Utf8NoBom);
						var newSolution = info.WithText(currentSolution, newText);
						
						// The file has the text already, so the workspace takes it without
						// writing it there a second time (#403). If the apply fails (rare — the
						// solution moved on, or an unsupported kind), flag for full reload so the
						// next GetCompilation picks up the new content.
						if(!ApplyTextsInMemory(newSolution, currentSolution, ws)) {
							
							logger.LogInfo("Reload", $"Flagged (incremental apply failed): {fullPath}");
							Interlocked.Increment(ref reloadVersion);
						}
					}
					catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
						
						// Left to the watcher flush, which reads the file again (#403): dropped here, the
						// workspace would keep the text from before the write.
						RetryUnreadable(fullPath, ex);
					}
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
					// an existing analyzer-config document, which EditClassifier.Classify never reports as
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
			
			// Adhoc needs no classification: SyncAdhocDocument takes a source or an .editorconfig
			// and returns for any other path, and ReloadIfNeeded clears any flag without loading
			// in adhoc mode.
			else if(ws is AdhocWorkspace adhoc)
				try {
					SyncAdhocDocument(adhoc, adhocProjectId, fullPath);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
					
					// Left to the watcher flush, which reads the file again (#403): dropped here, the
					// workspace would keep the text from before the write.
					RetryUnreadable(fullPath, ex);
				}
		}
		
		// ── FileSystemWatcher ────────────────────────────────────────────────
		
		/// <summary>
		///     Applies solution changes to the workspace, which in a project writes the changed
		///     documents to disk (MSBuildWorkspace.TryApplyChanges). The watchers report those
		///     writes like any other; the flush compares the file with the document's text and
		///     finds them equal, so they apply nothing a second time (#49). The watchers are not
		///     switched off for the write: a change someone else made meanwhile would be lost (#358).
		/// </summary>
		internal bool ApplyChangesToDisk(Solution newSolution)
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
			
			return ApplyChangesToDisk(newSolution, baseSolution, ws);
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
		internal bool ApplyChangesToDisk(Solution newSolution, Solution baseSolution, Workspace expectedWs)
			=> Apply(newSolution, baseSolution, expectedWs, persist: true)
		;
		
		/// <summary>
		///     Brings texts that were read from disk into the workspace, and writes nothing (#403).
		///     For the watcher flush and <see cref="InvalidateFile"/>: the file already holds the
		///     text, and <see cref="ApplyChangesToDisk(Solution, Solution, Workspace)"/> would have
		///     the workspace write it there again. That second write put an older text back over
		///     whatever someone had saved since the read, and was reported as a workspace failure
		///     when the file was still open elsewhere (#433). Everything else is
		///     as for an apply that writes: it fails when the solution has moved on since
		///     <paramref name="baseSolution"/>, and the documents at other names of a changed file
		///     are refreshed.
		/// </summary>
		internal bool ApplyTextsInMemory(Solution newSolution, Solution baseSolution, Workspace expectedWs)
			=> Apply(newSolution, baseSolution, expectedWs, persist: false)
		;
		
		// One change to the workspace's solution at a time, whether it writes or not. An apply
		// that does not write checks for itself that the solution has not moved on, which
		// TryApplyChanges does inside; under this gate nothing can get between its check and its
		// change. Never taken under the workspace lock.
		readonly Lock applyGate = new();
		
		// The texts of newSolution that differ from baseSolution, set on the workspace one
		// document at a time. False when the solution has moved on, as TryApplyChanges answers;
		// null when this Roslyn has no way to set a text without writing it.
		bool? SetTexts(Workspace ws, Solution newSolution, Solution baseSolution, ProjectChanges[] projectChanges)
		{
			if(!WorkspaceTextSetter.Available)
				
				return null;
			
			if(!ReferenceEquals(ws.CurrentSolution, baseSolution))
				
				return false;
			
			var set = 0;
			
			foreach(var project in projectChanges) {
				
				var changed = project.GetChangedDocuments().Select(id => (Id: id, Additional: false))
					.Concat(project.GetChangedAdditionalDocuments().Select(id => (Id: id, Additional: true)))
				;
				
				foreach(var (id, additional) in changed) {
					
					TextDocument? document = additional ? newSolution.GetAdditionalDocument(id) : newSolution.GetDocument(id);
					
					// A changed document without a text at hand cannot be set. Passing it over
					// would report an apply that left it as it was.
					if(document is null || !document.TryGetText(out var text))
						
						return false;
					
					// Not there to be called: before anything was set, the apply that writes
					// takes over. After, the solution has moved on under it, so the apply has
					// failed, and the caller reloads.
					if(!WorkspaceTextSetter.TrySet(ws, id, text, additional))
						
						return set == 0 ? null : false;
					
					set++;
				}
			}
			
			return true;
		}
		
		bool Apply(Solution newSolution, Solution baseSolution, Workspace expectedWs, bool persist)
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
			ProjectChanges[] projectChanges = [];
			
			if(isMSBuild) {
				
				// Source documents and AdditionalFiles items: TryApplyChanges writes both kinds to
				// disk, and either can be a link whose target has to be brought up to date.
				projectChanges = newSolution.GetChanges(baseSolution)
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
			// brought up to date afterwards (#338).
			// Only an apply that writes. One that does not has written nothing through a link,
			// and treating the file behind it as just written would be wrong twice over: its
			// watcher reports are dropped while it is synced, and a file no project compiles is
			// stamped as the server's own write, so a save someone made at that moment would
			// go unseen (#403). The server's real writes do this themselves in
			// WriteAndInvalidate, and a file changed from outside is reported under its own path.
			// By whether a write will happen, not by what was asked for: where this Roslyn
			// cannot set a text without writing, the apply below writes after all. Only the one
			// call that finds that out goes without, once in the life of the process.
			var writes = persist || !WorkspaceTextSetter.Available;
			
			LinkedWrite[] linkedWrites = writes
				? [..ownedPaths.Select(LinkedWriteFor).OfType<LinkedWrite>()]
				: []
			;
			
			// The watchers stay live through the write (#358), so it is reported, at each path
			// written and at the file behind each link. Those reports are kept, not dropped, and
			// held back by FlushPendingChanges until the apply is over (see applyingPaths): then
			// the comparison in the flush recognises RM's own write, and still applies a save
			// someone else made to one of these files in the meantime.
			// Counted directly before the try that uncounts: nothing may come between the two,
			// or an exception leaves a file held back for good.
			string[] applying = [..ownedPaths, ..linkedWrites.Select(linked => linked.Target)];
			
			foreach(var path in applying)
				applyingPaths.AddOrUpdate(path, 1, (_, count) => count + 1);
			
			bool applied;
			
			try {
				
				try {
					
					lock(applyGate) {
						
						// Without a write when the caller read these texts from the files
						// themselves. An AdhocWorkspace never writes, and a Roslyn that cannot
						// set a text otherwise gets the apply that does.
						applied = (writes || !isMSBuild ? null : SetTexts(ws, newSolution, baseSolution, projectChanges))
							?? ws.TryApplyChanges(newSolution);
					}
				}
				catch(Exception ex) when(ex is ObjectDisposedException or InvalidOperationException or NotSupportedException or ArgumentException) {
					// ObjectDisposedException/InvalidOperationException: workspace disposed by a concurrent
					// ReloadIfNeeded between the staleness check and TryApplyChanges.
					// ArgumentException: the same for an apply that sets texts — a workspace that
					// was disposed has no documents, and a document that is not in it is refused.
					// NotSupportedException: TryApplyChanges throws for unsupported change kinds
					// (e.g. AddDocument/RemoveDocument on MSBuildWorkspace). Both treated as false return.
					_ = ex
					;
					applied = false;
				}
				
				// The sync below is a write of its own through WriteAndInvalidate's rules: the
				// file behind each link is ignored while it is brought up to date, so that its
				// InvalidateFile is not raced by the watcher (#338).
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
			}
			finally {
				
				foreach(var path in applying)
					applyingPaths.AddOrUpdate(path, 0, (_, count) => count - 1);
			}
			
			// The other direction: a changed document that is a file with links to it. The
			// documents at those links are the same file and are refreshed with it (#341).
			// Asked for every changed path; a path that was itself written through a link has
			// no names of its own, and neither has any path in a workspace without links.
			if(applied && physicalIndex.Count > 0)
				foreach(var path in ownedPaths)
					SyncAliases(path, null);
			
			InvalidateCompilation();
			
			return applied;
		}
		
		
		// For MSBuild-tracked source/additional text documents: routes through TryApplyChanges as the single
		// disk write (via ApplyChangesToDisk). Returns false
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
			
			return ApplyChangesToDisk(newSolution, currentSolution, ws);
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
				
				// Inside the same window, for the same reason as above. InvalidateFile takes the
				// text into the workspace without writing, so it has synced nothing behind a
				// link (#403), and this is the one sync of the file the write went into.
				if(linked is { } afterWrite)
					SyncLinkTarget(afterWrite);
				
				// A write straight to a file that has links to it: the documents at the links
				// have new content too, and nobody reports a change at their paths (#341).
				if(linked is null)
					SyncAliases(fullPath, null);
				
				MarkSynced();
			}
			finally {
				ignoredPaths.AddOrUpdate(fullPath, 0, (_, count) => count - 1);
				
				if(linked is { } released)
					ignoredPaths.AddOrUpdate(released.Target, 0, (_, count) => count - 1);
			}
		}
	}
}
