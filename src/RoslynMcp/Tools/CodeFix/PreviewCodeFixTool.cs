using System.Collections.Immutable;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class PreviewCodeFixTool : RoslynMcpTool
{
	readonly ApprovalStore approvals;
	readonly CodeFixHost   codeFixHost;
	
	public PreviewCodeFixTool(WorkspaceResolver workspace, ApprovalStore approvals, CodeFixHost codeFixHost, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, logger, paginationCache)
	{
		this.approvals   = approvals;
		this.codeFixHost = codeFixHost;
	}
	
	[McpServerTool(Name = "roslyn_preview_code_fix", ReadOnly = true, Title = "Preview Code Fix", OpenWorld = false, Idempotent = true)]
	[Description(
		"Preview Roslyn CodeFixProvider actions for a single diagnostic at a file location. " +
		"Returns available action choices when multiple fixes exist, or returns a unified diff plus token " +
		"for the selected fix. This is step 1 of a two-step workflow; no files are written until " +
		"roslyn_apply_code_fix is called. Phase 1 supports targeted single-diagnostic fixes only, not FixAll.")]
	public async Task<object> PreviewCodeFix(
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Relative or absolute path to the C# file containing the diagnostic.")] string filePath,
		[Description("1-based line number near or inside the diagnostic span.")] int line,
		[Description("1-based column number near or inside the diagnostic span.")] int column,
		CancellationToken cancellationToken,
		[Description("Optional diagnostic ID to disambiguate when multiple diagnostics are on the same line, e.g. 'RMCP001'.")] string? diagnosticId = null,
		[Description("Optional zero-based code action index. Required when multiple fixes are available.")] int? actionIndex = null)
	{
		using var scope = BeginTool("roslyn_preview_code_fix", filePath, new { line, column, diagnosticId, actionIndex });
		
		if(!TryGetProject(projectPath, out var project, out var error))
			
			return scope.Error(error!);
		
		var rootPath = workspace.GetRootPath(projectPath);
		var fullPath = ResolveFilePath(filePath, rootPath);
		
		if(fullPath is null)
			return scope.Failed("file not found", new PreviewCodeFixResult(
				null, null, null, [],
				$"File '{filePath}' not found under project root.",
				"file not found"));
		
		var document = project.Documents.FirstOrDefault(d =>
			string.Equals(d.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
		
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
		
		ImmutableArray<AvailableCodeFix> fixes;
		
		try {
			
			fixes = await codeFixHost.GetFixesAsync(document, diagnostic, cancellationToken);
		}
		catch(CodeFixProviderException ex) {
			
			return scope.Failed("code fix provider failed", new PreviewCodeFixResult(
				null, diagnostic.Id, null, [],
				ex.Message,
				"code fix provider failed"));
		}
		
		var choices = fixes
			.Select((f, i) => new CodeFixActionChoice(i, diagnostic.Id, diagnostic.GetMessage(), f.Title, f.ProviderName, f.EquivalenceKey))
			.ToArray()
		;
		
		if(fixes.Length == 0)
			return scope.Failed("no fixes", new PreviewCodeFixResult(
				null, diagnostic.Id, null, [],
				$"Diagnostic '{diagnostic.Id}' was found, but no loaded CodeFixProvider registered a fix.",
				"no fixes"));
		
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
		var newSolution = await GetChangedSolutionAsync(selected.Action, cancellationToken);
		
		if(newSolution is null)
			return scope.Failed("no solution changes", new PreviewCodeFixResult(
				null, diagnostic.Id, null, choices,
				"Selected fix did not produce solution changes.",
				"no solution changes"));
		
		var diff = await SolutionDiff.BuildAsync(document.Project.Solution, newSolution, cancellationToken);
		
		if(diff == "(no changes)")
			return scope.Failed("no changes", new PreviewCodeFixResult(
				null, diagnostic.Id, diff, choices,
				"Selected fix completed but produced no file changes.",
				"no changes"));
		
		var operationKey = $"codefix:{diagnostic.Id}:{selected.ProviderName}:{selected.EquivalenceKey ?? selected.Title}";
		IReadOnlyDictionary<string, PreviewFileState> fileStates;
		
		try {
			
			fileStates = BuildPreviewFileStates(document.Project.Solution, newSolution);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			return scope.Failed("preview file state changed", new PreviewCodeFixResult(
				null, diagnostic.Id, diff, [choices[selectedIndex]],
				$"Could not capture a safe file state for this preview: {ex.Message} Re-run roslyn_preview_code_fix.",
				"preview file state changed"));
		}
		
		var token = approvals.Register(document.Project.Solution, newSolution, diff, operationKey, null, fileStates);
		var selectedAction = choices[selectedIndex];
		
		return scope.Outcome("preview ready", new PreviewCodeFixResult(
			token,
			diagnostic.Id,
			diff,
			[selectedAction],
			$"Review the diff, then call roslyn_apply_code_fix with token '{token}' and approval 'y', 'session', or 'n'.",
			null));
	}
	
	private static async Task<Solution?> GetChangedSolutionAsync(CodeAction action, CancellationToken cancellationToken)
	{
		var operations = await action.GetOperationsAsync(cancellationToken)
			.ConfigureAwait(false)
		;
		
		return operations
			.OfType<ApplyChangesOperation>()
			.SingleOrDefault()
			?.ChangedSolution;
	}
	
	private static IReadOnlyDictionary<string, PreviewFileState> BuildPreviewFileStates(Solution baseSolution, Solution newSolution)
	{
		var states = new Dictionary<string, PreviewFileState>(StringComparer.OrdinalIgnoreCase);
		
		foreach(var projectChange in newSolution.GetChanges(baseSolution).GetProjectChanges()) {
			
			foreach(var docId in projectChange.GetChangedDocuments()) {
				
				var doc = baseSolution.GetDocument(docId);
				
				if(doc?.FilePath is null)
					continue;
				
				if(!File.Exists(doc.FilePath))
					throw new IOException($"Affected file '{doc.FilePath}' no longer exists while creating the preview.");
				
				states[doc.FilePath] = new PreviewFileState(ExpectedFileState.Exists, ComputeFileHash(doc.FilePath));
			}
			
			foreach(var docId in projectChange.GetAddedDocuments()) {
				
				var doc = newSolution.GetDocument(docId);
				
				if(doc?.FilePath is null)
					continue;
				
				if(!baseSolution.GetDocumentIdsWithFilePath(doc.FilePath).IsEmpty) {
					
					if(!File.Exists(doc.FilePath))
						throw new IOException($"Affected linked file '{doc.FilePath}' no longer exists while creating the preview.");
					
					states[doc.FilePath] = new PreviewFileState(ExpectedFileState.Exists, ComputeFileHash(doc.FilePath));
					
					continue;
				}
				
				if(File.Exists(doc.FilePath))
					throw new IOException($"New file '{doc.FilePath}' already exists while creating the preview.");
				
				states[doc.FilePath] = new PreviewFileState(ExpectedFileState.Absent, null);
			}
			
			foreach(var docId in projectChange.GetRemovedDocuments()) {
				
				var doc = baseSolution.GetDocument(docId);
				
				if(doc?.FilePath is null || !newSolution.GetDocumentIdsWithFilePath(doc.FilePath).IsEmpty)
					continue;
				
				if(!File.Exists(doc.FilePath))
					throw new IOException($"Affected file '{doc.FilePath}' no longer exists while creating the preview.");
				
				states[doc.FilePath] = new PreviewFileState(ExpectedFileState.Exists, ComputeFileHash(doc.FilePath));
			}
		}
		
		return states;
	}
	
	private static string ComputeFileHash(string path)
	{
		using var stream = File.OpenRead(path);
		var hash = SHA256.HashData(stream);
		
		return Convert.ToHexString(hash);
	}
	
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
				&& string.Equals(d.Location.SourceTree?.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)
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
	
	private static async Task<ImmutableArray<Diagnostic>> GetDocumentDiagnosticsAsync(Project project, Document document, CancellationToken cancellationToken)
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
		
		var builder = ImmutableArray.CreateBuilder<Diagnostic>();
		builder.AddRange(compilation.GetSemanticModel(tree).GetDiagnostics(cancellationToken: cancellationToken));
		
		var analyzers = await GetAnalyzersAsync(project, cancellationToken);
		
		if(analyzers.Length > 0) {
			
			var withAnalyzers = compilation.WithAnalyzers(analyzers, project.AnalyzerOptions);
			var analyzerDiagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken)
				.ConfigureAwait(false)
			;
			
			builder.AddRange(analyzerDiagnostics.Where(d => d.Location.SourceTree == tree));
		}
		
		return builder.ToImmutable();
	}
	
	private static async Task<ImmutableArray<DiagnosticAnalyzer>> GetAnalyzersAsync(Project project, CancellationToken cancellationToken)
	{
		var builder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
		
		foreach(var reference in project.AnalyzerReferences) {
			
			builder.AddRange(reference.GetAnalyzers(project.Language));
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
}

internal sealed record CodeFixActionChoice(
	int     Index,
	string  DiagnosticId,
	string  DiagnosticMessage,
	string? Title,
	string? Provider,
	string? EquivalenceKey
);
