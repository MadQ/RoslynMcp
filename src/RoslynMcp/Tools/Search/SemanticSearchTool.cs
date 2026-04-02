using System.ComponentModel;
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
	
	[
		McpServerTool(Name = "roslyn_semantic_search", ReadOnly = true), Description(
			"Searches C# files using Roslyn syntax-tree filtering. " +
			"Allows filtering by syntax context (comments, strings, identifiers, code, etc.). " +
			"More precise than search_files but C#-only and slower due to parsing. " +
			"Use this when you need to search within specific code contexts."
		)
	]
	public async Task<object> SemanticSearch(
		[Description("Regex pattern to search for (e.g., 'TODO.*performance', 'UserName').")]
		string pattern,

		[Description(ProjectPathDescription)]
		string projectPath,

		CancellationToken cancellationToken,

		[Description("Syntax context to search within: 'comments', 'strings', 'identifiers', 'code', 'xmldocs', 'all'. Default: 'all'.")]
		string? context = null,
		
		[Description("Exclude compiler-generated and auto-generated code. Default: true.")]
		bool excludeGenerated = true,
		
		[Description("Case-sensitive matching. Default: false.")]
		bool caseSensitive = false,
		
		[Description("File glob pattern (e.g., '*.cs'). Default: '*.cs'.")]
		string? filePattern = null,

		[Description("Only match within nodes of this syntax kind (e.g., 'MethodDeclaration', 'ClassDeclaration', 'IfStatement'). Omit to search everywhere.")]
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

		var cachedPage = TryServeCachedPage<SemanticMatchResult>(scope, page_token, ref skip, ref take, 200);
		if(cachedPage is not null)
			return cachedPage;

		// Validate context parameter
		var validContexts = new[] { "comments", "strings", "identifiers", "code", "xmldocs", "all" };
		
		if(!validContexts.Contains(context.ToLowerInvariant())) {

			return scope.Error(new DetailedErrorResult(
				"Invalid context parameter",
				$"Must be one of: {string.Join(", ", validContexts)}"
			));
		}
		
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
				
				if(!MatchesGlob(fileName, filePattern))
					continue;
				
				// Skip non-C# files
				if(!fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
					continue;
				
				// Get syntax tree
				var tree = await document.GetSyntaxTreeAsync(cancellationToken);
				
				if(tree is null)
					continue;
				
				// Check for generated code
				if(excludeGenerated && IsGeneratedCode(tree)) {
				
					continue;
				}
				
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

						var pos  = text.Lines[m.Line - 1].Start;
						var node = root.FindToken(pos).Parent;

						while(node is not null) {

							if(node.IsKind(requiredKind))
								return true;

							node = node.Parent;
						}

						return false;
					});
				}

				var relativePath = Path.GetRelativePath(rootPath, document.FilePath);

				foreach(var match in matches) {

					match.File = relativePath;
					allMatches.Add(match);
				}
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
			
			var span	 = trivia.Span;
			var lineSpan = text.Lines.GetLinePositionSpan(span);
			var triviaText = trivia.ToString();
			
			if(regex.IsMatch(triviaText)) {
			
				yield return new SemanticMatchResult {
					Line	= lineSpan.Start.Line + 1,
					Text	= triviaText.Trim(),
					Context	= "comment"
				};
			}
		}
	}
	
	IEnumerable<SemanticMatchResult> SearchInStrings(SyntaxNode root, SourceText text, Regex regex)
	{
		foreach(var token in root.DescendantTokens()) {
		
			if(!token.IsKind(SyntaxKind.StringLiteralToken) &&
			   !token.IsKind(SyntaxKind.InterpolatedStringTextToken))
				continue;
			
			var span	 = token.Span;
			var lineSpan = text.Lines.GetLinePositionSpan(span);
			var tokenText = token.ToString();
			
			if(regex.IsMatch(tokenText)) {
			
				yield return new SemanticMatchResult {
					Line	= lineSpan.Start.Line + 1,
					Text	= tokenText.Trim(),
					Context	= "string"
				};
			}
		}
	}
	
	IEnumerable<SemanticMatchResult> SearchInIdentifiers(SyntaxNode root, SourceText text, Regex regex)
	{
		foreach(var token in root.DescendantTokens()) {
		
			if(!token.IsKind(SyntaxKind.IdentifierToken))
				continue;
			
			var span	 = token.Span;
			var lineSpan = text.Lines.GetLinePositionSpan(span);
			var tokenText = token.ToString();
			
			if(regex.IsMatch(tokenText)) {
			
				// Get the containing line for context
				var line	 = text.Lines[lineSpan.Start.Line];
				var lineText = line.ToString().Trim();
				
				yield return new SemanticMatchResult {
					Line	= lineSpan.Start.Line + 1,
					Text	= lineText,
					Context	= "identifier"
				};
			}
		}
	}
	
	IEnumerable<SemanticMatchResult> SearchInCode(SyntaxNode root, SourceText text, Regex regex)
	{
		// Search line-by-line (supports multi-token patterns like "new List"),
		// but skip lines whose primary content is a comment or string literal.
		var lines    = text.Lines;
		var reported = new HashSet<int>();

		for(int i = 0; i < lines.Count; i++) {

			var lineText = lines[i].ToString();

			if(!regex.IsMatch(lineText))
				continue;

			// Determine whether this line is primarily a comment or string.
			var context = DetermineContext(root, lines[i].Span);

			if(context is "comment" or "xmldoc" or "string")
				continue;

			if(reported.Add(i)) {

				yield return new SemanticMatchResult {
					Line    = i + 1,
					Text    = lineText.Trim(),
					Context = "code"
				};
			}
		}
	}
	
	IEnumerable<SemanticMatchResult> SearchInXmlDocs(SyntaxNode root, SourceText text, Regex regex)
	{
		foreach(var trivia in root.DescendantTrivia()) {
		
			if(!trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) &&
			   !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
				continue;
			
			var span	 = trivia.Span;
			var lineSpan = text.Lines.GetLinePositionSpan(span);
			var triviaText = trivia.ToString();
			
			if(regex.IsMatch(triviaText)) {
			
				yield return new SemanticMatchResult {
					Line	= lineSpan.Start.Line + 1,
					Text	= triviaText.Trim(),
					Context	= "xmldoc"
				};
			}
		}
	}
	
	IEnumerable<SemanticMatchResult> SearchInAll(SyntaxNode root, SourceText text, Regex regex)
	{
		// Simple line-by-line search, similar to SearchFilesTool but with syntax awareness
		var lines = text.Lines;
		
		for(int i = 0; i < lines.Count; i++) {
		
			var lineText = lines[i].ToString();
			
			if(regex.IsMatch(lineText)) {
			
				// Determine context by checking what's on this line
				var lineSpan = lines[i].Span;
				var context = DetermineContext(root, lineSpan);
				
				yield return new SemanticMatchResult {
					Line	= i + 1,
					Text	= lineText.Trim(),
					Context	= context
				};
			}
		}
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
				
				if(trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
				   trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
					return "comment";
				
				if(trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
				   trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
					return "xmldoc";
			}
			
			// Check token kinds
			if(token.IsKind(SyntaxKind.StringLiteralToken) ||
			   token.IsKind(SyntaxKind.InterpolatedStringTextToken))
				return "string";
			
			if(token.IsKind(SyntaxKind.IdentifierToken))
				return "identifier";
		}
		
		return "code";
	}
	
	static bool MatchesGlob(string fileName, string pattern)
	{
		// Simple glob matching (same as SearchFilesTool)
		var regexPattern = "^" + Regex.Escape(pattern)
			.Replace("\\*", ".*")
			.Replace("\\?", ".") + "$";
		
		return Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase);
	}
	
	sealed class SemanticMatchResult
	{
		public string File	  { get; set; } = "";
		public int	  Line	  { get; set; }
		public string Text	  { get; set; } = "";
		public string Context { get; set; } = "";
	}
}
