using Microsoft.CodeAnalysis.Text;
using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ReplaceInFileTool : RoslynMcpTool
{
	readonly BackupStore backups;
	
	public ReplaceInFileTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache, BackupStore backups) : base(workspace, logger, paginationCache) { this.backups = backups; }
	
	[McpServerTool(Name = "roslyn_replace_in_file", Destructive = true, Title = "Replace In File", OpenWorld = false)]
		[Description(
		"Use for text-level find/replace in any file type — JSON, XML, .csproj, Markdown, plain text, or source code. " +
		"For C# files, prefer roslyn_replace_in_code instead — it is semantically aware, validates syntax, and preserves formatting. " +
		"For inserting new lines without replacing existing content, use roslyn_insert_lines instead. " +
		"Supports literal string patterns (default) or regular expressions when useRegex=true; " +
		"regex replacements support $1/$2 backreferences. Replaces ALL occurrences of the pattern in the file. " +
		"Returns the number of replacements made and the 1-based line numbers that were changed. " +
		"Literal patterns are matched case-sensitively and whitespace-exactly — verify the exact text with " +
		"roslyn_read_file or roslyn_search_files before attempting a replacement if unsure of the content. " +
		"Line endings in the replacement text are normalized to match the file's existing style by default (normalizeLineEndings=true). " +
		"Supports dryRun=true to preview what would change without writing."
	)]
	public async Task<object> ReplaceInFile(
		[Description("Relative path to the file from the workspace root.")                                                ] string  filePath,
		[Description(ProjectPathDescription)] string projectPath,
		[Description("The pattern to find. Interpreted as a literal string by default; treated as a regular expression when useRegex=true.")                         ] string  pattern,
		[Description("The replacement text. Literal string by default. Supports $1/$2 backreferences when useRegex=true.")                           ] string  replacement,
		[Description("Treat pattern as a regular expression. Default: false.")                                            ] bool    useRegex  = false,
		[Description("Preview replacements without writing the file. Returns what would change. Default: false.")         ] bool    dryRun    = false,
		[Description("Case-sensitive matching. Default: true.")                                                           ] bool    caseSensitive = true,
		[Description(
			"Match line endings in the replacement text to the file's existing style (CRLF or LF). " +
			"Default: true — prevents mixed line endings in the file. " +
			"Set false only if your replacement text already has the correct line endings."
		)] bool normalizeLineEndings = true
	)
	{
		using var scope = BeginTool("roslyn_replace_in_file", filePath);
		
		var rootPath = workspace.GetRootPath(projectPath);
		var fullPath = ResolveFilePath(filePath, rootPath);
		
		if(fullPath is null)
			return scope.Failed("file not found", new ErrorResult($"File not found: {filePath}"));
		
		Regex regex;
		
		try {

			if(!useRegex)
				regex = BuildLiteralRegex(pattern, caseSensitive);
			
			else {
				var options = RegexOptions.Compiled;

				if(!caseSensitive)
					options |= RegexOptions.IgnoreCase;

				regex = new Regex(pattern, options);
			}
		}
		catch(ArgumentException ex) {
			return scope.Error(new ErrorResult($"Invalid regex pattern: {ex.Message}"));
		}
		
		string originalContent;
		
		try {
			originalContent = File.ReadAllText(fullPath);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			return scope.Error(new ErrorResult($"Failed to read file: {ex.Message}"));
		}
		
		// Split into lines to compute 1-based line numbers for each match position.
		var lines        = originalContent.Split('\n');
		var lineStarts   = BuildLineStartMap(lines);
		var matches      = regex.Matches(originalContent);
		int[] changedLines = [..
			matches
				.Select(m => GetLineNumber(lineStarts, m.Index))
				.Distinct()
				.Order()
		];
		
		if(matches.Count == 0)
			return scope.Failed("No matches found.", new ReplaceInFileResult(false, 0, [], "No matches found."));
		
		if(dryRun)
			return scope.Outcome("dry run", new ReplaceInFileResult(false, matches.Count, changedLines, Message: $"Dry run: {matches.Count} replacement(s) would be made."));
		
		
		var effectiveReplacement = normalizeLineEndings ? NormalizeLineEndings(replacement, originalContent) : replacement;
		var newContent = regex.Replace(originalContent, effectiveReplacement);
		
		var newBytes = FileWriter.Utf8NoBom.GetBytes(newContent);

		bool preSaved = false;

		try {
			preSaved = await backups.SavePreAsync(fullPath, projectPath, "roslyn_replace_in_file") is not null;
			await backups.SavePostAsync(fullPath, projectPath, "roslyn_replace_in_file", newBytes);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			var hint = preSaved
				? "Resolve the issue and retry. The pre-change snapshot that was saved is not needed since the file was not touched."
				: "Resolve the issue (disk space or permissions) and retry.";

			return scope.Error(new ErrorResult(
				$"Write aborted — could not save {(preSaved ? "post" : "pre")}-change backup: {ex.Message}. The file was not modified.",
				Hint: hint));
		}

		// For .cs files: single write via workspace API (MSBuild-tracked goes through
		// TryApplyChanges; untracked/Adhoc goes through FileWriter with FSW suppressed).
		// For all other types: direct FileWriter write, then InvalidateFile.
		if(fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) {

			try {
				await workspace.ApplyTextChange(projectPath, fullPath, SourceText.From(newContent, FileWriter.Utf8NoBom));
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				return scope.Error(new ErrorResult($"Write failed — '{filePath}' may be in an inconsistent state: {ex.Message}.", Hint: BackupRecoveryHint(filePath)));
			}
		}
		else {

			try {
				await FileWriter.WriteAllTextAsync(fullPath, newContent);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				return scope.Error(new ErrorResult($"Write failed — '{filePath}' may be in an inconsistent state: {ex.Message}.", Hint: BackupRecoveryHint(filePath)));
			}

			workspace.InvalidateFile(projectPath, fullPath);
		}
		
		if(CheckForTruncation(filePath, fullPath, newContent.Length) is { } truncErr)
			return scope.Error(truncErr);
		
		return scope.Outcome($"{matches.Count} replacement(s)", new ReplaceInFileResult(true, matches.Count, changedLines));
	}
	
	// Builds a sorted array of character offsets where each line starts.
	private static int[] BuildLineStartMap(string[] lines)
	{
		var starts  = new int[lines.Length];
		var current = 0;
		
		for(int i = 0; i < lines.Length; i++) {
			
			starts[i]  = current;
			// +1 for the '\n' we split on. Files with bare '\r' line endings (rare, pre-Mac OS X)
				// would be off by one per line; that platform is not a supported target.
			current   += lines[i].Length + 1; // +1 for the '\n' we split on
		}
		
		return starts;
	}
	
	// Returns the 1-based line number for a given character offset.
	private static int GetLineNumber(int[] lineStarts, int charOffset)
	{
		var lo = 0;
		var hi = lineStarts.Length - 1;
		
		while(lo < hi) {
			
			var mid = (lo + hi + 1) / 2;
			
			if(lineStarts[mid] <= charOffset)
				lo = mid;
			else
				hi = mid - 1;
		}
		
		return lo + 1;
	}
}
