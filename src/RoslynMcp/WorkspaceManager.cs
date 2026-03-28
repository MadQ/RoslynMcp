using Microsoft.CodeAnalysis;

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
	record CacheEntry(string Key, WorkspaceInstance Instance, DateTime LastAccess);

	readonly Dictionary<string, CacheEntry> cache = new(StringComparer.OrdinalIgnoreCase);

	// Secondary index: maps normalized .csproj paths → cache keys for solution-level entries.
	// Enables O(1) lookup when a tool passes a .csproj that's part of an already-loaded solution.
	readonly Dictionary<string, string> projectToCacheKey = new(StringComparer.OrdinalIgnoreCase);

	readonly object     cacheLock = new();
	readonly int        maxCachedWorkspaces;
	readonly FileLogger logger;


	public WorkspaceManager(FileLogger logger)
	{
		this.logger = logger;

		maxCachedWorkspaces = int.TryParse(
			Environment.GetEnvironmentVariable("ROSLYNMCP_MAX_CACHED_WORKSPACES"),
			out var val
		) ? val : 5;
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

			if(projectToCacheKey.TryGetValue(normalizedPath, out var mappedKey)
				&& cache.TryGetValue(mappedKey, out var mappedEntry)) {

				cache[mappedKey] = mappedEntry with { LastAccess = DateTime.UtcNow };
				return mappedEntry.Instance;
			}

			if(cache.TryGetValue(normalizedPath, out var entry)) {

				cache[normalizedPath] = entry with { LastAccess = DateTime.UtcNow };
				return entry.Instance;
			}
		}

		// Slow path: load outside lock so other tool calls can still hit the cache.
		WorkspaceInstance instance;
		string cacheKey;

		if(normalizedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) {

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
				return existing.Instance;
			}

			foreach(var csproj in instance.ProjectPaths)
				projectToCacheKey[csproj] = cacheKey;

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

				lru.Value.Instance.Dispose();
			}

			cache[cacheKey] = new CacheEntry(cacheKey, instance, DateTime.UtcNow);
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

	public void Dispose()
	{
		lock(cacheLock) {

			foreach(var entry in cache.Values)
				entry.Instance.Dispose();

			cache.Clear();
			projectToCacheKey.Clear();
		}
	}

}
