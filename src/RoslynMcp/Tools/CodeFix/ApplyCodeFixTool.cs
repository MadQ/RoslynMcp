using System.ComponentModel;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ApplyCodeFixTool : RoslynMcpTool
{
	readonly ApprovalStore approvals;
	readonly BackupStore    backups;
	
	public ApplyCodeFixTool(WorkspaceResolver workspace, ApprovalStore approvals, BackupStore backups, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, logger, paginationCache)
	{
		this.approvals = approvals;
		this.backups   = backups;
	}
	
	[McpServerTool(Name = "roslyn_apply_code_fix", Destructive = true, Title = "Apply Code Fix", OpenWorld = false)]
	[Description(
		"Commits or cancels a code fix previewed by roslyn_preview_code_fix. " +
		"Always call preview first to obtain a token. Pass approval 'y' to apply once, " +
		"'session' to apply and approve similar code-fix actions for this session, or 'n' to cancel. " +
		"Phase 1 applies targeted single-diagnostic fixes only, not FixAll.")]

	public async Task<ApplyCodeFixResult> ApplyCodeFix(
		[Description("The confirmation token returned by roslyn_preview_code_fix.")] string token,
		[Description("'y' to apply this code fix once; 'session' to apply and approve similar fixes this session; 'n' to cancel without writing files.")] string approval,
		[Description(ProjectPathDescription)] string projectPath)
	{
		using var scope = BeginTool("roslyn_apply_code_fix", $"{token} ({approval})");
		
		if(approval.Equals("n", StringComparison.OrdinalIgnoreCase)) {
			
			approvals.Reject(token);
			
			return scope.Failed("rejected", new ApplyCodeFixResult("Code fix rejected. No files were changed.", null, "rejected"));
		}
		
		if(!approval.Equals("y", StringComparison.OrdinalIgnoreCase) && !approval.Equals("session", StringComparison.OrdinalIgnoreCase))
			
			return scope.Failed("invalid approval", new ApplyCodeFixResult("Invalid approval value. Use 'y', 'session', or 'n'.", null, "invalid approval"));
		
		var forSession = approval.Equals("session", StringComparison.OrdinalIgnoreCase);
		var operation  = approvals.Peek(token);
		
		if(operation is null)
			
			return scope.Failed("token not found", new ApplyCodeFixResult($"Token '{token}' not found or already consumed. Run preview_code_fix again.", null, "token not found"));
		
		var rootPath       = workspace.GetRootPath(projectPath);
		var projectChanges = operation.NewSolution.GetChanges(operation.BaseSolution)
			.GetProjectChanges()
			.ToArray()
		;
		var changedDocs = projectChanges
			.SelectMany(p => p.GetChangedDocuments().Concat(p.GetAddedDocuments())
				.Select(id => operation.NewSolution.GetDocument(id))
				.OfType<Document>())
			.Where(d => d.FilePath is not null)
			.GroupBy(d => d.FilePath!, StringComparer.OrdinalIgnoreCase)
			.Select(g => g.First())
			.ToArray()
		;
		var removedDocs = projectChanges
			.SelectMany(p => p.GetRemovedDocuments()
				.Select(id => operation.BaseSolution.GetDocument(id))
				.OfType<Document>())
			.Where(d => d.FilePath is not null)
			.GroupBy(d => d.FilePath!, StringComparer.OrdinalIgnoreCase)
			.Select(g => g.First())
			.Where(d => operation.NewSolution.GetDocumentIdsWithFilePath(d.FilePath!).IsEmpty)
			.ToArray()
		;
		var affectedDocs = changedDocs.Concat(removedDocs).ToArray();
		
		if(affectedDocs.Length == 0)
			return scope.Failed("no files changed", new ApplyCodeFixResult("Previewed code fix has no changed files. Re-run preview_code_fix.", null, "no files changed"));
		
		if(CheckForStalePreview(operation.FileStates) is { } staleError)
			return scope.Failed("stale preview", new ApplyCodeFixResult(staleError, null, "stale preview"));
		
		bool preSaved = false
		;
		
		try {
			
			foreach(var doc in changedDocs) {
				
				var bytes = FileWriter.Utf8NoBom.GetBytes((await doc.GetTextAsync()).ToString());
				preSaved = false;
				await backups.SavePreAsync(doc.FilePath!, projectPath, "roslyn_apply_code_fix");
				preSaved = true;
				await backups.SavePostAsync(doc.FilePath!, projectPath, "roslyn_apply_code_fix", bytes);
			}
			
			foreach(var doc in removedDocs) {
				
				preSaved = false;
				await backups.SavePreAsync(doc.FilePath!, projectPath, "roslyn_apply_code_fix");
				preSaved = true;
			}
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			var phaseWord = preSaved ? "post" : "pre";
			var snapNote  = preSaved ? " Any pre-change snapshots already saved are not needed." : string.Empty;
			
			return scope.Failed("backup failed", new ApplyCodeFixResult(
				$"Write aborted — could not save {phaseWord}-change backup: {ex.Message}. " +
				$"No files were modified.{snapNote} " +
				"The approval token is still valid — retry after resolving the issue.",
				null,
				"backup failed"));
		}
		
		approvals.Consume(token, forSession);
		
		if(!workspace.IsAdhoc(projectPath)) {
			
			if(!workspace.ApplyChanges(projectPath, operation.NewSolution))
				return scope.Failed("apply failed", new ApplyCodeFixResult(
					"Workspace refused to apply the previewed code fix after backups were saved. " +
					RecoveryHint(affectedDocs, rootPath),
					null,
					"apply failed"));
			
			foreach(var doc in changedDocs) {
				
				var path    = doc.FilePath!;
				var relPath = TryMakeRelative(path, rootPath) ?? path;
				var bytes   = FileWriter.Utf8NoBom.GetBytes((await doc.GetTextAsync()).ToString());
				
				if(await TryRecoverTruncation(relPath, path, projectPath, bytes) is { } truncErr)
					return scope.Failed("truncation detected", new ApplyCodeFixResult(truncErr.Error ?? "File truncation detected.", null, "truncation detected"));
			}
		}
		else {
			
			try {
				
				await SolutionDiff.ApplyToDiskAsync(operation.BaseSolution, operation.NewSolution,
					async (path, content) => {
						
						await workspace.WriteAndInvalidate(projectPath, path,
							() => FileWriter.WriteAllTextAsync(path, content));
						
						var relPath = TryMakeRelative(path, rootPath) ?? path;
						
						if(CheckForTruncation(relPath, path, content.Length) is { } truncErr)
							throw new IOException(truncErr.Error);
					});
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
				
				return scope.Failed("apply failed", new ApplyCodeFixResult(
					$"Code fix write failed after backups were saved: {ex.Message}. " +
					RecoveryHint(affectedDocs, rootPath),
					null,
					"apply failed"));
			}
		}
		
		var filesDeleted = 0
		;
		
		foreach(var doc in removedDocs) {
			
			try {
				
				if(File.Exists(doc.FilePath!))
					File.Delete(doc.FilePath!);
				
				workspace.InvalidateFile(projectPath, doc.FilePath!);
				filesDeleted++;
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				
				return scope.Failed("delete failed", new ApplyCodeFixResult(
					$"Code fix content changes were applied, but '{doc.FilePath}' could not be deleted: {ex.Message}. " +
					RecoveryHint(affectedDocs, rootPath),
					changedDocs.Length,
					"delete failed",
					filesDeleted));
			}
		}
		
		var sessionNote = forSession ? " Code fix approved for the remainder of this session." : string.Empty;
		var deletionNote = filesDeleted > 0 ? $" {filesDeleted} file(s) deleted." : string.Empty;
		
		return scope.Outcome($"{changedDocs.Length} file(s) written, {filesDeleted} deleted", new ApplyCodeFixResult(
			$"Code fix applied.{sessionNote} Files written to disk.{deletionNote}",
			changedDocs.Length,
			null,
			filesDeleted));
	}
	
	private static string? CheckForStalePreview(IReadOnlyDictionary<string, PreviewFileState>? fileStates)
	{
		if(fileStates is null || fileStates.Count == 0)
			return null;
		
		foreach(var (path, state) in fileStates) {
			
			if(state.ExpectedState == ExpectedFileState.Absent) {
				
				if(File.Exists(path))
					return $"Apply aborted — '{path}' was created after preview. Re-run roslyn_preview_code_fix before applying.";
				
				continue;
			}
			
			if(!File.Exists(path))
				return $"Apply aborted — '{path}' changed since preview: file no longer exists. Re-run roslyn_preview_code_fix.";
			
			var currentHash = ComputeFileHash(path);
			
			if(!string.Equals(currentHash, state.ContentHash, StringComparison.OrdinalIgnoreCase))
				return $"Apply aborted — '{path}' changed since preview. Re-run roslyn_preview_code_fix before applying.";
		}
		
		return null;
	}
	
	private static string RecoveryHint(Document[] changedDocs, string rootPath)
	{
		var files = changedDocs
			.Select(d => TryMakeRelative(d.FilePath, rootPath) ?? d.FilePath ?? d.Name)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray()
		;
		
		var firstFile = files.FirstOrDefault();
		var fileHint = firstFile is not null
			? $" Start with roslyn_local_history action 'list' for filePath '{firstFile}'."
			: string.Empty
		;
		
		return "Backups were saved before writing. Use roslyn_local_history to list snapshots for the affected file(s); " +
			"apply a 'pre' snapshot to roll back, or a 'post' snapshot to restore the intended code-fix content." +
			fileHint;
	}
	
	private static string ComputeFileHash(string path)
	{
		using var stream = File.OpenRead(path);
		var hash = SHA256.HashData(stream);
		
		return Convert.ToHexString(hash);
	}
}

internal sealed record ApplyCodeFixResult : ToolResult, IToolError
{
	public ApplyCodeFixResult(string message, int? filesWritten, string? error, int? filesDeleted = null)
	{
		Message      = message;
		FilesWritten = filesWritten;
		FilesDeleted = filesDeleted;
		Error        = error;
	}
	
	public string Message      { get; }
	public int?   FilesWritten { get; }
	public int?   FilesDeleted { get; }
}
