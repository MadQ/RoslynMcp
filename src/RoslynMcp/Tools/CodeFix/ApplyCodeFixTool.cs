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
		"Phase 1 applies targeted single-diagnostic fixes only, not FixAll.")]
	public async Task<ApplyCodeFixResult> ApplyCodeFix(
		[Description("The confirmation token returned by roslyn_preview_code_fix.")] string token,
		[Description("'y' to apply this code fix; 'n' to cancel without writing files.")] string approval,
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken)
	{
		using var scope = BeginTool("roslyn_apply_code_fix", $"{token} ({approval})");
		
		if(approval.Equals("n", StringComparison.OrdinalIgnoreCase)) {
			
			approvals.Reject(token);
			
			return scope.Failed("rejected", new ApplyCodeFixResult("Code fix rejected. No files were changed.", null, "rejected"));
		}
		
		if(!approval.Equals("y", StringComparison.OrdinalIgnoreCase))
			
			return scope.Failed("invalid approval", new ApplyCodeFixResult("Invalid approval value. Use 'y' or 'n'.", null, "invalid approval"));
		
		var operation = approvals.TryBeginApply(token);
		
		if(operation is null)
			
			return scope.Failed("token unavailable", new ApplyCodeFixResult($"Token '{token}' was not found, was consumed, or is already being applied. Run roslyn_preview_code_fix again if the operation is not currently in progress.", null, "token unavailable"));
		
		var physicalApplyStarted = false;
		
		try {
			
			if(operation.WorkspaceBinding is null)
				return scope.Failed("workspace binding missing", new ApplyCodeFixResult(
					"The preview token does not contain an originating workspace. Re-run roslyn_preview_code_fix.",
					null,
					"workspace binding missing"));
			
			var (requestedRoot, requestedIsMSBuild, requestedCsproj) = workspace.GetWorkspaceInfo(projectPath);
			var requestedBinding = WorkspaceBinding.Create(requestedRoot, requestedIsMSBuild, requestedCsproj);
			
			if(!operation.WorkspaceBinding.Matches(requestedBinding))
				return scope.Failed("workspace mismatch", new ApplyCodeFixResult(
					$"Apply aborted — this token belongs to '{operation.WorkspaceBinding.CanonicalPath}', " +
					$"but projectPath resolved to '{requestedBinding.CanonicalPath}'. Re-run roslyn_preview_code_fix for the intended workspace.",
					null,
					"workspace mismatch"));
			
			var boundProjectPath = operation.WorkspaceBinding.CanonicalPath;
			var rootPath         = workspace.GetRootPath(boundProjectPath);
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
			
			if(plan.ValidateCurrentState() is { } postBackupStaleError)
				return scope.Failed("stale preview", new ApplyCodeFixResult(
					$"{postBackupStaleError} The file changed while backups were being prepared. " +
					"No source files were modified; the approval token is still valid.",
					null,
					"stale preview"));
			
			physicalApplyStarted = true;
			
			var report = await physicalApplier.ApplyAsync(plan, operation.NewSolution, boundProjectPath);
			var files = report.Files
				.Select(file => new CodeFixFileResult(
					TryMakeRelative(file.Path, rootPath) ?? file.Path,
					file.State.ToString().ToLowerInvariant(),
					file.Error))
				.ToArray()
			;
			
			if(report.Succeeded)
				return scope.Outcome($"{report.FilesWritten} file(s) written, {report.FilesDeleted} deleted", new ApplyCodeFixResult(
					$"Code fix applied. {report.FilesWritten} file(s) written and {report.FilesDeleted} deleted.",
					report.FilesWritten,
					null,
					report.FilesDeleted,
					files));
			
			var error = report.IsPartial ? "partial apply" : "apply failed";
			var executionNote = report.ExecutionError is null
				? string.Empty
				: $" Persistence error: {report.ExecutionError}."
			;
			
			return scope.Failed(error, new ApplyCodeFixResult(
				$"Code fix did not reach its intended state for every file.{executionNote} " +
				RecoveryHint(plan, rootPath),
				report.FilesWritten,
				error,
				report.FilesDeleted,
				files));
		}
		finally {
			
			if(physicalApplyStarted)
				approvals.CompleteApply(token, approveForSession: false);
			else
				approvals.ReturnToPending(token);
		}
	}
	
	private static string RecoveryHint(PhysicalSolutionApplyPlan plan, string rootPath)
	{
		var firstFile = plan.Files
			.Select(file => TryMakeRelative(file.Path, rootPath) ?? file.Path)
			.FirstOrDefault()
		;
		var fileHint = firstFile is not null
			? $" Start with roslyn_local_history action 'list' for filePath '{firstFile}'."
			: string.Empty
		;
		
		return "Backups were saved before writing. Use roslyn_local_history to list snapshots for the affected file(s); " +
			"apply a 'pre' snapshot to roll back, or a 'post' snapshot to restore the intended code-fix content." +
			fileHint;
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
	string? Error
);
