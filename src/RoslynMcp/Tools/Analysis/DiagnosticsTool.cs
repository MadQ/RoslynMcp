using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using ModelContextProtocol.Server;

#pragma warning disable IDE0130 // Namespace does not match folder structure

namespace RoslynMcp.Tools;


[McpServerToolType]
internal sealed class DiagnosticsTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : RoslynMcpTool(workspace, logger, paginationCache)
{
	// Heuristic for detecting transient workspace-load noise — a flood of type-resolution errors
	// across many files that usually clears once Roslyn finishes resolving dependencies.
	const int    workspaceLoadMinErrors         = 20;
	const double workspaceLoadNamespaceFraction = 0.60;
	const int    workspaceLoadMinDistinctFiles  = 5;
	static readonly HashSet<string> workspaceLoadCodes = ["CS0246", "CS0103", "CS0234", "CS0012"];

	[McpServerTool(Name = "roslyn_get_diagnostics", ReadOnly = true, Title = "Get Diagnostics", OpenWorld = false, Idempotent = true)]
	[Description(
		"Check your code for compiler errors and warnings — fast, in-process, no build needed. " +
		"Use this after editing code to verify correctness before committing or continuing work. " +
		"Returns a structured summary (error count, warning count) plus paginated individual items " +
		"with code, file, line, and message. A .sln/.slnx projectPath checks every project in the solution " +
		"(with filePath: the project or projects that compile that file). " +
		"Pass take: 0 for a lightweight error-count-only check " +
		"with no items returned. When take: 0, the response includes full counts but items is null — " +
		"not an empty array. An empty array means items were requested but none matched; null means items were not requested. " +
		"Covers C# type/symbol errors by default; set includeAnalyzers: true to also run the project's " +
		"analyzer references and include their diagnostics — slower; analyzer assemblies are shadow-copied " +
		"before loading, so the originals are never locked. " +
		"For NuGet restore failures, MSBuild target errors, or " +
		"source generator issues, use roslyn_build_project instead.")]
	public async Task<object> GetDiagnostics(
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken,
		[Description("Optional relative file path to scope results, e.g. 'Core/Foo.cs'. Omit to check all files in the project. A path that names no file in the compilation is an error, never an empty (clean) result.")] string? filePath = null,
		[Description("Filter by severity: 'errors', 'warnings', or 'all'. Omit to return all (errors and warnings).")] string? severity = null,
		[Description("Number of items to skip. Default: 0.")] int skip = 0,
		[Description("Maximum items to return (default 50, max 200). Pass 0 to return only the summary counts — a fast way to check if there are any errors without retrieving individual items. When take: 0, items in the response is null (not an empty array).")] int take = 50,
		[Description("Token from a previous response to get the next page without re-running the compilation.")] string? page_token = null,
		[Description("When true, also runs the project's analyzer references (NuGet + project analyzers) via CompilationWithAnalyzers and includes their diagnostics. Slower than compiler-only; assemblies are shadow-copied so the originals are never locked. Default: false.")] bool includeAnalyzers = false)
	{
		using var scope = BeginTool("roslyn_get_diagnostics", filePath, new { severity, skip, take, includeAnalyzers });
		
		// Stateless page token overrides skip/severity — agents don't need to track offsets manually.
		if(page_token is not null)
			try {
				
				var decoded = JsonSerializer.Deserialize<PageTokenData>(Convert.FromBase64String(page_token));
				
				if(decoded is not null) {
					
					skip     = decoded.Skip;
					severity = decoded.Severity;
				}
			}
			catch { /* malformed token — fall through to explicit params */ }
		
		if(!TryResolveRoot(projectPath, out var rootPath, out var rootError))
			
			return scope.Error(rootError);
		
		// A file is diagnosed in the project that compiles it. For a solution that is the project
		// holding the file, found the way every file-anchored tool finds it — a short name that two
		// projects' files end with is ambiguous_file, never the two files' diagnostics merged.
		var givenPath     = projectPath;
		var solutionGiven = filePath is not null && ResolvedSolutionPath(projectPath) is not null;
		
		if(filePath is not null && !TryResolveProjectArg(projectPath, filePath, out projectPath, out var fileError))
			
			return scope.Error(fileError);
		
		// What the path stands for, compilation by compilation (#376): every project of a solution,
		// and every target framework of a project — each is compiled on its own, and an error under
		// one framework's #if is not an error under another. Given the solution, the tool used to
		// check its first project alone, and its "0 errors" said nothing about the rest.
		var (targets, targetError) = await GetTargetsAsync(projectPath, forFile: filePath is not null, cancellationToken);
		
		if(targetError is not null)
			
			return scope.Error(targetError);
		
		// A file is looked up by the name given until its own path is known.
		var fileKey = filePath;
		
		// One file can be compiled by several projects of a solution (a linked or shared source),
		// and it can be an error in one and fine in another. The lookup above stops at the first
		// project that has it; given the solution, every other project with that same file is
		// diagnosed too — by the file's full path, which names that one file and no namesake.
		if(solutionGiven
			&& targets.Length > 0
			&& FindSyntaxTree(targets[0].Compilation, filePath!, targets[0].Project?.FilePath ?? projectPath, rootPath, out _) is { FilePath: { Length: > 0 } sharedPath }
			&& TryResolveSolution(givenPath, out var whole, out _)) {
			
			fileKey = sharedPath;
			
			var present = targets.Select(target => target.Project?.Id).OfType<ProjectId>().ToHashSet();
			var others  = new List<(Compilation Compilation, Project? Project)>();
			
			foreach(var projectId in whole.GetDocumentIdsWithFilePath(sharedPath).Select(id => id.ProjectId).Distinct())
				if(present.Add(projectId) && whole.GetProject(projectId) is { } other && await other.GetCompilationAsync(cancellationToken) is { } otherCompilation)
					others.Add((otherCompilation, other));
			
			targets = [..targets, ..others];
		}
		
		var diagnostics = new List<Diagnostic>();
		var notes       = new List<string>();
		var fileFound   = false;
		
		foreach(var (compilation, project) in targets) {
			
			// A project's own directory counts as well as the root: a solution may list a project
			// outside its directory (../Lib/Lib.csproj), and that project's errors are no less
			// real. The filter is for external and generated files (#113), not for them.
			var projectDirectory = Path.GetDirectoryName(project?.FilePath);
			
			bool Ours(Diagnostic diagnostic)
				=> IsUnderRoot(diagnostic, rootPath)
					|| (projectDirectory is not null && IsUnderRoot(diagnostic, projectDirectory))
			;
			
			SyntaxTree? tree = null;
			
			if(filePath is not null) {
				
				// Single-file: use SemanticModel for that tree only — avoids compiling the entire project.
				tree = FindSyntaxTree(compilation, fileKey!, project?.FilePath ?? projectPath, rootPath, out var ambiguous);
				
				if(ambiguous.Length > 0)
					
					return scope.Error(AmbiguousSourceFileError(filePath, ambiguous));
				
				if(tree is null)
					continue;
				
				fileFound = true;
				diagnostics.AddRange(compilation.GetSemanticModel(tree).GetDiagnostics());
			}
			else
				diagnostics.AddRange(compilation.GetDiagnostics().Where(Ours));
			
			if(includeAnalyzers && project is not null) {
				
				var (analyzerDiags, note) = await RunAnalyzersAsync(project, compilation, cancellationToken);
				
				// Across several compilations a note is about one of them: "no analyzer references"
				// for one project is not that no analyzer ran.
				if(note is not null)
					notes.Add(targets.Length > 1 ? $"{project.Name}: {note}" : note);
				
				var ours = analyzerDiags.Where(Ours);
				
				// Keep file-scoped queries file-scoped for analyzer output too.
				diagnostics.AddRange(tree is null ? ours : ours.Where(d => d.Location.SourceTree == tree));
			}
		}
		
		// An empty result here would read as "this file is clean" for a mistyped path, a file of
		// another project, or one the workspace has not loaded — the worst answer for the edit
		// check this tool is used for (#366). Fail like every other file-based tool.
		if(filePath is not null && !fileFound)
			
			return scope.Error(new ErrorResult($"File '{filePath}' not found in the compilation."));
		
		string? analyzerNote = notes.Count > 0 ? string.Join(" ", notes.Distinct()) : null;
		
		// Deduplicate by identity tuple — multi-TFM workspaces can surface the same
		// diagnostic from duplicate document entries across target frameworks.
		var filtered = diagnostics
			.Where(GetSeverityFilter(severity))
			.DistinctBy(d => (d.Severity, d.Id, d.Location.SourceTree?.FilePath, d.Location.GetLineSpan().StartLinePosition.Line, d.Location.GetLineSpan().StartLinePosition.Character, d.GetMessage()))
			.OrderByDescending(d => d.Severity)
			.ThenBy(d => d.Location.SourceTree?.FilePath)
			.ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
			.ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Character)
			.ThenBy(d => d.Id)
			.ThenBy(d => d.GetMessage())
			.ToArray()
		;
		
