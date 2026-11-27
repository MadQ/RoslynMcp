using System.Diagnostics;
using System.IO.Enumeration;

namespace RoslynMcp;

/// <summary>
///     One directory a workspace watches. <see cref="Recursive"/> roots cover a whole subtree;
///     the others cover only their own files. <see cref="Expands"/> marks a non-recursive root on
///     the path to a skipped directory: its other children are each watched recursively, so a
///     directory created under it later has to be adopted the same way.
/// </summary>
internal readonly record struct WatchRoot(string Path, bool Recursive, bool Expands);

/// <summary>
///     Decides which directories to watch so that the trees named by
///     <see cref="IgnoreRules.IsNeverInput"/> are never entered. A <see cref="FileSystemWatcher"/>
///     cannot exclude a directory, and on Linux a recursive one registers an inotify watch per
///     directory below it — a single watcher on a repository root walks all of <c>node_modules</c>
///     and <c>.git</c> (#309). Splitting the root into a few watchers around those directories
///     keeps the same coverage without the walk.
/// </summary>
internal static class WatchPlanner
{
	/// <summary>
	///     Most watchers one workspace may hold. Every <see cref="FileSystemWatcher"/> is its own
	///     inotify instance on Linux, where the per-user default limit is 128 and is shared with
	///     every other process of that user.
	/// </summary>
	public const int MaxRoots = 32;
	
	/// <summary>The server's path comparison (<see cref="SecurityBoundary.PathComparer"/>, #391).</summary>
	public static StringComparer PathComparer => SecurityBoundary.PathComparer;
	
	/// <summary>
	///     Plans the watch roots for the workspace at <paramref name="rootPath"/>, trying three
	///     shapes in order of coverage:
	///     <list type="number">
	///         <item>
	///             <c>root</c> — the whole root, split around skipped directories. Covers
	///             everything a single recursive watcher did except the skipped trees, whose events
	///             were dropped anyway.
	///         </item>
	///         <item>
	///             <c>projects</c> — when that needs more than <see cref="MaxRoots"/> watchers (a
	///             monorepo with a <c>node_modules</c> per package): only the project directories,
	///             split the same way, plus their ancestors and the directories of documents linked
	///             from elsewhere, non-recursively. An imported <c>.props</c> or <c>.targets</c>
	///             outside those directories is no longer watched.
	///         </item>
	///         <item>
	///             <c>fallback</c> — a single recursive watcher on the root, as before #309.
	///         </item>
	///     </list>
	///     Directories outside <paramref name="rootPath"/> are never watched, in any shape.
	/// </summary>
	public static WatchRoot[] Plan(
		string       rootPath,
		string[]     projectDirectories,
		string[]     documentDirectories,
		IgnoreRules  rules,
		out string   shape)
	{
		var whole = new Dictionary<string, WatchRoot>(PathComparer);
		
		if(Partition(rootPath, rules, whole)) {
			
			shape = "root";
			
			return [..whole.Values];
		}
		
		// Top-most project directories under the root: a project nested in another is already
		// covered by the outer one.
		var scopes = projectDirectories
			.Where(directory => IsUnderOrEqual(directory, rootPath))
			.Distinct(PathComparer)
			.ToArray()
		;
		
		scopes = [..scopes.Where(scope => !scopes.Any(other => !PathComparer.Equals(other, scope) && IsUnderOrEqual(scope, other)))];
		
		var scoped   = new Dictionary<string, WatchRoot>(PathComparer);
		var complete = scopes.Length > 0;
		
		foreach(var scope in scopes) {
			
			if(Partition(scope, rules, scoped))
				continue;
			
			complete = false;
			
			break;
		}
		
		if(complete) {
			
			// Evaluation inputs sit above the projects: Directory.Build.props, global.json,
			// .editorconfig, the solution file itself.
			foreach(var scope in scopes)
				for(var ancestor = Path.GetDirectoryName(scope); ancestor is not null && IsUnderOrEqual(ancestor, rootPath); ancestor = Path.GetDirectoryName(ancestor))
					scoped.TryAdd(ancestor, new(ancestor, Recursive: false, Expands: false));
			
			scoped.TryAdd(rootPath, new(rootPath, Recursive: false, Expands: false));
			
			foreach(var directory in documentDirectories)
				if(IsUnderOrEqual(directory, rootPath) && !scopes.Any(scope => IsUnderOrEqual(directory, scope)))
					scoped.TryAdd(directory, new(directory, Recursive: false, Expands: false));
			
			if(scoped.Count <= MaxRoots) {
				
				shape = "projects";
				
				return [..scoped.Values];
			}
		}
		
		shape = "fallback";
		
		return [new(rootPath, Recursive: true, Expands: false)];
	}
	
