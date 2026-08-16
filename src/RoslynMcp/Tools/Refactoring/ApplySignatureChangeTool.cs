using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ApplySignatureChangeTool : RoslynMcpTool
{
	readonly ApprovalStore approvals;
	readonly BackupStore    backups;
	
	public ApplySignatureChangeTool(WorkspaceResolver workspace, ApprovalStore approvals, BackupStore backups, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, logger, paginationCache)
	{
		this.approvals = approvals;
		this.backups   = backups;
	}
	
	[McpServerTool(Name = "roslyn_apply_signature_change", Destructive = true, Title = "Apply Signature Change", OpenWorld = false)]
	[Description(
		"Commits or cancels a signature change previewed by roslyn_change_signature — always call that tool first to obtain a token. " +
		"This is step 2 of a two-step signature-change workflow; calling this without a valid token will fail. " +
		"Pass approval 'y' to apply once, 'session' to apply and auto-approve the same method for all future changes this session, " +
		"or 'n' to cancel without writing any files. " +
		"Tokens are single-use — once consumed or rejected, run roslyn_change_signature again if another change is needed. " +
		"On success, writes all changed files to disk — including the new overload and any updated call sites — and reports the number of files modified.")]
	public async Task<ApplySignatureChangeResult> ApplySignatureChange(
		[Description("The confirmation token returned by roslyn_change_signature. Tokens are single-use — they expire after being applied or rejected.")] string token,
		[Description("'y' to apply this signature change once; 'session' to apply and auto-approve the same method for all future changes in this session; 'n' to cancel without writing any files.")] string approval,
		[Description(ProjectPathDescription)] string projectPath)
	{
		using var scope = BeginTool("roslyn_apply_signature_change", $"{token} ({approval})");
		
		if(approval.Equals("n", StringComparison.OrdinalIgnoreCase)) {
			
			approvals.Reject(token, ApprovalWorkflow.SignatureChange);
			
			return scope.Outcome("cancelled", new ApplySignatureChangeResult("Signature change cancelled. No files were changed.", null, null));
		}
		
		if(!approval.Equals("y", StringComparison.OrdinalIgnoreCase) && !approval.Equals("session", StringComparison.OrdinalIgnoreCase))
			
			return scope.Failed("invalid approval", new ApplySignatureChangeResult("Invalid approval value. Use 'y', 'session', or 'n'.", null, "invalid approval"));
		
		var forSession        = approval.Equals("session", StringComparison.OrdinalIgnoreCase);
		var operation         = approvals.Peek(token, ApprovalWorkflow.SignatureChange);
		
		if(operation is null)
			
			return scope.Failed("token not found", new ApplySignatureChangeResult($"Token '{token}' not found or already consumed. Run change_signature again.", null, "token not found"));
		
		// Collect changed docs so we can back them up and verify each one after writing.
		if(!TryResolveRoot(projectPath, out var rootPath, out var rootError)) {
			
			var (message, kind) = DescribeResolveFailure(rootError);
			
			return scope.Failed("workspace unavailable", new ApplySignatureChangeResult(message, null, kind));
		}
		
		var changedDocs = operation.NewSolution.GetChanges(operation.BaseSolution)
			.GetProjectChanges()
			.SelectMany(p => p.GetChangedDocuments()
				.Select(id => operation.NewSolution.GetDocument(id)!))
			.Where(d => d.FilePath is not null)
			.ToArray()
		;
		
		// Save pre- and post-change snapshots for each file before consuming the token so a
		// backup failure leaves the token intact — the user can retry without running preview again.
		bool preSaved = false
		;
		
		try {
			
			foreach(var doc in changedDocs) {
				
				var bytes = FileWriter.Utf8NoBom.GetBytes((await doc.GetTextAsync()).ToString());
				preSaved = false;
				await backups.SavePreAsync(doc.FilePath!, projectPath, "roslyn_apply_signature_change");
				preSaved = true;
				await backups.SavePostAsync(doc.FilePath!, projectPath, "roslyn_apply_signature_change", bytes);
			}
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			var phaseWord = preSaved ? "post" : "pre";
			var snapNote  = preSaved ? " Any pre-change snapshots already saved are not needed." : string.Empty;
			
			return scope.Failed("backup failed", new ApplySignatureChangeResult(
				$"Write aborted — could not save {phaseWord}-change backup: {ex.Message}. " +
				$"No files were modified.{snapNote} " +
				"The approval token is still valid — retry after resolving the issue.",
				null, "backup failed"));
		}
		
		// Backups saved — now consume the token (point of no return).
		approvals.Consume(token, ApprovalWorkflow.SignatureChange, forSession);
		
		// MSBuildWorkspace.TryApplyChanges writes to disk; AdhocWorkspace does not.
		if(!workspace.IsAdhoc(projectPath)) {
			
			workspace.ApplyChanges(projectPath, operation.NewSolution);
			
			// Self-healing recovery: if TryApplyChanges truncated a file, re-write from memory.
			foreach(var doc in changedDocs) {
				
				var path    = doc.FilePath!;
				var relPath = TryMakeRelative(path, rootPath) ?? path;
				var bytes   = FileWriter.Utf8NoBom.GetBytes((await doc.GetTextAsync()).ToString());
				
				if(await TryRecoverTruncation(relPath, path, projectPath, bytes) is { } truncErr)
					return scope.Failed("truncation detected", new ApplySignatureChangeResult(truncErr.Error ?? "File truncation detected.", null, "truncation detected"));
			}
		}
		else {
			
			await SolutionDiff.ApplyToDiskAsync(operation.BaseSolution, operation.NewSolution,
			async (path, content) => {
				
				await workspace.WriteAndInvalidate(projectPath, path,
					() => FileWriter.WriteAllTextAsync(path, content));
				
				var relPath = TryMakeRelative(path, rootPath) ?? path;
				
				if(CheckForTruncation(relPath, path, content.Length) is { } truncErr)
					throw new IOException(truncErr.Error);
			});
		}
		
		var filesChanged = changedDocs.Length;
		
		var sessionNote = forSession ? " Method approved for the remainder of this session." : string.Empty;
		
		return scope.Outcome($"{filesChanged} file(s) written", new ApplySignatureChangeResult(
			$"Signature change applied.{sessionNote} Files written to disk.",
			filesChanged, null));
	}
}

internal sealed record ApplySignatureChangeResult(
	string  Message,
	int?    FilesWritten,
	string? Error
);
