using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ApplyCodeFixTool : CodeActionApplyTool
{
	static readonly WorkflowNames Names = new(
		ApprovalWorkflow.CodeFix,
		"codefix:",
		"roslyn_preview_code_fix",
		"roslyn_apply_code_fix",
		"Code fix",
		"fix");
	
	public ApplyCodeFixTool(WorkspaceResolver workspace, ApprovalStore approvals, BackupStore backups, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, approvals, backups, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_apply_code_fix", Destructive = true, Title = "Apply Code Fix", OpenWorld = false)]
	[Description(
		"Commits or cancels a code fix previewed by roslyn_preview_code_fix. " +
		"Always call preview first to obtain a token. Pass approval 'y' to apply " +
		"or 'n' to cancel without changing files. " +
		"Applies exactly the previewed changes — edits to existing in-workspace text documents and the creation or deletion " +
		"of .cs source files — with crash-safe backups; the result names each file's state and any files created or deleted. " +
		"Omit projectPath to apply against the workspace the preview was created in; when given, it must resolve to that same workspace.")]
	public async Task<ApplyCodeActionResult> ApplyCodeFix(
		[Description("The confirmation token returned by roslyn_preview_code_fix.")] string token,
		[Description("'y' to apply this code fix; 'n' to cancel without writing files.")] string approval,
		CancellationToken cancellationToken,
		[Description(OptionalProjectPathDescription)] string? projectPath = null)
	{
		using var scope = BeginTool("roslyn_apply_code_fix", $"{token} ({approval})");
		
		var outcome = await ApplyAsync(token, approval, projectPath, Names, cancellationToken);
		
		return outcome.Success
			? scope.Outcome(outcome.Detail, outcome.Result)
			: scope.Failed(outcome.Detail, outcome.Result)
		;
	}
}
