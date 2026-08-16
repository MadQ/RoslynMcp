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
		"with code, file, line, and message. Pass take: 0 for a lightweight error-count-only check " +
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
		[Description("Optional relative file path to scope results, e.g. 'Core/Foo.cs'. Omit to check all files in the project.")] string? filePath = null,
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
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return scope.Error(error!);
		
		if(!TryResolveRoot(projectPath, out var rootPath, out var rootError))
			
			return scope.Error(rootError);
		IEnumerable<Diagnostic> diagnostics;
		
		if(filePath is not null) {
			
			// Single-file: use SemanticModel for that tree only — avoids compiling the entire project.
			var tree = FindSyntaxTree(compilation, filePath)
			;
			
			diagnostics = tree is not null
				? compilation.GetSemanticModel(tree).GetDiagnostics()
				: [];
		}
		else
			diagnostics = compilation.GetDiagnostics()
				.Where(d => IsUnderRoot(d, rootPath))
			;
		
		string? analyzerNote = null;
		
		if(includeAnalyzers) {
			
			var (analyzerDiags, note) = await RunAnalyzersAsync(projectPath, compilation, cancellationToken);
			
			analyzerNote = note;
			
			var inRoot = analyzerDiags.Where(d => IsUnderRoot(d, rootPath));
			
			// Keep file-scoped queries file-scoped for analyzer output too; a missing
			// file already produced an empty compiler set — add nothing.
			diagnostics = filePath is null
				? diagnostics.Concat(inRoot)
				: FindSyntaxTree(compilation, filePath) is { } scopeTree
					? diagnostics.Concat(inRoot.Where(d => d.Location.SourceTree == scopeTree))
					: diagnostics;
		}
		
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
		// that loaded without metadata references is an authoritative answer, and it does not
		// depend on error volume — a small project produces only a handful of phantom errors, far
		// below the thresholds below, and would otherwise be reported as ordinary broken code.
		var health = TryGetHealth(projectPath)
		;

		if(health is { IsHealthy: false }) {

			possibleLoadIssue = true;
			loadWarnings      = health.LoadWarnings is { Length: > 0 } w ? w : null;

			hint = "These errors are phantom. The workspace loaded without metadata references on: "
				+ $"{string.Join(", ", health.ProjectsWithoutReferences)} — usually a contended MSBuild "
				+ "design-time build, not a problem with your code. Call roslyn_respawn to reload the "
				+ "workspace. To confirm the code itself is fine, call roslyn_build_project — it detects "
				+ "this state and runs a real dotnet build rather than trusting this compilation.";
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
	private async Task<(Diagnostic[] Diagnostics, string? Note)> RunAnalyzersAsync(string projectPath, Compilation compilation, CancellationToken cancellationToken)
	{
		try {
			
			var project   = workspace.GetProject(projectPath);
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
