using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class SearchFilesTool : RoslynMcpTool
{
	public SearchFilesTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_search_files", ReadOnly = true)]
	[Description(
		"Searches workspace files for lines matching a regex pattern. " +
		"Returns file paths, line numbers, and matching text with paging support. " +
		"Use this to discover code locations before applying Roslyn tools.")]
	public async Task<object> SearchFiles(
		[Description("Regex pattern to search for (e.g., 'class.*Tool', 'TODO.*performance').")] string pattern,
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken,
		[Description("File glob pattern (e.g., '*.cs', '*.csproj'). Default: '*.cs'.")] string? filePattern = null,
		[Description("Case-sensitive matching. Default: false.")] bool caseSensitive = false,
		[Description("Number of results to skip (for paging). Default: 0.")] int skip = 0,
		[Description("Maximum number of results to return. Default: 50, max: 200.")] int take = 50,
		[Description("Token from a previous response to get the next page without re-executing the query.")] string? page_token = null
	)
	{
		using var scope = BeginTool("roslyn_search_files", pattern);
		filePattern ??= "*.cs";

		var cachedPage = TryServeCachedPage<object>(scope, page_token, ref skip, ref take, 200);
		if(cachedPage is not null)
			return cachedPage;
		
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
		var seenPaths  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach(var project in solution.Projects)
			foreach(var document in project.Documents) {

				if(document.FilePath is null || !seenPaths.Add(document.FilePath))
					continue;
				
				var fileName = Path.GetFileName(document.FilePath);
				
				if(!MatchesGlob(fileName, filePattern))
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
			result.HasMore,
			AdhocCaution(projectPath)
		));
	}
	
	// TODO: Future enhancement — add syntax-tree-based semantic filtering.
	// Allow searching only within specific syntax contexts:
	// - Comments only
	// - String literals only
	// - Identifiers only (class/method/variable names)
	// - Exclude generated code
	// This would use SyntaxTree.GetRoot() and filter by SyntaxKind before applying regex.
	
	private static bool MatchesGlob(string fileName, string pattern)
	{
		if(pattern is "*" or "*.*")
			return true;

		// Fast-path for the common *.ext form.
		if(pattern.StartsWith("*.") && !pattern.AsSpan(2).Contains('*') && !pattern.AsSpan(2).Contains('?'))
			return fileName.EndsWith(pattern.AsSpan(1), StringComparison.OrdinalIgnoreCase);

		// General glob: convert wildcards to regex and match.
		var regexPat = "^" + string.Concat(pattern.Select(c => c switch {
			'*' => ".*",
			'?' => ".",
			'.' => "\\.",
			_   => Regex.Escape(c.ToString())
		})) + "$";

		return Regex.IsMatch(fileName, regexPat, RegexOptions.IgnoreCase);
	}
	
	private sealed class MatchResult
	{
		public required string File { get; init; }
		public required int Line { get; init; }
		public required string Text { get; init; }
	}
}
