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
	
	public static readonly StringComparer PathComparer = OperatingSystem.IsLinux()
		? StringComparer.Ordinal
		: StringComparer.OrdinalIgnoreCase;
	
	static readonly StringComparison pathComparison = OperatingSystem.IsLinux()
		? StringComparison.Ordinal
		: StringComparison.OrdinalIgnoreCase;
	
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
					if(rules.IsNeverInput(child.Name) || child.Attributes.HasFlag(FileAttributes.ReparsePoint))
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
		
		return path.AsSpan().StartsWith(trimmed, pathComparison)
			&& (path.Length == trimmed.Length || path[trimmed.Length] == Path.DirectorySeparatorChar || path[trimmed.Length] == Path.AltDirectorySeparatorChar);
	}
}

/// <summary>
///     The file watchers of one workspace: the roots <see cref="WatchPlanner"/> chose, started on a
///     background thread so a tool call never waits for them. Events from every watcher funnel
///     into one callback, which is the workspace's existing debounce entry point — nothing
///     downstream knows there is more than one watcher. Whenever a plan replaces watchers, the
///     reconcile callback is run once the new ones are live, with the moment the change began.
/// </summary>
internal sealed class WorkspaceWatchSet(string rootPath, IgnoreRules rules, Action<string, bool> onChange, Action<DateTime> reconcile, FileLogger logger) : IDisposable
{
	// Files reported for a directory that appeared under an expanding root. Bounded so moving a
	// huge tree into the workspace cannot flood the debounce queue.
	const int AdoptedFileLimit = 10_000;
	
	// Guards watchers, suspendCount, disposed and scopeKey. Never held across a watcher start,
	// which is the slow operation this class exists to keep off the caller's thread.
	readonly Lock gate = new();
	
	// One plan-and-start at a time; a second Apply waits for the first rather than interleaving.
	readonly Lock applyLock = new();
	
	readonly Dictionary<string, (FileSystemWatcher Watcher, WatchRoot Root)> watchers = new(WatchPlanner.PathComparer);
	
	int     suspendCount;
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
			
			if(disposed || key == scopeKey)
				
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
					
					if(wanted.TryGetValue(path, out var root) && root == entry.Root)
						continue;
					
