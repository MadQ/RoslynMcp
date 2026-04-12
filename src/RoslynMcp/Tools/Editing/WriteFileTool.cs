using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class WriteFileTool : RoslynMcpTool
{
	readonly BackupStore backups;
	
	public WriteFileTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache, BackupStore backups)
		: base(workspace, logger, paginationCache)
	{
		this.backups = backups;
	}
	
	[McpServerTool(Name = "roslyn_write_file", Destructive = true, Title = "Write File", OpenWorld = false)]
	[Description(
		"Writes full file content — the nuclear option when surgical edits are impractical. " +
		"Use roslyn_replace_in_file, roslyn_replace_in_code, or roslyn_insert_lines for targeted edits instead. " +
		"Agents should reach for this when creating a new file or when the content is so restructured that anchored edits would be fragile. " +
		"By default (createNew: false) the file must already exist — prevents accidental path creation. " +
		"Set createNew: true to create a new file or overwrite an existing one. " +
		"For existing files, a crash-safe backup is taken automatically before writing; the returned backupToken " +
		"can be passed to roslyn_local_history (action: apply) to restore (backups are branch-agnostic — verify your current branch before restoring). " +
		"SDK-style .NET projects auto-include new .cs files via implicit glob — no .csproj edit required. " +
		"Write is atomic: content is written to a temp file then renamed, preventing partial writes on crash. " +
		"Supports dryRun: true to preview line count without touching disk."
	)]
	public async Task<object> WriteFile(
		[Description("Relative path to the file from the workspace root.")]                                                                          string  filePath,
		[Description(ProjectPathDescription)]                                                                                                        string  projectPath,
		[Description("Full file content to write.")]                                                                                                 string  content,
		[Description("false (default): file must already exist. true: create new file or overwrite existing.")]                                      bool    createNew = false,
		[Description("Preview without writing — returns line count and encoding info. Default: false.")]                                             bool    dryRun    = false
	)
	{
		using var scope   = BeginTool("roslyn_write_file", filePath, new { createNew, dryRun });
		
		var       rootPath = workspace.GetRootPath(projectPath);
		
		string fullPath;
		
		if(createNew) {
			
			if(!TryResolveTargetPath(filePath, rootPath, out var target, out var pathError))
				return scope.Failed("invalid path", new ErrorResult(pathError));
			
			fullPath = target;
		}
		else {
			
			var existing = ResolveFilePath(filePath, rootPath);
			
			if(existing is null)
				return scope.Failed("file not found", new ErrorResult($"File not found: {filePath}. Set createNew: true to create a new file."));
			
			fullPath = existing;
		}
		
		// Attempt to read the existing file to detect line-ending style.
		// Catching FileNotFoundException is more correct than File.Exists — avoids the
		// TOCTOU race and correctly treats access-denied as an error rather than "new file".
		string? existingContent = null;
		
		try {
			existingContent = await File.ReadAllTextAsync(fullPath);
		}
		catch(FileNotFoundException) { }
		
		var isNewFile = existingContent is null;
		
		// Detect line ending style from existing file; always write UTF-8 without BOM.
		var targetEncoding = FileWriter.Utf8NoBom;
		var hasCrlf        = existingContent?.Contains("\r\n") ?? true; // CRLF default for new files on Windows
		var normalizedContent = NormalizeContentLineEndings(content, hasCrlf);
		
		var lineCount = CountLines(normalizedContent);
		
		if(dryRun)
			return scope.Outcome("dry run", new WriteFileResult(
				Written:      false,
				FilePath:     filePath,
				LineCount:    lineCount,
				Created:      isNewFile,
				BackupToken:  null,
				Message:      $"Dry run: {lineCount} line(s) would be written to '{filePath}'."
			));
		
		// Compute the exact bytes that will land on disk so we can pass them to BackupStore.
		// This lets Save() do correct dedup (skip if identical) and store PostWriteHash upfront.
		byte[] writeBytes = targetEncoding.GetBytes(normalizedContent);
		
		// Save pre-change snapshot (existing files only) and post-change snapshot (always).
		// Abort without touching the file if either snapshot fails to save.
		var (preToken, backupErr) = await SaveBackupsAsync(
			backups, fullPath, projectPath, "roslyn_write_file", writeBytes,
			skipPre: isNewFile, fileState: isNewFile ? "created" : "modified");
		
		if(backupErr is not null)
			return scope.Error(backupErr);

		// Atomic write: temp file in the same directory → rename.
		var dir     = Path.GetDirectoryName(fullPath)!;
		var tmpFile = Path.Combine(dir, $".roslynmcp_write_{Guid.NewGuid():N}.tmp");
		
		try {
			Directory.CreateDirectory(dir);
			
			// For .cs files: FSW suppression ensures the rename event is ignored, and
			// InvalidateFile is called by WriteAndInvalidate to sync workspace state.
			// For all other types: direct atomic write, then InvalidateFile.
			if(fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) {
				await workspace.WriteAndInvalidate(projectPath, fullPath, async () => {
					await FileWriter.WriteAllBytesAsync(tmpFile, writeBytes);
					FileWriter.Move(tmpFile, fullPath, overwrite: true);
				});
			}
			else {
				await FileWriter.WriteAllBytesAsync(tmpFile, writeBytes);
				FileWriter.Move(tmpFile, fullPath, overwrite: true);
				workspace.InvalidateFile(projectPath, fullPath);
			}
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			TryDeleteTemp(tmpFile);

			return scope.Error(new ErrorResult(
				$"Write failed — '{filePath}' may be in an inconsistent state: {ex.Message}.",
				Hint: BackupRecoveryHint(filePath)));
		}

		// Verify the rename produced a non-empty file — filesystem/AV interference can silently empty it.
		if(writeBytes.Length > 4 && new FileInfo(fullPath).Length <= 4)
			return scope.Error(new ErrorResult(
				$"Write appeared to succeed but '{filePath}' is empty on disk — filesystem or antivirus interference is suspected.",
				Hint: BackupRecoveryHint(filePath)));

		return scope.Outcome($"{lineCount} line(s) written", new WriteFileResult(
			Written:     true,
			FilePath:    filePath,
			LineCount:   lineCount,
			Created:     isNewFile,
			BackupToken: preToken
		));
	}
	
	static string NormalizeContentLineEndings(string content, bool hasCrlf)
	{
		// Normalize to LF first, then to CRLF if the target file uses CRLF.
		var lf = content.Replace("\r\n", "\n");
		
		return hasCrlf ? lf.Replace("\n", "\r\n") : lf;
	}
	
	static int CountLines(string content)
	{
		if(content.Length == 0)
			return 0;
		
		var count = 1;
		
		foreach(var ch in content.AsSpan())
			if(ch == '\n')
				count++;
		
		// Don't count a trailing newline as an extra line.
		if(content[^1] == '\n')
			count--;
		
		return count;
	}
	
	static void TryDeleteTemp(string tmpFile)
	{
		try {
			File.Delete(tmpFile);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
	}
}

internal sealed record WriteFileResult(
	bool    Written,
	string  FilePath,
	int     LineCount,
	bool    Created,
	string? BackupToken,
	string? Message = null
);
