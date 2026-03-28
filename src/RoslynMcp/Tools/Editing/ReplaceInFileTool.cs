using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

/// <summary>
///     Text-level find-and-replace tool. Works on any file type (code, config, markdown, etc.).
///     For semantic C# code manipulation (modify syntax nodes, preserve formatting, validate edits),
///     consider a future `replace_in_code` tool that uses Roslyn's syntax tree rewriting.
/// </summary>
[McpServerToolType]
internal sealed class ReplaceInFileTool : RoslynMcpTool
{
	public ReplaceInFileTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_replace_in_file", Destructive = true)]
	[Description(
		"Replaces occurrences of a pattern in a file. Supports literal string or regex replacement. " +
		"Returns the number of replacements made and the 1-based line numbers that were changed. " +
		"Use dryRun=true to preview what would change without writing the file. " +
		"This is a text-level tool — it works on any file type but has no semantic understanding of code structure."
	)]
	public object ReplaceInFile(
		[Description("Relative path to the file from the workspace root.")                                                ] string  filePath,
		[Description(ProjectPathDescription)] string projectPath,
		[Description("The pattern to find. Literal string by default; regex when useRegex=true.")                         ] string  pattern,
		[Description("The replacement text. Supports $1/$2 backreferences when useRegex=true.")                           ] string  replacement,
		[Description("Treat pattern as a regular expression. Default: false.")                                            ] bool    useRegex  = false,
		[Description("Preview replacements without writing the file. Returns what would change. Default: false.")         ] bool    dryRun    = false,
		[Description("Case-sensitive matching. Default: true.")                                                           ] bool    caseSensitive = true
	)
	{
		using var scope = BeginTool("roslyn_replace_in_file", filePath);
		var rootPath = workspace.GetRootPath(projectPath);
		var fullPath = ResolveFilePath(filePath, rootPath);

		if(fullPath is null)
			return scope.Failed("file not found", new { error = $"File not found: {filePath}" });
		
		Regex regex;
		
		try {
		
			var options = RegexOptions.Compiled;
			
			if(!caseSensitive)
				options |= RegexOptions.IgnoreCase;
			
			// Escape literal patterns so special characters match as-is.
			var regexPattern = useRegex ? pattern : Regex.Escape(pattern);
			regex            = new Regex(regexPattern, options);
		}
		catch(ArgumentException ex) {
		
			return new {
				error   = "Invalid regex pattern",
				details = ex.Message
			};
		}
		
		string originalContent;
		
		try {
			originalContent = File.ReadAllText(fullPath);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {

			return new {
				error   = "Failed to read file",
				details = ex.Message
			};
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
		
			return new {
				applied      = false,
				matchCount   = 0,
				changedLines = (int[]) [],
				message      = "No matches found."
			};
		}
		
		if(dryRun) {
		
			return new {
				applied      = false,
				matchCount   = matches.Count,
				changedLines,
				message      = $"Dry run: {matches.Count} replacement(s) would be made."
			};
		}
		
		var newContent = regex.Replace(originalContent, replacement);
		
		try {
			File.WriteAllText(fullPath, newContent);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {

			return new {
				error   = "Failed to write file",
				details = ex.Message
			};
		}
		
		// Invalidate the workspace so subsequent Roslyn tools see the updated source.
		workspace.InvalidateFile(projectPath, fullPath);
		
		return new {
			applied      = true,
			matchCount   = matches.Count,
			changedLines,
		};
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
