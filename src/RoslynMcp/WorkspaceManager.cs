using Microsoft.Build.Locator;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Tools;

namespace RoslynMcp;

/// <summary>
///     Manages multiple Roslyn workspaces with LRU caching. Supports smart project path resolution
///     (directory, file, or .csproj). Thread-safe for parallel agent access.
/// </summary>
internal sealed class WorkspaceManager : IDisposable
{
	record CacheEntry(string NormalizedPath, WorkspaceInstance Instance, DateTime LastAccess);

	readonly Dictionary<string, CacheEntry> cache = new();
	readonly object                         cacheLock = new();
	readonly int                            maxCachedWorkspaces;

	static WorkspaceManager()
	{
		// Register MSBuild instance once per process — required for MSBuildWorkspace.
		if(MSBuildLocator.CanRegister)
			MSBuildLocator.RegisterDefaults();
	}

	public WorkspaceManager()
	{
		maxCachedWorkspaces = int.TryParse(
			Environment.GetEnvironmentVariable("ROSLYNMCP_MAX_CACHED_WORKSPACES"),
			out var val
		) ? val : 5;
	}

	/// <summary>
	///     Resolves a project path using smart inference and returns the compilation.
	///     Caches workspaces with LRU eviction.
	/// </summary>
	public Compilation GetCompilation(string resolvedProjectPath)
	{
		var normalizedPath = Path.GetFullPath(resolvedProjectPath);

		lock(cacheLock) {

			// Cache hit
			if(cache.TryGetValue(normalizedPath, out var entry)) {

				cache[normalizedPath] = entry with { LastAccess = DateTime.UtcNow };

				return entry.Instance.GetCompilation();
			}

			// Cache miss - load workspace
			var instance = new WorkspaceInstance(normalizedPath);

			// Evict LRU if cache full
			if(cache.Count >= maxCachedWorkspaces) {

				var lru = cache.OrderBy(kvp => kvp.Value.LastAccess).First();
				cache.Remove(lru.Key);
				lru.Value.Instance.Dispose();
			}

			// Add to cache
			cache[normalizedPath] = new CacheEntry(normalizedPath, instance, DateTime.UtcNow);

			return instance.GetCompilation();
		}
	}

	/// <summary>
	///     Gets the solution for a specific project path.
	/// </summary>
	public Solution GetSolution(string resolvedProjectPath)
	{
		var normalizedPath = Path.GetFullPath(resolvedProjectPath);

		lock(cacheLock) {

			if(cache.TryGetValue(normalizedPath, out var entry)) {

				cache[normalizedPath] = entry with { LastAccess = DateTime.UtcNow };

				return entry.Instance.GetSolution();
			}

			// Load if not cached
			var instance = new WorkspaceInstance(normalizedPath);
			cache[normalizedPath] = new CacheEntry(normalizedPath, instance, DateTime.UtcNow);

			return instance.GetSolution();
		}
	}

	/// <summary>
	///     Resolves a project path using smart inference:
	///     - null or empty → CWD
	///     - .csproj file → use directly
	///     - directory → search for .csproj
	///     - source file → walk up to find .csproj
	/// </summary>
	public string ResolveProjectPath(string? inputPath)
	{
		// Use CWD if no path provided
		var basePath = string.IsNullOrWhiteSpace(inputPath)
			? Environment.CurrentDirectory
			: inputPath;

		var fullPath = Path.GetFullPath(basePath);

		// Already a .csproj file
		if(fullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) {

			if(!File.Exists(fullPath))
				throw new InvalidProjectPathException(fullPath, "File does not exist");

			return fullPath;
		}

		// Directory - search for .csproj
		if(Directory.Exists(fullPath))
			return FindProjectInDirectory(fullPath);

		// File path - walk up to find .csproj
		if(File.Exists(fullPath))
			return FindProjectFileUpwards(fullPath);

		throw new InvalidProjectPathException(fullPath, "Path does not exist");
	}

	string FindProjectInDirectory(string directory)
	{
		var csprojFiles = Directory.GetFiles(directory, "*.csproj");

		if(csprojFiles.Length == 0)
			throw new ProjectNotFoundException(directory);

		if(csprojFiles.Length == 1)
			return csprojFiles[0];

		// Multiple .csproj files - need disambiguation
		throw new MultipleProjectsFoundException(directory, csprojFiles);
	}

