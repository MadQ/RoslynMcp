using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class SemanticSearchTool : RoslynMcpTool
{
	public SemanticSearchTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_semantic_search", ReadOnly = true, Title = "Semantic Search", OpenWorld = false, Idempotent = true)]
	[Description(
		"Searches C# files using Roslyn syntax-tree filtering. Allows filtering by syntax context " +
		"(comments, strings, identifiers, code, xmldocs) so results are limited to matches within " +
		"a specific syntactic role — not just any line that contains the pattern. " +
		"Use this when the location of a match matters: find a symbol only in comments, find a string " +
		"literal containing a value, or find code references excluding documentation noise. " +
		"Also supports containingKind to restrict matches to nodes of a specific syntax kind " +
		"(e.g., only inside MethodDeclaration or ClassDeclaration). " +
		"C#-only — non-C# files are skipped regardless of filePattern. " +
		"Slower than roslyn_search_files due to syntax-tree parsing; use roslyn_search_files for fast " +
		"cross-file content search when context filtering is not needed. " +
		"For filename/path matching with no content search, use roslyn_list_files instead. " +
		"Each result includes file, line number, matched text, and a context label. " +
		"Results are paged; pass page_token from a previous response to retrieve the next page."
	)]
	public async Task<object> SemanticSearch(
		[Description("Regex pattern to search for (e.g., 'TODO.*performance', 'UserName').")]
		string pattern,
		
		[Description(ProjectPathDescription)]
		string projectPath,
		
		CancellationToken cancellationToken,
		
		[Description("Syntax context to restrict matches: 'comments' (// and /* */), 'strings' (string literals), 'identifiers' (symbol names), 'code' (non-comment, non-string executable lines), 'xmldocs' (/// XML documentation), 'all' (no filtering). Default: 'all'.")]
		string? context = null,
		
		[Description("Exclude compiler-generated and auto-generated code. Default: true.")]
		bool excludeGenerated = true,
		
		[Description("Case-sensitive matching. Default: false (case-insensitive).")]
		bool caseSensitive = false,
		
		[Description("File type filter (e.g., '*.cs'). Default: '*.cs'. Only C# files are processed — non-.cs files are skipped even if matched.")]
		string? filePattern = null,
		
		[Description("Only match within nodes of this syntax kind (e.g., 'MethodDeclaration', 'ClassDeclaration', 'IfStatement'). Omit to search everywhere. Unknown kind values are silently ignored.")]
		string? containingKind = null,
		
		[Description("Number of results to skip (for paging). Default: 0.")]
		int skip = 0,
		
		[Description("Maximum number of results to return. Default: 50, max: 200.")]
		int take = 50,
		
		[Description("Token from a previous response to get the next page without re-executing the query.")]
		string? page_token = null
	)
	{
		using var scope = BeginTool("roslyn_semantic_search", pattern);
		
		context     ??= "all";
		filePattern ??= "*.cs";
		
		if(scope.TryServeCachedPage<SemanticMatchResult>(page_token, ref skip, ref take, 200, out var cached))
			return scope.Outcome("cached page", cached);
		
		// Validate context parameter
		var validContexts = new[] { "comments", "strings", "identifiers", "code", "xmldocs", "all" };
		
		if(!validContexts.Contains(context.ToLowerInvariant()))
			return scope.Error(new DetailedErrorResult(
				"Invalid context parameter",
				$"Must be one of: {string.Join(", ", validContexts)}"
			));
		
		context = context.ToLowerInvariant();
		
		// Compile regex
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
		var allMatches = new List<SemanticMatchResult>();
		var seenPaths  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		
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
				
				if(excludeGenerated && IsGeneratedCode(tree))
					continue;
				
				var root = tree.GetRoot(cancellationToken);
				var text = tree.GetText(cancellationToken);
				
				// Search based on context
				var matches = context switch {
					
					"comments"    => SearchInComments(root, text, regex),
					"strings"     => SearchInStrings(root, text, regex),
					"identifiers" => SearchInIdentifiers(root, text, regex),
					"code"        => SearchInCode(root, text, regex),
					"xmldocs"     => SearchInXmlDocs(root, text, regex),
					"all"         => SearchInAll(root, text, regex),
					_             => Array.Empty<SemanticMatchResult>()
				};
				
				// Filter by containing syntax kind if specified.
				if(containingKind is not null && Enum.TryParse<SyntaxKind>(containingKind, ignoreCase: true, out var requiredKind)) {
					
					matches = matches.Where(m => {
						
						// Use the exact match position, not the line start — FindToken(lineStart)
						// returns the indentation trivia token for indented code, causing the
						// enclosing-kind walk to land on the wrong node.
						var node = root.FindToken(m.Position).Parent;
						
						while(node is not null) {
							
							if(node.IsKind(requiredKind))
								return true;
							
							node = node.Parent;
						}
						
						return false;
					});
				}
				
				var relativePath = Path.GetRelativePath(rootPath, document.FilePath);
				
				foreach(var match in matches)
					allMatches.Add(match with { File = relativePath });
			}
		
		var allResults = allMatches.ToArray();
		var result     = PaginateAndStore(allResults, ref skip, take);
		
		return scope.Outcome($"{result.Total} match(es)", new SemanticSearchResult(
			result.Items,
			result.Total,
			result.Items.Length,
			result.PageToken,
			result.HasMore,
			context,
			AdhocCaution(projectPath)
		));
	}
	
	bool IsGeneratedCode(SyntaxTree tree)
	{
		var root = tree.GetRoot();
		
		// Check for common generated code indicators
		var compilationUnit = root as CompilationUnitSyntax;
		
		if(compilationUnit is null)
			return false;
		
		// Check for [GeneratedCode] attribute
		foreach(var attributeList in compilationUnit.AttributeLists)
			foreach(var attribute in attributeList.Attributes) {
				
				var name = attribute.Name.ToString();
				
				if(name.Contains("GeneratedCode") || name.Contains("AutoGenerated"))
					return true;
			}
		
		// Check for auto-generated comment at top of file
		var firstToken = root.GetFirstToken();
		var leadingTrivia = firstToken.LeadingTrivia;
		
		foreach(var trivia in leadingTrivia) {
			
			if(trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)) {
				
				var text = trivia.ToString();
				
				if(text.Contains("auto-generated", StringComparison.OrdinalIgnoreCase) ||
				   text.Contains("autogenerated", StringComparison.OrdinalIgnoreCase) ||
				   text.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase))
					
					return true;
			}
		}
		
		return false;
	}
	
	IEnumerable<SemanticMatchResult> SearchInComments(SyntaxNode root, SourceText text, Regex regex)
	{
		foreach(var trivia in root.DescendantTrivia()) {

			if(!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) &&
			   !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
				continue;

			var span       = trivia.Span;
			var triviaText = trivia.ToString();

			foreach(Match m in regex.Matches(triviaText)) {

				// absPos is the absolute source position of the match — not the block start —
				// so multiline block comments report the correct line number per occurrence.
				var absPos  = span.Start + m.Index;
				var lineIdx = text.Lines.GetLinePosition(absPos).Line;

				yield return new SemanticMatchResult {

					Line     = lineIdx + 1,
					Text     = text.Lines[lineIdx].ToString().Trim(),
					Context  = "comment",
					Position = absPos
				};
			}
		}
	}
	
	IEnumerable<SemanticMatchResult> SearchInStrings(SyntaxNode root, SourceText text, Regex regex)
	{
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

			var span      = token.Span;
			var tokenText = token.ToString();

			foreach(Match m in regex.Matches(tokenText)) {

				// absPos is the absolute match position — handles multiline verbatim/raw strings.
				var absPos  = span.Start + m.Index;
				var lineIdx = text.Lines.GetLinePosition(absPos).Line;

				yield return new SemanticMatchResult {

					Line     = lineIdx + 1,
					Text     = text.Lines[lineIdx].ToString().Trim(),
					Context  = "string",
					Position = absPos
				};
			}
		}
	}
	
	IEnumerable<SemanticMatchResult> SearchInIdentifiers(SyntaxNode root, SourceText text, Regex regex)
	{
		foreach(var token in root.DescendantTokens()) {

			if(!token.IsKind(SyntaxKind.IdentifierToken))
				continue;

			var span      = token.Span;
			var lineSpan  = text.Lines.GetLinePositionSpan(span);
			var tokenText = token.ToString();

			if(regex.IsMatch(tokenText)) {

				// Get the containing line for context
				var line     = text.Lines[lineSpan.Start.Line];
				var lineText = line.ToString().Trim();

				yield return new SemanticMatchResult {

					Line     = lineSpan.Start.Line + 1,
					Text     = lineText,
					Context  = "identifier",
					Position = span.Start
				};
			}
		}
	}
	
	IEnumerable<SemanticMatchResult> SearchInCode(SyntaxNode root, SourceText text, Regex regex)
	{
		// Search line-by-line (supports multi-token patterns like "new List"),
		// skipping positions that fall within a comment, xmldoc, or string span.
		// All per-line matches are checked so a comment-excluded first match does not
		// hide a later code match on the same line (e.g., `/* Foo */ var Foo = 1;`).
		var lines    = text.Lines;
		var excluded = BuildExcludedSpans(root);

		for(int i = 0; i < lines.Count; i++) {

			var lineText = lines[i].ToString();

			foreach(Match m in regex.Matches(lineText)) {

				var matchPos = lines[i].Start + m.Index;

				if(IsInExcludedSpan(excluded, matchPos))
					continue;

				yield return new SemanticMatchResult {

					Line     = i + 1,
					Text     = lineText.Trim(),
					Context  = "code",
					Position = matchPos
				};

				break; // one result per line
			}
		}
	}
	
	IEnumerable<SemanticMatchResult> SearchInXmlDocs(SyntaxNode root, SourceText text, Regex regex)
	{
		foreach(var trivia in root.DescendantTrivia()) {

			if(!trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) && !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
				continue;

			var span       = trivia.Span;
			var triviaText = trivia.ToString();

			foreach(Match m in regex.Matches(triviaText)) {

				var absPos  = span.Start + m.Index;
				var lineIdx = text.Lines.GetLinePosition(absPos).Line;

				yield return new SemanticMatchResult {

					Line     = lineIdx + 1,
					Text     = text.Lines[lineIdx].ToString().Trim(),
					Context  = "xmldoc",
					Position = absPos
				};
			}
		}
	}
	
	IEnumerable<SemanticMatchResult> SearchInAll(SyntaxNode root, SourceText text, Regex regex)
	{
		// Simple line-by-line search, similar to SearchFilesTool but with syntax awareness.
		var lines = text.Lines;

		for(int i = 0; i < lines.Count; i++) {

			var lineText = lines[i].ToString();
			var m        = regex.Match(lineText);

			if(m.Success) {

				// Determine context by checking what's on this line
				var lineSpan = lines[i].Span;
				var context  = DetermineContext(root, lineSpan);

				yield return new SemanticMatchResult {
					Line     = i + 1,
					Text     = lineText.Trim(),
					Context  = context,
					Position = lines[i].Start + m.Index
				};
			}
		}
	}
	
	// Returns sorted (Start, End) pairs for all non-code spans (comments, xmldocs, string literals).
	// Used by SearchInCode to check whether a match position falls in a non-code region,
	// allowing lines like `Foo(); // note` to match when searching code patterns.
	static (int Start, int End)[] BuildExcludedSpans(SyntaxNode root)
	{
		var spans = new List<(int Start, int End)>();

		foreach(var token in root.DescendantTokens()) {

			foreach(var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia)) {

				var kind = trivia.Kind();

				if(kind is SyntaxKind.SingleLineCommentTrivia or
				           SyntaxKind.MultiLineCommentTrivia or
				           SyntaxKind.SingleLineDocumentationCommentTrivia or
				           SyntaxKind.MultiLineDocumentationCommentTrivia)
					spans.Add((trivia.Span.Start, trivia.Span.End));
			}

			var tk = token.Kind();

			if(tk is SyntaxKind.StringLiteralToken or
			         SyntaxKind.InterpolatedStringTextToken or
			         SyntaxKind.SingleLineRawStringLiteralToken or
			         SyntaxKind.MultiLineRawStringLiteralToken or
			         SyntaxKind.Utf8StringLiteralToken or
			         SyntaxKind.Utf8SingleLineRawStringLiteralToken or
			         SyntaxKind.Utf8MultiLineRawStringLiteralToken)
				spans.Add((token.Span.Start, token.Span.End));
		}

		spans.Sort((a, b) => a.Start.CompareTo(b.Start));

		return spans.ToArray();
	}
	
	static bool IsInExcludedSpan((int Start, int End)[] excluded, int position)
	{
		// Binary search for the last span starting at or before position.
		int lo = 0, hi = excluded.Length - 1;

		while(lo <= hi) {

			int mid = (lo + hi) >>> 1;

			if(excluded[mid].Start <= position)
				lo = mid + 1;
			else
				hi = mid - 1;
		}

		return hi >= 0 && excluded[hi].End > position;
	}
	
	
	string DetermineContext(SyntaxNode root, TextSpan lineSpan)
	{
		// Find tokens/trivia that intersect with this line
		var tokens = root.DescendantTokens(lineSpan, descendIntoTrivia: true);
		
		foreach(var token in tokens) {
			
			// Check trivia first (comments, xml docs)
			foreach(var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia)) {
				
				if(!lineSpan.IntersectsWith(trivia.Span))
					continue;
				
				if(trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
					return "comment";
				
				if(trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
				   trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
					
					return "xmldoc";
			}
			
			// Check token kinds
			if(token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.InterpolatedStringTextToken))
				return "string";
			
			if(token.IsKind(SyntaxKind.IdentifierToken))
				return "identifier";
		}
		
		return "code";
	}
	
	sealed record SemanticMatchResult
	{
		public string File    { get; init; } = "";
		public int    Line    { get; init; }
		public string Text    { get; init; } = "";
		public string Context { get; init; } = "";

		// Internal cursor for containingKind filtering — not exposed in the JSON response.
		[JsonIgnore]
		public int Position { get; init; }
	}
}
