using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using System.Collections.Concurrent;
using RoslynMcp.Tools;

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
		
		static WorkspaceLoadTimeoutException LoadTimeout(string path) =>
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
		static ProjectKey[] ProjectsWithoutReferences(Solution solution) =>
			[..solution.Projects
				.Where(p => p.MetadataReferences.Count == 0)
				.Select(ProjectKey.Of)
				.Distinct()];

		static ProjectKey[] ProjectsWithoutReferences(Workspace ws) => ProjectsWithoutReferences(ws.CurrentSolution);
		
		/// <summary>
		///     Projects of <paramref name="solution"/> that one of the load's <c>WorkspaceFailed</c> failures is
		///     about (#300). Such a project can keep its references while its compilation options fall back to
		///     defaults — a design-time build that could not write a file under obj\ loaded <c>Nullable</c> off
		///     in a project that enables it. Roslyn reports these failures as text only, with no project id and
		///     in the user's language, so a project file's full path in the message is what identifies the
		///     project. A message can quote other projects too — a reference that cannot be resolved, an
		///     import — so only the project whose path comes first is blamed: the one MSBuild was processing
		///     (<c>Msbuild failed when processing the file '…' with message: …</c>). Every project of that file
		///     is, one per target framework.
		///     <para>
		///         Text search cannot ask the file system how it compares names, so a match is found ignoring case
		///         where the platform usually does (<see cref="SecurityBoundary.PathInTextComparison"/>) and then
		///         confirmed with <see cref="SecurityBoundary.PathComparer"/>: in a case-sensitive directory,
		///         <c>foo.csproj</c> in a message is not <c>Foo.csproj</c> beside it (#300 review).
		///     </para>
		/// </summary>
		static ProjectKey[] ProjectsWithLoadFailures(Solution solution, string[] failures)
		{
			if(failures.Length == 0)
				
				return [];
			
			var comparison = SecurityBoundary.PathInTextComparison;
			var blamed     = new HashSet<ProjectKey>();
			
			foreach(var failure in failures) {
				
				Project[] subject = [];
				var       at      = int.MaxValue;
				
				foreach(var project in solution.Projects) {
					
					if(project.FilePath is not { } path)
						continue;
					
					var index = failure.IndexOf(path, comparison);
					
					while(index >= 0 && !IsWholePath(failure, index, path))
						index = failure.IndexOf(path, index + 1, comparison);
					
					if(index < 0 || index > at)
						continue;
					
					// The same file at the same place: another target framework of the project found first.
					subject = index == at ? [..subject, project] : [project];
					at      = index;
				}
				
				foreach(var project in subject)
					blamed.Add(ProjectKey.Of(project));
			}
			
			return [..blamed];
		}
		
		/// <summary>
		///     Whether <paramref name="path"/>, found at <paramref name="index"/> of <paramref name="text"/>, is
		///     that path and not the start of a longer one: the file system must agree on the spelling, and what
		///     follows must end a path in a message — the end of the text, whitespace, a quote, or closing
		///     punctuation, or a period followed by one of those. What may end a path is listed rather than what
		///     may continue one, because a file name can hold almost anything: <c>…\Core.csproj</c> is found inside
		///     <c>…\Core.csproj.Tests.csproj</c>, <c>…\Core.csproj+Tests.csproj</c> and <c>…\Core.csproj.d\X.csproj</c>,
		///     and is none of them (#300 review).
		/// </summary>
		static bool IsWholePath(string text, int index, string path)
		{
			if(!SecurityBoundary.PathComparer.Equals(text.Substring(index, path.Length), path))
				
				return false;
			
			var end = index + path.Length;
			
			if(end < text.Length && text[end] == '.')
				end++;
			
			return end == text.Length || EndsPath(text[end]);
		}
		
		// Characters a message puts after a path: whitespace, quotes in the forms MSBuild and translations use,
		// and closing punctuation.
		static bool EndsPath(char c) =>
			char.IsWhiteSpace(c) || c is '\'' or '"' or '`' or '‘' or '’' or '“' or '”' or '«' or '»' or ',' or ';' or ':' or ')' or ']' or '>';
		
		/// <summary>
		///     <see cref="ProjectsWithLoadFailures"/> for the load that just produced <paramref name="ws"/>.
		///     Call it straight after the load: until a finding is recorded, <see cref="loadWarnings"/> holds that
		///     load's <c>WorkspaceFailed</c> failures and nothing else.
		/// </summary>
		ProjectKey[] LoadFailedProjects(Workspace ws) => ProjectsWithLoadFailures(ws.CurrentSolution, [..loadWarnings]);
		
		/// <summary>Every project of <paramref name="ws"/> that is unhealthy for either reason, deduplicated.</summary>
		static ProjectKey[] UnhealthyProjects(Workspace ws, ProjectKey[] loadFailed) =>
			[..ProjectsWithoutReferences(ws).Union(loadFailed)];
		
		/// <summary>Why <paramref name="ws"/> is unhealthy, for a log line: one clause per reason that applies.</summary>
		static string UnhealthyReason(Workspace ws, ProjectKey[] loadFailed)
		{
			var dropped = ProjectsWithoutReferences(ws);
			
			return (dropped.Length > 0, loadFailed.Length > 0) switch {
				
				(true, true)  => $"no metadata references on: {ProjectKey.Names(dropped)}; an MSBuild failure on the project file of: {ProjectKey.Names(loadFailed)}",
				(true, false) => $"no metadata references on: {ProjectKey.Names(dropped)}",
				_             => $"an MSBuild failure on the project file of: {ProjectKey.Names(loadFailed)}",
			};
		}
		
		void WarnIfUnhealthy(Workspace ws, ProjectKey[] loadFailed)
		{
			if(ProjectsWithoutReferences(ws) is { Length: > 0 } dropped)
				RecordUnhealthyLoad($"Projects loaded without metadata references — symbol results may be incomplete: {ProjectKey.Names(dropped)}");
			
			if(loadFailed.Length > 0)
				RecordUnhealthyLoad(
					$"MSBuild failed on the project file of: {ProjectKey.Names(loadFailed)} — their compilation options "
					+ "(Nullable, LangVersion, …) may be defaults rather than the projects' own, so diagnostics can be phantom"
				);
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
		
		/// <summary>
		///     Loads a solution as the only MSBuild run in the process (<see cref="MSBuildGate"/>, #379). The
		///     load timeout starts once the gate is held, so time spent queued is not held against the load.
		///     <paramref name="abandon"/> gives up a wait for the gate (see <see cref="EnterMSBuildGate"/>).
		/// </summary>
		static Workspace LoadSolution(string solutionPath, FileLogger? log, ConcurrentQueue<string> warnings, CancellationToken abandon)
		{
			EnterMSBuildGate(solutionPath, log, abandon);
			
			try {
				return LoadSolutionUngated(solutionPath, log, warnings);
			}
			finally {
				MSBuildGate.Release();
			}
		}
		
		/// <summary>
		///     Loads a project as the only MSBuild run in the process (<see cref="MSBuildGate"/>, #379). The
		///     load timeout starts once the gate is held, so time spent queued is not held against the load.
		///     <paramref name="abandon"/> gives up a wait for the gate (see <see cref="EnterMSBuildGate"/>).
		/// </summary>
		static (Workspace workspace, ProjectId projectId) LoadMSBuildWorkspace(string csprojPath, FileLogger? log, ConcurrentQueue<string> warnings, CancellationToken abandon)
		{
			EnterMSBuildGate(csprojPath, log, abandon);
			
			try {
				return LoadMSBuildWorkspaceUngated(csprojPath, log, warnings);
			}
			finally {
				MSBuildGate.Release();
			}
		}
		
		/// <summary>
		///     Takes <see cref="MSBuildGate"/> for a load of <paramref name="path"/>, waiting at most the load
		///     timeout (as long as it takes when the timeout is disabled). A load that waited that long fails
		///     rather than run beside what holds the gate: the collision on obj\ is what the gate prevents, and
		///     a long <c>dotnet build</c> is not a stuck one. The failure is not kept — the next call loads
		///     afresh (#377), and a reload stays pending. <paramref name="abandon"/> ends the wait with an
		///     <see cref="OperationCanceledException"/>: a reload queued here when its instance is disposed.
		/// </summary>
		static void EnterMSBuildGate(string path, FileLogger? log, CancellationToken abandon)
		{
			var seconds = ServerArgs.Current.LoadTimeoutSeconds;
			var limit   = seconds > 0 ? TimeSpan.FromSeconds(seconds) : Timeout.InfiniteTimeSpan;
			
			if(!MSBuildGate.Enter($"a workspace load ({Path.GetFileName(path)})", limit, note => log?.LogInfo("Load", $"{note}: {path}"), abandon, out var behind))
				
				throw new WorkspaceLoadTimeoutException(
					$"Loading '{path}' waited {seconds}s for {behind} to finish and gave up: loading beside it would " +
					"write the same obj\\ files. Try again once it is done, or raise ROSLYNMCP_LOAD_TIMEOUT_SECONDS.");
		}
		
		/// <summary>The load of <see cref="LoadSolution"/>, without the gate; call that instead.</summary>
		static Workspace LoadSolutionUngated(string solutionPath, FileLogger? log, ConcurrentQueue<string> warnings)
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
		
		/// <summary>The load of <see cref="LoadMSBuildWorkspace"/>, without the gate; call that instead.</summary>
		static (Workspace workspace, ProjectId projectId) LoadMSBuildWorkspaceUngated(string csprojPath, FileLogger? log, ConcurrentQueue<string> warnings)
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
