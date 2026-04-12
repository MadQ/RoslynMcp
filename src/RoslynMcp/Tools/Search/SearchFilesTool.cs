using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class SearchFilesTool : RoslynMcpTool
{
	public SearchFilesTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_search_files", ReadOnly = true, Title = "Search Files", OpenWorld = false, Idempotent = true)]
	[Description(
		"Fast and precise code search — use instead of grep, Select-String, or findstr. Searches workspace files for lines matching a regex pattern. " +
		"Returns file path, line number, and matching line text for each hit, with paging support. " +
		"Use this when searching file content — finding all lines where a symbol name, pattern, or phrase appears. " +
		"For filename/path matching only (no content), use roslyn_list_files instead. " +
		"For C#-specific context filtering (find matches only in comments, strings, identifiers, or xmldocs), use roslyn_semantic_search instead. " +
		"Searches C# source files registered in the Roslyn workspace (from the loaded solution/project). " +
		"Non-C# files (.csproj, .json, .props, etc.) are never included — only source documents in the compilation. " +
		"The filePattern parameter filters by filename within that set (e.g., '*Test.cs' to restrict to test files). " +
		"For searching non-C# files by content, use roslyn_list_files to enumerate paths and roslyn_read_file to inspect them. " +
		"Results are paged; pass page_token from a previous response to retrieve the next page.")]
	public async Task<object> SearchFiles(
		[Description("Regex pattern to search for (e.g., 'class.*Tool', 'TODO.*performance').")] string pattern,
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken,
		[Description("Filename glob filter applied within Roslyn workspace documents (e.g., '*.cs', '*Test.cs'). Default: '*.cs'. Non-C# files are never searched regardless of this filter.")] string? filePattern = null,
		[Description("Case-sensitive matching. Default: false (case-insensitive).")] bool caseSensitive = false,
		[Description("Number of results to skip (for paging). Default: 0.")] int skip = 0,
		[Description("Maximum number of results to return. Default: 50, max: 200.")] int take = 50,
		[Description("Token from a previous response to get the next page without re-executing the query.")] string? page_token = null
	)
	{
		using var scope = BeginTool("roslyn_search_files", pattern, new { filePattern, caseSensitive, skip, take });
		
		filePattern ??= "*.cs";
		
		if(scope.TryServeCachedPage<object>(page_token, ref skip, ref take, 200, out var cached))
			
			return scope.Outcome("cached page", cached);
		
		Regex regex;
		
		try {
			
			var options = RegexOptions.Compiled;
			
			if(!caseSensitive)
				options |= RegexOptions.IgnoreCase;
			
			regex = new Regex(pattern, options);
		}
		catch(ArgumentException ex) {
			return scope.Error(new ErrorResult($"Invalid regex pattern: {ex.Message}"));
		}
		
		var solution   = workspace.GetSolution(projectPath);
		var rootPath   = workspace.GetRootPath(projectPath);
		var allMatches = new List<MatchResult>();
		// seenPaths prevents searching the same physical file twice in multi-targeted projects.
		var seenPaths  = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		;
		
		foreach(var project in solution.Projects)
			foreach(var document in project.Documents) {
				
				if(document.FilePath is null || !seenPaths.Add(document.FilePath))
					continue;
				
				var fileName = Path.GetFileName(document.FilePath);
				
				if(!GlobMatcher.Matches(fileName, filePattern))
					continue;
				
				var text  = await document.GetTextAsync(cancellationToken);
				var lines = text.Lines;
				
				for(int i = 0; i < lines.Count; i++) {
					
					var lineText = lines[i].ToString();
					
					if(regex.IsMatch(lineText)) {
						
						allMatches.Add(new MatchResult {
							
							File = Path.GetRelativePath(rootPath, document.FilePath),
							Line = i + 1,
							Text = lineText.Trim()
						});
					}
				}
			}
		
		var allResults = allMatches.ToArray();
		var result     = PaginateAndStore(allResults, ref skip, take);
		
		return scope.Outcome($"{result.Total} match(es)", new SearchFilesResult(
			result.Items,
			result.Total,
			result.Items.Length,
			result.PageToken,
			result.HasMore)
		{
			Caution = AdhocCaution(projectPath)
		});
	}
	
	private sealed class MatchResult
	{
		public required string File { get; init; }
		public required int Line { get; init; }
		public required string Text { get; init; }
	}
}
