#if FALSE
using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class SearchFilesTool(WorkspaceManager workspace)
{
    [
		McpServerTool, Description(
			"Searches files in the workspace for lines matching a regex pattern. " +
			"Returns file paths, line numbers, and matching text. " +
			"Use this to discover code locations before applying Roslyn tools for detailed analysis."
		)
	]
    public object SearchFiles(
        [Description("Regex pattern to search for (e.g., 'class.*Tool', 'TODO.*performance').")	] string	pattern,
        [Description("File glob pattern (e.g., '*.cs', '*.csproj'). Default: '*.cs'.")			] string?	filePattern	  = null,
        [Description("Case-sensitive matching. Default: false.")								] bool		caseSensitive = false,
        [Description("Number of results to skip (for paging). Default: 0.")						] int		skip		  = 0,
        [Description("Maximum number of results to return. Default: 50, max: 200.")				] int		take		  = 50
	)
    {
        filePattern ??= "*.cs";
        take		  = Math.Clamp(take, 1, 200);
        skip		  = Math.Max(0, skip);

        Regex regex;

        try {

			var options = RegexOptions.Compiled;

            if(!caseSensitive)
                options |= RegexOptions.IgnoreCase;

            regex = new Regex(pattern, options);
		}
		catch(ArgumentException ex) {

            return new {
                error = "Invalid regex pattern",
                details = ex.Message
            };
        }

        var solution   = workspace.GetSolution();
        var allMatches = new List<MatchResult>();

        foreach(var project in solution.Projects)
            foreach(var document in project.Documents) {

				if(document.FilePath is null)
                    continue;

                var fileName = Path.GetFileName(document.FilePath);

                if(!MatchesGlob(fileName, filePattern))
                    continue;

                var text = document.GetTextAsync().GetAwaiter().GetResult();
                var lines = text.Lines;

                for(int i = 0; i < lines.Count; i++) {

					var lineText = lines[i].ToString();

                    if(regex.IsMatch(lineText)) {

						allMatches.Add(new MatchResult {
                            File = Path.GetRelativePath(workspace.RootPath, document.FilePath),
                            Line = i + 1,
                            Text = lineText.Trim()
                        });
                    }
                }
			}


		var totalMatches = allMatches.Count;
        var pagedMatches = allMatches.Skip(skip).Take(take).ToArray();

        return new {
            matches		  = pagedMatches,
            total_matches = totalMatches,
            returned	  = pagedMatches.Length,
            has_more	  = skip + pagedMatches.Length < totalMatches
        };
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
        if(pattern == "*" || pattern == "*.*")
            return true;

        if(pattern.StartsWith("*.")) {

			var extension = pattern.Substring(1);

            return fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
		}

		return fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase);
	}

	private sealed class MatchResult
    {
        public required string File { get; init; }
        public required int Line { get; init; }
        public required string Text { get; init; }
    }
}
#endif
