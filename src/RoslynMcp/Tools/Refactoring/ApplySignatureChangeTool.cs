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

	[McpServerTool(Name = "roslyn_apply_signature_change", Destructive = true)]
	[Description(
		"Applies or rejects a signature change previewed by change_signature. " +
		"approval: 'y' = apply once, 'session' = apply and auto-approve this method for the session, 'n' = reject.")]
	public async Task<string> ApplySignatureChange(
		[Description("The confirmation token returned by change_signature.")] string token,
		[Description("'y' to apply, 'session' to apply and remember, 'n' to reject.")] string approval,
		[Description(ProjectPathDescription)] string projectPath)
	{
		using var scope = BeginTool("roslyn_apply_signature_change", token);

		if(approval.Equals("n", StringComparison.OrdinalIgnoreCase)) {

			approvals.Reject(token);
			return scope.Failed("rejected", "Signature change rejected. No files were changed.");
		}

		if(!approval.Equals("y", StringComparison.OrdinalIgnoreCase)
			&& !approval.Equals("session", StringComparison.OrdinalIgnoreCase))
			return "Invalid approval value. Use 'y', 'session', or 'n'.";

		var forSession = approval.Equals("session", StringComparison.OrdinalIgnoreCase);
		var op         = approvals.Consume(token, forSession);

		if(op is null)
			return scope.Failed("token not found", $"Token '{token}' not found or already consumed. Run change_signature again.");

		await SolutionDiff.ApplyToDiskAsync(op.BaseSolution, op.NewSolution);

		var filesChanged = op.NewSolution.GetChanges(op.BaseSolution)
			.GetProjectChanges()
			.SelectMany(p => p.GetChangedDocuments())
			.Count()
		;

		var sessionNote = forSession ? " Method approved for the remainder of this session." : string.Empty;

		return scope.Outcome($"{filesChanged} file(s) written", $"Signature change applied.{sessionNote} Files written to disk.");
	}
}