	string FindProjectFileUpwards(string startPath)
	{
		var dir = File.Exists(startPath)
			? Path.GetDirectoryName(startPath)
			: startPath;

		if(dir is null)
			throw new ProjectNotFoundException(startPath);

		while(true) {

			var csprojFiles = Directory.GetFiles(dir, "*.csproj");

			if(csprojFiles.Length == 1)
				return csprojFiles[0];

			if(csprojFiles.Length > 1)
				throw new MultipleProjectsFoundException(dir, csprojFiles);

			var parent = Directory.GetParent(dir);

			if(parent is null)
				throw new ProjectNotFoundException(startPath);

			dir = parent.FullName;
		}
	}

	public void Dispose()
	{
		lock(cacheLock) {

			foreach(var entry in cache.Values)
				entry.Instance.Dispose();

			cache.Clear();
		}
	}

	// ── WorkspaceInstance (per-project workspace) ────────────────────────────────

	/// <summary>
	///     Encapsulates a single workspace (MSBuildWorkspace or AdhocWorkspace) for one project.
	///     This is what was previously the entire WorkspaceManager class.
	/// </summary>
	sealed class WorkspaceInstance : IDisposable
	{
		private readonly Workspace            workspace;
		private readonly ProjectId            projectId;
		private readonly string               rootPath;
		private readonly string?              csprojPath;
		private readonly bool                 isMSBuild;
		private readonly ReaderWriterLockSlim rwLock = new();
		private readonly FileSystemWatcher?   watcher;

		// The current compilation — replaced atomically on each file change.
		private Compilation? compilation;

		public WorkspaceInstance(string csprojPath)
		{
			this.csprojPath = csprojPath;
			this.rootPath   = Path.GetDirectoryName(csprojPath)!;

			// Use MSBuildWorkspace for .csproj files
			(workspace, projectId) = LoadMSBuildWorkspace(csprojPath);
			isMSBuild = true;

			// MSBuildWorkspace watches files via Roslyn's internal mechanisms — no manual watcher needed.
		}

		public string  RootPath   => rootPath;
		public bool    IsMSBuild  => isMSBuild;
		public string? CsprojPath => csprojPath;

		public Solution GetSolution() => workspace.CurrentSolution;

		public Project GetProject() => workspace.CurrentSolution.GetProject(projectId)!;

		public void InvalidateFile(string fullPath)
		{
			// MSBuildWorkspace tracks files internally — just invalidate the cache.
			InvalidateCompilation();
		}

		public Compilation GetCompilation()
		{
			rwLock.EnterReadLock();

			try {

				if(compilation is not null)
					return compilation;
			}
			finally {
				rwLock.ExitReadLock();
			}

			return RebuildCompilation();
		}

		public void Dispose()
		{
			watcher?.Dispose();
			rwLock.Dispose();
			workspace.Dispose();
		}

		private (Workspace workspace, ProjectId projectId) LoadMSBuildWorkspace(string csprojPath)
		{
			var msbuildWorkspace = MSBuildWorkspace.Create();
			var project = msbuildWorkspace.OpenProjectAsync(csprojPath).GetAwaiter().GetResult();

			return (msbuildWorkspace, project.Id);
		}

		private Compilation RebuildCompilation()
		{
			rwLock.EnterWriteLock();

			try {

				// Double-checked: another thread may have rebuilt while we waited.
				if(compilation is not null)
					return compilation;

				var project = workspace.CurrentSolution.GetProject(projectId)!;

				// GetCompilationAsync is the correct async path; block here because
				// tool calls arrive on a thread pool thread without a live SynchronizationContext.
				compilation = project.GetCompilationAsync().GetAwaiter().GetResult()
					?? CSharpCompilation.Create("empty");

				return compilation;
			}
			finally {
				rwLock.ExitWriteLock();
			}
		}

		private void InvalidateCompilation()
		{
			rwLock.EnterWriteLock();

			try {

				compilation = null;
			}
			finally {
				rwLock.ExitWriteLock();
			}
		}
	}
}
