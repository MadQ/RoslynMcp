using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class SearchFilesTool : RoslynMcpTool
{
	public SearchFilesTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_search_files", ReadOnly = true, Title = "Search Files", OpenWorld = false, Idempotent = true)]
	[Description(
		"Fast and precise code search — use instead of grep, Select-String, or findstr. Searches workspace files for lines matching a regex pattern. " +
		"Returns the matching lines grouped by file and, within a file, by the declaration each hit is in: files[].in[] names the " +
		"declaration ('method Enqueue', 'class Foo', 'field count'), its lines from its attached comment to its end ('108-158': " +
		"pass them to roslyn_read_file to read the whole member) and where its node starts ('112:2': the line and column " +
		"roslyn_replace_in_code takes), followed by matches[] with line and text. A match on the declaration's own name has " +
		"declares: true, so a plain name finds its declaration without a keyword in the pattern. Hits outside any declaration " +
		"(using directives, namespace lines) are in a group without those fields. " +
		"Use this when searching file content — finding all lines where a symbol name, pattern, or phrase appears. " +
		"For filename/path matching only (no content), use roslyn_list_files instead. " +
		"For C#-specific context filtering (find matches only in comments, strings, identifiers, or xmldocs), use roslyn_semantic_search instead. " +
		"Searches C# source files registered in the Roslyn workspace (from the loaded solution/project). " +
		"Non-C# files (.csproj, .json, .props, etc.) are never included — only source documents in the compilation. " +
		"The filePattern parameter filters by filename within that set (e.g., '*Test.cs' to restrict to test files). " +
		"For searching non-C# files by content, use roslyn_list_files to enumerate paths and roslyn_read_file to inspect them. " +
		"Results are paged; pass page_token from a previous response to retrieve the next page.")]
	public async Task<object> SearchFiles(
		[Description("Pattern to match against the CONTENT of each source line. Interpretation is controlled by 'mode' (default regex). This is not a filename filter — use filePattern to restrict which files are searched.")] string pattern,
		CancellationToken cancellationToken,
		[Description(OptionalProjectPathDescription)] string? projectPath = null,
		[Description("Filename glob filter selecting WHICH files are searched (matches the file name, not content): e.g. '*.cs', '*Test.cs', '*{Glob,ListFiles}*.cs'. Supports *, ? and {a,b}. Default: '*.cs'. Non-C# files are never searched regardless of this filter.")] string? filePattern = null,
		[Description("Case-sensitive matching. Default: false (case-insensitive).")] bool caseSensitive = false,
		[Description(MatchModeDescription + "Default: 'regex'.")] string? mode = null,
		[Description("Number of results to skip (for paging). Default: 0.")] int skip = 0,
		[Description("Maximum number of results to return. Default: 50, max: 200.")] int take = 50,
		[Description(PageTokenDescription)] string? page_token = null
	)
	{
		using var scope = BeginTool("roslyn_search_files", pattern, new { projectPath, filePattern, caseSensitive, mode, skip, take });
		
		if(!TryResolveProjectArg(projectPath, out projectPath, out var projectError))
			
			return scope.Error(projectError);
		
		filePattern ??= "*.cs";
		
		if(scope.TryServeCachedPage<MatchResult>(page_token, ref skip, ref take, 50, 200, out var cached))
			
			return scope.Outcome("cached page", cached);
		
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
		
		if(allMatches.Count == 0 && HasChatSymbolRefPrefix(pattern, out var stripped)) {
			
			if(stripped.Length == 0)
				fallbackHint = ChatRefTruncatedHint;
			else {
				
				Regex? strippedRegex = null;
				
				try {
					strippedRegex = BuildContentRegex(stripped, matchMode, caseSensitive);
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
		var shape      = PageShape(ComposeCautions(fallbackCaution, AdhocCaution(projectPath)), fallbackHint);
		var result     = PaginateAndStore(allResults, ref skip, take, shape);
		
		scope.Record(GroupingNote(result.Items));
		
		return scope.Outcome($"{result.Total} match(es)", shape(result));
		
		async Task<List<MatchResult>> CollectAsync(Regex rx)
		{
			var matches = new List<MatchResult>();
			// seenPaths prevents searching the same physical file twice in multi-targeted projects.
			var seenPaths = new HashSet<string>(SecurityBoundary.PathComparer)
			;
			
			foreach(var project in solution.Projects)
				foreach(var document in project.Documents) {
					
					if(document.FilePath is null || !seenPaths.Add(document.FilePath))
						continue;
					
					var fileName = Path.GetFileName(document.FilePath);
					
					if(!matchesFile(fileName))
						continue;
					
					var text  = await document.GetTextAsync(cancellationToken);
					var lines = text.Lines;
					var source = text.ToString();
					
					// Parsed for a file with a hit only, and then once: most files of a search have none.
					SyntaxNode? root = null;
					
					for(int i = 0; i < lines.Count; i++) {
						
						var line = lines[i];
						
						// The first match of the line says where on it the hit is; the line is one hit
						// however often the pattern matches it.
						var hit = FirstMatch(rx, source.AsSpan(line.Start, line.Span.Length));
						
						if(hit is null)
							continue;
						
						root ??= await document.GetSyntaxRootAsync(cancellationToken);
						
						var (declaration, names) = root is null
							? (null, [])
							: EnclosingDeclaration.At(root, text, line.Start + hit.Value.Index);
						
						// Any match of the line, not the first alone: 'logger' matches inside
						// 'FileLogger' before it matches the field's name on the same line.
						var declares = names.Length > 0 && MatchesAny(rx, source.AsSpan(line.Start, line.Span.Length), line.Start, names);
						
						matches.Add(new MatchResult {
							
							File        = Path.GetRelativePath(rootPath, document.FilePath),
							Line        = i + 1,
							Text        = source.AsSpan(line.Start, line.Span.Length).Trim().ToString(),
							Declaration = declaration,
							Declares    = declares,
						});
					}
				}
			
			return matches;
		}
	}
	
	// Builds every page of one query, first and cached alike, so a cached page keeps the same fields
	// — the caution and hint included.
	static Func<PaginatedResult<MatchResult>, object> PageShape(string? caution, string? hint)
		=> page => new SearchFilesResult(
			Group(page.Items),
			page.Total,
			page.Items.Length,
			page.PageToken,
			page.HasMore)
		{
			Caution = caution,
			Hint    = hint
		}
	;
	
	// The first match of the pattern in one line, or null. A span has no Match, only an enumerator.
	static (int Index, int Length)? FirstMatch(Regex regex, ReadOnlySpan<char> line)
	{
		foreach(var match in regex.EnumerateMatches(line))
			
			return (match.Index, match.Length);
		
		return null;
	}
	
	// Whether any match of the pattern in one line overlaps one of the spans, which are positions in
	// the file: the line starts at lineStart.
	static bool MatchesAny(Regex regex, ReadOnlySpan<char> line, int lineStart, TextSpan[] spans)
	{
		foreach(var match in regex.EnumerateMatches(line)) {
			
			var found = new TextSpan(lineStart + match.Index, match.Length);
			
			foreach(var span in spans)
				if(span.OverlapsWith(found))
					
					return true;
		}
		
		return false;
	}
	
	/// <summary>
	///     The hits of one page as the answer shows them (#460): by file, and within a file by the
	///     declaration each hit is in. The hits come in file and line order, so the groups are runs.
	///     A file's path is written once, not once per hit, and ten hits in one method share the
	///     method's header: what the lines and the start of the declaration cost is taken back by
	///     what is no longer repeated.
	/// </summary>
	static SearchFileGroup[] Group(MatchResult[] hits)
	{
		var files = new List<SearchFileGroup>();
		
		for(var index = 0; index < hits.Length;) {
			
			var file    = hits[index].File;
			var members = new List<SearchDeclarationGroup>();
			
			while(index < hits.Length && hits[index].File == file) {
				
				var declaration = hits[index].Declaration;
				var lines       = new List<SearchHit>();
				
				for(; index < hits.Length && hits[index].File == file && hits[index].Declaration == declaration; index++)
					lines.Add(new(hits[index].Line, hits[index].Text, hits[index].Declares ? true : null));
				
				members.Add(declaration is { } found
					? new($"{found.Kind} {found.Name}".TrimEnd(), $"{found.FirstLine}-{found.EndLine}", $"{found.StartLine}:{found.StartColumn}", [..lines])
					: new(null, null, null, [..lines]));
			}
			
			files.Add(new(file, [..members]));
		}
		
		return [..files];
	}
	
	/// <summary>
	///     What the grouping of one page came to, for the log (#460): how many hits it lists, in how
	///     many files and declaration groups, how many of the groups name a declaration and how many
	///     hits declare what they are in, and the characters the same hits would have taken listed
	///     flat, each with its file. The grouped answer measured 9% larger than the flat one on
	///     sixty searches of one repository; with this beside the entry's own size, what it costs
	///     in real use can be read from the log, per search.
	/// </summary>
	static string GroupingNote(MatchResult[] hits)
	{
		if(hits.Length == 0)
			
			return "";
		
		var files    = 0;
		var groups   = 0;
		var named    = 0;
		var flat     = 0;
		
		for(var index = 0; index < hits.Length; index++) {
			
			var hit      = hits[index];
			var newFile  = index == 0 || hits[index - 1].File != hit.File;
			var newGroup = newFile || hits[index - 1].Declaration != hit.Declaration;
			
			files  += newFile ? 1 : 0;
			groups += newGroup ? 1 : 0;
			named  += newGroup && hit.Declaration is not null ? 1 : 0;
			
			// {"file":"…","line":N,"text":"…"}, and the comma after it.
			flat += 30 + hit.File.Length + hit.Line.ToString().Length + hit.Text.Length;
		}
		
		return $"{hits.Length} hit(s) in {files} file(s), {groups} group(s), {named} in a declaration, {hits.Count(hit => hit.Declares)} declaring; flat ~{flat} chars";
	}
	
	// One hit as it is kept between pages: the line, and the declaration it is in.
	private sealed class MatchResult
	{
		public required string File { get; init; }
		public required int Line { get; init; }
		public required string Text { get; init; }
		public EnclosingDeclaration? Declaration { get; init; }
		public bool Declares { get; init; }
	}
}
