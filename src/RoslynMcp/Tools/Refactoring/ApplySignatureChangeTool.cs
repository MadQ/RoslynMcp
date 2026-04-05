using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ApplySignatureChangeTool : RoslynMcpTool
{
	readonly ApprovalStore approvals;
	
	public ApplySignatureChangeTool(WorkspaceResolver workspace, ApprovalStore approvals, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, logger, paginationCache)
	{
		this.approvals = approvals;
	}
	
	[McpServerTool(Name = "roslyn_apply_signature_change", Destructive = true, Title = "Apply Signature Change", OpenWorld = false)]
	[Description(
		"Commits or cancels a signature change previewed by roslyn_change_signature — always call that tool first to obtain a token. " +
		"This is step 2 of a two-step signature-change workflow; calling this without a valid token will fail. " +
		"Pass approval 'y' to apply once, 'session' to apply and auto-approve the same method for all future changes this session, " +
		"or 'n' to cancel without writing any files. " +
		"Tokens are single-use — once consumed or rejected, run roslyn_change_signature again if another change is needed. " +
		"On success, writes all changed files to disk — including the new overload and any updated call sites — and reports the number of files modified.")]
	public async Task<string> ApplySignatureChange(
		[Description("The confirmation token returned by roslyn_change_signature. Tokens are single-use — they expire after being applied or rejected.")] string token,
		[Description("'y' to apply this signature change once; 'session' to apply and auto-approve the same method for all future changes in this session; 'n' to cancel without writing any files.")] string approval,
		[Description(ProjectPathDescription)] string projectPath)
	{
		using var scope = BeginTool("roslyn_apply_signature_change", $"{token} ({approval})");
		
		if(approval.Equals("n", StringComparison.OrdinalIgnoreCase)) {
			approvals.Reject(token);
			return scope.Failed("rejected", "Signature change rejected. No files were changed.");
		}
		
		if(!approval.Equals("y", StringComparison.OrdinalIgnoreCase) && !approval.Equals("session", StringComparison.OrdinalIgnoreCase))
			return scope.Failed("invalid approval", "Invalid approval value. Use 'y', 'session', or 'n'.");
		
		var forSession = approval.Equals("session", StringComparison.OrdinalIgnoreCase);
		var operation         = approvals.Consume(token, forSession);
		
		if(operation is null)
			return scope.Failed("token not found", $"Token '{token}' not found or already consumed. Run change_signature again.");
		
		// MSBuildWorkspace.TryApplyChanges writes to disk; AdhocWorkspace does not.
		if(!workspace.IsAdhoc(projectPath))
			workspace.ApplyChanges(projectPath, operation.NewSolution);
		else
			await SolutionDiff.ApplyToDiskAsync(operation.BaseSolution, operation.NewSolution);
		
		var filesChanged = operation.NewSolution.GetChanges(operation.BaseSolution)
			.GetProjectChanges()
			.SelectMany(p => p.GetChangedDocuments())
			.Count()
		;
		
		var sessionNote = forSession ? " Method approved for the remainder of this session." : string.Empty;
		
		return scope.Outcome($"{filesChanged} file(s) written", $"Signature change applied.{sessionNote} Files written to disk.");
	}
}