	/// <summary>
	///     Adds the watch roots covering <paramref name="directory"/> to <paramref name="roots"/>:
	///     one recursive root when nothing below it is skipped; otherwise a non-recursive root for
	///     each directory on the way to a skipped one, and a recursive root for every other child.
	///     Returns false as soon as <paramref name="roots"/> outgrows <see cref="MaxRoots"/>.
	/// </summary>
	static bool Partition(string directory, IgnoreRules rules, Dictionary<string, WatchRoot> roots)
	{
		var skipped = WorkspaceWalker.FindPrunedDirectories(directory, (ref FileSystemEntry entry) => rules.IsNeverInput(entry.FileName));
		
		if(skipped.Count == 0) {
			
			roots[directory] = new(directory, Recursive: true, Expands: false);
			
			return roots.Count <= MaxRoots;
		}
		
		// Every directory with a skipped directory somewhere below it.
		var spine = new HashSet<string>(PathComparer);
		
		foreach(var path in skipped)
			for(var ancestor = Path.GetDirectoryName(path); ancestor is not null && IsUnderOrEqual(ancestor, directory) && spine.Add(ancestor); ancestor = Path.GetDirectoryName(ancestor)) { }
		
		var pending = new Stack<string>();
		
		pending.Push(directory);
		
		while(pending.TryPop(out var current)) {
			
			roots[current] = new(current, Recursive: false, Expands: true);
			
			if(roots.Count > MaxRoots)
				
				return false;
			
			try {
				
				foreach(var child in new DirectoryInfo(current).EnumerateDirectories()) {
					
					// A link is not followed by the walk above, so it is not watched either.
					if(rules.IsNeverInput(child.Name) || WorkspaceWalker.IsLink(child))
						continue;
					
					if(spine.Contains(child.FullName))
						pending.Push(child.FullName);
					
					else
						roots[child.FullName] = new(child.FullName, Recursive: true, Expands: false);
				}
			}
			catch(Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				// Unreadable — its own files stay watched, its children do not.
			}
		}
		
		return roots.Count <= MaxRoots;
	}
	
	internal static bool IsUnderOrEqual(string path, string ancestor)
	{
		var trimmed = Path.TrimEndingDirectorySeparator(ancestor.AsSpan());
		
		if(trimmed.IsEmpty || path.Length < trimmed.Length)
			
			return false;
		
		// A filesystem root ("C:\", "/") keeps its separator when trimmed, so the boundary is the
		// root's own last character rather than the one after it. Checked before the text, which can
		// ask the file system about a case-only difference (#391 review).
		var atBoundary = path.Length == trimmed.Length
			|| IsSeparator(trimmed[^1])
			|| IsSeparator(path[trimmed.Length])
		;
		
		return atBoundary && SecurityBoundary.StartsWithPath(path, trimmed);
		
		static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
	}
}

/// <summary>
///     The file watchers of one workspace: the roots <see cref="WatchPlanner"/> chose, started on a
///     background thread so a tool call never waits for them. Events from every watcher funnel
///     into one callback, which is the workspace's existing debounce entry point — nothing
///     downstream knows there is more than one watcher. Whenever a plan replaces watchers, the
///     reconcile callback is run once the new ones are live, with the moment the change began.
///     <para>
///         The watchers are never switched off while they are in place, not even around RM's own
///         writes (#358). A change made while they were off would go unseen — nothing catches up on
///         it — and switching one back on is not instant: on macOS it starts a new event stream, on
///         Linux it adds an inotify watch per directory again. RM's own writes are reported like
///         any other, and the workspace recognises them when it handles the report: the document
///         already holds the file's text, or the file still has the stamp a write through a link
///         leaves.
///     </para>
/// </summary>
internal sealed class WorkspaceWatchSet(string rootPath, IgnoreRules rules, Action<string, bool> onChange, Action<DateTime> reconcile, FileLogger logger) : IDisposable
{
	// Files reported for a directory that appeared under an expanding root. Bounded so moving a
	// huge tree into the workspace cannot flood the debounce queue.
	const int AdoptedFileLimit = 10_000;
	
	// Guards watchers, disposed and scopeKey. Never held across a watcher start,
	// which is the slow operation this class exists to keep off the caller's thread.
	readonly Lock gate = new();
	
	// One plan-and-start at a time; a second Apply waits for the first rather than interleaving.
	readonly Lock applyLock = new();
	
	readonly Dictionary<string, (FileSystemWatcher Watcher, WatchRoot Root)> watchers = new(WatchPlanner.PathComparer);
	
	// The roots whose watcher could not start, each with why (#495). Guarded by the gate. A root
	// leaves when a later start succeeds or a new plan no longer has it. Until then nothing under
	// it is seen, and that used to be said in the log alone.
	readonly Dictionary<string, string> unwatched = new(WatchPlanner.PathComparer);
	
