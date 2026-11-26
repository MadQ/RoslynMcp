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
		"Returns the matching lines grouped as roslyn_search_files groups its hits: files[] by file, in[] by the declaration each match is in " +
		"('method Enqueue'), with its lines ('108-158': pass them to roslyn_read_file to read the whole member) and where its node starts " +
		"('112:2': the line and column roslyn_replace_in_code takes), followed by matches[] with line and text. A match on the declaration's own name " +
		"has declares: true. Several matches on one line are listed once. The answer's context names the syntax context searched; " +
		"with context 'all', each match names its own (comment, string, identifier, xmldoc, code). " +
		"Results are paged; pass page_token from a previous response to retrieve the next page."
	)]
	public async Task<object> SemanticSearch(
		[Description("Pattern to match against source CONTENT within the chosen syntax context. Interpretation is controlled by 'mode' (default regex). This is not a filename filter — use filePattern to restrict which files are searched.")]
		string pattern,
		
		CancellationToken cancellationToken,
		
		[Description(OptionalProjectPathDescription)]
		string? projectPath = null,
		
		[Description("Syntax context to restrict matches: 'comments' (// and /* */), 'strings' (string literals), 'identifiers' (symbol names), 'code' (non-comment, non-string executable lines), 'xmldocs' (/// XML documentation), 'all' (no filtering). Default: 'all'.")]
		string? context = null,
		
		[Description("Exclude compiler-generated and auto-generated code. Default: true.")]
		bool excludeGenerated = true,
		
		[Description("Case-sensitive matching. Default: false (case-insensitive).")]
		bool caseSensitive = false,
		
		[Description("Filename glob filter selecting WHICH files are searched (matches the file name, not content): e.g. '*.cs', '{Program,Startup}.cs'. Supports *, ? and {a,b}. Default: '*.cs'. Only C# files are processed — non-.cs files are skipped even if matched.")]
		string? filePattern = null,
		
		[Description(MatchModeDescription + "Default: 'regex'.")]
		string? mode = null,
		
		[Description("Only match within nodes of this syntax kind (e.g., 'MethodDeclaration', 'ClassDeclaration', 'IfStatement'). Omit to search everywhere. Unknown kind values are silently ignored.")]
		string? containingKind = null,
		
		[Description("Number of results to skip (for paging). Default: 0.")]
		int skip = 0,
		
		[Description("Maximum number of results to return. Default: 50, max: 200.")]
		int take = 50,
		
		[Description(PageTokenDescription)]
		string? page_token = null
	)
	{
		using var scope = BeginTool("roslyn_semantic_search", pattern, new { projectPath, context, caseSensitive, excludeGenerated, filePattern, mode, containingKind, skip, take });
		
		if(!TryResolveProjectArg(projectPath, out projectPath, out var projectError))
			
			return scope.Error(projectError);
		
		context     ??= "all";
		filePattern ??= "*.cs";
		
		if(scope.TryServeCachedPage<SourcePlace>(page_token, ref skip, ref take, 50, 200, out var cached))
			
			return scope.Outcome("cached page", cached);
		
		// Validate context parameter
		var validContexts = new[] { "comments", "strings", "identifiers", "code", "xmldocs", "all" };
		
		if(!validContexts.Contains(context.ToLowerInvariant()))
			
			return scope.Error(new DetailedErrorResult(
				$"Must be one of: {string.Join(", ", validContexts)}")
			{
				Error = "Invalid context parameter"
			});
		
		context = context.ToLowerInvariant();
		
		var matchMode = MatchMode.Regex;
		
		if(mode is not null && !TryParseMatchMode(mode, out matchMode, out var modeError))
			
			return scope.Error(new ErrorResult(modeError));
		
		Regex regex;
		
		try {
			regex = BuildContentRegex(pattern, matchMode, caseSensitive);
		}
		catch(ArgumentException ex) {
			return scope.Error(new ErrorResult($"Invalid regex pattern: {ex.Message}"));
		}
		
		if(!TryResolveSolution(projectPath, out var solution, out var solutionError))
			
			return scope.Error(solutionError);
		
		if(!TryResolveRoot(projectPath, out var rootPath, out var rootError))
			
			return scope.Error(rootError);
		
		if(!GlobMatcher.TryCreateMatcher(filePattern, out var matchesFile, out var globError))
			
			return scope.Error(new ErrorResult(globError));
		

		var allMatches = await CollectAsync(regex);
		
		// Chat-reference fallback (#228): a 'sym:'-prefixed pattern that matched nothing verbatim
		// is retried with the prefix stripped — literal 'sym:' searches stay verbatim-first.
		string? fallbackCaution = null;
		string? fallbackHint    = null;
		
		if(allMatches.Length == 0 && HasChatSymbolRefPrefix(pattern, out var stripped)) {
			
			if(stripped.Length == 0)
				fallbackHint = ChatRefTruncatedHint;
			else {
				
				Regex? strippedRegex = null;
				
				try {
					strippedRegex = BuildContentRegex(stripped, matchMode, caseSensitive);
				}
				catch(ArgumentException) { }
				
				var retry = strippedRegex is null ? null : await CollectAsync(strippedRegex);
				
				if(retry is { Length: > 0 }) {
					
					allMatches      = retry;
					fallbackCaution = ChatRefFallbackCaution(pattern, stripped);
				}
				else
					fallbackHint = ChatRefBothTriedHint(pattern, stripped);
			}
		}
		
		var shape  = PageShape(context, ComposeCautions(fallbackCaution, AdhocCaution(projectPath)), fallbackHint);
		var result = PaginateAndStore(allMatches, ref skip, take, shape);
		
		// How the page grouped, and what the same matches took listed flat (#487).
		scope.Record(SourcePlace.GroupingNote(result.Items, "match(es)"));
		
		return scope.Outcome($"{result.Total} match(es)", shape(result));
		
		// The matches as places, by file and line, one per line: the text of a match is its line,
		// and several matches on a line said that line several times.
		async Task<SourcePlace[]> CollectAsync(Regex rx)
		{
			var collected = new List<SourcePlace>();
			// seenPaths prevents searching the same physical file twice in multi-targeted projects
			// (e.g., net8.0 + net10.0 each have their own Document for the same .cs file).
			// The first TFM's parse wins; #if NET10_0 blocks may be absent in the skipped parse.
			var seenPaths = new HashSet<string>(SecurityBoundary.PathComparer)
			;
			
			foreach(var project in solution.Projects)
				foreach(var document in project.Documents) {
					
					if(document.FilePath is null || !seenPaths.Add(document.FilePath))
						continue;
					
					var fileName = Path.GetFileName(document.FilePath);
					
					if(!matchesFile(fileName))
						continue;
					
					if(!IsCSharpSourcePath(fileName))
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
						
						"comments"    => SearchInComments(root, text, rx),
						"strings"     => SearchInStrings(root, text, rx),
						"identifiers" => SearchInIdentifiers(root, text, rx),
						"code"        => SearchInCode(root, text, rx),
						"xmldocs"     => SearchInXmlDocs(root, text, rx),
						"all"         => SearchInAll(root, text, rx),
						_             => Array.Empty<SemanticMatchResult>()
					};
					
					// Filter by containing syntax kind if specified.
					if(containingKind is not null && Enum.TryParse<SyntaxKind>(containingKind, ignoreCase: true, out var requiredKind)) {
						
						matches = matches.Where(m => {
							
							// Use the exact match position, not the line start — FindToken(lineStart)
							// returns the indentation trivia token for indented code, causing the
							// enclosing-kind walk to land on the wrong node.
							var node = root.FindToken(m.Position).Parent
							;
							
							while(node is not null) {
								
								if(node.IsKind(requiredKind))
									
									return true;
								
								node = node.Parent;
							}
							
							return false;
						});
					}
					
					// One place per line, made from the line's leftmost match: a place costs a
					// walk up the tree for its declaration, and a broad pattern matches many times
					// on a line. Any match of the line on the declaration's own name marks the
					// place, as in a text search: in 'SearchResult BuildResult()' the pattern
					// 'Result' matches the return type first.
					foreach(var onLine in matches.GroupBy(match => text.Lines.GetLineFromPosition(match.Position).LineNumber)) {
						
						var first = onLine.MinBy(match => match.Position)!;
						var names = EnclosingDeclaration.At(root, text, first.Position).Names;
						
						collected.Add(SourcePlace.Of(Location.Create(tree, new TextSpan(first.Position, first.Length)), rootPath, cancellationToken) with {
							
							Declares = onLine.Any(match => names.Any(name => name.OverlapsWith(new TextSpan(match.Position, match.Length)))),
							
							// Said per match only where it differs between them; a search of one
							// context has it once, in the answer.
							Context = context == "all" ? first.Context : null,
						});
					}
				}
			
			return SourcePlace.Sorted(collected);
		}
	}
	
	// Builds every page of one query, first and cached alike, so a cached page keeps the same fields
	// — the context, caution, and hint included.
	static Func<PaginatedResult<SourcePlace>, object> PageShape(string context, string? caution, string? hint)
		=> page => new SemanticSearchResult(
			SourcePlace.Group(page.Items, sayDeclares: true),
			page.Total,
			page.Items.Length,
			page.PageToken,
			page.HasMore,
			context)
		{
			Caution = caution,
			Hint    = hint
		}
	;
	
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
				var absPos  = span.Start + m.Index
				;
				
				yield return new SemanticMatchResult {
					
					Context  = "comment",
					Position = absPos,
					Length   = m.Length
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
				var absPos  = span.Start + m.Index
				;
				
				yield return new SemanticMatchResult {
					
					Context  = "string",
					Position = absPos,
					Length   = m.Length
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
			var tokenText = token.ToString();
			
			if(regex.IsMatch(tokenText)) {
				
				yield return new SemanticMatchResult {
					
					Context  = "identifier",
					Position = span.Start,
					Length   = span.Length
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
		// Every code match is returned: the caller folds a line's matches into one place.
		var lines    = text.Lines
		;
		var excluded = BuildExcludedSpans(root);
		
		for(int i = 0; i < lines.Count; i++) {
			
			var lineText = lines[i].ToString();
			
			foreach(Match m in regex.Matches(lineText)) {
				
				var matchPos = lines[i].Start + m.Index;
				
				if(IsInExcludedSpan(excluded, matchPos))
					continue;
				
				yield return new SemanticMatchResult {
					
					Context  = "code",
					Position = matchPos,
					Length   = m.Length
				};
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
				
				yield return new SemanticMatchResult {
					
					Context  = "xmldoc",
					Position = absPos,
					Length   = m.Length
				};
			}
		}
	}
	
	IEnumerable<SemanticMatchResult> SearchInAll(SyntaxNode root, SourceText text, Regex regex)
	{
		// Simple line-by-line search, similar to SearchFilesTool but with syntax awareness.
		var lines = text.Lines
		;
		
		for(int i = 0; i < lines.Count; i++) {
			
			var lineText = lines[i].ToString();
			
			// Every match of the line: the caller folds them into one place, and needs them all
			// to say whether one is the declaration's name.
			foreach(Match m in regex.Matches(lineText)) {
				
				// What the match is in is asked at its first character that is not whitespace:
				// a pattern such as '\s+// TODO' starts before the comment it is about.
				var start = m.Index;
				
				while(start < m.Index + m.Length - 1 && char.IsWhiteSpace(lineText[start]))
					start++;
				
				yield return new SemanticMatchResult {
					
					Context  = DetermineContext(root, lines[i].Start + start),
					Position = lines[i].Start + m.Index,
					Length   = m.Length
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
		int lo = 0, hi = excluded.Length - 1
		;
		
		while(lo <= hi) {
			
			int mid = (lo + hi) >>> 1;
			
			if(excluded[mid].Start <= position)
				lo = mid + 1;
			else
				hi = mid - 1;
		}
		
		return hi >= 0 && excluded[hi].End > position;
	}
	
	
	// The context of one match of a search over all contexts: what the character at the position
	// is in, which the caller takes from the match. By the match and not by its line: a line holds tokens of several kinds, and the first of
	// them said "identifier" for a doc comment and for a string after a name.
	static string DetermineContext(SyntaxNode root, int position)
	{
		var trivia = root.FindTrivia(position);
		
		if(trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
			
			return "comment";
		
		if(trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
			
			return "xmldoc";
		
		var token = root.FindToken(position);
		
		// A match in the whitespace between two tokens is in neither.
		if(!token.Span.Contains(position))
			
			return "code";
		
		// The full set used in SearchInStrings and BuildExcludedSpans.
		if(token.Kind() is SyntaxKind.StringLiteralToken or
		                   SyntaxKind.InterpolatedStringTextToken or
		                   SyntaxKind.SingleLineRawStringLiteralToken or
		                   SyntaxKind.MultiLineRawStringLiteralToken or
		                   SyntaxKind.Utf8StringLiteralToken or
		                   SyntaxKind.Utf8SingleLineRawStringLiteralToken or
		                   SyntaxKind.Utf8MultiLineRawStringLiteralToken)
			
			return "string";
		
		return token.IsKind(SyntaxKind.IdentifierToken) ? "identifier" : "code";
	}
	
	// One match as a search of a file finds it; the answer lists it as a SourcePlace.
	sealed record SemanticMatchResult
	{
		public string Context { get; init; } = "";
		
		// Where the match starts in the file and how long it is: what containingKind is checked
		// at, and what the place and its declaration are found from.
		public int Position { get; init; }
		public int Length   { get; init; }
	}
}
