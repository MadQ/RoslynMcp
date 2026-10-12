using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.IO.Enumeration;

namespace RoslynMcp;

internal sealed partial class WorkspaceManager
{
	sealed partial class WorkspaceInstance
	{
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
		///     existed — during the load itself, then while they started in the background — so a
		///     change made in that window raised no event. Three kinds are caught up on:
		///     <list type="bullet">
		///         <item>
		///             A tracked document <b>written</b> since construction began is queued as if
		///             the watcher had reported it; an unchanged one is dropped by the content
		///             comparison in the flush.
		///         </item>
		///         <item>
		///             A tracked file that has gone <b>missing</b>: see
		///             <see cref="QueueMissedDeletes"/>.
		///         </item>
		///         <item>
		///             A file that has <b>appeared</b>: see <see cref="QueueMissedCreates"/>.
		///         </item>
		///     </list>
		///     <para>
		///         Build output is left out: the design-time build writes generated documents under
		///         <c>obj</c> during the load, and reporting those would force a reload on every
		///         start. A generated document that has never been built is tracked but not on
		///         disk; it lies under <c>obj</c> too, and is not taken for a deletion.
		///     </para>
		/// </summary>
		void ReconcileMissedChanges(DateTime sinceUtc)
		{
			// Runs on a pool thread, possibly after Dispose: PeekSolution takes a lock that
			// Dispose tears down.
			try {
				
				if(disposed)
					
					return;
				
				var solution = PeekSolution();
				var index    = physicalIndex;
				var modified = 0;
				var missing  = new List<string>();
				
				var paths = solution.Projects
					.SelectMany(project => project.Documents.Concat<TextDocument>(project.AdditionalDocuments).Concat(project.AnalyzerConfigDocuments))
					.Select(document => document.FilePath)
					.OfType<string>()
					.Distinct(WatchPlanner.PathComparer)
				;
				
				foreach(var path in paths) {
					
					if(edits.IsUnderExcludedDirectory(path))
						continue;
					
					try {
						
						var writtenUtc = File.GetLastWriteTimeUtc(path);
						
						// A missing file reports the year 1601. Whether it was deleted in the
						// window or was never there is settled below, from its directory.
						if(writtenUtc.Year < 1602) {
							
							missing.Add(path);
							continue;
						}
						
						// Deliberately generous: a file system with coarse timestamps can stamp a
						// write made just after sinceUtc as earlier. A document caught by the margin
						// alone is unchanged and is dropped by the content comparison in the flush.
						if(writtenUtc <= sinceUtc - ReconcileTolerance)
							continue;
					}
					catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
						continue;
					}
					
					ScheduleDebounced(path);
					modified++;
				}
				
				// A file that only links represent is no document, and is not in the list above.
				missing.AddRange(index.Files.Where(file => solution.GetDocumentIdsWithFilePath(file).IsEmpty && !File.Exists(file)));
				
				var deleted = QueueMissedDeletes(missing, sinceUtc);
				var created = QueueMissedCreates(solution, sinceUtc);
				
				if(modified + deleted + created > 0)
					logger.LogInfo("Watch", $"Changed on disk while the workspace was loading — queued for sync: {modified} document(s) written, {created} file(s) created, {deleted} deleted");
			}
			catch(Exception ex) when(ex is ObjectDisposedException or InvalidOperationException) {
				// Disposed mid-flight — nothing left to reconcile.
			}
		}
		
		/// <summary>
		///     The deletes the watchers were not yet there to report (#348). A tracked file that
		///     is not on disk was either deleted in the window, or was never there — a project
		///     can list a source file that does not exist, and a document for it is tracked all
		///     the same. Taking the second for the first would flag a reload on every start, and
		///     again after that reload.
		///     <para>
		///         The directory tells them apart. Removing an entry writes the directory, so a
		///         file can only have gone missing since <paramref name="sinceUtc"/> if the
		///         nearest directory above it that still exists was written since then. No
		///         margin is taken off here, unlike for a modified document: a delete that is
		///         wrongly assumed costs a reload, not a comparison that comes out equal.
		///     </para>
		/// </summary>
		int QueueMissedDeletes(List<string> missing, DateTime sinceUtc)
		{
			var queued  = 0;
			var written = new Dictionary<string, bool>(WatchPlanner.PathComparer);
			
			foreach(var path in missing) {
				
				try {
					
					var directory = Path.GetDirectoryName(path);
					
					while(directory is not null && !Directory.Exists(directory))
						directory = Path.GetDirectoryName(directory);
					
					if(directory is null)
						continue;
					
					if(!written.TryGetValue(directory, out var since)) {
						
						since = Directory.GetLastWriteTimeUtc(directory) > sinceUtc;
						written.Add(directory, since);
					}
					
					if(!since)
						continue;
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
					continue;
				}
				
				ScheduleDebounced(path, deleted: true);
				queued++;
			}
			
			return queued;
		}
		
