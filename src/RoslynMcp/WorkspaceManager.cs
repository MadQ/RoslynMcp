using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp;

/// <summary>How a projectPath argument was resolved to a .csproj or directory.</summary>
enum ResolutionKind
{
	/// <summary>Agent passed a .csproj path directly — ideal path, no inference needed.</summary>
	Explicit,
	/// <summary>A .sln/.slnx path — passed by the agent, or the session default when projectPath was omitted.</summary>
	Solution,
	/// <summary>Agent passed a directory; a single .csproj was found inside it.</summary>
	Directory,
	/// <summary>Agent passed a source file; walked up the tree to find the .csproj.</summary>
	FileWalkUp,
	/// <summary>Agent passed a bare filename; matched to a cached MSBuild workspace by SyntaxTree scan.</summary>
	InferredFromCache,
	/// <summary>No .csproj found — running in AdhocWorkspace with reduced functionality.</summary>
	Adhoc,
}

/// <summary>
///     Load-health snapshot for a workspace. <paramref name="ProjectsWithoutReferences"/> is the
///     authoritative signal: a successfully built project always references at least the core
///     library, so a non-empty set means the design-time build silently dropped them and symbol
///     queries over those projects return wrong-but-plausible results rather than failing.
/// </summary>
/// <param name="ProjectsWithoutReferences">Projects the live workspace holds with zero metadata references.</param>
/// <param name="LoadWarnings">Warnings from the load that produced the live workspace.</param>
/// <param name="LastUnhealthyLoad">
///     Timestamped record of the most recent unhealthy load, retained across the healing reload
///     that clears <paramref name="LoadWarnings"/>. Non-null after an episode even once recovered.
/// </param>
/// <param name="ReloadPending">
///     A file change could not be applied incrementally and the reload servicing it has not
///     completed, so the workspace is behind disk. Distinct from drift: drift compares mtimes of
///     files the workspace already knows about, and cannot see a file that is not a document yet.
/// </param>
internal sealed record WorkspaceHealth(
	string[] ProjectsWithoutReferences,
	string[] LoadWarnings,
	string?  LastUnhealthyLoad,
	bool     ReloadPending)
{
	/// <summary>True when every loaded project resolved at least one metadata reference.</summary>
	public bool IsHealthy => ProjectsWithoutReferences.Length == 0;
}

/// <summary>
///     Manages Roslyn workspaces with LRU caching. When a .sln/.slnx is found above a .csproj,
///     loads the full solution so cross-project semantics (references, rename, implementations)
///     work naturally. Falls back to single-project or AdhocWorkspace when no solution exists.
///     Thread-safe for parallel agent access.
/// </summary>
internal sealed partial class WorkspaceManager : IDisposable
{
	record CacheEntry(string Key, WorkspaceInstance Instance, SecurityBoundary Boundary, DateTime LastAccess);
	
	readonly Dictionary<string, CacheEntry> cache = new(StringComparer.OrdinalIgnoreCase);
	
	// Secondary index: maps normalized .csproj paths → cache keys for solution-level entries.
	// Enables O(1) lookup when a tool passes a .csproj that's part of an already-loaded solution.
	readonly Dictionary<string, string> projectToCacheKey = new(StringComparer.OrdinalIgnoreCase)
	;
	
	readonly object     cacheLock = new();
	
	// Loads in progress, by the cache key they will be stored under (#377). Guarded by cacheLock;
	// the load itself runs outside it. A caller that needs a workspace whose load has already
	// started waits for that load instead of starting a second one: two design-time builds of
	// one solution race on its obj\ files, and one of them can drop references. The entry is
	// removed once the instance is in the cache, or once the load has failed — every caller
	// waiting on it then gets the failure, and the next call tries again.
	readonly Dictionary<string, Lazy<WorkspaceInstance>> pendingLoads = new(StringComparer.OrdinalIgnoreCase);
	
	readonly int        maxCachedWorkspaces;
	readonly FileLogger logger;
	
