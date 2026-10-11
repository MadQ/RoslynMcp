using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using System.Collections.Concurrent;

namespace RoslynMcp;

internal sealed partial class WorkspaceManager
{
	sealed partial class WorkspaceInstance
	{
		// ── Workspace loading ────────────────────────────────────────────────
		
		
		/// <summary>
		///     When workspace mode is Auto, peeks at the project to detect SDK vs Framework style,
		///     then calls EnsureReady with the detected mode. Logs the result.
		/// </summary>
		static void AutoDetectAndBootstrap(string path, FileLogger logger)
		{
			// Start from the user's explicit choice (CLI arg or env var), falling back to a
			// committed project-local config file; auto-detect only if neither specifies a mode.
			var mode = ProjectConfig.EffectiveWorkspaceMode(path, logger);
			
			if(mode != ServerArgs.Current.WorkspaceMode)
				logger.LogInfo("Workspace", $"mode={mode} (from {ProjectConfig.FileName})");
			
			// Same precedence for the Visual Studio version pin. It steers the .NET Framework
			// BuildHost in every mode but adhoc — see MSBuildBootstrap.EnsureReady.
			var vsVersion = ProjectConfig.EffectiveVsVersion(path, logger);
			
			if(vsVersion is not null && !ServerArgs.Current.VsVersionSpecified)
				logger.LogInfo("Workspace", $"vsVersion={vsVersion} (from {ProjectConfig.FileName})");
			
			if(mode == WorkspaceMode.Auto) {
				
				// A solution is judged by the projects it references — the first .csproj the file
				// system happens to enumerate may be a stale copy that is not even in the solution.
				var (detected, detail) = MSBuildBootstrap.DetectLoadStyle(path);
				
				logger.LogInfo("Workspace", $"auto-detected {detected}: {detail}");
				
				mode = detected;
			}
			
			WarnIfLargeSolution(path, mode, logger);
			
			var bootstrapFailure = MSBuildBootstrap.EnsureReady(mode, vsVersion);
			
			if(bootstrapFailure is not null)
				throw new InvalidOperationException($"MSBuild initialization failed: {bootstrapFailure}");
			
			logger.LogInfo("MSBuild", MSBuildBootstrap.DiscoveryMethod);
		}
		
		
		
		const int LargeSolutionThreshold = 30;
		
		/// <summary>
		///     Counts the projects a load is about to open — the ones a solution lists, or a bounded
		///     scan of a directory — and logs a warning before a potentially long MSBuild load when
		///     the count is over the threshold.
		/// </summary>
		static void WarnIfLargeSolution(string path, WorkspaceMode mode, FileLogger logger)
		{
			if(mode == WorkspaceMode.Adhoc)
				
				return;
			
			// A lone .csproj is one project by definition; counting its neighbours says nothing
			// about the load.
			if(path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
				
				return;
			
			var isSolution = path.EndsWith(".sln",  StringComparison.OrdinalIgnoreCase)
			              || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
			
			// Never an unbounded recursive enumeration: this runs on the first tool call, and a
			// walk through node_modules or .git on a network share takes longer than an MCP
			// client waits (#309). The solution's own project list is exact; the directory scan is
			// pruned and capped (just above the threshold), so its count is a lower bound.
			int count;
			
			// Advisory only: a warning that cannot be computed must never fail the load. Both
			// helpers swallow the I/O failures they expect; this covers whatever they do not.
			try {
				
				count = isSolution             ? MSBuildBootstrap.ReadSolutionProjects(path).Length
				      : Directory.Exists(path) ? MSBuildBootstrap.FindCsprojCandidates(path).Length
				      : 0;
			}
			catch(Exception) {
				return;
			}
			
			if(count > LargeSolutionThreshold)
				logger.LogInfo("Workspace",
					$"Large solution detected: {(isSolution ? "" : "at least ")}{count} projects. " +
					$"MSBuild loading may take several minutes. " +
					$"For a faster load, use --workspace adhoc or set ROSLYNMCP_WORKSPACE=adhoc.");
		}
		
		
		/// <summary>
		///     Cancellation source bounding a single MSBuild workspace load. Fires after
		///     <see cref="ServerArgs.LoadTimeoutSeconds"/>; never fires when the timeout is disabled.
		/// </summary>
		static CancellationTokenSource CreateLoadTimeoutCts()
		{
			var cts     = new CancellationTokenSource();
			var seconds = ServerArgs.Current.LoadTimeoutSeconds;
			
			if(seconds > 0)
				cts.CancelAfter(TimeSpan.FromSeconds(seconds));
			
			return cts;
		}
		
		static InvalidOperationException LoadTimeout(string path) =>
			new(
				$"Loading '{path}' timed out after {ServerArgs.Current.LoadTimeoutSeconds}s. " +
				"The workspace may be too large or MSBuild may be stuck. Try loading a single .csproj " +
				"instead of the full solution, or raise ROSLYNMCP_LOAD_TIMEOUT_SECONDS.");
		
		/// <summary>
		///     Projects whose design-time build produced zero metadata references. A successfully
		///     built project always references at least the core library, so an empty set means the
		///     build silently dropped them and symbol queries over that project would return
		///     wrong-but-plausible results. The BuildHost-contention cause and the retry-on-fresh-
		///     workspace mitigation are undocumented MSBuild behavior, documented empirically by
		///     MarcelRoozekrans/roslyn-codelens-mcp (no code reused).
		/// </summary>
		static string[] ProjectsWithoutReferences(Solution solution) =>
			[..solution.Projects
				.Where(p => p.MetadataReferences.Count == 0)
				.Select(p => p.Name)
				.Distinct()];

		static string[] ProjectsWithoutReferences(Workspace ws) => ProjectsWithoutReferences(ws.CurrentSolution);

		void WarnIfReferencesDropped(Workspace ws)
		{
			if(ProjectsWithoutReferences(ws) is { Length: > 0 } dropped)
				RecordUnhealthyLoad($"Projects loaded without metadata references — symbol results may be incomplete: {string.Join(", ", dropped)}");
		}

		/// <summary>
		///     Records a load-health finding in both the per-load queue and the retained
		///     <see cref="LastUnhealthyLoad"/> slot. The queue is cleared by every subsequent load,
		///     so without the retained copy the evidence disappears at exactly the moment a healthy
		///     reload fixes the symptom — leaving the episode undiagnosable (issue #235).
		/// </summary>
		void RecordUnhealthyLoad(string message)
		{
			loadWarnings.Enqueue(message);
			lastUnhealthyLoad = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z] {message}";
		}
		