		var errorCount   = Array.FindAll(filtered, d => d.Severity == DiagnosticSeverity.Error).Length;
		var warningCount = Array.FindAll(filtered, d => d.Severity == DiagnosticSeverity.Warning).Length;
		var total        = filtered.Length;
		
		var summary = errorCount == 0 && warningCount == 0
			? "0 errors, 0 warnings"
			: $"{errorCount} error(s), {warningCount} warning(s)"
		;
		
		take = Math.Clamp(take, 0, 200);

		// Detect transient workspace-load noise: flood of type-resolution failures across many files.
		// Full-project queries only — a file-scoped query hitting the same pattern is plausibly real
		// breakage in one file, not workspace load state.
		var    possibleLoadIssue = false;
		
		string? hint              = null;

		string[]? loadWarnings = null;

		// Ask the workspace directly before falling back to the error-shape heuristic. A workspace
		// that loaded without metadata references, or with an MSBuild failure on a project file
		// (#300), is an authoritative answer, and it does not
		// depend on error volume — a small project produces only a handful of phantom errors, far
		// below the thresholds below, and would otherwise be reported as ordinary broken code.
		var health = TryGetHealth(projectPath)
		;
		
		// Only what these compilations are built from: a broken project elsewhere in the solution does not
		// make this one's diagnostics phantom.
		if(health is not null && targets.FirstOrDefault(target => target.Project is not null).Project is { } first)
			health = health.Covering(first.Solution, targets.Select(target => target.Project?.FilePath).OfType<string>());

