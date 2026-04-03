using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ReplaceInFileTool : RoslynMcpTool
{
	public ReplaceInFileTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_replace_in_file", Destructive = true, Title = "Replace In File", OpenWorld = false)]
	[Description(
		"Replaces occurrences of a pattern in a file. Supports literal string or regex replacement. " +
		"Returns the number of replacements made and the 1-based line numbers that were changed. " +
		"Use dryRun=true to preview what would change without writing the file. " +
		"This is a text-level tool — it works on any file type but has no semantic understanding of code structure."
	)]
	public async Task<object> ReplaceInFile(
		[Description("Relative path to the file from the workspace root.")                                                ] string  filePath,
		[Description(ProjectPathDescription)] string projectPath,
		[Description("The pattern to find. Literal string by default; regex when useRegex=true.")                         ] string  pattern,
		[Description("The replacement text. Supports $1/$2 backreferences when useRegex=true.")                           ] string  replacement,
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

			if(useRegex) {

				var options = RegexOptions.Compiled;

				if(!caseSensitive)
					options |= RegexOptions.IgnoreCase;

				regex = new Regex(pattern, options);
			}
			else {
				regex = BuildLiteralRegex(pattern, caseSensitive);
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
		
		if(matches.Count == 0) {

			return scope.Error(new ReplaceInFileResult(false, 0, [], "No matches found."));
		}
		
		if(dryRun) {

			return new ReplaceInFileResult(false, matches.Count, changedLines,
				$"Dry run: {matches.Count} replacement(s) would be made.");
		}
		
		var effectiveReplacement = normalizeLineEndings ? NormalizeLineEndings(replacement, originalContent) : replacement;
		var newContent = regex.Replace(originalContent, effectiveReplacement);
		
		try {
			await File.WriteAllTextAsync(fullPath, newContent);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {

			return scope.Error(new ErrorResult($"Failed to write file: {ex.Message}"));
		}

		// Invalidate the workspace so subsequent Roslyn tools see the updated source.
		workspace.InvalidateFile(projectPath, fullPath);
		
		return new ReplaceInFileResult(true, matches.Count, changedLines);
	}
	
	// Builds a sorted array of character offsets where each line starts.
	private static int[] BuildLineStartMap(string[] lines)
	{
		var starts  = new int[lines.Length];
		var current = 0;
		
		for(int i = 0; i < lines.Length; i++) {
		
			starts[i]  = current;
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