	// Deferred disposal: evicted instances are held here with a grace period so concurrent
	// callers that already hold a reference can finish before the instance is disposed.
	// Swept on each eviction and in Dispose(). See issue #145 item 1.
	readonly List<(WorkspaceInstance Instance, DateTime EvictedAt)> retired = new()
	;
	const    int RetiredGraceSeconds = 30;
	
	
	public WorkspaceManager(FileLogger logger)
	{
		this.logger         = logger;
		maxCachedWorkspaces = ServerArgs.Current.MaxCachedWorkspaces;
	}
	
	// ── Cache helper ─────────────────────────────────────────────────────────
	
	/// <summary>
	///     Resolves a path to a cached WorkspaceInstance, loading if necessary.
	///     For .csproj paths, searches upward for a .sln/.slnx and loads the full solution
	///     so all projects share one workspace. Falls back to single-project loading when
	///     no solution is found. Handles LRU eviction.
	/// </summary>
	WorkspaceInstance GetOrLoadInstance(string resolvedPath)
	{
		var normalizedPath = Path.GetFullPath(resolvedPath);
		
		// Fast path: cache hit under short lock — doesn't block on workspace loading.
		lock(cacheLock) {
			
			if(projectToCacheKey.TryGetValue(normalizedPath, out var mappedKey) && cache.TryGetValue(mappedKey, out var mappedEntry)) {
				
				cache[mappedKey] = mappedEntry with { LastAccess = DateTime.UtcNow };
				
				return mappedEntry.Instance;
			}
			
			if(cache.TryGetValue(normalizedPath, out var entry)) {
				
				cache[normalizedPath] = entry with { LastAccess = DateTime.UtcNow };
				
				return entry.Instance;
			}
		}
		
		// Slow path: load outside lock so other tool calls can still hit the cache.
		// The cache key is decided first, from the path alone, so that a load in progress can be
		// found by it (#377). Cheap: a mode lookup and, for a .csproj, a walk up for a solution.
		var (cacheKey, load) = PlanLoad(normalizedPath);
		
		Lazy<WorkspaceInstance> pending;
		bool                    waiting;
		
		lock(cacheLock) {
			
			// Loaded while the key was being worked out.
			if(cache.TryGetValue(cacheKey, out var loaded)) {
				
				TouchAndAlias(cacheKey, loaded, normalizedPath);
				
				return loaded.Instance;
			}
			
			waiting = pendingLoads.TryGetValue(cacheKey, out pending!);
			
			if(!waiting) {
				
				// The load stores its own result, once, from inside the Lazy: a caller that waited on
				// it only ever reads the cache afterwards. Were every caller to store what it was
				// handed, one arriving after the instance had been stored and evicted again would put
				// a retired instance back — and the sweep would dispose it while it was cached.
				Lazy<WorkspaceInstance>? created = null;
				
				created = new(() => Publish(load(), cacheKey, normalizedPath, created!), LazyThreadSafetyMode.ExecutionAndPublication);
				pending = created;
				pendingLoads[cacheKey] = created;
			}
		}
		
		// Logged outside the lock, which is kept free of anything slow (#277).
		if(waiting)
			logger.LogInfo("Load", $"Waiting for the load already in progress: {cacheKey}");
		
		WorkspaceInstance instance;
		
		try {
			instance = pending.Value;
		}
		catch {
			
			// Every caller waiting on this load gets the same exception (Lazy keeps it). The entry
			// goes, so that the next call loads afresh.
			lock(cacheLock)
				if(pendingLoads.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, pending))
					pendingLoads.Remove(cacheKey);
			
			throw;
		}
		
		// The path this caller came in by is remembered as well — but only while the cache still
		// holds this very instance. One evicted since is on its way to disposal, and never goes back.
		if(waiting)
			lock(cacheLock)
				if(cache.TryGetValue(cacheKey, out var entry) && ReferenceEquals(entry.Instance, instance))
					TouchAndAlias(cacheKey, entry, normalizedPath);
		
