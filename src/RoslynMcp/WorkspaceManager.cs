using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp;

/// <summary>How a projectPath argument was resolved to a .csproj or directory.</summary>
enum ResolutionKind
{
	/// <summary>Agent passed a .csproj path directly — ideal path, no inference needed.</summary>
	Explicit,
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
		WorkspaceInstance	instance
		;
		string				cacheKey;
		
		// Route on the *requested* effective mode, not just the post-hoc ResolvedMode — on a
		// fresh process ResolvedMode is still Auto (EnsureReady only runs inside the
		// WorkspaceInstance constructor, after routing has committed), so the first .csproj
		// load in adhoc mode would wrongly take the MSBuild branch and fail (#229).
		// ResolvedMode == Adhoc stays as a fallback: once the process has skipped MSBuild
		// registration, MSBuildWorkspace can never work, whatever this path requests.
		if(MSBuildBootstrap.ResolvedMode == WorkspaceMode.Adhoc
			|| ProjectConfig.EffectiveWorkspaceMode(normalizedPath, logger) == WorkspaceMode.Adhoc) {
			
			instance = WorkspaceInstance.ForDirectory(
				Directory.Exists(normalizedPath) ? normalizedPath : Path.GetDirectoryName(normalizedPath)!, logger)
			;
			cacheKey = instance.RootPath;
		}
		
		else if(normalizedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) {
			
			var solutionPath = FindSolutionFileUpwards(Path.GetDirectoryName(normalizedPath)!);
			
			if(solutionPath is not null) {
				
				instance = WorkspaceInstance.ForSolution(solutionPath, logger);
				cacheKey = Path.GetFullPath(solutionPath);
			}
			
			else {
				
				instance = WorkspaceInstance.ForProject(normalizedPath, logger);
				cacheKey = normalizedPath;
			}
		}
		else {
			
			instance = WorkspaceInstance.ForDirectory(normalizedPath, logger);
			cacheKey = normalizedPath;
		}
		
		// Re-acquire lock to insert. Another thread may have loaded the same workspace.
		lock(cacheLock) {
			
			if(projectToCacheKey.TryGetValue(normalizedPath, out var raceKey)
				&& cache.TryGetValue(raceKey, out var raceEntry)) {
				
				instance.Dispose();
				cache[raceKey] = raceEntry with { LastAccess = DateTime.UtcNow };
				
				return raceEntry.Instance;
			}
			
			if(cache.TryGetValue(cacheKey, out var existing)) {
				
				instance.Dispose();
				cache[cacheKey] = existing with { LastAccess = DateTime.UtcNow };
				
				// Record the alias even on a lost race, or the next call for this path would
				// slow-path load-and-discard again (see below).
				if(!normalizedPath.Equals(cacheKey, StringComparison.OrdinalIgnoreCase))
					projectToCacheKey[normalizedPath] = cacheKey;
				
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
				SweepRetired();
			}
			
			var boundary = new SecurityBoundary(instance.RootPath);
			
			cache[cacheKey] = new CacheEntry(cacheKey, instance, boundary, DateTime.UtcNow);
			
			return instance;
		}
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
				
				if(entry.Instance.ApplyChangesWithFswSuppressed(newSolution))
					
					return true;
				
				entry.Instance.MarkReloadNeeded();
				
				return false;
			}
			
			if(cache.TryGetValue(normalizedPath, out var directEntry)) {
				
				if(directEntry.Instance.ApplyChangesWithFswSuppressed(newSolution))
					
					return true;
				
				directEntry.Instance.MarkReloadNeeded();
				
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
	///     Disposes retired instances whose grace period has elapsed.
	///     Must be called under <see cref="cacheLock"/>.
	/// </summary>
	void SweepRetired()
	{
		var cutoff = DateTime.UtcNow.AddSeconds(-RetiredGraceSeconds);
		
		for(var i = retired.Count - 1; i >= 0; i--) {
			
			if(retired[i].EvictedAt < cutoff) {
				
				retired[i].Instance.Dispose();
				retired.RemoveAt(i);
			}
		}
	}
	
	public void Dispose()
	{
		lock(cacheLock) {
			
			foreach(var entry in cache.Values)
				entry.Instance.Dispose();
			
			foreach(var (instance, _) in retired)
				instance.Dispose();
			
			cache.Clear();
			projectToCacheKey.Clear();
			retired.Clear();
		}
	}

}
