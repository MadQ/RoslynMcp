using Microsoft.CodeAnalysis.Text;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class InsertLinesTool : RoslynMcpTool
{
	readonly BackupStore backups;
	static readonly string[] LineSeparators = ["\r\n", "\n"];
	
	public InsertLinesTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache, BackupStore backups)
		: base(workspace, logger, paginationCache) { this.backups = backups; }
	
	[McpServerTool(Name = "roslyn_insert_lines", Destructive = false, Title = "Insert Lines", OpenWorld = false)]
		[Description(
		"Use this to add new lines to a file at a specific location without constructing surrounding-context replacement patterns. " +
		"Specify the insertion point by 1-based line number (atLine), by a literal anchor pattern to insert after " +
		"(insertAfter), or by a literal anchor pattern to insert before (insertBefore) — exactly one specifier is required. " +
		"Lines are inserted exactly as provided; indentation is the caller's responsibility. " +
		"Anchor patterns use case-sensitive, whitespace-exact substring matching against line content — " +
		"tabs and spaces are NOT interchangeable. If an anchor fails, use roslyn_read_file to verify the exact content " +
		"(including indentation characters) before retrying. " +
		"Supports dryRun=true to preview the insertion without writing. " +
		"For replacing existing C# syntax nodes, prefer roslyn_replace_in_code; " +
		"for text-level find/replace in any file type, use roslyn_replace_in_file."
	)]
	public async Task<object> InsertLines(
		[Description("Relative path to the file from the workspace root.")                                                ] string  filePath,
		[Description(ProjectPathDescription)] string projectPath,
		[Description("The text to insert. May contain newlines for multi-line insertion.")                                 ] string  text,
		[Description("1-based line number to insert BEFORE. Mutually exclusive with insertAfter/insertBefore.")            ] int?    atLine       = null,
		[Description("Literal string pattern — inserts AFTER the first line that contains it. Returns an error if no match is found. Mutually exclusive with atLine/insertBefore.")                    ] string? insertAfter  = null,
		[Description("Literal string pattern — inserts BEFORE the first line that contains it. Returns an error if no match is found. Mutually exclusive with atLine/insertAfter.")                   ] string? insertBefore = null,
		[Description("Preview the insertion without writing. Returns what would change. Default: false.")                   ] bool    dryRun       = false
	)
	{
		using var scope = BeginTool("roslyn_insert_lines", filePath, new { atLine, insertAfter, insertBefore, dryRun });
		
		if(!TryResolveFileContext(projectPath, out var rootPath, out var boundary, out var resolveError))
			
			return scope.Error(resolveError);
		
		var fullPath = ResolveFilePath(filePath, rootPath, boundary);
		
		if(fullPath is null)
			
			return scope.Failed("file not found", new ErrorResult($"File not found: {filePath}"));
		
		// Exactly one location specifier required.
		var specCount = (atLine.HasValue ? 1 : 0) + (insertAfter is not null ? 1 : 0) + (insertBefore is not null ? 1 : 0)
		;
		
		if(specCount == 0)
			
			return scope.Failed("no location", new ErrorResult("Specify exactly one of: atLine, insertAfter, or insertBefore."));
		
		if(specCount > 1)
			
			return scope.Failed("multiple locations", new ErrorResult("Specify only one of: atLine, insertAfter, or insertBefore."));
		
		string   rawContent;
		string[] lines;
		
		try {
			rawContent = File.ReadAllText(fullPath);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			return scope.Error(new ErrorResult($"Failed to read file: {ex.Message}"));
		}
		
		// Detect existing line-ending style before splitting strips them.
		var eol = rawContent.Contains("\r\n") ? "\r\n" : "\n"
		;
		lines   = rawContent.Split(LineSeparators, StringSplitOptions.None);
		
		// Split produces a trailing empty element when the file ends with a newline — trim it
		// so the insertion index math stays consistent with File.ReadAllLines behavior.
		if(lines.Length > 0 && lines[^1].Length == 0)
			lines = lines[..^1];
		
		// Resolve insertion index (0-based, insert BEFORE this index).
		int insertIndex
		;
		
		if(atLine.HasValue)
			// atLine is 1-based, clamp to valid range [1, lines.Length + 1].
			insertIndex = Math.Clamp(atLine.Value, 1, lines.Length + 1) - 1
			;
		
		else if(insertAfter is not null) {
			
			var matchIndex = Array.FindIndex(lines, l => l.Contains(insertAfter, StringComparison.Ordinal));
			
			if(matchIndex < 0)
				
				return scope.Failed("anchor not found", new ErrorResult($"insertAfter pattern not found: {insertAfter}"));
			
			insertIndex = matchIndex + 1;
		}
		
		else {
			
			var matchIndex = Array.FindIndex(lines, l => l.Contains(insertBefore!, StringComparison.Ordinal));
			
			if(matchIndex < 0)
				
				return scope.Failed("anchor not found", new ErrorResult($"insertBefore pattern not found: {insertBefore}"));
			
			insertIndex = matchIndex;
		}
		
		// Split on both \r\n and \n to avoid trailing \r in lines.
		// WriteAllLines uses Environment.NewLine on output, matching platform convention.
		var newLines    = text.Split(LineSeparators, StringSplitOptions.None)
		;
		var resultLines = new List<string>(lines.Length + newLines.Length);
		
		resultLines.AddRange(lines[..insertIndex]);
		resultLines.AddRange(newLines);
		resultLines.AddRange(lines[insertIndex..]);
		
		// 1-based line numbers of inserted lines.
		var insertedLines = Enumerable.Range(insertIndex + 1, newLines.Length).ToArray()
		;
		
		if(dryRun)
			
			return scope.Outcome("dry run", new InsertLinesResult(false, insertIndex + 1, newLines.Length, insertedLines,
				$"Dry run: {newLines.Length} line(s) would be inserted at line {insertIndex + 1}."));
		
		// Preserve the file's original line-ending style — computed once, used for backup,
		// write, and post-write verification.
		var resultText  = string.Join(eol, resultLines) + eol
		;
		var resultBytes = FileWriter.Utf8NoBom.GetBytes(resultText)
		;
		
		bool    preSaved  = false;
		string? backupToken = null;
		
		try {
			
			backupToken = await backups.SavePreAsync(fullPath, projectPath, "roslyn_insert_lines");
			preSaved    = backupToken is not null;
			await backups.SavePostAsync(fullPath, projectPath, "roslyn_insert_lines", resultBytes);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			var hint = preSaved
				? "Resolve the issue and retry. The pre-change snapshot that was saved is not needed since the file was not touched."
				: "Resolve the issue (disk space or permissions) and retry.";
			
			return scope.Error(new ErrorResult(
				$"Write aborted — could not save {(preSaved ? "post" : "pre")}-change backup: {ex.Message}. The file was not modified.",
				hint));
		}
		
		// For .cs files: single write via workspace API with FSW suppression + self-healing recovery.
		// For all other types: direct FileWriter write, then InvalidateFile.
		if(fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) {
			
			try {
				await workspace.ApplyTextChange(projectPath, fullPath, SourceText.From(resultText, FileWriter.Utf8NoBom));
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				return scope.Error(new ErrorResult($"Write failed — '{filePath}' may be in an inconsistent state: {ex.Message}.", BackupRecoveryHint(filePath)));
			}
			
			if(await TryRecoverTruncation(filePath, fullPath, projectPath, resultBytes) is { } truncErr)
				
				return scope.Error(truncErr);
		}
		else {
			
			try {
				FileWriter.WriteAllText(fullPath, resultText);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or NotSupportedException) {
				return scope.Error(new ErrorResult($"Write failed — '{filePath}' may be in an inconsistent state: {ex.Message}.", BackupRecoveryHint(filePath)));
			}
			
			workspace.InvalidateFile(projectPath, fullPath);
			
			if(CheckForTruncation(filePath, fullPath, resultText.Length) is { } truncErr)
				
				return scope.Error(truncErr);
		}
		
		return scope.Outcome("inserted", new InsertLinesResult(true, insertIndex + 1, newLines.Length, insertedLines, BackupToken: backupToken));
	}
}