	// The unwatched roots the last news was about, so the same state is announced once.
	string announced = "";
	
	// The roots whose watcher runs and reported that it cannot watch all that is under it (#499):
	// on Linux the limit on watches, which fails one directory at a time and stops nothing.
	// Guarded by the gate. Such a watcher is started again by the next plan, and reports again
	// then whatever it still cannot watch.
	readonly Dictionary<string, string> degraded = new(WatchPlanner.PathComparer);
	
	// The latest moment up to which what is on disk is known: the caller's word at an Apply, or
	// the start of the last scan after an overflow. The next such scan looks from here.
	DateTime? syncedUtc;
	
	// A scan after an overflow is on its way; further overflows until it runs need no second one.
	bool catchUpPending;
	
	// The watchers Add is starting, until they are in the set or given up (guarded by the gate).
	// A watcher can report an error while it starts, before the set has it.
	readonly HashSet<FileSystemWatcher> adding = [];
	
	// Starts that are running (guarded by the gate). While one is, the state is on its way from
	// one plan to the next, and is announced when the start is over, not in between.
	int starting;
	
	// An overflow rarely comes alone: a checkout raises one after another.
	static readonly TimeSpan CatchUpDelay = TimeSpan.FromSeconds(1);
	
	// When an adopted directory is listed again, counted from its adoption (#339). Twice: soon,
	// for the file written right behind the directory, and once more for a watcher that took
	// its time to come up on a loaded machine.
	static readonly TimeSpan[] AdoptedLookBack = [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3)];
	
	// The adopted directories that are still to be listed again, each with the files reported
	// from it so far: by the listings, and by the watchers, which note theirs here (Note) so
	// that a look back does not say them a second time. Read and written under the gate.
	readonly Dictionary<string, HashSet<string>> lookingBack = new(WatchPlanner.PathComparer);
	
	bool    disposed;
	string? scopeKey;
	
	// Bumped by every Apply that changes the plan; a background start that finds a newer
	// version than its own leaves the watchers alone.
	int     applyVersion;
	
	// The directories of the last Apply, kept so Replan can plan again without the workspace.
	(string[] Projects, string[] Documents)? plannedInputs;
	
	// Set while a plan change is waiting for its watchers: the moment from which changes on disk
	// may have gone unseen. Held here rather than by each start, because a start that is
	// superseded must not reconcile before the plan that replaced it is watching.
	DateTime? reconcileSinceUtc;
	
	/// <summary>
	///     Plans and starts the watchers in the background, replacing any earlier plan. A call whose
	///     directories match the plan already in place does nothing, so it is cheap to make after
	///     every reload. <paramref name="sinceUtc"/> is the moment from which the caller cannot
	///     vouch for what is on disk; once the watchers are live — the point from which changes are
	///     seen — the reconcile callback is run with it.
	/// </summary>
	public void Apply(string[] projectDirectories, string[] documentDirectories, DateTime sinceUtc)
	{
		// Both sets shape the plan: project directories always, document directories in the
		// "projects" shape, where a document linked from elsewhere gets its own watch root.
		var key = string.Join('|', projectDirectories.Order(WatchPlanner.PathComparer))
		        + "\n"
		        + string.Join('|', documentDirectories.Order(WatchPlanner.PathComparer));
		
		int version;
		
		lock(gate) {
			
			if(disposed)
				
				return;
			
			// Also when the plan stays as it is: the caller has just read the disk, so a scan for
			// events an overflow dropped need not look further back than this.
			if(syncedUtc is not { } synced || sinceUtc > synced)
				syncedUtc = sinceUtc;
			
			if(key == scopeKey)
				
				return;
			
			scopeKey      = key;
			version       = ++applyVersion;
			plannedInputs = (projectDirectories, documentDirectories);
			
			OweReconcile(sinceUtc);
		}
		
		StartInBackground(version, projectDirectories, documentDirectories);
	}
	
	/// <summary>
	///     Plans again from the directories of the last <see cref="Apply"/>. For when the tree
	///     changed under an unchanged workspace — a root that has run out of watcher budget.
	/// </summary>
	void Replan()
	{
		int      version;
		string[] projectDirectories;
		string[] documentDirectories;
		
		lock(gate) {
			
			if(disposed || plannedInputs is not var (projects, documents))
				
				return;
			
			version             = ++applyVersion;
			projectDirectories  = projects;
			documentDirectories = documents;
			
			// Replacing watchers leaves a moment with the old ones gone and the new ones not
			// yet live, exactly as a plan change after a reload does.
			OweReconcile(DateTime.UtcNow);
		}
		
		StartInBackground(version, projectDirectories, documentDirectories);
	}
	
	// Callers hold the gate. The earliest moment wins: one reconcile from there covers every
	// plan change still waiting for its watchers.
	void OweReconcile(DateTime sinceUtc)
	{
		if(reconcileSinceUtc is not { } pending || sinceUtc < pending)
			reconcileSinceUtc = sinceUtc;
	}
	
	void StartInBackground(int version, string[] projectDirectories, string[] documentDirectories)
	{
		_ = Task.Run(() => {
			
			// Nothing is above this on a pool thread — an escape would take the process down.
			try {
				
				var live = Start(version, projectDirectories, documentDirectories);
				
				DateTime? since;
				
				lock(gate) {
					
					// Superseded, before or during the start: the newer plan's own start settles
					// everything, including the callbacks still pending.
					if(version != applyVersion)
						
						return;
					
					// Nothing is watching. Forget the key so the next Apply — after the next
					// reload — tries again instead of taking this plan as already in place.
					if(!live) {
						
						scopeKey = null;
						
						return;
					}
					
					// Part of the plan is not watching (#495): the same, so that a limit that was
					// raised since is noticed at the next reload. What is live stays live. A root
					// watched only in part (#499) is started again then, which is the one way to
					// give it the watches it lacks.
					if(unwatched.Count > 0 || degraded.Count > 0)
						scopeKey = null;
					
					since             = reconcileSinceUtc;
					reconcileSinceUtc = null;
				}
				
				if(since is { } sinceUtc)
					reconcile(sinceUtc);
			}
			catch(Exception ex) {
				
				logger.LogError("Watch", $"Starting watchers for '{rootPath}' failed: {ex.GetType().Name}: {ex.Message}");
				
				lock(gate)
					if(version == applyVersion)
						scopeKey = null;
			}
		});
	}
	
	// Returns whether watchers are live for the newest plan. False means nothing is watching.
	bool Start(int version, string[] projectDirectories, string[] documentDirectories)
	{
		lock(gate)
			starting++;
		
		try {
			return StartPlanned(version, projectDirectories, documentDirectories);
		}
		finally {
			
			lock(gate)
				starting--;
			
			// Here and not from each error while the watchers started: a degraded root is
			// cleared and reports again at every start, and each step would read as news.
			AnnounceUnwatched();
		}
	}
	
	bool StartPlanned(int version, string[] projectDirectories, string[] documentDirectories)
	{
		lock(applyLock) {
			
			// Pool tasks start in no particular order. A newer Apply that already ran owns the
			// watchers; putting this older plan in place over it would leave stale roots live
			// with nothing left to correct them.
			lock(gate) {
				
				if(disposed)
					
					return false;
				
				if(version != applyVersion)
					
					return false;
			}
			
			var started = Stopwatch.GetTimestamp();
			var plan    = WatchPlanner.Plan(rootPath, projectDirectories, documentDirectories, rules, out var shape);
			var wanted  = plan.ToDictionary(root => root.Path, WatchPlanner.PathComparer);
			var stale   = new List<FileSystemWatcher>();
			
			lock(gate) {
				
				if(disposed)
					
					return false;
				
				foreach(var (path, entry) in watchers) {
					
					// A degraded root's watcher is replaced: a watcher adds its watches when it
					// starts, so only a new start can add the ones it was refused.
					if(wanted.TryGetValue(path, out var root) && root == entry.Root && !degraded.ContainsKey(path))
						continue;
					
					stale.Add(entry.Watcher);
					watchers.Remove(path);
				}
				
				// A root the new plan does not have is no longer one that should be watched.
				foreach(var path in unwatched.Keys.Where(path => !wanted.ContainsKey(path)).ToArray())
					unwatched.Remove(path);
				
				// Each of them is started again below, or is no longer planned.
				degraded.Clear();
			}
			
			foreach(var watcher in stale)
				watcher.Dispose();
			
			var live = 0;
			
			foreach(var root in plan)
				if(IsWatched(root.Path) || Add(root, withinCap: false))
					live++;
			
			logger.LogInfo("Watch", $"{live}/{plan.Length} watch root(s) live ({shape}) in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms: {rootPath}");
			
			return live > 0;
		}
	}
	
	/// <summary>How many roots are watched, and the ones that are not or only in part, with why.</summary>
	public WatchStatus Status {
		get {
			static WatchRootProblem[] Sorted(Dictionary<string, string> roots)
				=> [..roots.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => new WatchRootProblem(entry.Key, entry.Value))];
			
			lock(gate)
				return new(watchers.Count, Sorted(unwatched), Sorted(degraded));
		}
	}
	
	// Says once, on the next tool answer, that part of the workspace is not watched: an agent
	// that never asks roslyn_check_drift would otherwise not learn that new files go unseen.
	// Once per state: the same roots failing again at the next reload are not news. A state is
	// taken as said only when the news was accepted; a full queue means it is tried again.
	void AnnounceUnwatched()
	{
		var    status = Status;
		string state  = string.Join('|', status.Unwatched.Select(root => root.Path)) + "\n" + string.Join('|', status.Degraded.Select(root => root.Path));
		
		lock(gate)
			if(starting > 0 || state == announced)
				
				return;
		
		if(status.HasProblem && !WatchNews.Post($"File watching is off for {status.Extent} '{rootPath}': {status.Problem} roslyn_check_drift lists the directories."))
			
			return;
		
		lock(gate)
			announced = state;
	}
	
	bool IsWatched(string path)
	{
		lock(gate)
			return watchers.ContainsKey(path);
	}
	
	bool Add(WatchRoot root, bool withinCap)
	{
		FileSystemWatcher? watcher = null;
		var                stopped = false;
		
		try {
			
			// All files, not only .cs: MSBuild inputs such as AdditionalFiles, .editorconfig,
			// props/targets, and global.json must all reach the shared reload classifier.
			// Directory names wherever a directory can arrive or leave as a whole: moving one
			// raises a single event for the directory and none for the files in it.
			watcher = new FileSystemWatcher(root.Path) {
				
				IncludeSubdirectories = root.Recursive,
				NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName | (root.Expands || root.Recursive ? NotifyFilters.DirectoryName : 0),
			};
			
			watcher.Changed += (_, e) => Report(e.FullPath, deleted: false);
			watcher.Created += (_, e) => Appeared(root, e.FullPath);
			watcher.Deleted += (_, e) => Vanished(root, e.FullPath);
			watcher.Renamed += (_, e) => {
				
				Vanished(root, e.OldFullPath);
				Appeared(root, e.FullPath);
			};
			watcher.Error += (sender, e) => Failed(root, (FileSystemWatcher) sender, e.GetException());
			
			// The slow step on Linux — one inotify watch per directory below a recursive root —
			// and the reason it runs outside the gate.
			lock(gate)
				adding.Add(watcher);
			
			watcher.EnableRaisingEvents = true;
		}
		catch(Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException) {
			
			watcher?.Dispose();
			logger.LogInfo("Watch", $"WARN: cannot watch '{root.Path}' ({ex.GetType().Name}: {ex.Message}) — changes under it are not seen; roslyn_check_drift names it");
			
			// A directory that is not there is not one that should be watched and is not.
			var exists = Directory.Exists(root.Path);
			
			lock(gate) {
				
				if(watcher is not null)
					adding.Remove(watcher);
				
				// Where the set has a watcher for the root, a second start that failed changes
				// nothing: the root is watched.
				if(!disposed && !watchers.ContainsKey(root.Path)) {
					
					// What it reported while starting is about a watcher that is not there.
					degraded.Remove(root.Path);
					
					// Tried again at the next reload either way. A start from a plan gets that from
					// its caller; a directory adopted later has only this.
					scopeKey = null;
					
					if(exists)
						unwatched[root.Path] = $"{ex.GetType().Name}: {ex.Message}";
					else
						unwatched.Remove(root.Path);
					
					stopped = exists;
				}
			}
			
			// Silent while a start runs, which announces at its end; said here for an adopted one.
			if(stopped)
				AnnounceUnwatched();
			
			return false;
		}
		catch {
			
			// Anything else goes to the caller, but not with the watcher left behind.
			if(watcher is not null) {
				
				lock(gate)
					adding.Remove(watcher);
				
				watcher.Dispose();
			}
			
			throw;
		}
		
		lock(gate) {
			
			// The cap is checked here, where the count cannot move, so two directories adopted
			// at once cannot both take the last slot. A planned root is always within it.
			var fits = !withinCap || watchers.Count < WatchPlanner.MaxRoots;
			
			// Windows switches a watcher off when it fails, and it can fail while it starts: such
			// a watcher is not taken, or the root would count as watched with nothing watching.
			var running = !OperatingSystem.IsWindows() || watcher.EnableRaisingEvents;
			
			adding.Remove(watcher);
			
			if(!disposed && fits && running && watchers.TryAdd(root.Path, (watcher, root))) {
				
				unwatched.Remove(root.Path);
				
				return true;
			}
			
			// Not taken. Where the set has another watcher for the root, this one changes nothing.
			if(!watchers.ContainsKey(root.Path)) {
				
				// What it reported while starting is about no watcher of the set.
				degraded.Remove(root.Path);
				
				// It stopped while it started. The root is tried again at the next reload, and
				// is listed unless its directory is what went away. TryAdd: Failed may have been
				// first, with the watcher's own words, and has then said it already.
				if(!running && !disposed) {
					
					scopeKey = null;
					
					if(Directory.Exists(root.Path))
						stopped = unwatched.TryAdd(root.Path, "the watcher stopped while it was starting");
					else
						unwatched.Remove(root.Path);
				}
			}
		}
		
		watcher.Dispose();
		
		if(stopped) {
			
			logger.LogInfo("Watch", $"WARN: watcher on '{root.Path}' stopped while it was starting — changes under it are not seen; roslyn_check_drift names it");
			AnnounceUnwatched();
		}
		
		return false;
	}
	
	// Watcher callbacks run on pool threads with nothing above them: an exception here would
	// take the process down, so every one of them reports through this guard.
	void Report(string fullPath, bool deleted)
	{
		try {
			
			if(!deleted)
				Note(fullPath);
			
			onChange(fullPath, deleted);
		}
		catch(Exception ex) {
			logger.LogError("Watch", $"Change callback failed for '{fullPath}': {ex.GetType().Name}: {ex.Message}");
		}
	}
	
	// A watcher's Error event. What it means depends on what is left of the watcher (#499):
	//   - Its directory is gone (on Windows the deletion surfaces as an access-denied error): the
	//     watcher is retired without a warning, and the plan's key is forgotten, so a directory that
	//     comes back is watched again after the next reload.
	//   - An event-buffer overflow: events were dropped and the watcher goes on. No state: what was
	//     missed is found by the scan that also runs once watchers go live.
	//   - On Windows any other error has switched the watcher off before it was raised. The root is
	//     then not watched at all, and is listed as such.
	//   - Elsewhere the watcher goes on. Linux reports this way each directory it could not add a
	//     watch for (the limit on watches), so the root is watched with holes: degraded.
	// Runs on a pool thread, or inside a watcher's start, with nothing above it. An error is taken
	// only from the watcher the set has for the root or from one that Add is starting: one that
	// was replaced can still report, and what it says is not about the root's watcher any more.
	void Failed(WatchRoot root, FileSystemWatcher watcher, Exception error)
	{
		try {
			
			var reason = $"{error.GetType().Name}: {error.Message}";
			
			if(!Directory.Exists(root.Path)) {
				
				var retired = false;
				
				lock(gate) {
					
					if(watchers.TryGetValue(root.Path, out var entry) && ReferenceEquals(entry.Watcher, watcher)) {
						
						watchers.Remove(root.Path);
						degraded.Remove(root.Path);
						
						scopeKey = null;
						retired  = true;
					}
				}
				
				if(retired)
					watcher.Dispose();
				
				return;
			}
			
			if(error is InternalBufferOverflowException) {
				
				logger.LogInfo("Watch", $"WARN: watcher on '{root.Path}' dropped events ({error.Message}) — scanning for what was missed");
				CatchUpSoon();
				
				return;
			}
			
			if(OperatingSystem.IsWindows() && !watcher.EnableRaisingEvents) {
				
				bool current;
				
				lock(gate) {
					
					if(disposed)
						
						return;
					
					current = watchers.TryGetValue(root.Path, out var entry) && ReferenceEquals(entry.Watcher, watcher);
					
					// One that Add is starting counts only while the set has no watcher for the
					// root: a second start for a watched root says nothing about the first.
					if(!current && (!adding.Contains(watcher) || watchers.ContainsKey(root.Path)))
						
						return;
					
					// One that Add is still starting stays Add's to dispose; Add sees that it is off.
					if(current)
						watchers.Remove(root.Path);
					
					degraded.Remove(root.Path);
					
					unwatched[root.Path] = reason;
					scopeKey             = null;
				}
				
				if(current)
					watcher.Dispose();
				
				logger.LogInfo("Watch", $"WARN: watcher on '{root.Path}' stopped ({reason}) — changes under it are not seen; roslyn_check_drift names it");
				AnnounceUnwatched();
				
				return;
			}
			
			lock(gate) {
				
				if(disposed)
					
					return;
				
				var current = watchers.TryGetValue(root.Path, out var entry) && ReferenceEquals(entry.Watcher, watcher);
				
				// One error per directory that got no watch, thousands at the limit: the first says it.
				if(!current && (!adding.Contains(watcher) || watchers.ContainsKey(root.Path)) || !degraded.TryAdd(root.Path, reason))
					
					return;
				
				scopeKey = null;
			}
			
			logger.LogInfo("Watch", $"WARN: watcher on '{root.Path}' reported an error and goes on ({reason}) — some directories under it may not be watched; roslyn_check_drift names it");
			AnnounceUnwatched();
		}
		catch(Exception ex) {
			logger.LogError("Watch", $"Handling a watcher error for '{root.Path}' failed: {ex.GetType().Name}: {ex.Message}");
		}
	}
	
	// Scans for what an overflow dropped, a moment later and once for all the overflows until then.
	// From the latest moment the disk was known: nothing says which events were lost, and a file
	// found by the scan that was not changed is dropped again by the flush's comparison.
	void CatchUpSoon()
	{
		lock(gate) {
			
			if(disposed || catchUpPending)
				
				return;
			
			catchUpPending = true;
		}
		
		_ = Task.Run(async () => {
			
			// Nothing is above this on a pool thread.
			try {
				
				await Task.Delay(CatchUpDelay);
				
				DateTime since;
				
				lock(gate) {
					
					// Cleared before the scan: an overflow during it asks for another one.
					catchUpPending = false;
					
					if(disposed)
						
						return;
					
					since = syncedUtc ?? DateTime.UtcNow;
				}
				
				var started = DateTime.UtcNow;
				
				reconcile(since);
				
				// Only a scan that ran to its end moves the moment the next one looks from.
				lock(gate)
					if(syncedUtc is not { } synced || started > synced)
						syncedUtc = started;
			}
			catch(Exception ex) {
				logger.LogError("Watch", $"Scanning for missed changes under '{rootPath}' failed: {ex.GetType().Name}: {ex.Message}");
			}
		});
	}
	
	void Appeared(WatchRoot root, string fullPath)
	{
		try {
			
			if(root.Expands && Directory.Exists(fullPath))
				Adopt(fullPath);
			
			else if(root.Recursive && Directory.Exists(fullPath)) {
				
				// Already covered by this watcher, so only its files are news.
				if(!IsLeftAlone(fullPath))
					_ = ReportFilesOf(fullPath, null);
			}
			else {
				
				Note(fullPath);
				onChange(fullPath, false);
			}
		}
		catch(Exception ex) {
			logger.LogError("Watch", $"Change callback failed for '{fullPath}': {ex.GetType().Name}: {ex.Message}");
		}
	}
	
	void Vanished(WatchRoot root, string fullPath)
	{
		if(root.Expands) {
			
			// A directory that goes away takes every watcher at or below it along. Left in the
			// table they would keep counting toward the cap — on Linux nothing else retires
			// them — and push later adoptions into needless re-plans.
			var gone = new List<FileSystemWatcher>();
			
			lock(gate) {
				
				foreach(var (path, entry) in watchers) {
					
					if(!WatchPlanner.IsUnderOrEqual(path, fullPath))
						continue;
					
					gone.Add(entry.Watcher);
					watchers.Remove(path);
				}
			}
			
			foreach(var watcher in gone)
				watcher.Dispose();
		}
		
		Report(fullPath, deleted: true);
	}
	
	/// <summary>
	///     A directory created (or moved) under an expanding root: watch it like its siblings, then
	///     report the files already in it. A directory rarely arrives empty — <c>mkdir</c> and the
	///     first file written into it are milliseconds apart, and a moved directory arrives full —
	///     and those files produced no event any watcher was there to see.
	/// </summary>
	void Adopt(string directory)
	{
		if(IsLeftAlone(directory))
			
			return;
		
		if(!Add(new(directory, Recursive: true, Expands: false), withinCap: true)) {
			
			bool full;
			
			lock(gate)
				full = !watchers.ContainsKey(directory) && watchers.Count >= WatchPlanner.MaxRoots;
			
			// Already watched, or the watcher could not start: nothing more to do here.
			if(!full)
				
				return;
			
			// One more watcher would pass the cap the planner holds itself to. Planning again
			// picks the shape that fits — project directories, or a single recursive root — and
			// covers the new directory through it.
			Replan();
		}
		
		var seen = new HashSet<string>(WatchPlanner.PathComparer);
		
		FileSystemWatcher? watcher;
		
		// Before the listing: what the new watcher reports while the directory is listed is
		// noted from here on. No watcher of its own (a new plan covers the directory another
		// way) means no look back either.
		lock(gate) {
			
			watcher = watchers.TryGetValue(directory, out var entry) ? entry.Watcher : null;
			
			if(watcher is not null)
				lookingBack[directory] = seen;
		}
		
		int reported;
		
		// A directory removed again at once, a temporary one, fails the listing: its entry
		// must not stay, or every later event would be compared with it for nothing.
		try {
			reported = ReportFilesOf(directory, seen);
		}
		catch {
			
			Forget(directory, seen);
			
			throw;
		}
		
		// Said in the log (#339): a directory that was made and not adopted, and one adopted
		// before its files were there, looked the same from outside, a new file nobody saw.
		logger.LogInfo("Watch", $"Adopted '{directory}', created under a watched root: {reported} file(s) in it reported");
		
		// A directory past the limit is not listed again: what was left out the first time
		// would be reported in the next ten thousand, and the limit would bound nothing.
		if(watcher is not null && reported < AdoptedFileLimit)
			LookBackAt(directory, watcher, seen);
		else
			Forget(directory, seen);
	}
	
	// Notes a file a watcher reported, for the directories still to be listed again.
	void Note(string fullPath)
	{
		lock(gate) {
			
			if(lookingBack.Count == 0)
				
				return;
			
			foreach(var (directory, seen) in lookingBack)
				if(WatchPlanner.IsUnderOrEqual(fullPath, directory))
					seen.Add(fullPath);
		}
	}
	
	// Ends the look back at a directory, unless a later adoption of the same path has begun its own.
	void Forget(string directory, HashSet<string> seen)
	{
		lock(gate)
			if(lookingBack.TryGetValue(directory, out var current) && ReferenceEquals(current, seen))
				lookingBack.Remove(directory);
	}
	
	/// <summary>
	///     Lists an adopted directory again a moment later and reports what has appeared since
	///     (#339). A directory is often adopted before its first file is there: the event for the
	///     directory comes, the listing finds nothing, and the file written the next instant is
	///     the new watcher's to see. Whether a watcher delivers from the instant it was started
	///     is the system's affair, and where it does not, that file was seen by nobody: not by
	///     the listing, which was before it, and not by the watcher, which was not up. A new
	///     source file then stayed unknown until something else caused a reload.
	///     <para>
	///         Only what nobody reported yet is reported: not what an earlier listing said, and
	///         not what a watcher said in between (<see cref="Note"/>). Said a second time, a
	///         project file, a props file or a source file no project compiles flags a reload
	///         of its own, after the one the first report caused has been made.
	///     </para>
	///     <para>
	///         For this watcher only: a directory removed and made again within the three
	///         seconds has a new watcher and a look back of its own.
	///     </para>
	/// </summary>
	void LookBackAt(string directory, FileSystemWatcher watcher, HashSet<string> seen)
	{
		_ = Task.Run(async () => {
			
			// Nothing is above this on a pool thread.
			try {
				
				var waited = TimeSpan.Zero;
				
				foreach(var at in AdoptedLookBack) {
					
					await Task.Delay(at - waited);
					
					waited = at;
					
					// A directory this watcher no longer watches is no longer this look back's to
					// report on: it went away, or a new plan covers it another way.
					lock(gate)
						if(disposed || !watchers.TryGetValue(directory, out var entry) || !ReferenceEquals(entry.Watcher, watcher))
							
							return;
					
					if(ReportFilesOf(directory, seen) is > 0 and var late)
						logger.LogInfo("Watch", $"'{directory}' held {late} more file(s) {at.TotalMilliseconds:0} ms after it was adopted that no watcher had reported — reported");
				}
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				// The directory went away between the check and the listing.
			}
			catch(Exception ex) {
				logger.LogError("Watch", $"Looking back at the adopted '{directory}' failed: {ex.GetType().Name}: {ex.Message}");
			}
			finally {
				Forget(directory, seen);
			}
		});
	}
	
	bool IsLeftAlone(string directory)
		=> rules.IsNeverInput(Path.GetFileName(directory.AsSpan()))
			|| WorkspaceWalker.IsLink(new DirectoryInfo(directory))
	;
	
	// Reports the files in a directory that arrived, and returns how many. With a set, only the
	// files that are not in it yet are reported, and they are added to it; without one the
	// files are noted for a look back that may be under way above the directory.
	int ReportFilesOf(string directory, HashSet<string>? seen)
	{
		var reported = 0;
		
		foreach(var file in WorkspaceWalker.EnumerateFiles(directory, "*", recursive: true, (ref FileSystemEntry entry) => rules.IsNeverInput(entry.FileName))) {
			
			// Asked and taken in one step, with a set: between the two a watcher could note and
			// report the same file. At the limit nothing is taken, so the file it stops at
			// stays unsaid.
			if(seen is not null) {
				
				lock(gate)
					if(seen.Contains(file) || reported < AdoptedFileLimit && !seen.Add(file))
						continue;
			}
			
			if(reported == AdoptedFileLimit) {
				
				logger.LogInfo("Watch", $"WARN: '{directory}' appeared with more than {AdoptedFileLimit} files — the rest were not reported; roslyn_check_drift cannot see new files, so reload with roslyn_respawn if sources are missing");
				
				break;
			}
			
			if(seen is null)
				Note(file);
			
			reported++;
			onChange(file, false);
		}
		
		return reported;
	}
	
	public void Dispose()
	{
		FileSystemWatcher[] toDispose;
		
		lock(gate) {
			
			if(disposed)
				
				return;
			
			disposed  = true;
			toDispose = [..watchers.Values.Select(entry => entry.Watcher)];
			
			watchers.Clear();
			unwatched.Clear();
			degraded.Clear();
		}
		
		foreach(var watcher in toDispose)
			watcher.Dispose();
	}
}