		/// <summary>
		///     The new files the watchers were not yet there to report (#348): a file under a
		///     watched tree that no document tracks, queued as if its creation had been
		///     reported. The flush decides what it means — a new document, an input of the
		///     project evaluation, or nothing.
		///     <para>
		///         Only a directory that was written since <paramref name="sinceUtc"/> is looked
		///         into: adding an entry writes the directory, so any other one cannot hold a
		///         file that appeared in the window. After an ordinary load that is no
		///         directory at all, and the pass costs one walk of directory entries.
		///     </para>
		///     <para>
		///         In such a directory, an AdhocWorkspace takes every source file it has no
		///         document for. A project cannot: it may leave source files out on purpose
		///         (<c>Compile Remove</c>), and those would look new on every start. There a
		///         file also has to have been created since <paramref name="sinceUtc"/>. A file
		///         moved in with its old timestamps is therefore not found in a project. No
		///         margin is taken off either time, for the reason given for deletes.
		///     </para>
		/// </summary>
		int QueueMissedCreates(Solution solution, DateTime sinceUtc)
		{
			var queued = 0;
			
			// The root, and every project directory outside it — what the watch set covers.
			var roots = solution.Projects
				.Select(project => Path.GetDirectoryName(project.FilePath))
				.OfType<string>()
				.Where(directory => !SecurityBoundary.IsUnderDirectory(directory, rootPath))
				.Prepend(rootPath)
				.Distinct(WatchPlanner.PathComparer)
				.ToArray()
			;
			
			foreach(var root in roots) {
				
				IEnumerable<(string Path, DateTime WrittenUtc)> directories;
				
				try {
					
					directories = WorkspaceWalker
						.EnumerateDirectories(root, PruneFromWalk)
						.Prepend((root, Directory.GetLastWriteTimeUtc(root)))
					;
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
					continue;
				}
				
				foreach(var (directory, writtenUtc) in directories) {
					
					if(writtenUtc <= sinceUtc)
						continue;
					
					try {
						
						foreach(var file in Directory.EnumerateFiles(directory)) {
							
							if(!solution.GetDocumentIdsWithFilePath(file).IsEmpty)
								continue;
							
							if(isMSBuild ? File.GetCreationTimeUtc(file) <= sinceUtc : !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
								continue;
							
							ScheduleDebounced(file);
							queued++;
						}
					}
					catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
						// Gone or unreadable since the walk listed it.
						_ = ex
						;
					}
				}
			}
			
			return queued;
		}
		
