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
		
		var rootPath       = workspace.GetRootPath(projectPath);
		var projectChanges = operation.NewSolution.GetChanges(operation.BaseSolution)
			.GetProjectChanges()
			.ToArray()
		;
		
		// Track which doc IDs are new files (added) vs modified in-place (changed).
		// Both sets must be written to disk; this set drives the pre-delete safety check.
		var addedDocIds = projectChanges
			.SelectMany(p => p.GetAddedDocuments())
			.ToHashSet()
		;
		
		// changedDocs: all files to write — both renamed-in-place changes and new files at the renamed path.
		// Deduped by physical path: multi-TFM produces the same file in multiple projects; writing once is enough.
		var changedDocs = projectChanges
			.SelectMany(p => p.GetChangedDocuments().Concat(p.GetAddedDocuments())
				.Select(id => operation.NewSolution.GetDocument(id))
				.OfType<Document>())
			.Where(d => d.FilePath is not null)
			.GroupBy(d => d.FilePath!, StringComparer.OrdinalIgnoreCase)
			.Select(g => g.First())
			.ToArray()
		;
		
		// removedDocs: old files to delete after confirming all new files are written.
		// Deduped by physical path; paths still referenced anywhere in the new solution
		// are excluded — linked/shared files can appear removed in one project but live
		// in another.
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
		
		// Save pre- and post-change snapshots for each file before writing.
		// SavePreAsync reads the current disk content (pre-rename); SavePostAsync saves the intended new content.
		// For added docs, SavePreAsync returns null (file doesn't exist yet) — that is correct.
		// Abort without touching any files if any snapshot fails.
		bool preSaved = false;
		
		try {
			
			foreach(var doc in changedDocs) {
				var bytes = FileWriter.Utf8NoBom.GetBytes((await doc.GetTextAsync()).ToString());
				preSaved = false;
				await backups.SavePreAsync(doc.FilePath!, projectPath, "roslyn_apply_rename");
				preSaved = true;
				await backups.SavePostAsync(doc.FilePath!, projectPath, "roslyn_apply_rename", bytes);
			}
			
			// Pre-snapshot old files to be deleted — enables roslyn_local_history restore.
			// No post-snapshot: the post-state is absence of the file.
			foreach(var doc in removedDocs) {
				preSaved = false;
				await backups.SavePreAsync(doc.FilePath!, projectPath, "roslyn_apply_rename");
			}
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			var phaseWord = preSaved ? "post" : "pre";
			var snapNote  = preSaved ? " Any pre-change snapshots already saved are not needed." : string.Empty;
			
			// The approval token was already consumed — the agent must run roslyn_preview_rename again.
			return scope.Failed("backup failed",
				$"Write aborted — could not save {phaseWord}-change backup: {ex.Message}. " +
				$"No files were modified.{snapNote} " +
				"The approval token has been consumed — run roslyn_preview_rename again to get a new token, then retry.");
		}
		
		// Token already consumed — wrap the write phase so any exception produces a
		// clear recovery message rather than a raw MCP error with no guidance.
		try {
			
			// Write changed files directly to disk for both MSBuildWorkspace and AdhocWorkspace.
			// Bypasses TryApplyChanges entirely — that path is unreliable because the stored solution
			// snapshot can be rejected as stale if a FSW-triggered reload fires between preview and apply.
			foreach(var doc in changedDocs) {
				var path    = doc.FilePath!;
				var relPath = TryMakeRelative(path, rootPath) ?? path;
				var content = (await doc.GetTextAsync()).ToString();
				
				await workspace.WriteAndInvalidate(projectPath, path,
					() => FileWriter.WriteAllTextAsync(path, content));
				
				if(CheckForTruncation(relPath, path, content.Length) is { } truncErr)
					return scope.Failed("truncation detected", truncErr.Error);
			}
			
			// Compute the physical paths of newly-created files (from added docs).
			// All of these must exist on disk before we delete the old files — this guards
			// against data loss when TryApplyChanges failed to create any new files.
			var addedFilePaths = changedDocs
				.Where(d => addedDocIds.Contains(d.Id))
				.Select(d => d.FilePath!)
				.ToArray()
			;
			
			var filesDeleted = 0;
			
			// Only delete old files when new files are confirmed on disk.
			if(removedDocs.Length > 0 && addedFilePaths.Length > 0 && addedFilePaths.All(File.Exists)) {
				
				foreach(var doc in removedDocs) {
					var path = doc.FilePath!;
					
					await workspace.WriteAndInvalidate(projectPath, path, () => {
						File.Delete(path);
						return Task.CompletedTask;
					});
					
					filesDeleted++;
				}
			}
			
			var filesChanged = changedDocs.Length;
			var sessionNote  = forSession ? " Symbol approved for the remainder of this session." : string.Empty;
			
			var summary = filesDeleted > 0
				? $"{filesChanged} file(s) written, {filesDeleted} old file(s) deleted"
				: $"{filesChanged} file(s) written"
			;
			
			return scope.Outcome(summary, $"Rename applied.{sessionNote} Files written to disk. Compilation will refresh automatically.");
		}
		catch(OperationCanceledException) {
			throw;
		}
		catch(Exception ex) {
			return scope.Failed("write failed",
				$"Rename failed during apply: {ex.GetType().Name}: {ex.Message} " +
				"Token has been consumed — run roslyn_preview_rename again to get a new token, then retry.");
		}
	}
}
