using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ApplySignatureChangeTool : RoslynMcpTool
{
	readonly ApprovalStore           approvals;
	readonly BackupStore             backups;
	readonly PhysicalSolutionApplier physicalApplier;
	
	public ApplySignatureChangeTool(WorkspaceResolver workspace, ApprovalStore approvals, BackupStore backups, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, logger, paginationCache)
	{
		this.approvals  = approvals;
		this.backups    = backups;
		physicalApplier = new PhysicalSolutionApplier(workspace, backups);
	}
	
	[McpServerTool(Name = "roslyn_apply_signature_change", Destructive = true, Title = "Apply Signature Change", OpenWorld = false)]
	[Description(
		"Commits or cancels a signature change previewed by roslyn_change_signature — always call that tool first to obtain a token. " +
		"This is step 2 of a two-step signature-change workflow; calling this without a valid token will fail. " +
		"Pass approval 'y' to apply once, 'session' to apply and auto-approve the same method for all future changes this session, " +
		"or 'n' to cancel without writing any files. " +
		"Tokens are single-use — once consumed or rejected, run roslyn_change_signature again if another change is needed. " +
		"On success, writes all changed files to disk — including the new overload and any updated call sites — via a " +
		"crash-safe, TOCTOU-guarded physical apply, and reports a per-file files[] breakdown (state, error, recovery " +
		"guidance) alongside the file count. " +
		"projectPath must resolve to the same workspace the preview was created against; a mismatch fails with a structured error.")]
	public async Task<ApplySignatureChangeResult> ApplySignatureChange(
		[Description("The confirmation token returned by roslyn_change_signature. Tokens are single-use — they expire after being applied or rejected.")] string token,
		[Description("'y' to apply this signature change once; 'session' to apply and auto-approve the same method for all future changes in this session; 'n' to cancel without writing any files.")] string approval,
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken)
	{
		using var scope = BeginTool("roslyn_apply_signature_change", $"{token} ({approval})");
		
		if(approval.Equals("n", StringComparison.OrdinalIgnoreCase)) {
			
			if(!approvals.Reject(token, ApprovalWorkflow.SignatureChange))
				return scope.Failed("token unavailable", new ApplySignatureChangeResult(
					$"Token '{token}' was not found, was consumed, or is already being applied.",
					null, "token unavailable", null));
			
			return scope.Outcome("cancelled", new ApplySignatureChangeResult("Signature change cancelled. No files were changed.", null, null, null));
		}
		
		if(!approval.Equals("y", StringComparison.OrdinalIgnoreCase) && !approval.Equals("session", StringComparison.OrdinalIgnoreCase))
			
			return scope.Failed("invalid approval", new ApplySignatureChangeResult("Invalid approval value. Use 'y', 'session', or 'n'.", null, "invalid approval", null));
		
		var forSession = approval.Equals("session", StringComparison.OrdinalIgnoreCase);
		var operation  = approvals.TryBeginApply(token, ApprovalWorkflow.SignatureChange);
		
		if(operation is null)
			
			return scope.Failed("token unavailable", new ApplySignatureChangeResult(
				$"Token '{token}' was not found, was consumed, or is already being applied. Run roslyn_change_signature again if the operation is not currently in progress.",
				null, "token unavailable", null));
		
		var physicalApplyStarted = false;
		
		try {
			
			if(operation.WorkspaceBinding is null)
				return scope.Failed("workspace binding missing", new ApplySignatureChangeResult(
					"The preview token does not contain an originating workspace. Re-run roslyn_change_signature.",
					null, "workspace binding missing", null));
			
			if(!TryResolveWorkspaceInfo(projectPath, out var requestedRoot, out var requestedIsMSBuild, out var requestedCsproj, out var wsInfoError)) {
				
				var (message, kind) = DescribeResolveFailure(wsInfoError);
				
				return scope.Failed("workspace unavailable", new ApplySignatureChangeResult(message, null, kind, null));
			}
			
			var requestedBinding = WorkspaceBinding.Create(requestedRoot, requestedIsMSBuild, requestedCsproj);
			
			if(!operation.WorkspaceBinding.Matches(requestedBinding))
				return scope.Failed("workspace mismatch", new ApplySignatureChangeResult(
					$"Apply aborted — this token belongs to '{operation.WorkspaceBinding.CanonicalPath}', " +
					$"but projectPath resolved to '{requestedBinding.CanonicalPath}'. Re-run roslyn_change_signature for the intended workspace.",
					null, "workspace mismatch", null));
			
			var boundProjectPath = operation.WorkspaceBinding.CanonicalPath;
			
			if(!TryResolveRoot(boundProjectPath, out var rootPath, out var rootError)) {
				
				var (message, kind) = DescribeResolveFailure(rootError);
				
				return scope.Failed("workspace unavailable", new ApplySignatureChangeResult(message, null, kind, null));
			}
			
			PhysicalSolutionApplyPlan plan;
			
			try {
				
				plan = await PhysicalSolutionApplyPlan.BuildAsync(
					operation.BaseSolution,
					operation.NewSolution,
					operation.FileStates,
					cancellationToken);
			}
			catch(PhysicalApplyPlanException ex) {
				
				return scope.Failed("invalid physical plan", new ApplySignatureChangeResult(
					$"Signature change cannot be applied safely: {ex.Message}",
					null, "invalid physical plan", null));
			}
			
			// Signature changes only ever add a forwarding overload and update call sites — both are
			// in-place edits to existing .cs files. Create/Delete would indicate a plan built from an
			// unexpected solution diff.
			// Stricter than apply_code_fix's guard, deliberately. Since #288 the shared plan builder also
			// emits changed additional/analyzer-config documents, so a signature change that somehow
			// touched one now fails loudly here instead of being dropped silently.
			if(plan.Files.Any(file => file.Operation != PhysicalFileOperation.Write
				|| !IsCSharpSourcePath(file.Path)))
				return scope.Failed("unsupported signature change", new ApplySignatureChangeResult(
					"Signature-change apply accepts modifications to existing C# files only; file creation and deletion are not supported.",
					null, "unsupported signature change", null));
			
			// Second layer of the TOCTOU guard: the preview may be minutes old. Re-validate against
			// disk before spending any effort on backups, so a stale preview aborts early and cheap.
			if(plan.ValidateCurrentState() is { } staleError)
				return scope.Failed("stale preview", new ApplySignatureChangeResult(
					$"{staleError} Re-run roslyn_change_signature.",
					null, "stale preview", null));
			
			try {
				
				await physicalApplier.PrepareBackupsAsync(plan, boundProjectPath, "roslyn_apply_signature_change");
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				
				return scope.Failed("backup failed", new ApplySignatureChangeResult(
					$"Write aborted — backups could not be prepared: {ex.Message}. No source files were modified. " +
					"The approval token is still valid — retry after resolving the issue.",
					null, "backup failed", null));
			}
			
			// Third layer: preparing backups did real I/O and took time — re-validate so a file that
			// changed during backup is caught before the write loop (which validates a fourth time,
			// per-file, immediately before each atomic swap). Each layer narrows the TOCTOU window.
			if(plan.ValidateCurrentState() is { } postBackupStaleError)
				return scope.Failed("stale preview", new ApplySignatureChangeResult(
					$"{postBackupStaleError} The file changed while backups were being prepared. " +
					"No source files were modified; the approval token is still valid.",
					null, "stale preview", null));
			
			// ApplyAsync can throw OperationCanceledException before touching any file (e.g. the
			// concurrency gate itself is canceled) — physicalApplyStarted is only latched to true once
			// the call returns a report, so a cancellation that never wrote anything returns the token
			// to pending instead of permanently consuming it.
			PhysicalApplyReport report;
			
			try {
				
				report = await physicalApplier.ApplyAsync(plan, boundProjectPath, cancellationToken);
				physicalApplyStarted = true;
			}
			catch(OperationCanceledException) {
				
				throw;
			}
			var files  = PhysicalApplyResultMapper.MapFiles(
				report,
				plan,
				path => TryMakeRelative(path, rootPath) ?? path,
				backups.IsEnabled,
				"signature change");
			
			if(report.Succeeded) {
				
				var sessionNote = forSession ? " Method approved for the remainder of this session." : string.Empty;
				
				return scope.Outcome($"{report.FilesWritten} file(s) written", new ApplySignatureChangeResult(
					$"Signature change applied.{sessionNote} Files written to disk.",
					report.FilesWritten, null, files));
			}
			
			var error = report.AllFilesReachedIntendedState
				? "apply state uncertain"
				: report.IsPartial ? "partial apply" : "apply failed"
			;
			var executionNote = report.ExecutionError is null
				? string.Empty
				: $" Persistence error: {report.ExecutionError}."
			;
			var outcomeMessage = report.AllFilesReachedIntendedState
				? "Every physical file matches the intended signature-change state, but the workspace reported a persistence failure."
				: "Signature change did not reach its intended state for every file."
			;
			
			return scope.Failed(error, new ApplySignatureChangeResult(
				$"{outcomeMessage}{executionNote} " +
				"Review each files[].state and files[].recovery value before deciding whether to roll back or complete the change.",
				report.FilesWritten,
				error,
				files));
		}
		finally {
			
			if(physicalApplyStarted)
				approvals.CompleteApply(token, ApprovalWorkflow.SignatureChange, forSession);
			else
				approvals.ReturnToPending(token, ApprovalWorkflow.SignatureChange);
		}
	}
}

internal sealed record ApplySignatureChangeResult(
	string             Message,
	int?               FilesWritten,
	string?            Error,
	ApplyFileResult[]? Files
);
