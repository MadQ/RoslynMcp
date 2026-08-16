using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class FindStringLiteralTool : RoslynMcpTool
{
	public FindStringLiteralTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }

	[McpServerTool(Name = "roslyn_find_string_literal", ReadOnly = true, Title = "Find String Literal", OpenWorld = false, Idempotent = true)]
	[Description(
		"Searches C# string literal tokens for a pattern and returns both the raw source text and the decoded " +
		"value (quotes stripped, escape sequences resolved) for each match. " +
		"Covers all string forms: regular (\"\"), verbatim (@\"\"), interpolated literal parts ($\"\"), " +
		"raw (\"\"\"...\"\"\"), and UTF-8 literal variants. " +
		"Use this instead of roslyn_semantic_search context:\"strings\" when you need: " +
		"(1) glob matching via useGlob: true (e.g. *roslynmcp* with no regex escaping required); " +
		"(2) to match against the decoded value (e.g. search for a tab character, not \\\\t); " +
		"(3) a clean result with separate 'text' (raw) and 'value' (decoded) fields per match. " +
		"Results are paged; pass page_token from a previous response to retrieve the next page."
	)]
	public async Task<object> FindStringLiteral(
		[Description("Pattern to search for. Regex by default; set useGlob: true for glob matching (* = any chars, ? = one char).")]
		string pattern,

		[Description(ProjectPathDescription)]
		string projectPath,

		CancellationToken cancellationToken,

		[Description("When true, treat pattern as a glob (*, ?). Default: false (regex).")]
		bool useGlob = false,

		[Description("When true, match against raw source text including quotes and unresolved escapes (e.g. \\\"\\\\n\\\"). Default: false — matches against the decoded value (quotes stripped, escapes resolved).")]
		bool matchRaw = false,

		[Description("Case-sensitive matching. Default: false (case-insensitive).")]
		bool caseSensitive = false,

		[Description("Filename glob filter applied to workspace C# files. Default: '*.cs'.")]
		string? filePattern = null,

		[Description("Number of results to skip. Default: 0.")]
		int skip = 0,

		[Description("Maximum results to return. Default: 50, max: 200.")]
		int take = 50,

		[Description("Token from a previous response to get the next page without re-executing the query.")]
		string? page_token = null
	)
	{
		using var scope = BeginTool("roslyn_find_string_literal", pattern, new { useGlob, matchRaw, caseSensitive, filePattern, skip, take });

		filePattern ??= "*.cs";

		if(scope.TryServeCachedPage<StringLiteralMatch>(page_token, ref skip, ref take, 200, out var cached))

			return scope.Outcome("cached page", cached);

		var opts = RegexOptions.Compiled;

		if(!caseSensitive)
			opts |= RegexOptions.IgnoreCase;

		Regex regex;

		try {
			regex = useGlob
				? new Regex(BuildGlobRegex(pattern), opts)
				: new Regex(pattern, opts)
			;
		}
		catch(ArgumentException ex) {
			return scope.Error(new ErrorResult($"Invalid pattern: {ex.Message}"));
		}

		if(!TryResolveSolution(projectPath, out var solution, out var solutionError))
			
			return scope.Error(solutionError);
		
		if(!TryResolveRoot(projectPath, out var rootPath, out var rootError))
			
			return scope.Error(rootError);

		var allMatches = await CollectAsync(regex);

		// Chat-reference fallback (#228): a 'sym:'-prefixed pattern that matched nothing verbatim
		// is retried with the prefix stripped — literal 'sym:' searches stay verbatim-first.
		string? fallbackCaution = null;
		string? fallbackHint    = null;

		if(allMatches.Count == 0 && HasChatSymbolRefPrefix(pattern, out var stripped)) {

			if(stripped.Length == 0)
				fallbackHint = ChatRefTruncatedHint;
			else {

				Regex? strippedRegex = null;

				try {
					strippedRegex = useGlob
						? new Regex(BuildGlobRegex(stripped), opts)
						: new Regex(stripped, opts)
					;
				}
				catch(ArgumentException) { }

				var retry = strippedRegex is null ? null : await CollectAsync(strippedRegex);

				if(retry is { Count: > 0 }) {

					allMatches      = retry;
					fallbackCaution = ChatRefFallbackCaution(pattern, stripped);
				}
				else
					fallbackHint = ChatRefBothTriedHint(pattern, stripped);
			}
		}

		var allResults = allMatches.ToArray();
		var result     = PaginateAndStore(allResults, ref skip, take);

		return scope.Outcome($"{result.Total} match(es)", new FindStringLiteralResult(
			result.Items,
			result.Total,
			result.Items.Length,
			result.PageToken,
			result.HasMore)
		{
			Caution = ComposeCautions(fallbackCaution, AdhocCaution(projectPath)),
			Hint    = fallbackHint
		});

		async Task<List<StringLiteralMatch>> CollectAsync(Regex rx)
		{
			var matches = new List<StringLiteralMatch>();
			// seenPaths prevents searching the same physical file twice in multi-targeted projects.
			var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach(var project in solution.Projects)
				foreach(var document in project.Documents) {

					if(document.FilePath is null || !seenPaths.Add(document.FilePath))
						continue;

					var fileName = Path.GetFileName(document.FilePath);

					if(!GlobMatcher.Matches(fileName, filePattern))
						continue;

					if(!fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
						continue;

					var tree = await document.GetSyntaxTreeAsync(cancellationToken);

					if(tree is null)
						continue;

					var root         = tree.GetRoot(cancellationToken);
					var sourceText   = tree.GetText(cancellationToken);
					var relativePath = Path.GetRelativePath(rootPath, document.FilePath);

					foreach(var token in root.DescendantTokens()) {

						var tk = token.Kind();

						if(tk is not (SyntaxKind.StringLiteralToken or
						              SyntaxKind.InterpolatedStringTextToken or
						              SyntaxKind.SingleLineRawStringLiteralToken or
						              SyntaxKind.MultiLineRawStringLiteralToken or
						              SyntaxKind.Utf8StringLiteralToken or
						              SyntaxKind.Utf8SingleLineRawStringLiteralToken or
						              SyntaxKind.Utf8MultiLineRawStringLiteralToken))
							continue;

						var candidate = matchRaw ? token.Text : token.ValueText;

						if(!rx.IsMatch(candidate))
							continue;

						var pos = sourceText.Lines.GetLinePosition(token.SpanStart);

						matches.Add(new StringLiteralMatch(relativePath, pos.Line + 1, pos.Character + 1, token.Text, token.ValueText));
					}
				}

			return matches;
		}
	}

	// ** is not supported here — this is value-content matching, not path matching.
	static string BuildGlobRegex(string glob)
		=> "^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$"
	;
}

sealed record StringLiteralMatch(
	[property: JsonPropertyName("file")]   string File,
	[property: JsonPropertyName("line")]   int    Line,
	[property: JsonPropertyName("column")] int    Column,
	[property: JsonPropertyName("text")]   string Text,
	[property: JsonPropertyName("value")]  string Value
);

sealed record FindStringLiteralResult(
	[property: JsonPropertyName("matches")]       StringLiteralMatch[] Matches,
	[property: JsonPropertyName("total_matches")] int                  Total,
	[property: JsonPropertyName("returned")]      int                  Count,
	[property: JsonPropertyName("page_token")]    string?              PageToken,
	[property: JsonPropertyName("has_more")]      bool                 HasMore
) : ToolResult;
