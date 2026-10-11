using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Diagnostics;

namespace RoslynMcp;

internal sealed partial class WorkspaceManager
{
	sealed partial class WorkspaceInstance
	{
		/// <summary>
		///     A write that goes through a link, as it stood before the write: the path that was
		///     written, the file that path led to, and whether that file was there.
		/// </summary>
		readonly record struct LinkedWrite(string Path, string Target, bool TargetExisted);
		
		// The linked path ApplyChangesWithFswSuppressed last synced on this thread. Lets
		// WriteAndInvalidate see that its InvalidateFile call has already done the sync.
		[ThreadStatic] static string? syncedByApply;
		
		// Null for the ordinary case: the path leads nowhere else.
		LinkedWrite? LinkedWriteFor(string fullPath)
			=> links.TargetOf(fullPath) is { } target
				? new LinkedWrite(fullPath, target, File.Exists(target))
				: null
		;
		
		/// <summary>
		///     After a write through a link: brings the workspace up to date with the file the
		///     write landed in — if it landed there. A write can also replace the link where it
		///     sits (a link that leads out of the workspace, a restore that must not follow one),
		///     and then the file behind it was never touched; see <see cref="SyncReplacedLink"/>.
		///     Nothing is done when the link was dangling and still is.
		/// </summary>
		void SyncLinkTarget(LinkedWrite linked)
		{
			// Compared the way the file system compares names: on Linux a link Foo.cs to a
			// file foo.cs is a link to another file, not a path that leads to itself.
			if(links.TargetOf(linked.Path) is not { } target || !SecurityBoundary.PathComparer.Equals(target, linked.Target)) {
				
				SyncReplacedLink(linked);
				
				return;
			}
			
			if(!linked.TargetExisted && !File.Exists(linked.Target))
				
				return;
			
			InvalidateLinkTarget(linked.Target, linked.TargetExisted);
			SyncAliases(linked.Target, linked.Path);
		}
		
		/// <summary>
		///     After a write that replaced a link where it sat. The written path is now a file
		///     of its own, and the file it used to lead to was not touched. The index still has
		///     the path, and every link that led through it, under the old file.
		///     <para>
		///         An MSBuild workspace reloads: see <see cref="FlagLinkChanged"/>. An adhoc
		///         workspace has no reload, so the names are looked at again here, and the
		///         documents that now reach the new content through the written path are
		///         refreshed.
		///     </para>
		/// </summary>
		void SyncReplacedLink(LinkedWrite linked)
		{
			var index = physicalIndex;
			
			// Only a link that is a tracked document matters here. Replacing any other link
			// changes nothing about where a document leads.
			if(!index.IsName(linked.Path))
				
				return;
			
			var names = index.NamesOf(linked.Target);
			
			if(isMSBuild) {
				
				if(NoteLinkChanged(index, linked.Path))
					FlagLinkChanged(linked.Path);
				
				// The reload comes with the next call that needs the workspace. Until then
				// the documents that reached the old file through the written path would
				// hold its text; they are read again now, each through its own path.
				RefreshNames(names, linked.Path);
				
				return;
			}
			
			ReindexNames(names);
			SyncAliases(links.TargetOf(linked.Path) ?? linked.Path, linked.Path);
		}
		
		/// <summary>
		///     Flags a reload because a tracked path leads somewhere else than
		///     <see cref="physicalIndex"/> says: a write by this server replaced a link with a
		///     file (<see cref="SyncReplacedLink"/>), or the watcher reported a path whose link
		///     someone else has changed (<see cref="FindChangedLinks"/>). Callers go through
		///     <see cref="NoteLinkChanged"/> first, so this runs once per path and index.
		///     <para>
		///         In an MSBuild workspace the index is never changed in place. It is built by
		///         a load and swapped in together with the solution it was built from, so the
		///         two cannot disagree, and nothing has to be kept in step while a reload is
		///         under way. Until the reload, the index is behind for that path — which can
		///         cost a refresh and nothing worse, because every document is read and
		///         written through its own path and the index never decides which file a text
		///         belongs to.
		///     </para>
		///     <para>
		///         The generation is moved unconditionally, like for a new compilation input:
		///         a reload that is already running may have built its index before the change.
		///     </para>
		/// </summary>
		void FlagLinkChanged(string path)
		{
			logger.LogInfo("Reload", $"Flagged ({ReasonLinkChanged}): {path}");
			Interlocked.Increment(ref reloadVersion);
			InvalidateCompilation();
		}
		
		// The paths a reload has already been flagged for because they lead elsewhere than
		// linkChangesIn, the index they were compared with, says. Guarded by indexLock. Starts
		// over when the index is another one: a reload has swapped in a new index, and what
		// was known about the old one says nothing about it.
		private PhysicalFileIndex?       linkChangesIn;
		private readonly HashSet<string> linkChanges = new(SecurityBoundary.PathComparer);
		