		// The directories a walk for source files stays out of. An AdhocWorkspace also leaves
		// out hidden and system directories, as its loading walk does; a project is walked
		// the way the watch set watches it.
		bool PruneFromWalk(ref FileSystemEntry directory)
			=> ignoreRules.IsExcluded(directory.FileName)
				|| (!isMSBuild && (directory.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
		;
		
		void ScheduleDebounced(string fullPath, bool deleted = false)
		{
			// Nothing under these can be a compilation document, so drop the event before it is
			// queued. Without this, a stray .cs anywhere below the root — a package cache, a
			// sample in .git, anything — reaches FlushMSBuild as an unknown document and forces a
			// full workspace reload, which is both slow and the operation that can drop metadata
			// references (issue #235).
			if(edits.IsUnderNeverInputDirectory(fullPath))

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
		//
		// How the exact name is checked depends on what the platform makes cheap. On Windows the
		// file system is asked for the one entry. Elsewhere a name filter is applied after
		// reading the whole directory, so the directory is read once per flush and kept in
		// listings — a checkout that rewrites two thousand files in one directory would
		// otherwise read it two thousand times. Linux gets no shortcut for being case-sensitive:
		// a mounted Windows drive or a casefold directory is not.
		static bool ExistsAsNamed(string path, Dictionary<string, HashSet<string>> listings)
		{
			try {
				
				if(!File.Exists(path))
					
					return false;
				
				var name = Path.GetFileName(path);
				
				if(Path.GetDirectoryName(path) is not { } directory)
					
					return false;
				
				if(OperatingSystem.IsWindows())
					
					return Directory.EnumerateFiles(directory, name).Any(found => Path.GetFileName(found) == name);
				
				if(!listings.TryGetValue(directory, out var names)) {
					
					names = new HashSet<string>(Directory.EnumerateFiles(directory).Select(found => Path.GetFileName(found)), StringComparer.Ordinal);
					listings.Add(directory, names);
				}
				
				return names.Contains(name);
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
				// A delete report for a path RM is writing at this moment is the replace in
				// progress. Acting on it now would race the write — as a change it gets in the
				// way of the sync the write does when it is done, as a delete it reloads the
				// workspace for a file that is about to be there again. It is put back for the
				// next flush, not dropped: should the write fail and leave the file gone, the
				// report is the only word of that.
				var inFlight = deleted
					.Where(path => ignoredPaths.TryGetValue(path, out var writing) && writing > 0)
					.ToArray()
				;
				
				if(inFlight.Any()) {
					
					deleted = [.. deleted.Except(inFlight, SecurityBoundary.PathComparer)];
					
					foreach(var path in inFlight)
						ScheduleDebounced(path, true);
				}
				
				var listings = new Dictionary<string, HashSet<string>>(SecurityBoundary.PathComparer);
				var replaced = deleted
					.Where(path => ExistsAsNamed(path, listings))
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
				
				// A change is reported under the name the file system knows the file by. Every
				// document that is that file under another name has changed with it, and is
				// added here, so that both flushes compare its text like any other (#341).
				// The added names are kept apart: they come from the index, not from a report,
				// and are only ever documents to refresh, never news of a file.
				// The batch has been taken out of the pending sets by now. Whatever goes wrong
				// in this pass must not cost the batch itself, so it is fenced off.
				var unreported  = new HashSet<string>(SecurityBoundary.PathComparer);
				var linkChanged = false;
				
				try {
					
					// An adhoc workspace keeps its index current here. An MSBuild workspace
					// never changes its index: it reloads when a link has changed (below).
					if(!isMSBuild)
						unreported.UnionWith(RelinkAdhoc(changed));
					
					PhysicalFileIndex index;
					Solution          tracked;
					
					// Together, under the lock a reload swaps both under.
					@lock.EnterReadLock();
					
					try {
						
						index   = physicalIndex;
						tracked = workspace.CurrentSolution;
					}
					finally {
						@lock.ExitReadLock();
					}
					
					// A reported path that leads elsewhere than the index says: the workspace
					// reloads, and the documents that depended on that path are read again.
					if(isMSBuild)
						unreported.UnionWith(FindChangedLinks(changed, index, tracked, out linkChanged));
					
					if(index.Count > 0) {
						
						unreported.UnionWith(changed
							.SelectMany(index.NamesOf)
							.Where(name => !tracked.GetDocumentIdsWithFilePath(name).IsEmpty));
						
						// A deleted file takes the documents at its links with it: each of
						// them is a link that leads nowhere now. They are handled as deleted
						// documents — a reload in an MSBuild workspace, a removed document in
						// an adhoc one. Nobody reports a delete at their paths: the links
						// themselves are still there (#343).
						var orphaned = deleted
							.SelectMany(index.NamesOf)
							.Where(name => !tracked.GetDocumentIdsWithFilePath(name).IsEmpty)
							.Except(deleted, SecurityBoundary.PathComparer)
							.ToArray()
						;
						
						if(orphaned.Length > 0)
							deleted = [.. deleted, .. orphaned];
					}
				}
				catch(Exception ex) {
					
					// What was gathered before the failure is kept: an adhoc workspace may
					// already have entered those names under another file, and this batch is
					// the only one that will read them again.
					logger.LogInfo("Links", $"Other names of changed files were not added to this flush: {ex.GetType().Name}: {ex.Message}");
				}
				
				unreported.ExceptWith(changed);
				
				if(unreported.Count > 0)
					changed = [.. changed, .. unreported];
				
				// True when the flush could only flag a reload. The workspace is then behind disk
				// until that reload completes, so the sync clock must not advance — otherwise
				// roslyn_check_drift reports "in sync" for changes it has not applied, and a
				// reload that is later discarded becomes invisible.
				var reloadFlagged = false;

				if(isMSBuild)
					reloadFlagged = FlushMSBuild(changed, deleted, unreported) | linkChanged;

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
		// unreported: the paths in changed that no watcher report named — documents the index
		// lists as other names of a changed file.
		bool FlushMSBuild(string[] changed, string[] deleted, HashSet<string> unreported)
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
					
					var impact = edits.Classify(newSolution, path, out var info, out var reason);

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

						// A path that came from the index and is no document of this solution is
						// not a new source file: a reload dropped it between the lookup and
						// here. It is skipped, not taken for a file that has just appeared.
						if(impact is WorkspaceEditImpact.Reload && !(reason == EditClassifier.ReasonNewDocument && unreported.Contains(path)))
							FlagReload(reason, path);

						continue;
					}

					// Read through the document's own path, every time — also when the batch
					// holds several names of one file (see FlushPendingChanges). The text is
					// written back through that same path, so what is read through a path and
					// what is written through it are always the same file. Sharing one read
					// between the names of a file was tried and removed: it needs to know
					// which file a path leads to, and when that answer is out of date one
					// file's text is written into another (#341).
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
					SyncAdhocDocument(adhoc, projectId, path);
				}
				catch(FileNotFoundException) {
					
					RemoveDocument(adhoc, projectId, path);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
		
		}
	}
}
