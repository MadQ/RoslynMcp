using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ApplyRefactoringTool : CodeActionApplyTool
{
	static readonly WorkflowNames Names = new(
		ApprovalWorkflow.Refactoring,
		"refactoring:",
		"roslyn_preview_refactoring",
		"roslyn_apply_refactoring",
		"Refactoring",
		"refactoring");
	
	public ApplyRefactoringTool(WorkspaceResolver workspace, ApprovalStore approvals, BackupStore backups, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, approvals, backups, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_apply_refactoring", Destructive = true, Title = "Apply Refactoring", OpenWorld = false)]
	[Description(
		"Commits or cancels a refactoring previewed by roslyn_preview_refactoring. Always call preview first to obtain a token. " +
		"Pass approval 'y' to apply or 'n' to cancel without changing files. Applies exactly the previewed changes — edits to " +
		"existing source and the creation or deletion of .cs files — with crash-safe backups; the result names each file's state " +
		"and lists createdFiles and deletedFiles. A created file is picked up by the workspace on its next reload. " +
		"Omit projectPath to apply against the workspace the preview was created in; when given, it must resolve to that same workspace.")]
	public async Task<ApplyCodeActionResult> ApplyRefactoring(
		[Description("The confirmation token returned by roslyn_preview_refactoring.")] string token,
		[Description("'y' to apply this refactoring; 'n' to cancel without writing files.")] string approval,
		CancellationToken cancellationToken,
		[Description(OptionalProjectPathDescription)] string? projectPath = null)
	{
		using var scope = BeginTool("roslyn_apply_refactoring", $"{token} ({approval})");
		
		var outcome = await ApplyAsync(token, approval, projectPath, Names, cancellationToken);
		
		return outcome.Success
			? scope.Outcome(outcome.Detail, outcome.Result)
			: scope.Failed(outcome.Detail, outcome.Result)
		;
	}
}
