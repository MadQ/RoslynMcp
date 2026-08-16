using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ApplyCodeFixTool : RoslynMcpTool
{
	readonly ApprovalStore            approvals;
	readonly BackupStore              backups;
	readonly PhysicalSolutionApplier physicalApplier;
	
	public ApplyCodeFixTool(WorkspaceResolver workspace, ApprovalStore approvals, BackupStore backups, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, logger, paginationCache)
	{
		this.approvals  = approvals;
		this.backups    = backups;
		physicalApplier = new PhysicalSolutionApplier(workspace, backups);
	}
	
	[McpServerTool(Name = "roslyn_apply_code_fix", Destructive = true, Title = "Apply Code Fix", OpenWorld = false)]
	[Description(
		"Commits or cancels a code fix previewed by roslyn_preview_code_fix. " +
		"Always call preview first to obtain a token. Pass approval 'y' to apply " +
		"or 'n' to cancel without changing files. " +
		"Phase 1 applies targeted modifications to existing in-workspace .cs files only, not file lifecycle or project-system changes.")]
	public async Task<ApplyCodeFixResult> ApplyCodeFix(
		[Description("The confirmation token returned by roslyn_preview_code_fix.")] string token,
		[Description("'y' to apply this code fix; 'n' to cancel without writing files.")] string approval,
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken)
	{
		using var scope = BeginTool("roslyn_apply_code_fix", $"{token} ({approval})");
		
		if(approval.Equals("n", StringComparison.OrdinalIgnoreCase)) {
			
			if(!approvals.Reject(token, ApprovalWorkflow.CodeFix))
				return scope.Failed("token unavailable", new ApplyCodeFixResult(
					$"Token '{token}' was not found, was consumed, or is already being applied.",
					null,
					"token unavailable"));
			
			return scope.Failed("rejected", new ApplyCodeFixResult("Code fix rejected. No files were changed.", null, "rejected"));
		}
		
		if(!approval.Equals("y", StringComparison.OrdinalIgnoreCase))
			
			return scope.Failed("invalid approval", new ApplyCodeFixResult("Invalid approval value. Use 'y' or 'n'.", null, "invalid approval"));
		
		var operation = approvals.TryBeginApply(token, ApprovalWorkflow.CodeFix);
		
		if(operation is null)
			
			return scope.Failed("token unavailable", new ApplyCodeFixResult($"Token '{token}' was not found, was consumed, or is already being applied. Run roslyn_preview_code_fix again if the operation is not currently in progress.", null, "token unavailable"));
		
		var physicalApplyStarted = false;
		
		try {
			
			if(!operation.SymbolKey.StartsWith("codefix:", StringComparison.Ordinal))
				return scope.Failed("token type mismatch", new ApplyCodeFixResult(
					"The approval token was not created by roslyn_preview_code_fix. Use the matching apply tool.",
					null,
					"token type mismatch"));
			

			if(operation.WorkspaceBinding is null)
				return scope.Failed("workspace binding missing", new ApplyCodeFixResult(
					"The preview token does not contain an originating workspace. Re-run roslyn_preview_code_fix.",
					null,
					"workspace binding missing"));
			
			if(!TryResolveWorkspaceInfo(projectPath, out var requestedRoot, out var requestedIsMSBuild, out var requestedCsproj, out var wsInfoError)) {
				
				var (message, kind) = DescribeResolveFailure(wsInfoError);
				
				return scope.Failed("workspace unavailable", new ApplyCodeFixResult(message, null, kind));
			}
			
			var requestedBinding = WorkspaceBinding.Create(requestedRoot, requestedIsMSBuild, requestedCsproj);
			
			if(!operation.WorkspaceBinding.Matches(requestedBinding))
				return scope.Failed("workspace mismatch", new ApplyCodeFixResult(
					$"Apply aborted — this token belongs to '{operation.WorkspaceBinding.CanonicalPath}', " +
					$"but projectPath resolved to '{requestedBinding.CanonicalPath}'. Re-run roslyn_preview_code_fix for the intended workspace.",
					null,
					"workspace mismatch"));
			
			var boundProjectPath = operation.WorkspaceBinding.CanonicalPath;
			if(!TryResolveRoot(boundProjectPath, out var rootPath, out var rootError)) {
				
				var (message, kind) = DescribeResolveFailure(rootError);
				
				return scope.Failed("workspace unavailable", new ApplyCodeFixResult(message, null, kind));
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
				
				return scope.Failed("invalid physical plan", new ApplyCodeFixResult(
					$"Code fix cannot be applied safely: {ex.Message}",
					null,
					"invalid physical plan"));
			}
			
			if(plan.Files.Any(file => file.Operation != PhysicalFileOperation.Write
				|| !string.Equals(Path.GetExtension(file.Path), ".cs", StringComparison.OrdinalIgnoreCase)))
				return scope.Failed("unsupported code-fix change", new ApplyCodeFixResult(
					"Code-fix apply accepts modifications to existing C# files only; file creation and deletion are not supported.",
					null,
					"unsupported code-fix change"));
			

			// Second layer of the TOCTOU guard: the preview may be minutes old. Re-validate against
			// disk before spending any effort on backups, so a stale preview aborts early and cheap.
			if(plan.ValidateCurrentState() is { } staleError)
				return scope.Failed("stale preview", new ApplyCodeFixResult(
					$"{staleError} Re-run roslyn_preview_code_fix.",
					null,
					"stale preview"));
			
			try {
				
				await physicalApplier.PrepareBackupsAsync(plan, boundProjectPath, "roslyn_apply_code_fix");
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				
				return scope.Failed("backup failed", new ApplyCodeFixResult(
					$"Write aborted — backups could not be prepared: {ex.Message}. No source files were modified. " +
					"The approval token is still valid — retry after resolving the issue.",
					null,
					"backup failed"));
			}
			
			// Third layer: preparing backups did real I/O and took time — re-validate so a file that
			// changed during backup is caught before the write loop (which validates a fourth time,
			// per-file, immediately before each atomic swap). Each layer narrows the TOCTOU window.
			if(plan.ValidateCurrentState() is { } postBackupStaleError)
				return scope.Failed("stale preview", new ApplyCodeFixResult(
					$"{postBackupStaleError} The file changed while backups were being prepared. " +
					"No source files were modified; the approval token is still valid.",
					null,
					"stale preview"));
			
			physicalApplyStarted = true;
			
			var report = await physicalApplier.ApplyAsync(plan, boundProjectPath, cancellationToken);
			var plannedFiles = plan.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
			var files = report.Files
				.Select(file => {
					
					var relativePath = TryMakeRelative(file.Path, rootPath) ?? file.Path;
					var plannedFile  = plannedFiles[file.Path];
					
					return new CodeFixFileResult(
						relativePath,
						file.State.ToString().ToLowerInvariant(),
						file.Error,
						RecoveryGuidance(file.State, plannedFile.Operation, relativePath));
				})
				.ToArray()
			;
			
			if(report.Succeeded)
				return scope.Outcome($"{report.FilesWritten} file(s) written, {report.FilesDeleted} deleted", new ApplyCodeFixResult(
					$"Code fix applied. {report.FilesWritten} file(s) written and {report.FilesDeleted} deleted.",
					report.FilesWritten,
					null,
					report.FilesDeleted,
					files));
			
			var error = report.AllFilesReachedIntendedState
				? "apply state uncertain"
				: report.IsPartial ? "partial apply" : "apply failed"
			;
			var executionNote = report.ExecutionError is null
				? string.Empty
				: $" Persistence error: {report.ExecutionError}."
			;
			var outcomeMessage = report.AllFilesReachedIntendedState
				? "Every physical file matches the intended code-fix state, but the workspace reported a persistence failure."
				: "Code fix did not reach its intended state for every file."
			;
			
			return scope.Failed(error, new ApplyCodeFixResult(
				$"{outcomeMessage}{executionNote} " +
				"Review each files[].state and files[].recovery value before deciding whether to roll back or complete the fix.",
				report.FilesWritten,
				error,
				report.FilesDeleted,
				files));
		}
		finally {
			
			if(physicalApplyStarted)
				approvals.CompleteApply(token, ApprovalWorkflow.CodeFix, approveForSession: false);
			else
				approvals.ReturnToPending(token, ApprovalWorkflow.CodeFix);
		}
	}
	
	private string RecoveryGuidance(
		PhysicalApplyState state,
		PhysicalFileOperation operation,
		string filePath)
	{
		if(state == PhysicalApplyState.Untouched)
			return "No recovery is needed; the file still matches its preview baseline.";
		
		if(!backups.IsEnabled)
			return "Local history is disabled. Inspect the file and use source control or another backup to recover it.";
		
		var listStep = $"Use roslyn_local_history with action 'list' and filePath '{filePath}', " +
			"then call action 'preview' with the selected backup token before applying it.";
		
		return (state, operation) switch {
			
			(PhysicalApplyState.Written, PhysicalFileOperation.Write) =>
				$"{listStep} Apply the 'pre' snapshot to restore the original file.",
			
			(PhysicalApplyState.Written, PhysicalFileOperation.Create) =>
				$"This file did not exist before the fix, so there is no 'pre' snapshot. " +
				$"Delete it to roll back. To restore the intended content, {listStep} Apply the 'post' snapshot.",
			
			(PhysicalApplyState.Deleted, _) =>
				$"{listStep} Apply the 'pre' snapshot to recreate the deleted original file.",
			
			(PhysicalApplyState.Truncated or PhysicalApplyState.Uncertain, PhysicalFileOperation.Create) =>
				$"Inspect the file first. It had no original version, so delete it to roll back. " +
				$"To complete the fix, {listStep} Apply the 'post' snapshot.",
			
			(PhysicalApplyState.Truncated or PhysicalApplyState.Uncertain, PhysicalFileOperation.Delete) =>
				$"Inspect the file first. {listStep} Apply the 'pre' snapshot to restore the original content.",
			
			_ =>
				$"Inspect the file first. {listStep} Apply the 'pre' snapshot to restore the original content, " +
				"or the 'post' snapshot to complete the intended fix."
		};
	}
}

internal sealed record ApplyCodeFixResult : ToolResult, IToolError
{
	public ApplyCodeFixResult(
		string message,
		int? filesWritten,
		string? error,
		int? filesDeleted = null,
		CodeFixFileResult[]? files = null)
	{
		Message      = message;
		FilesWritten = filesWritten;
		FilesDeleted = filesDeleted;
		Files        = files;
		Error        = error;
	}
	
	public string               Message      { get; }
	public int?                 FilesWritten { get; }
	public int?                 FilesDeleted { get; }
	public CodeFixFileResult[]? Files        { get; }
}

internal sealed record CodeFixFileResult(
	string  Path,
	string  State,
	string? Error,
	string  Recovery
);
