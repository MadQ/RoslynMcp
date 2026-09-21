using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ApplyRenameTool : RoslynMcpTool
{
	readonly ApprovalStore           approvals;
	readonly BackupStore             backups;
	readonly PhysicalSolutionApplier physicalApplier;
	
	public ApplyRenameTool(WorkspaceResolver workspace, ApprovalStore approvals, BackupStore backups, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache)
	{
		this.approvals  = approvals;
		this.backups    = backups;
		physicalApplier = new PhysicalSolutionApplier(workspace, backups);
	}
	
	[McpServerTool(Name = "roslyn_apply_rename", Destructive = true, Title = "Apply Rename", OpenWorld = false)]
	[Description(
		"Commits or cancels a rename previewed by roslyn_preview_rename — always call that tool first to obtain a token. " +
		"This is step 2 of a two-step rename workflow; calling this without a valid token will fail. " +
		"Pass approval 'y' to apply once, 'session' to apply and auto-approve the same symbol for all future renames this session, " +
		"or 'n' to cancel without writing any files. " +
		"Tokens are single-use — once consumed or rejected, run roslyn_preview_rename again if another rename is needed. " +
		"On success, writes all changed files to disk via a crash-safe, TOCTOU-guarded physical apply and reports a " +
		"per-file files[] breakdown (state, error, recovery guidance) alongside the file/deletion/rename counts. " +
		"When the renamed symbol is a type whose file name matches the type name, the old file is also renamed on disk " +
		"after its content is confirmed written — a case-only rename (e.g. Foo.cs → foo.cs) uses a two-step move. " +
		"projectPath must resolve to the same workspace the preview was created against; a mismatch fails with a structured error.")]
	public async Task<ApplyRenameResult> ApplyRename(
		[Description("The confirmation token returned by roslyn_preview_rename. Tokens are single-use — they expire after being applied or rejected.")] string token,
		[Description("'y' to apply this rename once; 'session' to apply and auto-approve the same symbol for all future renames in this session; 'n' to cancel without writing any files.")] string approval,
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken
	)
	{
		using var scope = BeginTool("roslyn_apply_rename", $"{token} ({approval})");
		
		if(approval.Equals("n", StringComparison.OrdinalIgnoreCase)) {
			
			if(!approvals.Reject(token, ApprovalWorkflow.Rename))
				return scope.Failed("token unavailable", new ApplyRenameResult(
					$"Token '{token}' was not found, was consumed, or is already being applied.",
					null, null, "token unavailable", null, null));
			
			return scope.Outcome("cancelled", new ApplyRenameResult("Rename cancelled. No files were changed.", null, null, null, null, null));
		}
		
		if(!approval.Equals("y", StringComparison.OrdinalIgnoreCase) && !approval.Equals("session", StringComparison.OrdinalIgnoreCase))
			
			return scope.Failed("invalid approval", new ApplyRenameResult("Invalid approval value. Use 'y', 'session', or 'n'.", null, null, "invalid approval", null, null));
		
		var forSession = approval.Equals("session", StringComparison.OrdinalIgnoreCase);
		var operation  = approvals.TryBeginApply(token, ApprovalWorkflow.Rename);
		
		if(operation is null)
			
			return scope.Failed("token unavailable", new ApplyRenameResult(
				$"Token '{token}' was not found, was consumed, or is already being applied. Run roslyn_preview_rename again if the operation is not currently in progress.",
				null, null, "token unavailable", null, null));
		
		var physicalApplyStarted = false;
		
		try {
			
			if(operation.WorkspaceBinding is null)
				return scope.Failed("workspace binding missing", new ApplyRenameResult(
					"The preview token does not contain an originating workspace. Re-run roslyn_preview_rename.",
					null, null, "workspace binding missing", null, null));
			
			if(!TryResolveWorkspaceInfo(projectPath, out var requestedRoot, out var requestedIsMSBuild, out var requestedCsproj, out var wsInfoError)) {
				
				var (message, kind) = DescribeResolveFailure(wsInfoError);
				
				return scope.Failed("workspace unavailable", new ApplyRenameResult(message, null, null, kind, null, null));
			}
			
			var requestedBinding = WorkspaceBinding.Create(requestedRoot, requestedIsMSBuild, requestedCsproj);
			
			if(!operation.WorkspaceBinding.Matches(requestedBinding))
				return scope.Failed("workspace mismatch", new ApplyRenameResult(
					$"Apply aborted — this token belongs to '{operation.WorkspaceBinding.CanonicalPath}', " +
					$"but projectPath resolved to '{requestedBinding.CanonicalPath}'. Re-run roslyn_preview_rename for the intended workspace.",
					null, null, "workspace mismatch", null, null));
			
			var boundProjectPath = operation.WorkspaceBinding.CanonicalPath;
			
			if(!TryResolveRoot(boundProjectPath, out var rootPath, out var rootError)) {
				
				var (message, kind) = DescribeResolveFailure(rootError);
				
				return scope.Failed("workspace unavailable", new ApplyRenameResult(message, null, null, kind, null, null));
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
				
				return scope.Failed("invalid physical plan", new ApplyRenameResult(
					$"Rename cannot be applied safely: {ex.Message}",
					null, null, "invalid physical plan", null, null));
			}
			
			// Create/Delete are only expected for .cs files (type-matching file rename edge cases —
			// Renamer.RenameSymbolAsync with RenameFile: false never produces these in practice, but
			// non-.cs lifecycle changes slipping through would indicate a plan built from the wrong
			// symbol or solution). Write is unrestricted to match prior rename behavior.
			if(plan.Files.Any(file => file.Operation != PhysicalFileOperation.Write
				&& !IsCSharpSourcePath(file.Path)))
				return scope.Failed("unsupported rename change", new ApplyRenameResult(
					"Rename apply only supports creating or deleting .cs files; a non-C# file lifecycle change was detected.",
					null, null, "unsupported rename change", null, null));
			
			// Second layer of the TOCTOU guard: the preview may be minutes old. Re-validate against
			// disk before spending any effort on backups, so a stale preview aborts early and cheap.
			if(plan.ValidateCurrentState() is { } staleError)
				return scope.Failed("stale preview", new ApplyRenameResult(
					$"{staleError} Re-run roslyn_preview_rename.",
					null, null, "stale preview", null, null));
			
			try {
				
				await physicalApplier.PrepareBackupsAsync(plan, boundProjectPath, "roslyn_apply_rename");
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				
				return scope.Failed("backup failed", new ApplyRenameResult(
					$"Write aborted — backups could not be prepared: {ex.Message}. No files were modified. " +
					"The approval token is still valid — retry after resolving the issue.",
					null, null, "backup failed", null, null));
			}
			
			// Third layer: preparing backups did real I/O and took time — re-validate so a file that
			// changed during backup is caught before the write loop (which validates a fourth time,
			// per-file, immediately before each atomic swap). Each layer narrows the TOCTOU window.
			if(plan.ValidateCurrentState() is { } postBackupStaleError)
				return scope.Failed("stale preview", new ApplyRenameResult(
					$"{postBackupStaleError} The file changed while backups were being prepared. " +
					"No files were modified; the approval token is still valid.",
					null, null, "stale preview", null, null));
			
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
				"rename");
			
			if(!report.Succeeded) {
				
				var error = report.AllFilesReachedIntendedState
					? "apply state uncertain"
					: report.IsPartial ? "partial apply" : "apply failed"
				;
				var executionNote = report.ExecutionError is null
					? string.Empty
					: $" Persistence error: {report.ExecutionError}."
				;
				var outcomeMessage = report.AllFilesReachedIntendedState
					? "Every physical file matches the intended rename state, but the workspace reported a persistence failure."
					: "Rename did not reach its intended state for every file."
				;
				
				return scope.Failed(error, new ApplyRenameResult(
					$"{outcomeMessage}{executionNote} " +
					"Review each files[].state and files[].recovery value before deciding whether to roll back or complete the rename. " +
					"The file-rename step (if any) was skipped because content changes did not fully succeed.",
					report.FilesWritten,
					report.FilesDeleted > 0 ? report.FilesDeleted : null,
					error,
					null,
					files));
			}
			
			// Content writes succeeded — now handle the type-matching file-rename step. Physical
			// SolutionApplier has no concept of this; it operates purely on Roslyn Document identities
			// at their existing paths, not on symbol-driven physical moves, so this stays a distinct
			// post-apply phase — matching the file-rename step's design as documented in #264.
			var filesRenamed = 0;
			
			if(operation.FileRename is { } fr) {
				
				var (oldFilePath, newFilePath) = fr;
				
				// Case-only rename (e.g. Foo.cs → foo.cs) on case-insensitive filesystems
				// requires a two-step move via a temp path to force the case change.
				var caseOnly = string.Equals(oldFilePath, newFilePath, StringComparison.OrdinalIgnoreCase)
					&& !string.Equals(oldFilePath, newFilePath, StringComparison.Ordinal);
				
				if(!caseOnly && File.Exists(newFilePath))
					
					return scope.Failed("file rename conflict",
						new ApplyRenameResult(
							$"Symbol renamed, but file rename failed: '{Path.GetFileName(newFilePath)}' already exists. " +
							"Rename the file manually.",
							report.FilesWritten, report.FilesDeleted > 0 ? report.FilesDeleted : null, "file rename conflict", null, files));
				
				try {
					
					if(caseOnly) {
						
						// Two-step via temp: old → temp → new, FSW suppressed at each destination.
						var temp = oldFilePath + ".roslynmcp_rename_tmp"
						;
						await workspace.WriteAndInvalidate(boundProjectPath, temp, oldFilePath,
							() => { FileWriter.Move(oldFilePath, temp, false); return Task.CompletedTask; });
						await workspace.WriteAndInvalidate(boundProjectPath, newFilePath, temp,
							() => { FileWriter.Move(temp, newFilePath, false); return Task.CompletedTask; });
					}
					else {
						
						await workspace.WriteAndInvalidate(boundProjectPath, newFilePath, oldFilePath,
							() => { FileWriter.Move(oldFilePath, newFilePath, false); return Task.CompletedTask; });
					}
					
					filesRenamed++;
				}
				catch(Exception ex) {
					
					return scope.Failed("file rename failed",
						new ApplyRenameResult(
							$"Symbol renamed, but file rename failed: {ex.Message}. Rename the file manually.",
							report.FilesWritten, report.FilesDeleted > 0 ? report.FilesDeleted : null, "file rename failed", null, files));
				}
			}
			
			var sessionNote = forSession ? " Symbol approved for the remainder of this session." : string.Empty;
			
			return scope.Outcome($"{report.FilesWritten} file(s) written, {report.FilesDeleted} deleted", new ApplyRenameResult(
				$"Rename applied.{sessionNote} Files written to disk.",
				report.FilesWritten,
				report.FilesDeleted > 0 ? report.FilesDeleted : null,
				null,
				filesRenamed > 0 ? filesRenamed : null,
				files));
		}
		finally {
			
			if(physicalApplyStarted)
				approvals.CompleteApply(token, ApprovalWorkflow.Rename, forSession);
			else
				approvals.ReturnToPending(token, ApprovalWorkflow.Rename);
		}
	}
}

internal sealed record ApplyRenameResult(
	string           Message,
	int?             FilesWritten,
	int?             FilesDeleted,
	string?          Error,
	int?             FilesRenamed,
	ApplyFileResult[]? Files
);
