using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class PreviewRefactoringTool : RefactoringToolBase
{
	readonly ApprovalStore approvals;
	
	public PreviewRefactoringTool(WorkspaceResolver workspace, ApprovalStore approvals, CodeActionHost codeActionHost, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, codeActionHost, logger, paginationCache)
	{
		this.approvals = approvals;
	}
	
	[McpServerTool(Name = "roslyn_preview_refactoring", ReadOnly = true, Title = "Preview Refactoring", OpenWorld = false)]
	[Description(
		"Computes one refactoring listed by roslyn_list_refactorings for the same file location and returns a unified diff plus an " +
		"approval token; files it would create or delete are named. Step 2 of the three-step workflow (list, preview, apply): nothing " +
		"is written until roslyn_apply_refactoring is called with the token. actionIndex is the index from roslyn_list_refactorings " +
		"for the same location; the result echoes the action's title and provider so a mismatch is visible. A refactoring may modify " +
		"existing in-workspace source and create or delete .cs files inside the workspace; one that renames or moves a file, changes " +
		"the project, or needs a dialog is refused with a structured error.")]
	public async Task<object> PreviewRefactoring(
		[Description("Relative or absolute path to the C# file.")] string filePath,
		[Description("1-based line of the caret, or of the start of the selection — the same location given to roslyn_list_refactorings.")] int line,
		[Description("1-based column of the caret, or of the start of the selection.")] int column,
		[Description("Zero-based index of the action in the roslyn_list_refactorings result for the same location.")] int actionIndex,
		CancellationToken cancellationToken,
		[Description(OptionalProjectPathDescription)] string? projectPath = null,
		[Description("1-based line where the selection ends. Omit for a caret.")] int? endLine = null,
		[Description("1-based column where the selection ends. With endLine but no endColumn, the selection runs to the end of endLine.")] int? endColumn = null)
	{
		using var scope = BeginTool("roslyn_preview_refactoring", filePath, new { line, column, endLine, endColumn, actionIndex });
		
		if(!TryResolveProjectArg(projectPath, filePath, out projectPath, out var projectError))
			
			return scope.Error(projectError);
		
		var (target, error) = await LocateAsync(projectPath, filePath, line, column, endLine, endColumn, cancellationToken);
		
		if(target is null)
			return scope.Error(error!);
		
		var query = await codeActionHost.GetRefactoringsAsync(target.Document, target.Span, cancellationToken);
		
		if(!query.Settings.Enabled)
			return scope.Error(ProvidersDisabled());
		
		var choices = query.Actions.Select(Choice).ToArray();
		
		if(actionIndex < 0 || actionIndex >= choices.Length)
			return scope.Failed("invalid action index", new PreviewRefactoringResult(
				null, null, null,
				choices.Length == 0
					? $"No refactoring applies at this location. {LocationHint}"
					: $"actionIndex must be between 0 and {choices.Length - 1}; the choices for this location are listed under actions.",
				"invalid action index") { Actions = choices });
		
		var selected     = query.Actions[actionIndex];
		var choice       = choices[actionIndex];
		var baseSolution = target.Document.Project.Solution;
		Solution newSolution;
		
		try {
			
			newSolution = await CodeActionOperations.GetChangedSolutionAsync(selected.Action, cancellationToken);
		}
		catch(UnsupportedCodeActionOperationException ex) {
			
			return scope.Failed("unsupported operation shape", new PreviewRefactoringResult(
				null, null, choice, ex.Message, "unsupported operation shape"));
		}
		catch(Exception ex) when(ex is not OperationCanceledException) {
			
			return scope.Failed("code action calculation failed", new PreviewRefactoringResult(
				null, null, choice,
				$"Refactoring '{selected.Title}' ({selected.Provider}) failed while calculating its changes: {ex.GetType().Name}: {ex.Message}",
				"code action calculation failed"));
		}
		
		ValidatedChanges changes;
		
		try {
			
			changes = await CodeActionChangeValidator.ValidateAsync(baseSolution, newSolution, target.RootPath, target.Boundary, cancellationToken);
		}
		catch(UnsupportedCodeActionChangeException ex) {
			
			return scope.Failed("unsupported refactoring change", new PreviewRefactoringResult(
				null, null, choice, ex.Message, "unsupported refactoring change"));
		}
		
		var diff = await SolutionDiff.BuildAsync(baseSolution, newSolution, cancellationToken);
		
		if(diff == "(no changes)")
			return scope.Failed("no changes", new PreviewRefactoringResult(
				null, diff, choice, "The refactoring completed but produced no file changes.", "no changes"));
		
		var relativeFile = TryMakeRelative(target.FullPath, target.RootPath) ?? target.FullPath;
		var operationKey = $"refactoring:{selected.Provider}:{selected.EquivalenceKey ?? selected.Title}:{relativeFile}:{target.Span.Start}-{target.Span.End}";
		IReadOnlyDictionary<string, PreviewFileState> fileStates;
		
		try {
			
			fileStates = await PreviewFileStateBuilder.BuildAsync(baseSolution, newSolution, cancellationToken);
		}
		catch(Exception ex) when(ex is UnsupportedFileEncodingException or DecoderFallbackException or EncoderFallbackException) {
			
			return scope.Failed("unsupported file encoding", new PreviewRefactoringResult(
				null, diff, choice, $"{ex.Message} No approval token was created.", "unsupported file encoding"));
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			return scope.Failed("preview file state changed", new PreviewRefactoringResult(
				null, diff, choice,
				$"Could not capture a safe file state for this preview: {ex.Message} Re-run roslyn_preview_refactoring.",
				"preview file state changed"));
		}
		
		// A change that proposes different contents for two documents that are one file on disk
		// can never be applied, so it gets no token (#338).
		if(await PhysicalSolutionApplier.FindTargetConflictAsync(baseSolution, newSolution, fileStates, target.Boundary, cancellationToken) is { } conflict)
			return scope.Failed("conflicting link contents", new PreviewRefactoringResult(
				null, diff, choice, $"This refactoring cannot be applied: {conflict} No approval token was created.", "conflicting link contents"));
		
		if(!TryResolveWorkspaceInfo(projectPath, out var workspaceRoot, out var isMSBuild, out var csprojPath, out var workspaceInfoError))
			
			return scope.Error(workspaceInfoError);
		
		var workspaceBinding = WorkspaceBinding.Create(workspaceRoot, isMSBuild, csprojPath);
		var token            = approvals.Register(baseSolution, newSolution, diff, operationKey, ApprovalWorkflow.Refactoring, null, fileStates, workspaceBinding);
		
		return scope.Outcome($"preview ready: {choice.Title}", new PreviewRefactoringResult(
			token,
			diff,
			choice,
			$"Review the diff, then call roslyn_apply_refactoring with token '{token}' and approval 'y' or 'n'.",
			null)
		{
			CreatedFiles = changes.CreatedFiles.IsEmpty ? null : [..changes.CreatedFiles.Select(path => TryMakeRelative(path, target.RootPath) ?? path)],
			DeletedFiles = changes.DeletedFiles.IsEmpty ? null : [..changes.DeletedFiles.Select(path => TryMakeRelative(path, target.RootPath) ?? path)],
			Caution      = AdhocCaution(projectPath),
		});
	}
}