		static Workspace LoadSolution(string solutionPath, FileLogger? log, ConcurrentQueue<string> warnings)
		{
			using var loadCts = CreateLoadTimeoutCts();
			
			var msbuildWorkspace = MSBuildWorkspace.Create();
			msbuildWorkspace.RegisterWorkspaceFailedHandler(e =>
			{
				var level = e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure ? "ERROR" : "WARN";
				log?.LogInfo("WorkspaceFailed", $"[{level}] {e.Diagnostic.Message}");
				
				if(e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
					warnings.Enqueue(e.Diagnostic.Message);
			}, options: null);
			
			
			try {
				
				if(solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)) {
					
					// .slnx — parse XML and load each project into the same workspace.
					var doc    = System.Xml.Linq.XDocument.Load(solutionPath)
					;
					var slnDir = Path.GetDirectoryName(solutionPath)!;
					
					var projectPaths = doc.Root!
						.Descendants("Project")
						.Select(e => e.Attribute("Path")?.Value)
						.Where(p => p is not null)
						.Select(p => Path.GetFullPath(Path.Combine(slnDir, p!)))
						.Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
					;
					
					foreach(var projectPath in projectPaths) {
						
						try {
							msbuildWorkspace.OpenProjectAsync(projectPath, cancellationToken: loadCts.Token).GetAwaiter().GetResult();
						}
						catch(Exception ex) when(ex is not OperationCanceledException) {
							// Multi-TFM projects or transitive references may already be loaded
							// by a previous OpenProjectAsync call. Log and skip.
							log?.LogInfo("Load", $"Skipped {Path.GetFileName(projectPath)}: {ex.GetType().Name}: {ex.Message}")
							;
						}
					}
				}
				
				else
					msbuildWorkspace.OpenSolutionAsync(solutionPath, cancellationToken: loadCts.Token).GetAwaiter().GetResult();
				
				return msbuildWorkspace;
			}
			catch(OperationCanceledException) when(loadCts.IsCancellationRequested) {
				
				log?.LogError("LoadSolution", $"Load timed out after {ServerArgs.Current.LoadTimeoutSeconds}s: {solutionPath}");
				msbuildWorkspace.Dispose();
				throw LoadTimeout(solutionPath);
			}
			catch(Exception ex) when(ex is not OperationCanceledException) {
				
				log?.LogError("LoadSolution", $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
				msbuildWorkspace.Dispose();
				throw new InvalidOperationException($"Failed to load solution '{solutionPath}': {ex.Message}", ex);
			}
		}
		
		static (Workspace workspace, ProjectId projectId) LoadMSBuildWorkspace(string csprojPath, FileLogger? log, ConcurrentQueue<string> warnings)
		{
			using var loadCts = CreateLoadTimeoutCts();
			
			var msbuildWorkspace = MSBuildWorkspace.Create();
			
			msbuildWorkspace.RegisterWorkspaceFailedHandler(e =>
			{
				var level = e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure ? "ERROR" : "WARN";
				log?.LogInfo("WorkspaceFailed", $"[{level}] {e.Diagnostic.Message}");
				
				if(e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
					warnings.Enqueue(e.Diagnostic.Message);
			}, options: null);
			
			try {
				
				var project = msbuildWorkspace.OpenProjectAsync(csprojPath, cancellationToken: loadCts.Token).GetAwaiter().GetResult();
				
				return (msbuildWorkspace, project.Id);
			}
			catch(OperationCanceledException) when(loadCts.IsCancellationRequested) {
				msbuildWorkspace.Dispose();
				throw LoadTimeout(csprojPath);
			}
			catch(Exception ex) when(ex is not OperationCanceledException) {
				msbuildWorkspace.Dispose();
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
	}
}
