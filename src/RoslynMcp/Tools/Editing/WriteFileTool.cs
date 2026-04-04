using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using RoslynMcp.Tools;


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
		using var scope   = BeginTool("roslyn_write_file", filePath);
		
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
		
		var isNewFile = !File.Exists(fullPath);
		
		// Detect encoding and line ending style from existing file.
		Encoding targetEncoding;
		string   normalizedContent;
		
		if(!isNewFile) {
			var existingBytes    = await ReadBytesAsync(fullPath);
			targetEncoding       = DetectEncoding(existingBytes, fullPath);
			var existingContent  = targetEncoding.GetString(existingBytes);
			var hasCrlf          = existingContent.Contains("\r\n");
			
			normalizedContent = NormalizeContentLineEndings(content, hasCrlf);
		}
		
		else {
			// New file: BOM for .cs (VS default), no-BOM UTF-8 for everything else.
			targetEncoding    = IsCSharpFile(fullPath) ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true) : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
			;
			normalizedContent = NormalizeContentLineEndings(content, hasCrlf: true); // CRLF for new files on Windows
		}
		
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
		byte[] writeBytes = [..targetEncoding.GetPreamble(), ..targetEncoding.GetBytes(normalizedContent)];
		
		// Take backup before writing (existing files only).
		string? backupToken = null;
		
		if(!isNewFile)
			backupToken = backups.Save(fullPath, projectPath, "roslyn_write_file", writeBytes);
		
		// Atomic write: temp file in the same directory → rename.
		var dir     = Path.GetDirectoryName(fullPath)!;
		var tmpFile = Path.Combine(dir, $".roslynmcp_write_{Guid.NewGuid():N}.tmp");
		
		try {
			Directory.CreateDirectory(dir);
			await File.WriteAllBytesAsync(tmpFile, writeBytes);
			File.Move(tmpFile, fullPath, overwrite: true);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			TryDeleteTemp(tmpFile);
			
			return scope.Error(new ErrorResult($"Failed to write file: {ex.Message}"));
		}
		
		// Invalidate Roslyn workspace so subsequent tools see the new source.
		workspace.InvalidateFile(projectPath, fullPath);
		
		return scope.Outcome($"{lineCount} line(s) written", new WriteFileResult(
			Written:     true,
			FilePath:    filePath,
			LineCount:   lineCount,
			Created:     isNewFile,
			BackupToken: backupToken
		));
	}
	
	// ── Helpers ────────────────────────────────────────────────────────────
	
	static async Task<byte[]> ReadBytesAsync(string path)
	{
		try {
			return await File.ReadAllBytesAsync(path);
		}
		catch {
			return [];
		}
	}
	
	// Manual BOM sniff — avoids StreamReader encoding ambiguity.
	static Encoding DetectEncoding(byte[] bytes, string path)
	{
		if(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
			return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
		
		if(bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
			return Encoding.Unicode; // UTF-16 LE
		
		if(bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
			return Encoding.BigEndianUnicode;
		
		// No BOM — default to UTF-8 no-BOM for most files; BOM for .cs to match VS default.
		
		return IsCSharpFile(path)
			? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
			: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
		;
	}
	
	static bool IsCSharpFile(string path)
		=> path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
	
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
			if(File.Exists(tmpFile))
				File.Delete(tmpFile);
		}
		catch { }
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
