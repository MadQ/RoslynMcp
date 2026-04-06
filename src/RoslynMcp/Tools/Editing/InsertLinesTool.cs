using Microsoft.CodeAnalysis.Text;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class InsertLinesTool : RoslynMcpTool
{
	public InsertLinesTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, logger, paginationCache) { }
	
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
		using var scope = BeginTool("roslyn_insert_lines", filePath);
		
		var rootPath = workspace.GetRootPath(projectPath);
		var fullPath = ResolveFilePath(filePath, rootPath);
		
		if(fullPath is null)
			return scope.Failed("file not found", new ErrorResult($"File not found: {filePath}"));
		
		// Exactly one location specifier required.
		var specCount = (atLine.HasValue ? 1 : 0) + (insertAfter is not null ? 1 : 0) + (insertBefore is not null ? 1 : 0);
		
		if(specCount == 0)
			return scope.Failed("no location", new ErrorResult("Specify exactly one of: atLine, insertAfter, or insertBefore."));
		
		if(specCount > 1)
			return scope.Failed("multiple locations", new ErrorResult("Specify only one of: atLine, insertAfter, or insertBefore."));
		
		string[] lines;
		
		try {
			lines = File.ReadAllLines(fullPath);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			return scope.Error(new ErrorResult($"Failed to read file: {ex.Message}"));
		}
		
		// Resolve insertion index (0-based, insert BEFORE this index).
		int insertIndex;
		
		if(atLine.HasValue)
			// atLine is 1-based, clamp to valid range [1, lines.Length + 1].
			insertIndex = Math.Clamp(atLine.Value, 1, lines.Length + 1) - 1;
		
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
		var newLines    = text.Split(["\r\n", "\n"], StringSplitOptions.None)
		;
		var resultLines = new List<string>(lines.Length + newLines.Length);
		
		resultLines.AddRange(lines[..insertIndex]);
		resultLines.AddRange(newLines);
		resultLines.AddRange(lines[insertIndex..]);
		
		// 1-based line numbers of inserted lines.
		var insertedLines = Enumerable.Range(insertIndex + 1, newLines.Length).ToArray();
		
		if(dryRun)
			return scope.Outcome("dry run", new InsertLinesResult(false, insertIndex + 1, newLines.Length, insertedLines,
				$"Dry run: {newLines.Length} line(s) would be inserted at line {insertIndex + 1}."));
		
		// For .cs files: single write via workspace API with FSW suppression.
		// For all other types: direct FileWriter write, then InvalidateFile.
		if(fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) {
			
			// WriteAllLines uses Environment.NewLine + trailing newline; match that here.
			var resultText = string.Join(Environment.NewLine, resultLines) + Environment.NewLine;
			
			try {
				await workspace.ApplyTextChange(projectPath, fullPath, SourceText.From(resultText, FileWriter.Utf8NoBom));
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				return scope.Error(new ErrorResult($"Failed to write file: {ex.Message}"));
			}
		}
		else {
			
			try {
				FileWriter.WriteAllLines(fullPath, resultLines);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or NotSupportedException) {
				return scope.Error(new ErrorResult($"Failed to write file: {ex.Message}"));
			}
			
			workspace.InvalidateFile(projectPath, fullPath);
		}
		
		if(resultLines.Count > 0 && new FileInfo(fullPath).Length <= 4)
			return scope.Error(new ErrorResult(
				$"Write appeared to succeed but '{filePath}' is empty on disk — filesystem or antivirus interference is suspected. " +
				"Ask the user if they want to restore a previous version: call roslyn_local_history with action: 'list' to check for any prior backup of this file. " +
				"If no backup exists, ask the user whether to restore from git instead (git checkout -- <file-path>)."
			));
		
		return scope.Outcome("inserted", new InsertLinesResult(true, insertIndex + 1, newLines.Length, insertedLines));
	}
}