		if(health is { IsHealthy: false }) {

			possibleLoadIssue = true;
			loadWarnings      = health.LoadWarnings is { Length: > 0 } w ? w : null;

			// "May be": a failure of the project file itself (an import that is not there) and a real
			// error in the source can come together, and no reload repairs the first (#300 review).
			// A project that was never restored is not contention, and no reload repairs it (#399).
			hint = health.OnlyNeedsRestore
				? $"These diagnostics are not the code's: the workspace {health.Problem}. Until the projects are restored, "
					+ "no reload or respawn gives them references."
				: $"These diagnostics may be phantom. The workspace {health.Problem}. Usually that is a contended "
					+ "MSBuild design-time build — something else writing the same obj\\ files — and roslyn_respawn "
					+ "reloads it. If load_warnings show a failure of the project file itself, fix that instead: no "
					+ "reload will. roslyn_build_project tells the two apart — it detects this state and runs a real "
					+ "dotnet build rather than trusting this compilation.";
		}
		else if(filePath is null && errorCount >= workspaceLoadMinErrors) {

			var errorsOnly    = Array.FindAll(filtered, d => d.Severity == DiagnosticSeverity.Error);
			var loadCodeCount = Array.FindAll(errorsOnly, d => workspaceLoadCodes.Contains(d.Id)).Length;
			var distinctFiles = errorsOnly.Select(d => d.Location.SourceTree?.FilePath).Distinct().Count();

			if((double) loadCodeCount / errorsOnly.Length >= workspaceLoadNamespaceFraction
				&& distinctFiles >= workspaceLoadMinDistinctFiles) {

				// The workspace reports itself healthy, so this is a guess — but the old advice
				// ("wait a few seconds and retry, or call roslyn_build_project to verify") was
				// actively wrong: the state is latched until a reload, and build_project
				// short-circuits on this same compilation and repeats the errors (issue #235).
				possibleLoadIssue = true;

				hint = "High volume of CS0246/CS0103/CS0234/CS0012 across many files. If the project really "
					+ "does build, the workspace may have loaded badly — call roslyn_respawn to reload, or "
					+ "roslyn_check_drift to inspect workspace health. Note that roslyn_build_project "
					+ "short-circuits on this same compilation, so pass forceBuild: true to run a real build.";
			}
		}

