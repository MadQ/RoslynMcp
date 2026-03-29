using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp;

internal sealed partial class WorkspaceManager
{
	/// <summary>
	///     Encapsulates a single Roslyn workspace — loaded from a solution, a single .csproj,
	///     or a directory (AdhocWorkspace). Supports multi-project solutions with per-project
	///     compilation caching.
	/// </summary>
	sealed class WorkspaceInstance : IDisposable
	{
		enum LoadMode { Solution, Project, Adhoc }

		private Workspace            workspace;
		private readonly string      rootPath;
		private readonly bool        isMSBuild;
		private ProjectId            defaultProjectId;
		private readonly ReaderWriterLockSlim rwLock = new();
		private          FileSystemWatcher?   watcher;

		// Maps normalized .csproj paths → ProjectIds for all projects in the workspace.
		private readonly Dictionary<string, ProjectId> projectMap = new(StringComparer.OrdinalIgnoreCase);

		// Per-project compilation cache — cleared on any file change.
		private readonly Dictionary<ProjectId, Compilation> compilationCache = new();

		// Set when a new file is detected that requires a full workspace reload.
		private volatile bool reloadNeeded;

		// The path used to load this workspace (solution or csproj), for reloading.
		private readonly string     loadPath;
		private readonly LoadMode   loadMode;
		private readonly FileLogger logger;

