using System.Collections.Immutable;
using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class PreviewCodeFixTool : RoslynMcpTool
{
	readonly ApprovalStore  approvals;
	readonly CodeActionHost codeActionHost;
	
	public PreviewCodeFixTool(WorkspaceResolver workspace, ApprovalStore approvals, CodeActionHost codeActionHost, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, logger, paginationCache)
	{
		this.approvals      = approvals;
		this.codeActionHost = codeActionHost;
	}
	
	[McpServerTool(Name = "roslyn_preview_code_fix", ReadOnly = true, Title = "Preview Code Fix", OpenWorld = false)]
	[Description(
		"Preview the code fixes offered for a single diagnostic at a file location: Roslyn's own fixers, the fixers of the " +
		"project's analyzer packages, the server's bundled ones, and any providers the server is configured with. " +
		"Returns the available action choices when more than one fix exists, or a unified diff plus a token for the selected fix. " +
		"This is step 1 of a two-step workflow; no files are written until roslyn_apply_code_fix is called. " +
		"A fix may modify existing in-workspace text documents (.cs source, declared AdditionalFiles items, analyzer config) " +
		"and create or delete .cs source files inside the workspace; adding or removing other files, moving files, external " +
		"or linked files, project-system changes, and FixAll are rejected. When no fix is available, providerProblems names " +
		"the provider sources that failed to load or that this workspace cannot have, since a fixer may be missing for that " +
		"reason. For a refactoring that is not tied to a diagnostic, use roslyn_list_refactorings.")]
	public async Task<object> PreviewCodeFix(
		[Description("Relative or absolute path to the C# file containing the diagnostic.")] string filePath,
		[Description("1-based line number near or inside the diagnostic span.")] int line,
		[Description("1-based column number near or inside the diagnostic span.")] int column,
		CancellationToken cancellationToken,
		[Description(OptionalProjectPathDescription)] string? projectPath = null,
		[Description("Optional diagnostic ID to disambiguate when multiple diagnostics are on the same line, e.g. 'RMCP001'.")] string? diagnosticId = null,
		[Description("Optional zero-based code action index. Required when multiple fixes are available.")] int? actionIndex = null)
	{
		using var scope = BeginTool("roslyn_preview_code_fix", filePath, new { line, column, diagnosticId, actionIndex });
		
		if(!TryResolveProjectArg(projectPath, filePath, out projectPath, out var projectError))
			
			return scope.Error(projectError);
		
		if(!TryGetProject(projectPath, out var project, out var error))
			
			return scope.Error(error!);
		
		if(!TryResolveFileContext(projectPath, out var rootPath, out var boundary, out var resolveError))
			
			return scope.Error(resolveError);
		var fullPath = ResolveFilePath(filePath, rootPath, boundary);
		
		if(fullPath is null)
			return scope.Failed("file not found", new PreviewCodeFixResult(
				null, null, null, [],
				$"File '{filePath}' not found under project root.",
				"file not found"));
		
		var document = project.Documents.FirstOrDefault(d =>
			SecurityBoundary.PathComparer.Equals(d.FilePath, fullPath));
		
		if(document is null)
			return scope.Failed("document not found", new PreviewCodeFixResult(
				null, null, null, [],
				$"File '{filePath}' is not a C# document in the resolved project.",
				"document not found"));
		
		var text = await document.GetTextAsync(cancellationToken);
		var position = GetPosition(text, line, column);
		
		if(position < 0)
			return scope.Failed("invalid location", new PreviewCodeFixResult(
				null, null, null, [],
				$"Line {line}, column {column} is outside '{filePath}'.",
				"invalid location"));
		
		var diagnostics = await GetDocumentDiagnosticsAsync(project, document, cancellationToken);
		var diagnostic = SelectDiagnostic(diagnostics, fullPath, text, position, line, diagnosticId, out var matches);
		
		if(diagnostic is null) {
			
			var actions = matches
				.Select((d, i) => new CodeFixActionChoice(i, d.Id, d.GetMessage(), null, null, null))
				.ToArray()
			;
			
			var message = matches.Length > 1
				? "Multiple diagnostics matched this location. Re-run with diagnosticId."
				: "No diagnostic was found at the requested location."
			;
			
			return scope.Failed(matches.Length > 1 ? "diagnostic ambiguous" : "diagnostic not found", new PreviewCodeFixResult(
				null, null, null, actions, message,
				matches.Length > 1 ? "diagnostic ambiguous" : "diagnostic not found"));
		}
		
		var query = await codeActionHost.GetFixesAsync(document, diagnostic, cancellationToken);
		
		// Roslyn attaches some fixers to a companion of the diagnostic it shows: the unnecessary-using
		// fixer answers to RemoveUnnecessaryImportsFixable, not to the IDE0005 beside it at the same
		// span. When the diagnostic asked for has no fixer, its companions at the position are tried,
		// smallest span first, and the one that has a fixer is the one fixed — the choices say which.
		if(query.Fixes.IsEmpty && query.Failures.IsEmpty)
			foreach(var companion in CompanionsAt(diagnostics, diagnostic, fullPath, position)) {
				
				var companionQuery = await codeActionHost.GetFixesAsync(document, companion, cancellationToken);
				
				if(companionQuery.Fixes.IsEmpty)
					continue;
				
				scope.Record($"fixed through companion diagnostic {companion.Id}");
				diagnostic = companion;
				query      = companionQuery;
				
				break;
			}
		
		var fixes = query.Fixes;
		
		// A provider that throws no longer sinks the preview: the fixes of the others are still offered,
		// and the failure is only the answer when nothing is left.
		if(fixes.IsEmpty && !query.Failures.IsEmpty)
			return scope.Failed("code fix provider failed", new PreviewCodeFixResult(
				null, diagnostic.Id, null, [],
				string.Join(" ", query.Failures.Select(failure => $"Code fix provider '{failure.Provider}' failed: {failure.Error}")),
				"code fix provider failed"));
		
		if(!query.Failures.IsEmpty)
			scope.Record($"{query.Failures.Length} provider failure(s): {string.Join("; ", query.Failures.Select(failure => failure.Provider))}");
		
		var choices = fixes
			.Select((f, i) => new CodeFixActionChoice(i, diagnostic.Id, diagnostic.GetMessage(), f.Title, f.ProviderName, f.EquivalenceKey))
			.ToArray()
		;
		
		if(fixes.Length == 0) {
			
			// "No fix" is the one answer that needs its reason (#423): the fixer may be missing because
			// its source failed to load, or because this workspace mode cannot have it. Those sources
			// are named, as the refactoring list names them; an answer with a fix says nothing of them.
			var problems = query.Sources
				.Where(source => !source.Problems.IsEmpty)
				.Select(ProviderProblemInfo.From)
				.ToArray()
			;
			
			var reason = problems.Length > 0
				? $" {problems.Length} provider source(s) had a problem (providerProblems), so a fixer may be missing for that reason; roslyn_info lists every source."
				: ""
			;
			
			return scope.Failed("no fixes", new PreviewCodeFixResult(
				null, diagnostic.Id, null, [],
				$"Diagnostic '{diagnostic.Id}' was found, but no code-fix provider available to the server supports it.{reason}",
				"no fixes")
			{
				ProviderProblems = problems.Length > 0 ? problems : null,
			});
		}
		
		if(fixes.Length > 1 && actionIndex is null)
			return scope.Outcome($"{fixes.Length} action(s)", new PreviewCodeFixResult(
				null, diagnostic.Id, null, choices,
				"Multiple fixes are available. Re-run with actionIndex to preview one.",
				null));
		
		var selectedIndex = actionIndex ?? 0;
		
		if(selectedIndex < 0 || selectedIndex >= fixes.Length)
			return scope.Failed("invalid action index", new PreviewCodeFixResult(
				null, diagnostic.Id, null, choices,
				$"actionIndex must be between 0 and {fixes.Length - 1}.",
				"invalid action index"));
		
		var selected = fixes[selectedIndex];
		Solution newSolution;
		
		try {
			
			newSolution = await CodeActionOperations.GetChangedSolutionAsync(selected.Action, cancellationToken);
		}
		catch(UnsupportedCodeActionOperationException ex) {
			
			return scope.Failed("unsupported operation shape", new PreviewCodeFixResult(
				null, diagnostic.Id, null, [choices[selectedIndex]],
				ex.Message,
				"unsupported operation shape"));
		}
		catch(Exception ex) when(ex is not OperationCanceledException) {
			
			return scope.Failed("code action calculation failed", new PreviewCodeFixResult(
				null, diagnostic.Id, null, [choices[selectedIndex]],
				$"Selected code fix failed while calculating its operations: {ex.Message}",
				"code action calculation failed"));
		}
		
		ValidatedChanges changes;
		
		try {
			
			changes = await CodeActionChangeValidator.ValidateAsync(
				document.Project.Solution,
				newSolution,
				rootPath,
				boundary,
				cancellationToken);
		}
		catch(UnsupportedCodeActionChangeException ex) {
			return scope.Failed("unsupported code-fix change", new PreviewCodeFixResult(
				null, diagnostic.Id, null, [choices[selectedIndex]],
				ex.Message,
				"unsupported code-fix change"));
		}
		
		var diff = await SolutionDiff.BuildAsync(document.Project.Solution, newSolution, cancellationToken);
		
		if(diff == "(no changes)")
			return scope.Failed("no changes", new PreviewCodeFixResult(
				null, diagnostic.Id, diff, choices,
				"Selected fix completed but produced no file changes.",
				"no changes"));
		
		var operationKey = $"codefix:{diagnostic.Id}:{selected.ProviderName}:{selected.EquivalenceKey ?? selected.Title}";
		IReadOnlyDictionary<string, PreviewFileState> fileStates;
		
		try {
			
			fileStates = await PreviewFileStateBuilder.BuildAsync(
				document.Project.Solution,
				newSolution,
				cancellationToken);
		}
		catch(Exception ex) when(ex is UnsupportedFileEncodingException or DecoderFallbackException or EncoderFallbackException) {
			
			return scope.Failed("unsupported file encoding", new PreviewCodeFixResult(
				null, diagnostic.Id, diff, [choices[selectedIndex]],
				$"{ex.Message} No approval token was created.",
				"unsupported file encoding"));
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			return scope.Failed("preview file state changed", new PreviewCodeFixResult(
				null, diagnostic.Id, diff, [choices[selectedIndex]],
				$"Could not capture a safe file state for this preview: {ex.Message} Re-run roslyn_preview_code_fix.",
				"preview file state changed"));
		}
		
		// A change that proposes different contents for two documents that are one file on disk
		// can never be applied, so it gets no token (#338).
		if(await PhysicalSolutionApplier.FindTargetConflictAsync(document.Project.Solution, newSolution, fileStates, workspace.GetSecurityBoundary(projectPath), cancellationToken) is { } conflict)
			
			return scope.Failed("conflicting link contents", new PreviewCodeFixResult(
				null, diagnostic.Id, diff, [choices[selectedIndex]],
				$"This fix cannot be applied: {conflict} No approval token was created.",
				"conflicting link contents"));
		
		if(!TryResolveWorkspaceInfo(projectPath, out var workspaceRoot, out var isMSBuild, out var csprojPath, out var wsInfoError))
			
			return scope.Error(wsInfoError);
		var workspaceBinding = WorkspaceBinding.Create(workspaceRoot, isMSBuild, csprojPath);
		var token = approvals.Register(document.Project.Solution, newSolution, diff, operationKey, ApprovalWorkflow.CodeFix, null, fileStates, workspaceBinding);
		var selectedAction = choices[selectedIndex];
		
		return scope.Outcome("preview ready", new PreviewCodeFixResult(
			token,
			diagnostic.Id,
			diff,
			[selectedAction],
			$"Review the diff, then call roslyn_apply_code_fix with token '{token}' and approval 'y' or 'n'.",
			null)
		{
			CreatedFiles = changes.CreatedFiles.IsEmpty ? null : [..changes.CreatedFiles.Select(path => TryMakeRelative(path, rootPath) ?? path)],
			DeletedFiles = changes.DeletedFiles.IsEmpty ? null : [..changes.DeletedFiles.Select(path => TryMakeRelative(path, rootPath) ?? path)],
		});
	}
	
	// The other diagnostics reported at the position: same file, span containing it, another id.
	private static IEnumerable<Diagnostic> CompanionsAt(ImmutableArray<Diagnostic> diagnostics, Diagnostic requested, string fullPath, int position)
		=> diagnostics
			.Where(d => d.Location.IsInSource
				&& d.Id != requested.Id
				&& SecurityBoundary.PathComparer.Equals(d.Location.SourceTree?.FilePath, fullPath)
				&& (d.Location.SourceSpan.Contains(position) || d.Location.SourceSpan.Start == position))
			.OrderBy(d => d.Location.SourceSpan.Length);
	

	private static Diagnostic? SelectDiagnostic(
		ImmutableArray<Diagnostic> diagnostics,
		string fullPath,
		SourceText text,
		int position,
		int line,
		string? diagnosticId,
		out Diagnostic[] matches)
	{
		var sameFile = diagnostics
			.Where(d => d.Location.IsInSource
				&& SecurityBoundary.PathComparer.Equals(d.Location.SourceTree?.FilePath, fullPath)
				&& (diagnosticId is null || string.Equals(d.Id, diagnosticId, StringComparison.OrdinalIgnoreCase)))
			.ToArray()
		;
		
		var exact = sameFile
			.Where(d => d.Location.SourceSpan.Contains(position) || d.Location.SourceSpan.Start == position)
			.ToArray()
		;
		
		matches = exact.Length > 0
			? exact
			: sameFile.Where(d => text.Lines.GetLinePosition(d.Location.SourceSpan.Start).Line + 1 == line).ToArray()
		;
		
		return matches.Length == 1 ? matches[0] : null;
	}
	
	private async Task<ImmutableArray<Diagnostic>> GetDocumentDiagnosticsAsync(Project project, Document document, CancellationToken cancellationToken)
	{
		var compilation = await project.GetCompilationAsync(cancellationToken)
			.ConfigureAwait(false)
		;
		
		if(compilation is null)
			return [];
		
		var tree = await document.GetSyntaxTreeAsync(cancellationToken)
			.ConfigureAwait(false)
		;
		
		if(tree is null)
			return [];
		
		var semanticModel = compilation.GetSemanticModel(tree);
		var builder       = ImmutableArray.CreateBuilder<Diagnostic>();
		
		builder.AddRange(semanticModel.GetDiagnostics(cancellationToken: cancellationToken));
		
		// Analyzer failures must never sink the whole preview: the compiler diagnostics above (with
		// their fixes) are already collected. A broken or throwing project analyzer degrades
		// to compiler-only diagnostics instead of aborting; cancellation still propagates. Assemblies
		// load through the shared shadow-copy loader so the analyzed project's outputs stay unlocked.
		// The project's analyzers and Roslyn's IDE analyzers (#389) run over this document alone, as
		// an editor's light bulb does: a whole-compilation analysis is not needed to fix one diagnostic.
		try {
			
			var set = codeActionHost.AnalyzersFor(project);
			
			if(!set.Analyzers.IsEmpty) {
				
				var withAnalyzers = compilation.WithAnalyzers(set.Analyzers, project.AnalyzerOptions);
				
				builder.AddRange(await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(tree, cancellationToken).ConfigureAwait(false));
				builder.AddRange(await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(semanticModel, filterSpan: null, cancellationToken).ConfigureAwait(false));
			}
		}
		catch(Exception ex) when(ex is not OperationCanceledException) {
			
			logger.LogError("PreviewCodeFix", $"Analyzer run failed for '{document.FilePath}': {ex.GetType().Name}: {ex.Message}");
		}
		
		return builder.ToImmutable();
	}
}


internal sealed record PreviewCodeFixResult : ToolResult, IToolError
{
	public PreviewCodeFixResult(
		string? token,
		string? diagnosticId,
		string? diff,
		CodeFixActionChoice[] actions,
		string message,
		string? error)
	{
		Token        = token;
		DiagnosticId = diagnosticId;
		Diff         = diff;
		Actions      = actions;
		Message      = message;
		Error        = error;
	}
	
	public string?               Token        { get; }
	public string?               DiagnosticId { get; }
	public string?               Diff         { get; }
	public CodeFixActionChoice[] Actions      { get; }
	public string                Message      { get; }
	
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string[]? CreatedFiles { get; init; }
	
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string[]? DeletedFiles { get; init; }
	
	/// <summary>
	///     With a "no fixes" answer only: the provider sources that had a problem, each with its first
	///     problem, in the shape <c>roslyn_list_refactorings</c> gives them (#423).
	/// </summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public ProviderProblemInfo[]? ProviderProblems { get; init; }
}

internal sealed record CodeFixActionChoice(
	int     Index,
	string  DiagnosticId,
	string  DiagnosticMessage,
	string? Title,
	string? Provider,
	string? EquivalenceKey
);
