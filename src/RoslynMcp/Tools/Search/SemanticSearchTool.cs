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
	public SemanticSearchTool(WorkspaceResolver workspace, FileLogger logger) : base(workspace, logger) { }
	
	[
		McpServerTool(Name = "roslyn_semantic_search", ReadOnly = true), Description(
			"Searches C# files using Roslyn syntax-tree filtering. " +
			"Allows filtering by syntax context (comments, strings, identifiers, code, etc.). " +
			"More precise than search_files but C#-only and slower due to parsing. " +
			"Use this when you need to search within specific code contexts."
		)
	]
	public object SemanticSearch(
		[Description("Regex pattern to search for (e.g., 'TODO.*performance', 'UserName').")]
		string pattern,

		[Description(ProjectPathDescription)]
		string projectPath,

		[Description("Syntax context to search within: 'comments', 'strings', 'identifiers', 'code', 'xmldocs', 'all'. Default: 'all'.")]
		string? context = null,
		
		[Description("Exclude compiler-generated and auto-generated code. Default: true.")]
		bool excludeGenerated = true,
		
		[Description("Case-sensitive matching. Default: false.")]
		bool caseSensitive = false,
		
		[Description("File glob pattern (e.g., '*.cs'). Default: '*.cs'.")]
		string? filePattern = null,
		
		[Description("Number of results to skip (for paging). Default: 0.")]
		int skip = 0,
		
		[Description("Maximum number of results to return. Default: 50, max: 200.")]
		int take = 50
	)
	{
		using var scope = BeginTool("roslyn_semantic_search", pattern);
		context		??= "all";
		filePattern	??= "*.cs";
		take		  = Math.Clamp(take, 1, 200);
		skip		  = Math.Max(0, skip);
		
		// Validate context parameter
		var validContexts = new[] { "comments", "strings", "identifiers", "code", "xmldocs", "all" };
		
		if(!validContexts.Contains(context.ToLowerInvariant())) {
		
			return new {
				error = "Invalid context parameter",
				details = $"Must be one of: {string.Join(", ", validContexts)}"
			};
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
		
			return new {
				error = "Invalid regex pattern",
				details = ex.Message
			};
		}
		
		var solution   = workspace.GetSolution(projectPath);
		var rootPath   = workspace.GetRootPath(projectPath);
		var allMatches = new List<SemanticMatchResult>();
		
		foreach(var project in solution.Projects)
			foreach(var document in project.Documents) {
			
				if(document.FilePath is null)
					continue;
				
				var fileName = Path.GetFileName(document.FilePath);
				
				if(!MatchesGlob(fileName, filePattern))
					continue;
				
				// Skip non-C# files
				if(!fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
					continue;
				
				// Get syntax tree
				var tree = document.GetSyntaxTreeAsync().GetAwaiter().GetResult();
				
				if(tree is null)
					continue;
				
				// Check for generated code
				if(excludeGenerated && IsGeneratedCode(tree)) {
				
					continue;
				}
				
				var root = tree.GetRoot();
				var text = tree.GetText();
				
				// Search based on context
				var matches = context switch {
					"comments"	  => SearchInComments(root, text, regex),
					"strings"	  => SearchInStrings(root, text, regex),
					"identifiers" => SearchInIdentifiers(root, text, regex),
					"code"		  => SearchInCode(root, text, regex),
					"xmldocs"	  => SearchInXmlDocs(root, text, regex),
					"all"		  => SearchInAll(root, text, regex),
					_			  => Array.Empty<SemanticMatchResult>()
				};
				
				// Add file path to each match
				var relativePath = Path.GetRelativePath(rootPath, document.FilePath);
				
				foreach(var match in matches) {
				
					match.File = relativePath;
					allMatches.Add(match);
				}
			}
		
		var totalMatches = allMatches.Count;
		var pagedMatches = allMatches.Skip(skip).Take(take).ToArray();
		
		scope.Outcome($"{totalMatches} match(es)");
		
		return new {
			matches		  = pagedMatches,
			total_matches = totalMatches,
			returned	  = pagedMatches.Length,
			has_more	  = skip + pagedMatches.Length < totalMatches,
			context		  = context
		};
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
		// Search in all tokens that are not in comments or strings
		foreach(var token in root.DescendantTokens()) {
		
			// Skip comments
			if(token.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
			   token.IsKind(SyntaxKind.MultiLineCommentTrivia))
				continue;
			
			// Skip strings
			if(token.IsKind(SyntaxKind.StringLiteralToken) ||
			   token.IsKind(SyntaxKind.InterpolatedStringTextToken))
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
					Context	= "code"
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
