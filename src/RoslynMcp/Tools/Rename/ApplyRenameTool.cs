using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ApplyRenameTool : RoslynMcpTool
{
	readonly ApprovalStore approvals;
	readonly BackupStore    backups;
	
	public ApplyRenameTool(WorkspaceResolver workspace, ApprovalStore approvals, BackupStore backups, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache)
	{
		this.approvals = approvals;
		this.backups   = backups;
	}
	
	[McpServerTool(Name = "roslyn_apply_rename", Destructive = true, Title = "Apply Rename", OpenWorld = false)]
	[Description(
		"Commits or cancels a rename previewed by roslyn_preview_rename — always call that tool first to obtain a token. " +
		"This is step 2 of a two-step rename workflow; calling this without a valid token will fail. " +
		"Pass approval 'y' to apply once, 'session' to apply and auto-approve the same symbol for all future renames this session, " +
		"or 'n' to cancel without writing any files. " +
		"Tokens are single-use — once consumed or rejected, run roslyn_preview_rename again if another rename is needed. " +
		"On success, writes all changed files to disk and reports the number of files modified. " +
		"When the renamed symbol is a type whose file name matches the type name, the old file is also deleted and a new file is created at the new path.")]
	public async Task<ApplyRenameResult> ApplyRename(
		[Description("The confirmation token returned by roslyn_preview_rename. Tokens are single-use — they expire after being applied or rejected.")] string token,
		[Description("'y' to apply this rename once; 'session' to apply and auto-approve the same symbol for all future renames in this session; 'n' to cancel without writing any files.")] string approval,
		[Description(ProjectPathDescription)] string projectPath
	)
	{
		using var scope = BeginTool("roslyn_apply_rename", $"{token} ({approval})");
		
		if(approval.Equals("n", StringComparison.OrdinalIgnoreCase)) {
			
			approvals.Reject(token, ApprovalWorkflow.Rename);
			
			return scope.Outcome("cancelled", new ApplyRenameResult("Rename cancelled. No files were changed.", null, null, null, null));
		}
		
		if(!approval.Equals("y", StringComparison.OrdinalIgnoreCase) && !approval.Equals("session", StringComparison.OrdinalIgnoreCase))
			
			return scope.Failed("invalid approval", new ApplyRenameResult("Invalid approval value. Use 'y', 'session', or 'n'.", null, null, "invalid approval", null));
		
		var forSession = approval.Equals("session", StringComparison.OrdinalIgnoreCase);
		var operation  = approvals.Peek(token, ApprovalWorkflow.Rename);
		
		if(operation is null)
			
			return scope.Failed("token not found", new ApplyRenameResult($"Token '{token}' not found or already consumed. Run preview_rename again.", null, null, "token not found", null));
		
		var rootPath       = workspace.GetRootPath(projectPath);
		var projectChanges = operation.NewSolution.GetChanges(operation.BaseSolution)
			.GetProjectChanges()
			.ToArray()
		;
		
		// changedDocs: all files to write — both in-place changes and added docs at a new path.
		// Deduped by physical path: multi-TFM produces the same file in multiple projects; write once.
		var changedDocs = projectChanges
			.SelectMany(p => p.GetChangedDocuments().Concat(p.GetAddedDocuments())
				.Select(id => operation.NewSolution.GetDocument(id))
				.OfType<Document>())
			.Where(d => d.FilePath is not null)
			.GroupBy(d => d.FilePath!, StringComparer.OrdinalIgnoreCase)
			.Select(g => g.First())
			.ToArray()
		;
		
		// removedDocs: old files to delete after all new files are written.
		// Deduped; paths still referenced anywhere in the new solution are excluded —
		// linked/shared files can appear removed in one project but live in another.
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
		
		// Save pre- and post-change snapshots before consuming the token so a backup failure
		// leaves the token intact — the user can retry without running preview again.
		bool preSaved = false
		;
		
		try {
			
			foreach(var doc in changedDocs) {
				
				var bytes = FileWriter.Utf8NoBom.GetBytes((await doc.GetTextAsync()).ToString());
				preSaved = false;
				await backups.SavePreAsync(doc.FilePath!, projectPath, "roslyn_apply_rename");
				preSaved = true;
				await backups.SavePostAsync(doc.FilePath!, projectPath, "roslyn_apply_rename", bytes);
			}
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			var phaseWord = preSaved ? "post" : "pre";
			var snapNote  = preSaved ? " Any pre-change snapshots already saved are not needed." : string.Empty;
			
			return scope.Failed("backup failed", new ApplyRenameResult(
				$"Write aborted — could not save {phaseWord}-change backup: {ex.Message}. " +
				$"No files were modified.{snapNote} " +
				"The approval token is still valid — retry after resolving the issue.",
				null, null, "backup failed", null));
		}
		
		// Backups saved — now consume the token (point of no return).
		approvals.Consume(token, ApprovalWorkflow.Rename, forSession);
		
		// MSBuildWorkspace.TryApplyChanges writes to disk; AdhocWorkspace does not.
		if(!workspace.IsAdhoc(projectPath)) {
			
			workspace.ApplyChanges(projectPath, operation.NewSolution);
			
			// Self-healing recovery: if TryApplyChanges truncated a file, re-write from memory.
			foreach(var doc in changedDocs) {
				
				var path    = doc.FilePath!;
				var relPath = TryMakeRelative(path, rootPath) ?? path;
				var bytes   = FileWriter.Utf8NoBom.GetBytes((await doc.GetTextAsync()).ToString());
				
				if(await TryRecoverTruncation(relPath, path, projectPath, bytes) is { } truncErr)
					return scope.Failed("truncation detected", new ApplyRenameResult(truncErr.Error  ?? "File truncation detected.", null, null, "truncation detected", null));
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
		
		// Delete removed files. Roslyn's rename API never produces removed documents
		// in practice, but handled for correctness in edge cases.
		var filesDeleted = 0
		;
		
		foreach(var doc in removedDocs) {
			
			try {
				
				File.Delete(doc.FilePath!);
				workspace.InvalidateFile(projectPath, doc.FilePath!);
				filesDeleted++;
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
		}
		
		// File rename: if preview identified a top-level type whose file stem matched
		// the symbol name, rename the file now that the new content is on disk.
		var filesRenamed = 0
		;
		
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
						changedDocs.Length, filesDeleted > 0 ? filesDeleted : null, "file rename conflict", null));
			
			try {
				
				if(caseOnly) {
					
					// Two-step via temp: old → temp → new, FSW suppressed at each destination.
					var temp = oldFilePath + ".roslynmcp_rename_tmp"
					;
					await workspace.WriteAndInvalidate(projectPath, temp, oldFilePath,
						() => { FileWriter.Move(oldFilePath, temp, false); return Task.CompletedTask; });
					await workspace.WriteAndInvalidate(projectPath, newFilePath, temp,
						() => { FileWriter.Move(temp, newFilePath, false); return Task.CompletedTask; });
				}
				else {
					
					await workspace.WriteAndInvalidate(projectPath, newFilePath, oldFilePath,
						() => { FileWriter.Move(oldFilePath, newFilePath, false); return Task.CompletedTask; });
				}
				
				filesRenamed++;
			}
			catch(Exception ex) {
				
				return scope.Failed("file rename failed",
					new ApplyRenameResult(
						$"Symbol renamed, but file rename failed: {ex.Message}. Rename the file manually.",
						changedDocs.Length, filesDeleted > 0 ? filesDeleted : null, "file rename failed", null));
			}
		}
		
		var filesWritten = changedDocs.Length;
		var sessionNote  = forSession ? " Symbol approved for the remainder of this session." : string.Empty;
		
		return scope.Outcome($"{filesWritten} file(s) written", new ApplyRenameResult(
			$"Rename applied.{sessionNote} Files written to disk.",
			filesWritten, filesDeleted > 0 ? filesDeleted : null, null, filesRenamed > 0 ? filesRenamed : null));
	}
}

internal sealed record ApplyRenameResult(
	string  Message,
	int?    FilesWritten,
	int?    FilesDeleted,
	string? Error,
	int?    FilesRenamed
);