					stale.Add(entry.Watcher);
					watchers.Remove(path);
				}
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
	
	bool IsWatched(string path)
	{
		lock(gate)
			return watchers.ContainsKey(path);
	}
	
	bool Add(WatchRoot root, bool withinCap)
	{
		FileSystemWatcher? watcher = null;
		
		// Set under the gate once the watcher is in the table. Until then Suspend cannot see it,
		// so its events are dropped here instead whenever a suspension is active — otherwise a
		// watcher that is still starting would report the very write the suspension is hiding.
		var registered = false;
		
		bool Muted() => !Volatile.Read(ref registered) && Volatile.Read(ref suspendCount) > 0;
		
		try {
			
			// All files, not only .cs: MSBuild inputs such as AdditionalFiles, .editorconfig,
			// props/targets, and global.json must all reach the shared reload classifier.
			// Directory names only where a new directory has to be adopted.
			watcher = new FileSystemWatcher(root.Path) {
				
				IncludeSubdirectories = root.Recursive,
				NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName | (root.Expands ? NotifyFilters.DirectoryName : 0),
			};
			
			watcher.Changed += (_, e) => { if(!Muted()) Report(e.FullPath, deleted: false); };
			watcher.Created += (_, e) => { if(!Muted()) Appeared(root, e.FullPath); };
			watcher.Deleted += (_, e) => { if(!Muted()) Vanished(root, e.FullPath); };
			watcher.Renamed += (_, e) => {
				
				if(Muted())
					
					return;
				
				Vanished(root, e.OldFullPath);
				Appeared(root, e.FullPath);
			};
			watcher.Error += (_, e) => Failed(root, e.GetException());
			
			// The slow step on Linux — one inotify watch per directory below a recursive root —
			// and the reason it runs outside the gate.
			watcher.EnableRaisingEvents = true;
		}
		catch(Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException) {
			
			watcher?.Dispose();
			logger.LogInfo("Watch", $"WARN: cannot watch '{root.Path}' ({ex.GetType().Name}: {ex.Message}) — changes under it are not seen; roslyn_check_drift reports them");
			
			return false;
		}
		
		lock(gate) {
			
			// The cap is checked here, where the count cannot move, so two directories adopted
			// at once cannot both take the last slot. A planned root is always within it.
			var fits = !withinCap || watchers.Count < WatchPlanner.MaxRoots;
			
			if(!disposed && fits && watchers.TryAdd(root.Path, (watcher, root))) {
				
				// A suspension that began while this watcher was starting did not see it.
				if(suspendCount > 0)
					watcher.EnableRaisingEvents = false;
				
				Volatile.Write(ref registered, true);
				
				return true;
			}
		}
		
		watcher.Dispose();
		
		return false;
	}
	
	// Watcher callbacks run on pool threads with nothing above them: an exception here would
	// take the process down, so every one of them reports through this guard.
	void Report(string fullPath, bool deleted)
	{
		try {
			onChange(fullPath, deleted);
		}
		catch(Exception ex) {
			logger.LogError("Watch", $"Change callback failed for '{fullPath}': {ex.GetType().Name}: {ex.Message}");
		}
	}
	
	// A watcher's own directory being deleted surfaces here as an access-denied error; that is
	// the directory going away, not a missed change, so the watcher is retired without a warning.
	// Anything else — typically an event-buffer overflow — may have dropped events.
	void Failed(WatchRoot root, Exception error)
	{
		if(Directory.Exists(root.Path)) {
			
			logger.LogInfo("Watch", $"WARN: watcher on '{root.Path}' failed ({error.GetType().Name}: {error.Message}) — changes may have been missed; roslyn_check_drift reports them");
			
			return;
		}
		
		FileSystemWatcher? gone = null;
		
		lock(gate)
			if(watchers.Remove(root.Path, out var entry))
				gone = entry.Watcher;
		
		gone?.Dispose();
	}
	
	void Appeared(WatchRoot root, string fullPath)
	{
		try {
			
			if(root.Expands && Directory.Exists(fullPath))
				Adopt(fullPath);
			
			else
				onChange(fullPath, false);
		}
		catch(Exception ex) {
			logger.LogError("Watch", $"Change callback failed for '{fullPath}': {ex.GetType().Name}: {ex.Message}");
		}
	}
	
	void Vanished(WatchRoot root, string fullPath)
	{
		if(root.Expands) {
			
			FileSystemWatcher? gone = null;
			
			lock(gate)
				if(watchers.Remove(fullPath, out var entry))
					gone = entry.Watcher;
			
			gone?.Dispose();
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
		if(rules.IsNeverInput(Path.GetFileName(directory.AsSpan()))
		   || File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
			
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
		
		var reported = 0;
		
		foreach(var file in WorkspaceWalker.EnumerateFiles(directory, "*", recursive: true, (ref FileSystemEntry entry) => rules.IsNeverInput(entry.FileName))) {
			
			if(++reported > AdoptedFileLimit) {
				
				logger.LogInfo("Watch", $"WARN: '{directory}' appeared with more than {AdoptedFileLimit} files — the rest were not reported; roslyn_check_drift cannot see new files, so reload with roslyn_respawn if sources are missing");
				
				break;
			}
			
			onChange(file, false);
		}
	}
	
	/// <summary>
	///     Stops every watcher raising events until the matching <see cref="Resume"/>. Ref-counted:
	///     concurrent suspenders each increment, and events resume only when the last one leaves.
	/// </summary>
	public void Suspend()
	{
		lock(gate) {
			
			suspendCount++;
			
			foreach(var (watcher, _) in watchers.Values)
				watcher.EnableRaisingEvents = false;
		}
	}
	
	public void Resume()
	{
		var failed = new List<FileSystemWatcher>();
		
		lock(gate) {
			
			if(--suspendCount > 0 || disposed)
				
				return;
			
			foreach(var (path, (watcher, _)) in watchers) {
				
				try {
					watcher.EnableRaisingEvents = true;
				}
				catch(Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) {
					
					// The directory went away while suspended.
					failed.Add(watcher);
					watchers.Remove(path);
				}
			}
		}
		
		foreach(var watcher in failed)
			watcher.Dispose();
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
		}
		
		foreach(var watcher in toDispose)
			watcher.Dispose();
	}
}