		// Debounce: accumulate FSW events for 300ms before processing.
		private readonly object           debounceLock   = new();
		private readonly HashSet<string>  pendingChanges = new(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string>  pendingDeletes = new(StringComparer.OrdinalIgnoreCase);
		private          Timer?           debounceTimer;
		private const    int              DebounceMs     = 300;

		// ── Factory methods ──────────────────────────────────────────────────

		public static WorkspaceInstance ForSolution(string solutionPath, FileLogger logger)  => new(solutionPath, LoadMode.Solution, logger);
		public static WorkspaceInstance ForProject(string csprojPath, FileLogger logger)     => new(csprojPath, LoadMode.Project, logger);
		public static WorkspaceInstance ForDirectory(string dir, FileLogger logger)           => new(dir, LoadMode.Adhoc, logger);

		private WorkspaceInstance(string path, LoadMode mode, FileLogger logger)
		{
			loadPath    = path;
			loadMode    = mode;
			this.logger = logger;

			switch(mode) {

				case LoadMode.Solution:

					rootPath = Path.GetDirectoryName(path)!;
					MSBuildBootstrap.EnsureReady();
					logger.LogInfo("MSBuild", MSBuildBootstrap.DiscoveryMethod);
					logger.LogInfo("Load", $"Loading solution: {path}");
					var slnSw = System.Diagnostics.Stopwatch.StartNew();
					workspace = LoadSolution(path, logger);
					isMSBuild = true;

					BuildProjectMap();
					defaultProjectId = workspace.CurrentSolution.Projects
						.FirstOrDefault()?.Id
						?? throw new InvalidOperationException($"Solution '{path}' contains no projects.")
					;
					logger.LogInfo("Load", $"Solution loaded in {slnSw.ElapsedMilliseconds}ms ({workspace.CurrentSolution.Projects.Count()} projects)");
					StartWatcher();
					break;

				case LoadMode.Project:

					rootPath = Path.GetDirectoryName(path)!;
					MSBuildBootstrap.EnsureReady();
					logger.LogInfo("MSBuild", MSBuildBootstrap.DiscoveryMethod);
					logger.LogInfo("Load", $"Loading project: {path}");
					var projSw = System.Diagnostics.Stopwatch.StartNew();

					var (msbuildWs, pid) = LoadMSBuildWorkspace(path);
					workspace        = msbuildWs;
					defaultProjectId = pid;
					isMSBuild        = true;

					// OpenProjectAsync also loads referenced projects — map them all.
					BuildProjectMap();
					logger.LogInfo("Load", $"Project loaded in {projSw.ElapsedMilliseconds}ms ({workspace.CurrentSolution.Projects.Count()} projects)");
					StartWatcher();
					break;

				case LoadMode.Adhoc:

					rootPath = path;
					var dirInfo = new DirectoryInfo(path);

					if(dirInfo.Parent is null)
						throw new InvalidOperationException($"Cannot create AdhocWorkspace for root directory '{path}'. Specify a subdirectory or use a .csproj file.");

					var (adhocWs, adhocPid) = LoadAdhocWorkspace();
					workspace        = adhocWs;
					defaultProjectId = adhocPid;
					isMSBuild        = false;

					LoadAllFiles((AdhocWorkspace) workspace, defaultProjectId);
					StartWatcher();
					break;

				default:
					throw new ArgumentOutOfRangeException(nameof(mode));
			}
		}

		void BuildProjectMap()
		{
			foreach(var p in workspace.CurrentSolution.Projects)
				if(p.FilePath is not null)
					projectMap[Path.GetFullPath(p.FilePath)] = p.Id;
		}

		// ── Properties ───────────────────────────────────────────────────────

		public string              RootPath     => rootPath;
		public bool                IsMSBuild    => isMSBuild;
		public IEnumerable<string> ProjectPaths => projectMap.Keys;

		// ── Queries ──────────────────────────────────────────────────────────

		public Solution GetSolution()
		{
			ReloadIfNeeded();
			return workspace.CurrentSolution;
		}

		public Compilation GetCompilation(string? csprojPath = null)
		{
			ReloadIfNeeded();
			var pid = ResolveProjectId(csprojPath);

			rwLock.EnterReadLock();

			try {

				if(compilationCache.TryGetValue(pid, out var cached))
					return cached;
			}
			finally {
				rwLock.ExitReadLock();
			}

			return RebuildCompilation(pid);
		}

		public Project GetProject(string? csprojPath = null)
		{
			ReloadIfNeeded();
			var pid = ResolveProjectId(csprojPath);
			return workspace.CurrentSolution.GetProject(pid)!;
		}

		ProjectId ResolveProjectId(string? csprojPath)
		{
			if(csprojPath is null)
				return defaultProjectId;

			if(projectMap.TryGetValue(csprojPath, out var pid))
				return pid;

			// Match by filename only (agent may pass a relative path that doesn't match fully).
			var fileName = Path.GetFileName(csprojPath);
			var match    = projectMap.FirstOrDefault(kvp =>
				string.Equals(Path.GetFileName(kvp.Key), fileName, StringComparison.OrdinalIgnoreCase)
			);

			if(match.Value is not null)
				return match.Value;

			return defaultProjectId;
		}

		/// <summary>Returns the .csproj path for a given resolved path, or the first known .csproj.</summary>
		public string? FindCsprojForPath(string normalizedPath)
		{
			if(projectMap.ContainsKey(normalizedPath))
				return normalizedPath;

			return projectMap.Keys.FirstOrDefault();
		}

		/// <summary>Searches all projects for a document matching a file suffix. Returns the .csproj of the first match.</summary>
		public string? FindCsprojForFileSuffix(string suffix, string fileName)
		{
			foreach(var (csproj, pid) in projectMap) {

				var project = workspace.CurrentSolution.GetProject(pid);

				if(project is null)
					continue;

				var hasFile = project.Documents.Any(d =>
					d.FilePath is not null && (
						d.FilePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
						|| string.Equals(Path.GetFileName(d.FilePath), fileName, StringComparison.OrdinalIgnoreCase)
					)
				);

				if(hasFile)
					return csproj;
			}

			return null;
		}

		// ── File invalidation ────────────────────────────────────────────────

		public void InvalidateFile(string fullPath)
		{
			if(isMSBuild) {

				var docIds = workspace.CurrentSolution.GetDocumentIdsWithFilePath(fullPath);

				if(docIds.Length > 0) {

					try {

						var newText     = SourceText.From(File.ReadAllText(fullPath));
						var newSolution = workspace.CurrentSolution;

						foreach(var id in docIds)
							newSolution = newSolution.WithDocumentText(id, newText);

						ApplyChangesWithFswSuppressed(newSolution);
					}
					catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
				}
				else {
					reloadNeeded = true;
					InvalidateCompilation();
				}
			}
			else if(workspace is AdhocWorkspace adhoc) {

				try {

					AddOrUpdateDocument(adhoc, defaultProjectId, fullPath);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			}
		}

		public void Dispose()
		{
			watcher?.Dispose();

			lock(debounceLock)
				debounceTimer?.Dispose();

			rwLock.Dispose();
			workspace.Dispose();
		}

		// ── Workspace loading ────────────────────────────────────────────────

		static Workspace LoadSolution(string solutionPath, FileLogger? log = null)
		{
			var msbuildWorkspace = MSBuildWorkspace.Create();

			try {

				if(solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)) {

					// .slnx — parse XML and load each project into the same workspace.
					var doc    = System.Xml.Linq.XDocument.Load(solutionPath);
					var slnDir = Path.GetDirectoryName(solutionPath)!;

					var projectPaths = doc.Root!
						.Elements("Project")
						.Select(e => e.Attribute("Path")?.Value)
						.Where(p => p is not null)
						.Select(p => Path.GetFullPath(Path.Combine(slnDir, p!)))
						.Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
					;

					foreach(var projectPath in projectPaths) {

						try {
							msbuildWorkspace.OpenProjectAsync(projectPath).GetAwaiter().GetResult();
						}
						catch(Exception ex) when(ex is not OperationCanceledException) {
							// Multi-TFM projects or transitive references may already be loaded
							// by a previous OpenProjectAsync call. Log and skip.
							log?.LogInfo("Load", $"Skipped {Path.GetFileName(projectPath)}: {ex.GetType().Name}: {ex.Message}");
						}
					}
				}
				else {

					msbuildWorkspace.OpenSolutionAsync(solutionPath).GetAwaiter().GetResult();
				}

				return msbuildWorkspace;
			}
			catch(Exception ex) when(ex is not OperationCanceledException) {
				log?.LogError("LoadSolution", $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
				msbuildWorkspace.Dispose();
				throw new InvalidOperationException($"Failed to load solution '{solutionPath}': {ex.Message}", ex);
			}
		}

		static (Workspace workspace, ProjectId projectId) LoadMSBuildWorkspace(string csprojPath)
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

		(Workspace workspace, ProjectId projectId) LoadAdhocWorkspace()
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

			return (adhocWorkspace, projectInfo.Id);
		}

		// ── AdhocWorkspace file management ───────────────────────────────────

		void LoadAllFiles(AdhocWorkspace adhocWorkspace, ProjectId pid)
		{
			var files = EnumerateFilesWithErrorHandling(rootPath, "*.cs");

			foreach(var path in files)
				AddOrUpdateDocument(adhocWorkspace, pid, path);
		}

		IEnumerable<string> EnumerateFilesWithErrorHandling(string path, string searchPattern)
		{
			var pathInfo = new DirectoryInfo(path);
			if(pathInfo.Attributes.HasFlag(FileAttributes.System) || pathInfo.Parent is null)
				yield break;

			IEnumerable<string> files;
			try {
				files = Directory.EnumerateFiles(path, searchPattern, SearchOption.TopDirectoryOnly);
			}
			catch(Exception ex) when(ex is UnauthorizedAccessException or DirectoryNotFoundException) {
				yield break;
			}

			foreach(var file in files)
				yield return file;

			IEnumerable<string> directories;
			try {
				directories = Directory.EnumerateDirectories(path);
			}
			catch(Exception ex) when(ex is UnauthorizedAccessException or DirectoryNotFoundException) {
				yield break;
			}

			foreach(var directory in directories) {

				var dirInfo = new DirectoryInfo(directory);
				if(dirInfo.Attributes.HasFlag(FileAttributes.Hidden) ||
				   dirInfo.Attributes.HasFlag(FileAttributes.System) ||
				   dirInfo.Name is "node_modules" or "bin" or "obj" or ".git" or ".vs" or "packages")
					continue;

				foreach(var file in EnumerateFilesWithErrorHandling(directory, searchPattern))
					yield return file;
			}
		}

		void AddOrUpdateDocument(AdhocWorkspace adhocWorkspace, ProjectId pid, string path)
		{
			if(!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
				return;

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

		void RemoveDocument(AdhocWorkspace adhocWorkspace, ProjectId pid, string path)
		{
			var name     = Path.GetRelativePath(rootPath, path);
			var project  = adhocWorkspace.CurrentSolution.GetProject(pid)!;
			var existing = project.Documents.FirstOrDefault(d => d.Name == name);

			if(existing is null)
				return;

			adhocWorkspace.TryApplyChanges(adhocWorkspace.CurrentSolution.RemoveDocument(existing.Id));
			InvalidateCompilation();
		}

		// ── FileSystemWatcher ────────────────────────────────────────────────

		/// <summary>
		///     Applies solution changes with FSW suppressed to prevent a feedback loop.
		///     MSBuildWorkspace.TryApplyChanges writes text back to disk, which would
		///     re-trigger the FSW. See issue #49.
		/// </summary>
		void ApplyChangesWithFswSuppressed(Solution newSolution)
		{
			if(watcher is not null)
				watcher.EnableRaisingEvents = false;

			try {

				workspace.TryApplyChanges(newSolution);
			}
			finally {

				if(watcher is not null)
					watcher.EnableRaisingEvents = true;
			}

			InvalidateCompilation();
		}

		void StartWatcher()
		{
			watcher = new FileSystemWatcher(rootPath, "*.cs")
			{
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
			};

			watcher.Changed += (_, e) => ScheduleDebounced(e.FullPath);
			watcher.Created += (_, e) => ScheduleDebounced(e.FullPath);
			watcher.Deleted += (_, e) => ScheduleDebounced(e.FullPath, deleted: true);
			watcher.Renamed += (_, e) => {

				ScheduleDebounced(e.OldFullPath, deleted: true);
				ScheduleDebounced(e.FullPath);
			};

			watcher.EnableRaisingEvents = true;
		}

		void ScheduleDebounced(string fullPath, bool deleted = false)
		{
			lock(debounceLock) {

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

		void FlushPendingChanges(object? _)
		{
			// Timer callbacks must never throw — unhandled exceptions crash the process.
			try {

				string[] changed;
				string[] deleted;

				lock(debounceLock) {

					changed = [.. pendingChanges];
					deleted = [.. pendingDeletes];
					pendingChanges.Clear();
					pendingDeletes.Clear();
				}

				if(isMSBuild)
					FlushMSBuild(changed, deleted);
				else if(workspace is AdhocWorkspace adhoc)
					FlushAdhoc(adhoc, changed, deleted);
			}
			catch(Exception) {

				// Swallow — best effort. Next FSW event or explicit InvalidateFile will retry.
			}
		}

		void FlushMSBuild(string[] changed, string[] deleted)
		{
			var newSolution = workspace.CurrentSolution;
			var modified    = false;

			// MSBuildWorkspace.TryApplyChanges doesn't support RemoveDocument.
			// Clear the text instead — an empty file produces no diagnostics or types.
			foreach(var path in deleted) {

				var docIds = newSolution.GetDocumentIdsWithFilePath(path);

				foreach(var id in docIds) {

					newSolution = newSolution.WithDocumentText(id, SourceText.From(""));
					modified    = true;
				}
			}

			foreach(var path in changed) {

				try {

					var docIds = newSolution.GetDocumentIdsWithFilePath(path);

					// MSBuildWorkspace doesn't support AddDocument via TryApplyChanges —
					// it modifies the .csproj, conflicting with SDK-style implicit includes.
					// Flag for full workspace reload on next tool call.
					if(docIds.Length == 0) {
						reloadNeeded = true;
						continue;
					}

					var text = SourceText.From(File.ReadAllText(path));

					foreach(var id in docIds)
						newSolution = newSolution.WithDocumentText(id, text);

					modified = true;
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			}

			if(modified)
				ApplyChangesWithFswSuppressed(newSolution);
		}

		void FlushAdhoc(AdhocWorkspace adhoc, string[] changed, string[] deleted)
		{
			foreach(var path in deleted)
				RemoveDocument(adhoc, defaultProjectId, path);

			foreach(var path in changed) {

				try {

					AddOrUpdateDocument(adhoc, defaultProjectId, path);
				}
				catch(FileNotFoundException) {

					RemoveDocument(adhoc, defaultProjectId, path);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			}
		}


			// ── Workspace reload ────────────────────────────────────────────────

		void ReloadIfNeeded()
		{
			if(!reloadNeeded)
				return;

			logger.LogInfo("Reload", $"Reloading workspace ({loadMode}: {loadPath})");
			var sw = System.Diagnostics.Stopwatch.StartNew();

			rwLock.EnterWriteLock();

			try {

				if(!reloadNeeded)
					return;

				var oldWorkspace = workspace;

				switch(loadMode) {

					case LoadMode.Solution:
						workspace = LoadSolution(loadPath);
						projectMap.Clear();
						BuildProjectMap();
						defaultProjectId = workspace.CurrentSolution.Projects
							.FirstOrDefault()?.Id
							?? defaultProjectId;
						break;

					case LoadMode.Project:
						var (ws, pid) = LoadMSBuildWorkspace(loadPath);
						workspace        = ws;
						defaultProjectId = pid;
						projectMap.Clear();
						BuildProjectMap();
						break;

					default:
						reloadNeeded = false;
						return;
				}

				compilationCache.Clear();
				reloadNeeded = false;
				oldWorkspace.Dispose();

				logger.LogInfo("Reload", $"Workspace reloaded in {sw.ElapsedMilliseconds}ms ({workspace.CurrentSolution.Projects.Count()} projects)");
			}
			finally {
				rwLock.ExitWriteLock();
			}
		}

	// ── Compilation cache ────────────────────────────────────────────────

		Compilation RebuildCompilation(ProjectId pid)
		{
			rwLock.EnterWriteLock();

			try {

				// Double-checked: another thread may have rebuilt while we waited.
				if(compilationCache.TryGetValue(pid, out var cached))
					return cached;

				var project = workspace.CurrentSolution.GetProject(pid)!;

				var compilation = project.GetCompilationAsync().GetAwaiter().GetResult()
					?? CSharpCompilation.Create("empty")
				;

				compilationCache[pid] = compilation;
				return compilation;
			}
			finally {
				rwLock.ExitWriteLock();
			}
		}

		void InvalidateCompilation()
		{
			rwLock.EnterWriteLock();

			try {

				compilationCache.Clear();
			}
			finally {
				rwLock.ExitWriteLock();
			}
		}
	}
}
