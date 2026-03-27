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
	
	// Deferred MSBuild registration — only attempted on first MSBuildWorkspace use.
	static bool           msbuildRegistered;
	static readonly object msbuildLock = new();
	
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
		
			// Cache hit.
			if(cache.TryGetValue(normalizedPath, out var entry)) {
			
				cache[normalizedPath] = entry with { LastAccess = DateTime.UtcNow };
				
				return entry.Instance.GetCompilation();
			}
			
			// Cache miss - load workspace.
			var instance = normalizedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
				? new WorkspaceInstance(normalizedPath)                // MSBuildWorkspace
				: new WorkspaceInstance(normalizedPath, useAdhoc: true); // AdhocWorkspace
			
			// Evict LRU if cache full.
			if(cache.Count >= maxCachedWorkspaces) {
			
				var lru = cache.OrderBy(kvp => kvp.Value.LastAccess).First();
				cache.Remove(lru.Key);
				lru.Value.Instance.Dispose();
			}
			
			// Add to cache.
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
			
			// Load if not cached.
			var instance = normalizedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
				? new WorkspaceInstance(normalizedPath)
				: new WorkspaceInstance(normalizedPath, useAdhoc: true);
			cache[normalizedPath] = new CacheEntry(normalizedPath, instance, DateTime.UtcNow);
			
			return instance.GetSolution();
		}
	}
	
	/// <summary>
	///     Gets the project for a specific project path.
	/// </summary>
	public Project GetProject(string resolvedProjectPath)
	{
		var normalizedPath = Path.GetFullPath(resolvedProjectPath);
		
		lock(cacheLock) {
		
			if(cache.TryGetValue(normalizedPath, out var entry)) {
			
				cache[normalizedPath] = entry with { LastAccess = DateTime.UtcNow };
				
				return entry.Instance.GetProject();
			}
			
			// Load if not cached.
			var instance = normalizedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
				? new WorkspaceInstance(normalizedPath)
				: new WorkspaceInstance(normalizedPath, useAdhoc: true);
			cache[normalizedPath] = new CacheEntry(normalizedPath, instance, DateTime.UtcNow);
			
			return instance.GetProject();
		}
	}
	
	/// <summary>
	///     Gets metadata about the workspace instance for a project path.
	/// </summary>
	public (string RootPath, bool IsMSBuild, string? CsprojPath) GetWorkspaceInfo(string resolvedProjectPath)
	{
		var normalizedPath = Path.GetFullPath(resolvedProjectPath);
		
		lock(cacheLock) {
		
			if(cache.TryGetValue(normalizedPath, out var entry)) {
			
				cache[normalizedPath] = entry with { LastAccess = DateTime.UtcNow };
				
				return (entry.Instance.RootPath, entry.Instance.IsMSBuild, entry.Instance.CsprojPath);
			}
			
			// Load if not cached
			var instance = normalizedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
				? new WorkspaceInstance(normalizedPath)
				: new WorkspaceInstance(normalizedPath, useAdhoc: true);
			cache[normalizedPath] = new CacheEntry(normalizedPath, instance, DateTime.UtcNow);
			
			return (instance.RootPath, instance.IsMSBuild, instance.CsprojPath);
		}
	}
	
	/// <summary>
	///     Invalidates the cached compilation for a project when a file changes.
	/// </summary>
	public void InvalidateFile(string resolvedProjectPath, string fullPath)
	{
		var normalizedPath = Path.GetFullPath(resolvedProjectPath);
		
		lock(cacheLock) {
		
			if(cache.TryGetValue(normalizedPath, out var entry))
				entry.Instance.InvalidateFile(fullPath);
		}
	}
	
	/// <summary>
	///     Resolves a project path using smart inference:
	///     - .csproj file → use directly
	///     - directory → search for .csproj
	///     - source file → walk up to find .csproj
	/// </summary>
	public string ResolveProjectPath(string inputPath)
	{
		if(string.IsNullOrWhiteSpace(inputPath))
			throw new ArgumentException("Project path is required and cannot be empty. The agent must explicitly specify which project to operate on.", nameof(inputPath));

		var fullPath = Path.GetFullPath(inputPath);
		
		// Already a .csproj file
		if(fullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) {
		
			if(!File.Exists(fullPath))
				throw new InvalidProjectPathException(fullPath, "File does not exist");
			
			return fullPath;
		}
		
		// Directory - search for .csproj
		if(Directory.Exists(fullPath)) {
		
			var csprojPath = FindProjectInDirectory(fullPath);
			
			// Found .csproj — use MSBuildWorkspace
			if(csprojPath is not null)
				return csprojPath;
			
			// No .csproj — return directory path for AdhocWorkspace
			return fullPath;
		}
		
		// File path - walk up to find .csproj
		if(File.Exists(fullPath))
			return FindProjectFileUpwards(fullPath);
		
		throw new InvalidProjectPathException(fullPath, "Path does not exist");
	}
	
	string? FindProjectInDirectory(string directory)
	{
		var csprojFiles = Directory.GetFiles(directory, "*.csproj");
		
		if(csprojFiles.Length == 0)
			return null; // No .csproj — will use AdhocWorkspace
		
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
	
	// ── MSBuild registration ─────────────────────────────────────────────────────
	
	/// <summary>
	///     Registers the MSBuild SDK instance on first use. Deferred to avoid eager
	///     initialization at startup — only needed when opening an MSBuildWorkspace.
	///     Thread-safe via double-checked locking; failures are silently swallowed so
	///     the server stays alive and tools return structured errors instead.
	/// </summary>
	static void EnsureMSBuildRegistered()
	{
		if(msbuildRegistered)
			return;
		
		lock(msbuildLock) {
		
			if(msbuildRegistered)
				return;
			
			try {
				if(MSBuildLocator.CanRegister)
					MSBuildLocator.RegisterDefaults();
			}
			catch(Exception) {
				// Intentionally swallowed — MSBuildWorkspace tools will fail gracefully per-call.
			}
			finally {
				// Mark as attempted regardless of success — only one attempt per process.
				msbuildRegistered = true;
			}
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
		
		/// <summary>
		///     Creates a workspace instance for a .csproj file (MSBuildWorkspace).
		/// </summary>
		public WorkspaceInstance(string csprojPath)
		{
			this.csprojPath = csprojPath;
			this.rootPath   = Path.GetDirectoryName(csprojPath)!;
			
			EnsureMSBuildRegistered();
			
			// Use MSBuildWorkspace for .csproj files
			(workspace, projectId) = LoadMSBuildWorkspace(csprojPath);
			isMSBuild = true;
			
			// MSBuildWorkspace watches files via Roslyn's internal mechanisms — no manual watcher needed.
		}
		
		/// <summary>
		///     Creates a workspace instance for a directory without a .csproj (AdhocWorkspace).
		///     Loads all .cs files and watches for changes via FileSystemWatcher.
		/// </summary>
		public WorkspaceInstance(string directoryPath, bool useAdhoc)
		{
			if(!useAdhoc)
				throw new ArgumentException("Second constructor is for AdhocWorkspace only. Pass true.", nameof(useAdhoc));
			
			this.csprojPath = null;
			this.rootPath   = directoryPath;
			
			var dirInfo = new DirectoryInfo(directoryPath);
			if(dirInfo.Parent == null)
				throw new InvalidOperationException($"Cannot create AdhocWorkspace for root directory '{directoryPath}'. Specify a subdirectory or use a .csproj file.");
			
			// Use AdhocWorkspace for source-only scenarios
			(workspace, projectId) = LoadAdhocWorkspace();
			isMSBuild = false;
			
			// AdhocWorkspace requires manual file watching
			watcher = new FileSystemWatcher(rootPath, "*.cs")
			{
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
			};
			
			watcher.Changed += OnFileChanged;
			watcher.Created += OnFileChanged;
			watcher.Deleted += OnFileDeleted;
			watcher.Renamed += OnFileRenamed;
			watcher.EnableRaisingEvents = true;
		}
		
		public string  RootPath   => rootPath;
		public bool    IsMSBuild  => isMSBuild;
		public string? CsprojPath => csprojPath;
		
		public Solution GetSolution() => workspace.CurrentSolution;
		
		public Project GetProject() => workspace.CurrentSolution.GetProject(projectId)!;
		
		public void InvalidateFile(string fullPath)
		{
			if(isMSBuild) {
			
				// MSBuildWorkspace tracks files internally — just invalidate the cache.
				InvalidateCompilation();
			}
			else if(workspace is AdhocWorkspace adhoc) {
			
				// AdhocWorkspace requires manual reload.
				try {
				
					AddOrUpdateDocument(adhoc, projectId, fullPath);
				}
				catch(IOException) {
				
					// File may be locked — watcher will retry on next event.
				}
				catch(UnauthorizedAccessException) {
				
					// Insufficient permissions — ignore.
				}
			}
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
			try {
				var msbuildWorkspace = MSBuildWorkspace.Create();
				var project = msbuildWorkspace.OpenProjectAsync(csprojPath).GetAwaiter().GetResult();
				
				return (msbuildWorkspace, project.Id);
			}
			catch(Exception ex) when(ex is not OperationCanceledException) {
				throw new InvalidOperationException($"Failed to load MSBuildWorkspace for '{csprojPath}': {ex.Message}", ex);
			}
		}
		
		private (Workspace workspace, ProjectId projectId) LoadAdhocWorkspace()
		{
			var adhocWorkspace = new AdhocWorkspace();
			
			var projectInfo = ProjectInfo.Create(
				id:             ProjectId.CreateNewId(),
				version:        VersionStamp.Create(),
				name:           Path.GetFileName(rootPath),
				assemblyName:   Path.GetFileName(rootPath),
				language:       LanguageNames.CSharp,
				compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
				parseOptions:       new CSharpParseOptions(LanguageVersion.Preview)
			);
			
			adhocWorkspace.AddProject(projectInfo);
			
			LoadAllFiles(adhocWorkspace, projectInfo.Id);
			
			return (adhocWorkspace, projectInfo.Id);
		}
		
		private void LoadAllFiles(AdhocWorkspace adhocWorkspace, ProjectId pid)
		{
			// Enumerate files with error handling for protected directories.
			var files = EnumerateFilesWithErrorHandling(rootPath, "*.cs");
			
			foreach(var path in files)
				AddOrUpdateDocument(adhocWorkspace, pid, path);
		}
		
		private IEnumerable<string> EnumerateFilesWithErrorHandling(string path, string searchPattern)
		{
			// Don't scan system directories or drive roots.
			var pathInfo = new DirectoryInfo(path);
			if(pathInfo.Attributes.HasFlag(FileAttributes.System) || pathInfo.Parent is null)
				yield break;
			
			// Try to enumerate files in current directory.
			IEnumerable<string> files;
			try {
				files = Directory.EnumerateFiles(path, searchPattern, SearchOption.TopDirectoryOnly);
			}
			catch(UnauthorizedAccessException) {
				yield break; // Skip directories we can't access.
			}
			catch(DirectoryNotFoundException) {
				yield break;
			}
			
			foreach(var file in files)
				yield return file;
			
			// Recursively enumerate subdirectories.
			IEnumerable<string> directories;
			try {
				directories = Directory.EnumerateDirectories(path);
			}
			catch(UnauthorizedAccessException) {
				yield break;
			}
			catch(DirectoryNotFoundException) {
				yield break;
			}
			
			foreach(var directory in directories) {
			
				// Skip hidden, system, and common large directories.
				var dirInfo = new DirectoryInfo(directory);
				if(dirInfo.Attributes.HasFlag(FileAttributes.Hidden) ||
				   dirInfo.Attributes.HasFlag(FileAttributes.System) ||
				   dirInfo.Name is "node_modules" or "bin" or "obj" or ".git" or ".vs" or "packages")
					continue;
				
				foreach(var file in EnumerateFilesWithErrorHandling(directory, searchPattern))
					yield return file;
			}
		}
		
		private void AddOrUpdateDocument(AdhocWorkspace adhocWorkspace, ProjectId pid, string path)
		{
			var text = SourceText.From(File.ReadAllText(path));
			var name = Path.GetRelativePath(rootPath, path);
			
			var project  = adhocWorkspace.CurrentSolution.GetProject(pid)!;
			var existing = project.Documents.FirstOrDefault(d => d.Name == name);
			
			Solution newSolution;
			
			if(existing is not null)
				newSolution = adhocWorkspace.CurrentSolution.WithDocumentText(existing.Id, text);
			else
				newSolution = adhocWorkspace.CurrentSolution.AddDocument(
					DocumentId.CreateNewId(pid), name, text, filePath: path
				);
			
			adhocWorkspace.TryApplyChanges(newSolution);
			InvalidateCompilation();
		}
		
		private void RemoveDocument(AdhocWorkspace adhocWorkspace, ProjectId pid, string path)
		{
			var name     = Path.GetRelativePath(rootPath, path);
			var project  = adhocWorkspace.CurrentSolution.GetProject(pid)!;
			var existing = project.Documents.FirstOrDefault(d => d.Name == name);
			
			if(existing is null)
				return;
			
			adhocWorkspace.TryApplyChanges(adhocWorkspace.CurrentSolution.RemoveDocument(existing.Id));
			InvalidateCompilation();
		}
		
		private void OnFileChanged(object sender, FileSystemEventArgs e)
		{
			if(isMSBuild || workspace is not AdhocWorkspace adhoc)
				return;
			
			try {
			
				AddOrUpdateDocument(adhoc, projectId, e.FullPath);
			}
			catch(IOException) {
			
				// File may be locked mid-write by editor/build process.
				// Non-fatal: FileSystemWatcher will fire another event when write completes.
			}
			catch(UnauthorizedAccessException) {
			
				// Insufficient permissions — ignore.
			}
		}
		
		private void OnFileDeleted(object sender, FileSystemEventArgs e)
		{
			if(isMSBuild || workspace is not AdhocWorkspace adhoc)
				return;
			
			RemoveDocument(adhoc, projectId, e.FullPath);
		}
		
		private void OnFileRenamed(object sender, RenamedEventArgs e)
		{
			if(isMSBuild || workspace is not AdhocWorkspace adhoc)
				return;
			
			RemoveDocument(adhoc, projectId, e.OldFullPath);
			AddOrUpdateDocument(adhoc, projectId, e.FullPath);
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
