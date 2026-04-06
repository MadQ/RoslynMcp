using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ApplyRenameTool : RoslynMcpTool
{
	readonly ApprovalStore approvals;
	
	public ApplyRenameTool(WorkspaceResolver workspace, ApprovalStore approvals, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache)
	{
		this.approvals = approvals;
	}
	
	[McpServerTool(Name = "roslyn_apply_rename", Destructive = true, Title = "Apply Rename", OpenWorld = false)]
	[Description(
		"Commits or cancels a rename previewed by roslyn_preview_rename — always call that tool first to obtain a token. " +
		"This is step 2 of a two-step rename workflow; calling this without a valid token will fail. " +
		"Pass approval 'y' to apply once, 'session' to apply and auto-approve the same symbol for all future renames this session, " +
		"or 'n' to cancel without writing any files. " +
		"Tokens are single-use — once consumed or rejected, run roslyn_preview_rename again if another rename is needed. " +
		"On success, writes all changed files to disk and reports the number of files modified.")]
	public async Task<string> ApplyRename(
		[Description("The confirmation token returned by roslyn_preview_rename. Tokens are single-use — they expire after being applied or rejected.")] string token,
		[Description("'y' to apply this rename once; 'session' to apply and auto-approve the same symbol for all future renames in this session; 'n' to cancel without writing any files.")] string approval,
		[Description(ProjectPathDescription)] string projectPath
	)
	{
		using var scope = BeginTool("roslyn_apply_rename", $"{token} ({approval})");
		
		if(approval.Equals("n", StringComparison.OrdinalIgnoreCase)) {
			approvals.Reject(token);
			return scope.Failed("rejected", "Rename rejected. No files were changed.");
		}
		
		if(!approval.Equals("y", StringComparison.OrdinalIgnoreCase) && !approval.Equals("session", StringComparison.OrdinalIgnoreCase))
			return scope.Failed("invalid approval", "Invalid approval value. Use 'y', 'session', or 'n'.");
		
		var forSession = approval.Equals("session", StringComparison.OrdinalIgnoreCase);
		var operation  = approvals.Consume(token, forSession);
		
		if(operation is null)
			return scope.Failed("token not found", $"Token '{token}' not found or already consumed. Run preview_rename again.");
		
		// MSBuildWorkspace.TryApplyChanges writes to disk; AdhocWorkspace does not.
		if(!workspace.IsAdhoc(projectPath))
			workspace.ApplyChanges(projectPath, operation.NewSolution);
		else
			await SolutionDiff.ApplyToDiskAsync(operation.BaseSolution, operation.NewSolution,
			(path, content) => workspace.WriteAndInvalidate(projectPath, path,
				() => FileWriter.WriteAllTextAsync(path, content)));
		
		var filesChanged = operation.NewSolution.GetChanges(operation.BaseSolution)
			.GetProjectChanges()
			.SelectMany(p => p.GetChangedDocuments())
			.Count()
		;
		
		var sessionNote = forSession ? " Symbol approved for the remainder of this session." : string.Empty;
		
		return scope.Outcome($"{filesChanged} file(s) written", $"Rename applied.{sessionNote} Files written to disk. Compilation will refresh automatically.");
	}
}