		/// <summary>
		///     Records that <paramref name="path"/> leads elsewhere than
		///     <paramref name="index"/> says, and tells whether that is news: true the first
		///     time for this path and this index, false after that.
		///     <para>
		///         A flush that sees a changed link keeps seeing it until the reload it asked
		///         for has finished, because until then the index is the old one. Flagging
		///         moves the reload generation, and a reload whose generation has moved is
		///         thrown away. Without this record every flush in between would throw away
		///         the reload that is under way, and with steady file activity it would never
		///         finish.
		///     </para>
		/// </summary>
		bool NoteLinkChanged(PhysicalFileIndex index, string path)
		{
			lock(indexLock) {
				
				if(!ReferenceEquals(linkChangesIn, index)) {
					
					linkChangesIn = index;
					linkChanges.Clear();
				}
				
				return linkChanges.Add(path);
			}
		}
		
		/// <summary>
		///     MSBuild only, for the watcher flush: finds the reported paths that lead
		///     somewhere else than <paramref name="index"/> says — a link was repointed, a link
		///     was replaced by a file, a file of its own became a link (#343). Flags a reload
		///     for each such path once, and returns the documents that shared its file before
		///     or lead through it: they may have other content now, nobody reports a change at
		///     their paths, and the caller has them read again, each through its own path.
		///     <para>
		///         The index is not changed. It was built with the solution the workspace
		///         holds and is replaced with it; the reload builds the next one.
		///     </para>
		///     <para>
		///         Three things an earlier version got wrong, and how they are settled here.
		///         <b>Once per change, not once per flush</b>: see
		///         <see cref="NoteLinkChanged"/>. <b>A path that cannot be examined has not
		///         changed</b>: <see cref="LinkResolver.TryTargetOf"/> reports that apart from
		///         "leads nowhere else", and such a path is skipped. <b>No cost outside the
		///         root</b>: only a path that is an indexed name, or a tracked document under
		///         the root, is looked at — for the usual document that is one look per
		///         directory level, and a document elsewhere is not resolved at all. A file
		///         outside the root that becomes a link is therefore not noticed.
		///     </para>
		/// </summary>
		string[] FindChangedLinks(string[] reported, PhysicalFileIndex index, Solution tracked, out bool flagged)
		{
			flagged = false;
			
			var same       = SecurityBoundary.PathComparer;
			var dependents = new HashSet<string>(same);
			
			foreach(var path in reported) {
				
				var known = index.PhysicalOf(path);
				
				if(known is null
					&& !(SecurityBoundary.IsUnderDirectory(path, rootPath) && !tracked.GetDocumentIdsWithFilePath(path).IsEmpty))
					continue;
				
				if(!links.TryTargetOf(path, out var leadsTo) || same.Equals(leadsTo, known))
					continue;
				
				flagged = true;
				
				if(NoteLinkChanged(index, path))
					FlagLinkChanged(path);
				
				// Before: the documents that shared its file, some of them through this path.
				// Now: the documents that lead to this path as a file of its own.
				if(known is not null)
					dependents.UnionWith(index.NamesOf(known));
				
				dependents.UnionWith(index.NamesOf(path));
			}
			
			return [.. dependents.Where(name => !tracked.GetDocumentIdsWithFilePath(name).IsEmpty)];
		}
		
		const string ReasonLinkChanged = "link changed";
		
		// Adhoc only: looks at these document paths again and enters each under the file it
		// leads to now. An adhoc workspace never reloads, so its index is kept current in
		// place, and there is no swap for this to race.
		void ReindexNames(string[] names)
		{
			lock(indexLock)
				physicalIndex = physicalIndex.With(names, links.TargetOf);
		}
		
		/// <summary>
		///     Adhoc only, for the watcher flush: looks again at every changed path that is in
		///     the index — the link may have been repointed — or that is a document and a link
		///     now, together with every name that shared its file. Some of those reach the file
		///     through the changed path (a link to this link) and follow it wherever it leads.
		///     Returns the names that were looked at: nobody reports a change at their paths,
		///     and they may have other content now.
		/// </summary>
		string[] RelinkAdhoc(string[] changed)
		{
			var index    = physicalIndex;
			var solution = PeekSolution();
			var names    = new HashSet<string>(SecurityBoundary.PathComparer);
			
			foreach(var path in changed) {
				
				if(index.PhysicalOf(path) is { } former) {
					
					names.Add(path);
					names.UnionWith(index.NamesOf(former));
				}
				else if(!solution.GetDocumentIdsWithFilePath(path).IsEmpty && links.TargetOf(path) is not null) {
					
					names.Add(path);
					names.UnionWith(index.NamesOf(path));
				}
			}
			
			if(names.Count == 0)
				
				return [];
			
			ReindexNames([.. names]);
			
			return [.. names];
		}
		
		// Adhoc only: drops document paths that are no longer documents.
		void ForgetNames(IEnumerable<string> names)
		{
			lock(indexLock)
				physicalIndex = physicalIndex.With(names, _ => null);
		}
		
