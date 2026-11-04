using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp;

internal sealed partial class WorkspaceManager
{
	sealed partial class WorkspaceInstance
	{
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
				
				var impact = edits.Classify(currentSolution, fullPath, out var info, out var reason);
				
				if(impact is WorkspaceEditImpact.Incremental) {
					
					try {
						
						using var stream = File.OpenRead(fullPath);
						var newText     = SourceText.From(stream, FileWriter.Utf8NoBom);
						var newSolution = info.WithText(currentSolution, newText);
						
						// If TryApplyChanges fails (rare — workspace conflict or unsupported kind),
						// flag for full reload so the next GetCompilation picks up the new content.
						if(!ApplyChangesToDisk(newSolution, currentSolution, ws)) {
							
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
			
			// Adhoc needs no classification: AddOrUpdateDocument returns for non-.cs paths, and
			// ReloadIfNeeded clears any flag without loading in adhoc mode.
			else if(ws is AdhocWorkspace adhoc)
				try {
					SyncAdhocDocument(adhoc, adhocProjectId, fullPath);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
				{ }
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
			// brought up to date afterwards (#338).
			var linkedWrites = ownedPaths
				.Select(LinkedWriteFor)
				.OfType<LinkedWrite>()
				.ToArray()
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
				
				// The sync below is a write of its own through WriteAndInvalidate's rules: the
				// file behind each link is ignored while it is brought up to date, so that its
				// InvalidateFile is not raced by the watcher (#338).
				// Counted directly before the try that uncounts: nothing may come between the two,
				// or an exception leaves a file ignored for good.
				foreach(var linked in linkedWrites)
					ignoredPaths.AddOrUpdate(linked.Target, 1, (_, count) => count + 1);
				
				try {
					
					if(applied)
						foreach(var linked in linkedWrites) {
							
							SyncLinkTarget(linked);
							syncedByApply = linked.Path;
						}
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
				syncedByApply = null;
				
				InvalidateFile(fullPath)
				;
				
				// Inside the same window, for the same reason as above. Not a second time when
				// InvalidateFile applied the text through TryApplyChanges: that path has done
				// the sync, alias scan included. A target the write created is the exception —
				// by then it exists, the nested sync took it for a file that was there all
				// along, and only this call knows that it is new.
				if(linked is { } afterWrite
					&& !(afterWrite.TargetExisted && SecurityBoundary.PathComparer.Equals(syncedByApply, fullPath)))
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