		return instance;
	}
	
	// Callers hold cacheLock. Marks the entry used and lets the path find it directly next time.
	void TouchAndAlias(string cacheKey, CacheEntry entry, string normalizedPath)
	{
		cache[cacheKey] = entry with { LastAccess = DateTime.UtcNow };
		
		if(!normalizedPath.Equals(cacheKey, StringComparison.OrdinalIgnoreCase))
			projectToCacheKey[normalizedPath] = cacheKey;
	}
	
	/// <summary>
	///     Stores a workspace that has just been loaded, from inside the load's <see cref="Lazy{T}"/>
	///     — once per load, whoever else waits on it (#377). Returns the instance callers are to use:
	///     the one loaded, or one already cached for the same workspace under another path, in which
	///     case the one loaded is torn down.
	/// </summary>
	WorkspaceInstance Publish(WorkspaceInstance instance, string cacheKey, string normalizedPath, Lazy<WorkspaceInstance> pending)
	{
		// Instances this insert must tear down: the one just loaded when another thread won the
		// race, or retired instances whose grace period has expired. Collected under cacheLock,
		// disposed after it (#277): WorkspaceInstance.Dispose waits on timer callbacks and
		// in-flight reloads, and every tool call resolves its workspace through cacheLock — one
		// teardown that stalled inside the lock stopped the whole server answering.
		List<WorkspaceInstance>? doomed = null;
		
		try {
			
			lock(cacheLock) {
				
				if(pendingLoads.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, pending))
					pendingLoads.Remove(cacheKey);
				
				if(projectToCacheKey.TryGetValue(normalizedPath, out var raceKey)
					&& cache.TryGetValue(raceKey, out var raceEntry)) {
					
					doomed         = [instance];
					cache[raceKey] = raceEntry with { LastAccess = DateTime.UtcNow };
					
					return raceEntry.Instance;
				}
				
				if(cache.TryGetValue(cacheKey, out var existing)) {
					
					doomed = [instance];
					
					// Record the alias even on a lost race, or the next call for this path would
					// slow-path load-and-discard again (see below).
					TouchAndAlias(cacheKey, existing, normalizedPath);
					
					return existing.Instance;
				}
				
				foreach(var csproj in instance.ProjectPaths)
					projectToCacheKey[csproj] = cacheKey;
				
				// Adhoc instances are keyed by RootPath (the containing directory) but don't populate
				// ProjectPaths, so a .csproj-resolving request in adhoc mode would miss both fast-path
				// lookups on every call — re-loading and discarding a full AdhocWorkspace each time,
				// and bypassing the keyed lookups in WriteAndInvalidate/ApplyChanges/InvalidateFile
				// (losing FSW suppression). Alias the requested path to the cache key it landed on.
				if(!normalizedPath.Equals(cacheKey, StringComparison.OrdinalIgnoreCase))
					projectToCacheKey[normalizedPath] = cacheKey;
				
				if(cache.Count >= maxCachedWorkspaces) {
					
					var lru = cache.OrderBy(kvp => kvp.Value.LastAccess).First();
					
					cache.Remove(lru.Key);
					
					var staleKeys = projectToCacheKey
						.Where(kvp => string.Equals(kvp.Value, lru.Key, StringComparison.OrdinalIgnoreCase))
						.Select(kvp => kvp.Key)
						.ToArray()
					;
					
					foreach(var k in staleKeys)
						projectToCacheKey.Remove(k);
					
					// Defer disposal — concurrent callers may still hold a reference.
					retired.Add((lru.Value.Instance, DateTime.UtcNow))
					;
					doomed = SweepRetired();
				}
				
				var boundary = new SecurityBoundary(instance.RootPath);
				
				cache[cacheKey] = new CacheEntry(cacheKey, instance, boundary, DateTime.UtcNow);
				
				return instance;
			}
		}
		finally {
			
			if(doomed is { Count: > 0 })
				DisposeDetached(doomed);
		}
	}
	
	/// <summary>
	///     Where a path's workspace will be cached, and how to load it — decided before the load, so
	///     that a load in progress can be found by its key (#377). The routing is the one the load
	///     always followed: adhoc when the effective mode says so, a solution as itself, a .csproj
	///     through the solution above it when there is one, and anything else as a directory.
	/// </summary>
	(string CacheKey, Func<WorkspaceInstance> Load) PlanLoad(string normalizedPath)
	{
		// Route on the *requested* effective mode, not just the post-hoc ResolvedMode — on a
		// fresh process ResolvedMode is still Auto (EnsureReady only runs inside the
		// WorkspaceInstance constructor, after routing has committed), so the first .csproj
		// load in adhoc mode would wrongly take the MSBuild branch and fail (#229).
		// ResolvedMode == Adhoc stays as a fallback: once the process has skipped MSBuild
		// registration, MSBuildWorkspace can never work, whatever this path requests.
		if(MSBuildBootstrap.ResolvedMode == WorkspaceMode.Adhoc
			|| ProjectConfig.EffectiveWorkspaceMode(normalizedPath, logger) == WorkspaceMode.Adhoc) {
			
			// An adhoc instance is keyed by its RootPath, which is exactly this directory.
			var directory = Directory.Exists(normalizedPath) ? normalizedPath : Path.GetDirectoryName(normalizedPath)!;
			
			return (directory, () => WorkspaceInstance.ForDirectory(directory, logger));
		}
		
		if(IsSolutionPath(normalizedPath))
			
			return (normalizedPath, () => WorkspaceInstance.ForSolution(normalizedPath, logger));
		
		if(normalizedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) {
			
			var solutionPath = FindSolutionFileUpwards(Path.GetDirectoryName(normalizedPath)!);
			
			if(solutionPath is not null) {
				
				var solutionKey = Path.GetFullPath(solutionPath);
				
				return (solutionKey, () => WorkspaceInstance.ForSolution(solutionPath, logger));
			}
			
			return (normalizedPath, () => WorkspaceInstance.ForProject(normalizedPath, logger));
		}
		
		return (normalizedPath, () => WorkspaceInstance.ForDirectory(normalizedPath, logger));
	}
	
	// ── Public API ───────────────────────────────────────────────────────────
	
	public Compilation GetCompilation(string resolvedProjectPath)
	{
		var instance = GetOrLoadInstance(resolvedProjectPath);
		
		return instance.GetCompilation(Path.GetFullPath(resolvedProjectPath));
	}
	
	public Solution GetSolution(string resolvedProjectPath)
	{
		var instance = GetOrLoadInstance(resolvedProjectPath);
		
		return instance.GetSolution();
	}
	
	/// <summary>
	///     Load-health warnings for the workspace serving this path: MSBuild load failures and
	///     projects whose metadata references were silently dropped by the design-time build.
	/// </summary>
	public string[] GetLoadWarnings(string resolvedProjectPath)
		=> GetOrLoadInstance(resolvedProjectPath).LoadWarnings;

	/// <summary>Load-health snapshot for the workspace serving this path — see <see cref="WorkspaceHealth"/>.</summary>
	public WorkspaceHealth GetHealth(string resolvedProjectPath)
		=> GetOrLoadInstance(resolvedProjectPath).Health;
	
	/// <summary>UTC time of the last disk-sync event for the workspace serving this path.</summary>
	public DateTime GetLastSyncedUtc(string resolvedProjectPath)
		=> GetOrLoadInstance(resolvedProjectPath).LastSyncedUtc;
	
	/// <summary>Solution snapshot without triggering a pending reload — see WorkspaceInstance.PeekSolution.</summary>
	public Solution PeekSolution(string resolvedProjectPath)
		=> GetOrLoadInstance(resolvedProjectPath).PeekSolution();
	
	public Project GetProject(string resolvedProjectPath)
	{
		var instance = GetOrLoadInstance(resolvedProjectPath);
		
		return instance.GetProject(Path.GetFullPath(resolvedProjectPath));
	}
	
	public (string RootPath, bool IsMSBuild, string? CsprojPath) GetWorkspaceInfo(string resolvedProjectPath)
	{
		var instance   = GetOrLoadInstance(resolvedProjectPath);
		var csprojPath = instance.FindCsprojForPath(Path.GetFullPath(resolvedProjectPath));
		
		return (instance.RootPath, instance.IsMSBuild, csprojPath);
	}
	
	public SecurityBoundary GetSecurityBoundary(string resolvedProjectPath)
	{
		var normalizedPath = Path.GetFullPath(resolvedProjectPath);
		
		lock(cacheLock) {
			
			if(projectToCacheKey.TryGetValue(normalizedPath, out var mappedKey)
				&& cache.TryGetValue(mappedKey, out var mapped))
				
				return mapped.Boundary;
			
			if(cache.TryGetValue(normalizedPath, out var direct))
				
				return direct.Boundary;
		}
		
		// Not cached yet — load the workspace (which creates and caches the boundary).
		GetOrLoadInstance(resolvedProjectPath)
		;
		
		lock(cacheLock) {
			
			if(projectToCacheKey.TryGetValue(normalizedPath, out var mappedKey2)
				&& cache.TryGetValue(mappedKey2, out var mapped2))
				
				return mapped2.Boundary;
			
			if(cache.TryGetValue(normalizedPath, out var direct2))
				
				return direct2.Boundary;
		}
		
		throw new InvalidOperationException($"Workspace loaded but no boundary found for '{normalizedPath}'.");
	}
	
	
	public void InvalidateFile(string resolvedProjectPath, string fullPath)
	{
		var normalizedPath = Path.GetFullPath(resolvedProjectPath);
		
		lock(cacheLock) {
			
			// Check secondary index (solution-level entries).
			if(projectToCacheKey.TryGetValue(normalizedPath, out var mappedKey)
				&& cache.TryGetValue(mappedKey, out var entry)) {
				
				entry.Instance.InvalidateFile(fullPath);
				
				return;
			}
			
			if(cache.TryGetValue(normalizedPath, out var directEntry))
				directEntry.Instance.InvalidateFile(fullPath);
		}
	}
	
	
	/// <summary>
	///     Applies an updated solution to the workspace and writes changed documents to disk
	///     (MSBuildWorkspace only). FSW events are suppressed during the write to prevent
	///     reload loops. Use this instead of direct file I/O + <see cref="InvalidateFile"/>
	///     for tools that already hold the updated <see cref="Solution"/> in memory.
	/// </summary>
	public bool ApplyChanges(string resolvedProjectPath, Solution newSolution)
	{
		var normalizedPath = Path.GetFullPath(resolvedProjectPath);
		
		lock(cacheLock) {
			
			if(projectToCacheKey.TryGetValue(normalizedPath, out var mappedKey)
				&& cache.TryGetValue(mappedKey, out var entry)) {
				
				if(entry.Instance.ApplyChangesToDisk(newSolution))
					
					return true;
				
				entry.Instance.MarkReloadNeeded(normalizedPath);
				
				return false;
			}
			
			if(cache.TryGetValue(normalizedPath, out var directEntry)) {
				
				if(directEntry.Instance.ApplyChangesToDisk(newSolution))
					
					return true;
				
				directEntry.Instance.MarkReloadNeeded(normalizedPath);
				
				return false;
			}
			
			return false;
		}
	}
	
	public bool TryApplyTextChange(string resolvedProjectPath, string fullPath, SourceText text)
	{
		var normalizedPath = Path.GetFullPath(resolvedProjectPath);
		
		lock(cacheLock) {
			
			if(projectToCacheKey.TryGetValue(normalizedPath, out var mappedKey)
				&& cache.TryGetValue(mappedKey, out var entry))
				
				return entry.Instance.TryApplyTextChange(fullPath, text);
			
			if(cache.TryGetValue(normalizedPath, out var directEntry))
				
				return directEntry.Instance.TryApplyTextChange(fullPath, text);
		}
		
		return false;
	}
	
	public Task WriteAndInvalidate(string resolvedProjectPath, string fullPath, Func<Task> write)
	=> WriteAndInvalidate(resolvedProjectPath, fullPath, null, write);
	
	public async Task WriteAndInvalidate(string resolvedProjectPath, string fullPath, string? movedFromPath, Func<Task> write)
	{
		var normalizedPath = Path.GetFullPath(resolvedProjectPath);
		WorkspaceInstance? instance = null;
		
		// Grab the instance reference under the lock but await the write outside it.
		lock(cacheLock) {
			
			if(projectToCacheKey.TryGetValue(normalizedPath, out var mappedKey)
				&& cache.TryGetValue(mappedKey, out var entry))
				
				instance = entry.Instance;
			
			else if(cache.TryGetValue(normalizedPath, out var directEntry))
				instance = directEntry.Instance;
		}
		
		if(instance is not null)
			await instance.WriteAndInvalidate(fullPath, movedFromPath, write);
		else
			// No cached workspace — write directly. A concurrent thread could load the
			// workspace between the null-check and the write, but the resulting FSW event
			// is self-healing: the debounce timer re-reads the (correct) file from disk.
			await write()
			;
	}
	
	
	// ── Retired instance management ──────────────────────────────────────
	
	/// <summary>
	///     Collects retired instances whose grace period has elapsed for disposal after the lock is released.
	///     Must be called under <see cref="cacheLock"/> while mutating <see cref="retired"/>.
	/// </summary>
	List<WorkspaceInstance> SweepRetired()
	{
		var cutoff  = DateTime.UtcNow.AddSeconds(-RetiredGraceSeconds);
		var expired = new List<WorkspaceInstance>();
		
		for(var i = retired.Count - 1; i >= 0; i--) {
			
			if(retired[i].EvictedAt < cutoff) {
				
				expired.Add(retired[i].Instance);
				retired.RemoveAt(i);
			}
		}
		
		return expired;
	}
	
	/// <summary>
	///     Disposes evicted instances on the thread pool, never under <see cref="cacheLock"/>.
	///     <see cref="WorkspaceInstance.Dispose"/> bounds each of its waits, so a stalled teardown
	///     costs one pool thread for a few quiesce timeouts, logs, and leaks that one instance —
	///     it can no longer block tool calls for unrelated workspaces (#277).
	/// </summary>
	void DisposeDetached(List<WorkspaceInstance> instances)
	{
		_ = Task.Run(() => {
			
			foreach(var instance in instances) {
				
				try {
					instance.Dispose();
				}
				catch(Exception ex) {
					// Nothing above this on a pool thread — an escape would take the process down.
					logger.LogError("Dispose", $"Evicted workspace teardown failed: {ex.GetType().Name}: {ex.Message}");
				}
			}
		});
	}
	
	public void Dispose()
	{
		// Snapshot under the lock, tear down outside it — the same rule as eviction (#277).
		// Shutdown stays synchronous so the host waits for the teardown, which
		// WorkspaceInstance.Dispose bounds per instance.
		List<WorkspaceInstance> all;
		
		lock(cacheLock) {
			
			all = [.. cache.Values.Select(entry => entry.Instance), .. retired.Select(r => r.Instance)];
			
			cache.Clear();
			projectToCacheKey.Clear();
			retired.Clear();
		}
		
		foreach(var instance in all) {
			
			try {
				instance.Dispose();
			}
			catch(Exception ex) {
				// One failed teardown must not skip the rest on shutdown.
				logger.LogError("Dispose", $"Workspace teardown failed on shutdown: {ex.GetType().Name}: {ex.Message}");
			}
		}
	}

}
