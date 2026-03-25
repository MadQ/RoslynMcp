using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ApplyRenameTool : RoslynMcpTool
{
	readonly ApprovalStore approvals;

	public ApplyRenameTool(WorkspaceResolver workspace, ApprovalStore approvals, FileLogger logger) : base(workspace, logger)
	{
		this.approvals = approvals;
	}

	[McpServerTool(Name = "roslyn_apply_rename", Destructive = true)]
	[Description(
		"Applies or rejects a rename previewed by preview_rename. " +
		"approval: 'y' = apply once, 'session' = apply and auto-approve this symbol for the session, 'n' = reject."
	)]
	public async Task<string> ApplyRename(
		[Description("The confirmation token returned by preview_rename."								)] string token,
		[Description("'y' to apply, 'session' to apply and remember for this session, 'n' to reject."	)] string approval,
		[Description(ProjectPathDescription)] string? projectPath = null
	)
	{
		using var scope = BeginTool("roslyn_apply_rename", token);
		if(approval.Equals("n", StringComparison.OrdinalIgnoreCase)) {
			approvals.Reject(token);
			return scope.Failed("rejected", "Rename rejected. No files were changed.");
		}

		if(!approval.Equals("y", StringComparison.OrdinalIgnoreCase)
            && !approval.Equals("session", StringComparison.OrdinalIgnoreCase))
            return "Invalid approval value. Use 'y', 'session', or 'n'.";

		var forSession = approval.Equals("session", StringComparison.OrdinalIgnoreCase);
		var op         = approvals.Consume(token, forSession);

		if(op is null)
			return scope.Failed("token not found", $"Token '{token}' not found or already consumed. Run preview_rename again.");

		var oldSolution = workspace.GetSolution(projectPath);
		await SolutionDiff.ApplyToDiskAsync(oldSolution, op.NewSolution);

		var filesChanged = op.NewSolution.GetChanges(oldSolution)
			.GetProjectChanges()
			.SelectMany(p => p.GetChangedDocuments())
			.Count()
		;

		var sessionNote = forSession ? " Symbol approved for the remainder of this session." : string.Empty;

		return scope.Outcome($"{filesChanged} file(s) written", $"Rename applied.{sessionNote} Files written to disk. Compilation will refresh automatically.");
	}
}
