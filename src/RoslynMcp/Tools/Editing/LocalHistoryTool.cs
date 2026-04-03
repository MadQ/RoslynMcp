using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynMcp.Tools;


namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class LocalHistoryTool : RoslynMcpTool
{
	readonly BackupStore backups;

	public LocalHistoryTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache, BackupStore backups)
		: base(workspace, logger, paginationCache)
	{
		this.backups = backups;
	}

	[McpServerTool(Name = "roslyn_local_history", Destructive = false, Title = "Local History", OpenWorld = false)]
	[Description(
		"Browse and restore crash-safe file backups created automatically before destructive writes. " +
		"Modelled after VS Code's Local History feature. " +
		"Use action: \"list\" to enumerate restorable backups (optionally filtered by filePath). " +
		"Use action: \"preview\" with a token to check whether a restore would conflict " +
		"(i.e. the file has changed since the backup was taken). " +
		"Use action: \"apply\" with a token to restore a file from backup. " +
		"If the file was modified after the backup, apply returns a conflict response rather than silently overwriting — " +
		"pass force: true to overwrite unconditionally. " +
		"Backup tokens are returned by roslyn_write_file and survive process restarts. " +
		"Backups are branch-agnostic: a backup taken on one branch can be restored on any branch. " +
		"The list response includes a caution field when any backup was taken on a different branch than the current one — verify your branch before applying."
	)]
	public object LocalHistory(
		[Description("Action to perform: \"list\" (enumerate backups), \"preview\" (conflict check), or \"apply\" (restore from backup).")]
		string  action,
		[Description(ProjectPathDescription)]
		string  projectPath,
		[Description("Backup token returned by roslyn_write_file. Required for preview and apply; optional filter for list.")]
		string? token  = null,
		[Description("Relative path to filter list results to a specific file. Optional.")]
		string? filePath = null,
		[Description("For apply: overwrite the current file even if it was modified since the backup. Default: false.")]
		bool    force  = false
	)
	{
		using var scope = BeginTool("roslyn_local_history", token ?? filePath ?? action);

		return action.ToLowerInvariant() switch {
			"list"    => HandleList(scope, projectPath, token, filePath),
			"preview" => HandlePreview(scope, token),
			"apply"   => HandleApply(scope, projectPath, token, force),
			_         => scope.Failed("unknown action", new ErrorResult(
				$"Unknown action '{action}'. Valid values: list, preview, apply."))
		};
	}

	// ── Action handlers ────────────────────────────────────────────────────

	object HandleList(ToolScope scope, string projectPath, string? token, string? filePath)
	{
		string? absolutePath = null;

		if(filePath is not null) {

			var rootPath = workspace.GetRootPath(projectPath);
			absolutePath = ResolveFilePath(filePath, rootPath);
		}
		else if(token is not null) {

			// Token used as a file-scope filter: extract path from backup metadata.
			var entries = backups.List();
			absolutePath = entries.FirstOrDefault(e => e.Token == token)?.Meta.AbsolutePath;
		}

		var all           = backups.List(absolutePath);
		var rootDir       = workspace.GetRootPath(projectPath);
		var currentBranch = backups.GetCurrentBranch(rootDir);

		var items = all.Select(e => new LocalHistoryEntry(
			Token:           e.Token,
			AbsolutePath:    e.Meta.AbsolutePath,
			Operation:       e.Meta.Operation,
			Timestamp:       e.Meta.Timestamp,
			FileSizeBytes:   e.Meta.FileSizeBytes,
			ConflictRisk:    e.ConflictRisk,
			ChangedLineHint: e.Meta.ChangedLineHint,
			GitBranch:       e.Meta.GitBranch
		)).ToArray();

		string? caution = null;

		if(currentBranch is not null && all.Any(e => e.Meta.GitBranch is not null && e.Meta.GitBranch != currentBranch))
			caution = "One or more backups were taken on a different branch. Verify branch context before restoring.";

		return new LocalHistoryListResult(items, items.Length, caution);
	}

	object HandlePreview(ToolScope scope, string? token)
	{
		if(token is null)
			return scope.Failed("token required", new ErrorResult("token is required for action: preview."));

		var entries = backups.List();
		var entry   = entries.FirstOrDefault(e => e.Token == token);

		if(entry is null)
			return scope.Failed("not found", new ErrorResult($"No backup found for token '{token}'."));

		return new LocalHistoryPreviewResult(
			Token:          entry.Token,
			AbsolutePath:   entry.Meta.AbsolutePath,
			Operation:      entry.Meta.Operation,
			Timestamp:      entry.Meta.Timestamp,
			ConflictRisk:   entry.ConflictRisk,
			Message:        entry.ConflictRisk
				? "File has been modified since this backup was taken. Use force: true to overwrite, or resolve manually."
				: "No conflict detected — safe to apply."
		);
	}

	object HandleApply(ToolScope scope, string projectPath, string? token, bool force)
	{
		if(token is null)
			return scope.Failed("token required", new ErrorResult("token is required for action: apply."));

		var result = backups.TryRestore(token, force);

		if(result.Restored) {

			workspace.InvalidateFile(projectPath, result.AbsolutePath!);

			return new LocalHistoryApplyResult(
				Restored:     true,
				AbsolutePath: result.AbsolutePath!,
				Message:      $"Restored '{result.AbsolutePath}' from backup."
			);
		}

		if(result.IsConflict) {

			return new LocalHistoryConflictResult(
				Conflict:     true,
				AbsolutePath: result.AbsolutePath!,
				Message:      "File was modified after backup was taken. Pass force: true to overwrite, or use preview to inspect.",
				CurrentHash:  result.CurrentHash,
				BackupHash:   result.BackupHash
			);
		}

		if(result.IsDisabled)
			return scope.Failed("disabled", new ErrorResult(result.ErrorMessage!));

		return scope.Failed("restore failed", new ErrorResult(result.ErrorMessage ?? "Restore failed."));
	}
}

// ── Result types ────────────────────────────────────────────────────────────

internal sealed record LocalHistoryEntry(
	string  Token,
	string  AbsolutePath,
	string  Operation,
	string  Timestamp,
	long    FileSizeBytes,
	bool    ConflictRisk,
	int[]?  ChangedLineHint,
	string? GitBranch
);

internal sealed record LocalHistoryListResult(LocalHistoryEntry[] Items, int Count, string? Caution);

internal sealed record LocalHistoryPreviewResult(
	string Token,
	string AbsolutePath,
	string Operation,
	string Timestamp,
	bool   ConflictRisk,
	string Message
);

internal sealed record LocalHistoryApplyResult(bool Restored, string AbsolutePath, string Message);

internal sealed record LocalHistoryConflictResult(
	bool    Conflict,
	string  AbsolutePath,
	string  Message,
	string? CurrentHash,
	string? BackupHash
);