		/// <summary>
		///     Builds <see cref="physicalIndex"/> for a solution (see
		///     <see cref="LinkResolver.BuildIndex"/>). The time is logged, because it is paid on
		///     every load.
		/// </summary>
		PhysicalFileIndex BuildPhysicalIndex(Solution solution)
		{
			var watch = Stopwatch.StartNew();
			var index = links.BuildIndex(solution, out var documentPaths);
			
			logger.LogInfo("Links", $"{index.Count} of {documentPaths} document path(s) lead to another file ({watch.ElapsedMilliseconds}ms)");
			
			return index;
		}
		
		// Set while SyncAliases is at work on this thread. Refreshing an alias writes it
		// through TryApplyChanges, which comes back here for that alias; without the mark every
		// alias would start a round of its own.
		[ThreadStatic] static bool syncingAliases;
		
		/// <summary>
		///     After a change to the file at <paramref name="physical"/>: refreshes every tracked
		///     document that is that file under another name — a link to it, a link earlier in a
		///     chain, the same file reached through a linked directory. The change was reported
		///     under one name and, at most, seen by the watcher under the file's own; a document
		///     under another name hears of it from nobody else.
		///     <para>
		///         A lookup in <see cref="physicalIndex"/>, which is empty for a workspace
		///         without links. <paramref name="writtenPath"/> is the name the change was made
		///         under, which its writer has already brought up to date.
		///     </para>
		/// </summary>
		void SyncAliases(string physical, string? writtenPath)
			=> RefreshNames(physicalIndex.NamesOf(physical), writtenPath)
		;
		
		// Reads each of these documents again through its own path, except the one a writer
		// has already brought up to date.
		void RefreshNames(string[] names, string? writtenPath)
		{
			if(syncingAliases || names.Length == 0)
				
				return;
			
			syncingAliases = true;
			
			try {
				
				// One name that cannot be refreshed must not cost the others their turn.
				foreach(var name in names)
					try {
						
						if(!SecurityBoundary.PathComparer.Equals(name, writtenPath))
							InvalidateLinkTarget(name, existedBefore: true);
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
					? edits.Classify(currentSolution, linkTarget, out _, out var reason) is WorkspaceEditImpact.Reload
						&& reason == EditClassifier.ReasonNewDocument
					: currentSolution.GetDocumentIdsWithFilePath(linkTarget).IsEmpty
				;
				
				if(unknownSource) {
					
					StampOwnLinkTarget(linkTarget);
					
					return;
				}
				
				// Already has this text: an edit applied through TryApplyChanges syncs the
				// target once on its own, and a second pass would only write the file again.
				if(HasCurrentText(currentSolution, linkTarget))
					
					return;
			}
			
			InvalidateFile(linkTarget);
			
			// A file the write created is new to the workspace, and InvalidateFile has said so.
			// The watcher will say it again, at this path, on its own time — possibly after
			// the reload it caused has finished, when it would cause a second one.
			if(!existedBefore)
				StampOwnLinkTarget(linkTarget);
		}
		
		/// <summary>
		///     Records that RM has just written <paramref name="path"/> through a link, so that
		///     the watcher's reports of that write are recognised as such
		///     (see <see cref="IsOwnLinkTargetWrite"/>). The watcher can report a write after the
		///     suppression window has closed — its events arrive on their own time, and on
		///     Windows a replaced file is reported more than once.
		/// </summary>
		void StampOwnLinkTarget(string path)
		{
			lock(debounceLock) {
				
				// Room is made by dropping what has lapsed. Only when the table is full of live
				// stamps is it cleared, which costs those files a reload if a report still comes.
				if(ownedLinkTargets.Count >= MaxRmOwnedWriteSizes) {
					
					var now    = DateTime.UtcNow;
					var lapsed = ownedLinkTargets
						.Where(entry => entry.Value.UntilUtc < now)
						.Select(entry => entry.Key)
						.ToArray()
					;
					
					foreach(var key in lapsed)
						ownedLinkTargets.Remove(key);
					
					if(ownedLinkTargets.Count >= MaxRmOwnedWriteSizes)
						ownedLinkTargets.Clear();
				}
				
				try {
					
					var info = new FileInfo(path);
					
					ownedLinkTargets[path] = (info.Length, info.LastWriteTimeUtc, DateTime.UtcNow + OwnedLinkTargetWindow);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
					// Gone or locked — the flush handles the event the normal way.
					_ = ex
					;
				}
			}
		}
		
		// Whether the workspace's document at this path already holds what is on disk — whatever
		// kind of document it is: source, an AdditionalFiles item, analyzer config. False when
		// there is no such document, its text is not loaded, or the file cannot be read.
		static bool HasCurrentText(Solution solution, string path)
		{
			try {
				
				if(WorkspaceTextDocumentInfo.Resolve(solution, path).GetDocument(solution) is not { } document
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
	}
}