		// A recovered episode still explains results the caller may have already acted on.
		if(possibleLoadIssue && health?.LastUnhealthyLoad is { Length: > 0 } episode)
			hint += $" Last unhealthy load: {episode}";

		if(analyzerNote is not null)
			hint = hint is null ? analyzerNote : $"{analyzerNote} {hint}";
		
		// take: 0 fast path — return counts only. items is null (not []) to distinguish
		// "not requested" from "requested but empty".
		if(take is 0)
			return scope.Outcome(summary, new DiagnosticsResult(
				summary,
				"roslyn",
				errorCount,
				warningCount,
				total,
				0,
				total > 0,
				PageToken: null,
				Items: null,
				possibleLoadIssue,
				loadWarnings) {
					Hint = hint
				}
			);
		
		var effectiveSkip  = Math.Clamp(skip, 0, total);
		var effectiveCount = Math.Clamp(take, 0, total - effectiveSkip);
		var hasMore        = effectiveSkip + effectiveCount < total;
		
		var nextToken = hasMore
			? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
				new PageTokenData(effectiveSkip + effectiveCount, severity)))
			: null
		;
		
		var items = filtered[effectiveSkip..(effectiveSkip + effectiveCount)]
			.Select(d => ToItem(d, rootPath))
			.ToArray()
		;
		
		return scope.Outcome(summary, new DiagnosticsResult(
			summary,
			"roslyn",
			errorCount,
			warningCount,
			total,
			items.Length,
			hasMore,
			nextToken,
			items,
			possibleLoadIssue,
			loadWarnings) {
				Hint = hint
			}
		);
	}
	
	/// <summary>
	///     Runs the project's analyzer references over the compilation via
	///     CompilationWithAnalyzers.GetAnalysisResultAsync — the non-deprecated entry point.
	///     Analyzer failures must never break compiler diagnostics: any error (misbehaving
	///     analyzer, unresolved analyzer reference) degrades to compiler-only output with an
	///     explanatory note. Assemblies load through <see cref="ShadowCopyAnalyzerLoader"/> so the
	///     analyzed project's outputs are never locked.
	/// </summary>
	private async Task<(Diagnostic[] Diagnostics, string? Note)> RunAnalyzersAsync(Project project, Compilation compilation, CancellationToken cancellationToken)
	{
		try {
			
			var analyzers = project.AnalyzerReferences
				.SelectMany(r => AnalyzerLoading.GetShadowedAnalyzers(r, LanguageNames.CSharp))
				.ToImmutableArray()
			;
			
			if(analyzers.IsEmpty)
				
				return ([], "includeAnalyzers was requested, but the project has no analyzer references.");
			
			var options = new CompilationWithAnalyzersOptions(
				project.AnalyzerOptions,
				onAnalyzerException: null,
				concurrentAnalysis: true,
				logAnalyzerExecutionTime: false);
			
			var result = await new CompilationWithAnalyzers(compilation, analyzers, options)
				.GetAnalysisResultAsync(cancellationToken)
			;
			
			return ([..result.GetAllDiagnostics()], null);
		}
		catch(Exception ex) when(ex is not OperationCanceledException) {
			
			logger.LogError("Diagnostics", $"Analyzer run failed: {ex.GetType().Name}: {ex.Message}");
			
			return ([], $"Analyzer execution failed ({ex.GetType().Name}) — showing compiler diagnostics only.");
		}
	}
	
	// Both tools now return project-relative paths via TryMakeRelative on the base class.
	private static DiagnosticItem ToItem(Diagnostic d, string rootPath)
	{
		var span = d.Location.GetLineSpan();
		
		return new DiagnosticItem(
			Code:     d.Id,
			Severity: d.Severity.ToString().ToLowerInvariant(),
			File:     TryMakeRelative(span.Path, rootPath),
			Line:     span.StartLinePosition.Line + 1,
			Column:   span.StartLinePosition.Character + 1,
			Message:  d.GetMessage()
		);
	}
	
	private sealed record PageTokenData(int Skip, string? Severity);
}
