using Microsoft.CodeAnalysis.CSharp;
using System.ComponentModel;
using System.IO.Enumeration;
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
		"(using directives, namespace lines) are in a group without those fields. A line longer than 400 characters is cut around its match, with '…' at each cut. " +
		"Use this when searching file content — finding all lines where a symbol name, pattern, or phrase appears. " +
		"For filename/path matching only (no content), use roslyn_list_files instead. " +
		"For C#-specific context filtering (find matches only in comments, strings, identifiers, or xmldocs), use roslyn_semantic_search instead. " +
		"By default searches the C# source files registered in the Roslyn workspace (from the loaded solution/project). " +
		"A filePattern that can match other names ('*.csproj', '*.{props,targets}', '*.md', '*.json', '*') also searches every other " +
		"text file under the workspace root — project and props files, config, documentation — the files roslyn_list_files lists, " +
		"with the same ignored directories (bin, obj, .git, node_modules, …). Such a file's group has source: 'disk' and no declaration fields; " +
		"its line numbers are the ones roslyn_read_file uses. Files that were not searched are counted in 'skipped': binary files, " +
		"files that could not be read, files with a line over 1,000,000 characters, links leading out of the workspace, " +
		"and files on one line of which the pattern ran for more than two seconds (a backtracking regex on a very long line). " +
		"Results are paged; pass page_token from a previous response to retrieve the next page.")]
	public async Task<object> SearchFiles(
		[Description("Pattern to match against the CONTENT of each source line. Interpretation is controlled by 'mode' (default regex). This is not a filename filter — use filePattern to restrict which files are searched.")] string pattern,
		CancellationToken cancellationToken,
		[Description(OptionalProjectPathDescription)] string? projectPath = null,
		[Description("Filename glob filter selecting WHICH files are searched (matches the file name, not content): e.g. '*.cs', '*Test.cs', '*{Glob,ListFiles}*.cs'. Supports *, ? and {a,b}. Default: '*.cs', the workspace's C# files. A pattern that is not limited to '.cs' names ('*.csproj', '*.md', 'Directory.Build.*', '*') also searches the other text files under the workspace root, from disk.")] string? filePattern = null,
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
		
		if(scope.TryServeCachedPage<MatchResult>(page_token, ref skip, ref take, 50, 200, out var cached)) {
			
			// A later page reports ranges as the first did, and a read can take up any of them.
			scope.Record(RangesNote(cached));
			
			return scope.Outcome("cached page", cached);
		}
		
		// The default, and every pattern that can only name a .cs file, is the search it always
		// was: the workspace's documents, and no walk of the disk (#327).
		// Over the root of a solution set there are no documents (#501): every file is read from
		// disk, the C# files too, which are parsed there for the declarations their hits are in.
		var overSet      = workspace.IsSolutionSet(projectPath);
		var searchesDisk = overSet || !NamesOnlyCSharp(filePattern);
		
		var matchMode = MatchMode.Regex;
		
		if(mode is not null && !TryParseMatchMode(mode, out matchMode, out var modeError))
			
			return scope.Error(new ErrorResult(modeError));
		
		// A line of a disk file may hold a million characters, and the pattern is the agent's: on a
		// line of source a backtracking pattern costs little, on a minified bundle it can run for
		// minutes, and nothing between two lines could cancel it. So a search that reads disk
		// files matches in linear time where the pattern allows it (#472), which nearly every
		// pattern an agent sends does, and gives each match a time limit for the pattern that
		// needs backtracking. A search of source alone keeps the backtracking engine: a plain name
		// is three to four times slower on the other.
		var backtracks = !searchesDisk;
		
		Regex BuildRegex(string text)
		{
			if(!searchesDisk)
				
				return BuildContentRegex(text, matchMode, caseSensitive);
			
			try {
				return BuildContentRegex(text, matchMode, caseSensitive, MatchTimeout, RegexOptions.NonBacktracking);
			}
			catch(NotSupportedException) {
				
				backtracks = true;
				
				return BuildContentRegex(text, matchMode, caseSensitive, MatchTimeout, RegexOptions.None);
			}
		}
		
		Regex regex;
		
		try {
			regex = BuildRegex(pattern);
		}
		catch(ArgumentException ex) {
			return scope.Error(new ErrorResult($"Invalid regex pattern: {ex.Message}"));
		}
		
		Solution? solution = null;
		
		if(!overSet && !TryResolveSolution(projectPath, out solution, out var solutionError))
			
			return scope.Error(solutionError);
		
		if(!TryResolveFileContext(projectPath, out var rootPath, out var boundary, out var rootError))
			
			return scope.Error(rootError);
		
		if(!GlobMatcher.TryCreateMatcher(filePattern, out var matchesFile, out var globError))
			
			return scope.Error(new ErrorResult(globError));
		
		var     skipped     = new SkippedFiles();
		var     diskFiles   = 0;
		string? stopCaution = null;

		var (allMatches, collectError) = await TryCollectAsync(regex);
		
		if(allMatches is null)
			
			return scope.Error(collectError!);
		
		// Chat-reference fallback (#228): a 'sym:'-prefixed pattern that matched nothing verbatim
		// is retried with the prefix stripped — literal 'sym:' searches stay verbatim-first.
		string? fallbackCaution = null;
		string? fallbackHint    = null;
		
		// Not after a pass that stopped early: it found nothing in the files it reached, and a
		// second pass would replace its caution and its counts with its own.
		if(allMatches.Count == 0 && stopCaution is null && HasChatSymbolRefPrefix(pattern, out var stripped)) {
			
			if(stripped.Length == 0)
				fallbackHint = ChatRefTruncatedHint;
			else {
				
				Regex? strippedRegex = null;
				
				try {
					strippedRegex = BuildRegex(stripped);
				}
				catch(ArgumentException) { }
				
				var (retry, retryError) = strippedRegex is null ? (null, null) : await TryCollectAsync(strippedRegex);
				
				// A second pass that failed says so. Answered as "both tried, nothing found", the
				// answer would carry the counts of a pass that did not finish.
				if(retryError is not null)
					
					return scope.Error(retryError);
				
				if(retry is { Count: > 0 }) {
					
					allMatches      = retry;
					fallbackCaution = ChatRefFallbackCaution(pattern, stripped);
				}
				else
					fallbackHint = ChatRefBothTriedHint(pattern, stripped);
			}
		}
		
		var allResults = allMatches.ToArray();
		var shape      = PageShape(ComposeCautions(fallbackCaution, AdhocCaution(projectPath), stopCaution), fallbackHint, skipped.Report());
		var result     = PaginateAndStore(allResults, ref skip, take, shape);
		
		var answer     = shape(result);
		
		scope.Record(GroupingNote(result.Items));
		scope.Record(RangesNote(answer));
		
		if(searchesDisk)
			scope.Record($"disk: {diskFiles} file(s) searched{skipped.Note()}; engine: {(backtracks ? "backtracking" : "linear")}");
		
		return scope.Outcome($"{result.Total} match(es)", answer);
		
		// The walk of the disk can fail as a whole, and so can the pattern on a line of source; the
		// first pass and the pass for a chat reference answer both the same way.
		async Task<(List<MatchResult>? Matches, ErrorResult? Error)> TryCollectAsync(Regex rx)
		{
			try {
				return (await CollectAsync(rx), null);
			}
			catch(RegexMatchTimeoutException) {
				return (null, new ErrorResult($"The pattern ran for more than {MatchTimeout.TotalSeconds:N0} seconds on one line of a source file.", "Simplify the pattern: nested quantifiers such as (a+)+ backtrack without end on a line they almost match."));
			}
			catch(Exception ex) when(ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException) {
				return (null, new ErrorResult($"Failed to enumerate files: {ex.Message}"));
			}
		}
		
		async Task<List<MatchResult>> CollectAsync(Regex rx)
		{
			var matches = new List<MatchResult>();
			// seenPaths prevents searching the same physical file twice in multi-targeted projects.
			var seenPaths = new HashSet<string>(SecurityBoundary.PathComparer)
			;
			
			// A second pass, for a chat reference tried without its prefix, counts from zero.
			skipped     = new SkippedFiles();
			diskFiles   = 0;
			stopCaution = null;
			
			foreach(var project in solution?.Projects ?? [])
				foreach(var document in project.Documents) {
					
					if(document.FilePath is null || !seenPaths.Add(document.FilePath))
						continue;
					
					var fileName = Path.GetFileName(document.FilePath);
					
					if(!matchesFile(fileName))
						continue;
					
					await SearchSourceAsync(document.FilePath, await document.GetTextAsync(cancellationToken), document.GetSyntaxRootAsync);
				}
			
			if(!searchesDisk)
				
				return matches;
			
			// Every other file roslyn_list_files would list (#327), in the same walk with the same
			// ignored directories. A .cs file the workspace tracks was searched above.
			var rules = IgnoreRules.ForRoot(rootPath, logger);
			var set   = overSet ? new SolutionSet.Listing(rootPath, logger) : null;
			
			var files = WorkspaceWalker.EnumerateFiles(
				rootPath,
				"*",
				recursive: true,
				(ref FileSystemEntry directory) => set?.IsPruned(WorkspaceWalker.RelativeParent(ref directory), directory.FileName)
					?? rules.IsPrunedFromListing(WorkspaceWalker.RelativeParent(ref directory), directory.FileName));
			
			foreach(var fullPath in files) {
				
				cancellationToken.ThrowIfCancellationRequested();
				
				if(!matchesFile(Path.GetFileName(fullPath)) || !seenPaths.Add(fullPath))
					continue;
				
				// The walk enters no linked directory, but a file can itself be a link, and one
				// that leads out of the workspace is not read.
				if(!boundary.IsPathAllowed(fullPath)) {
					
					skipped.OutsideWorkspace++;
					continue;
				}
				
				var relative = Path.GetRelativePath(rootPath, fullPath);
				var before   = matches.Count;
				
				// A text document the workspace tracks without it being source (a declared
				// AdditionalFiles item, an .editorconfig) is searched as the workspace has it, which
				// is what roslyn_read_file returns for it.
				try {
					
					// Source of a solution set, which no workspace was loaded for: the file as it is
					// on disk, parsed alone, which is all a hit's declaration takes.
					if(solution is null) {
						
						if(IsCSharpSourcePath(fullPath)) {
							
							var bytes  = await FileReader.ReadAllBytesAsync(fullPath, FileReader.DocumentLimit);
							var onDisk = SourceText.From(bytes, bytes.Length);
							
							await SearchSourceAsync(fullPath, onDisk, token => Task.FromResult<SyntaxNode?>(CSharpSyntaxTree.ParseText(onDisk, DiskParseOptions, cancellationToken: token).GetRoot(token)));
							
							diskFiles++;
							
							if(matches.Count >= MaxDiskHits) {
								
								stopCaution = $"The search stopped at {MaxDiskHits:N0} matching lines and did not reach every file: narrow the pattern or the filePattern.";
								
								return matches;
							}
							
							continue;
						}
					}
					else if(WorkspaceTextDocumentInfo.Resolve(solution, fullPath).GetDocument(solution) is { } tracked) {
						
						var text = await tracked.GetTextAsync(cancellationToken);
						
						foreach(var line in text.Lines)
							AddHit(text.ToString(line.Span), relative, line.LineNumber + 1, disk: false);
						
						continue;
					}
					
					using var reader = FileReader.OpenLines(fullPath);
					
					if(reader.LooksBinary()) {
						
						skipped.Binary++;
						continue;
					}
					
					while(reader.Read(out var line)) {
						
						if((reader.LineNumber & 0xFFF) == 0)
							cancellationToken.ThrowIfCancellationRequested();
						
						AddHit(line, relative, reader.LineNumber, disk: true);
						
						// Every hit is held until the last page is asked for, and a file on disk
						// has no size the workspace would have refused: a common word in a log of
						// a gigabyte is millions of lines.
						if(matches.Count >= MaxDiskHits) {
							
							stopCaution = $"The search stopped at {MaxDiskHits:N0} matching lines and did not reach every file: narrow the pattern or the filePattern.";
							
							return matches;
						}
					}
					
					diskFiles++;
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
					
					// Hits of a file that was not searched to its end are not reported: the answer
					// says the file was skipped, and must not also look like a search of it.
					matches.RemoveRange(before, matches.Count - before);
					
					if(ex is LineTooLongException)
						skipped.LongLine++;
					else
						skipped.Unreadable++;
				}
				catch(RegexMatchTimeoutException) {
					
					matches.RemoveRange(before, matches.Count - before);
					
					skipped.PatternTimeout++;
					
					// The limit is for one match, and a pattern that runs out on one long line runs
					// out on the next: a tree of two hundred such files would be minutes all the same.
					if(skipped.PatternTimeout == MaxPatternTimeouts) {
						
						stopCaution = $"The search stopped after the pattern ran for more than {MatchTimeout.TotalSeconds:N0} seconds on a line of {MaxPatternTimeouts} files, and did not reach every file: simplify the pattern (nested quantifiers such as (a+)+ backtrack without end) or narrow the filePattern.";
						
						return matches;
					}
				}
			}
			
			return matches;
			
			// The hits of one C# file, each under the declaration it is in.
			async Task SearchSourceAsync(string filePath, SourceText text, Func<CancellationToken, Task<SyntaxNode?>> parse)
			{
				var lines  = text.Lines;
				var source = text.ToString();
				
				// Parsed for a file with a hit only, and then once: most files of a search have none.
				SyntaxNode? root = null;
				
				for(int i = 0; i < lines.Count; i++) {
					
					var line = lines[i];
					
					// The line is one hit however often the pattern matches it. Which of its
					// matches says where the hit is, and under which declaration it is listed,
					// is EnclosingDeclaration.Among's rule: the first, unless a later one is a
					// declaration's name ('logger' matches inside 'FileLogger' before it
					// matches the field's name on the same line).
					// The rest of the line's matches are looked for only where the rule needs
					// them, which is not on a line inside a member's body: most hits.
					if(FirstMatch(rx, source.AsSpan(line.Start, line.Span.Length)) is not { } hit)
						continue;
					
					root ??= await parse(cancellationToken);
					
					var leftmost = new TextSpan(line.Start + hit.Index, hit.Length);
					
					var (declaration, declares, at) = root is null
						? (null, false, leftmost)
						: EnclosingDeclaration.Among(root, text, leftmost, (Regex: rx, Source: source, Line: line), static found => MatchesOf(found.Regex, found.Source.AsSpan(found.Line.Start, found.Line.Span.Length), found.Line.Start));
					
					matches.Add(new MatchResult {
						
						File        = Path.GetRelativePath(rootPath, filePath),
						Line        = i + 1,
						Text        = Excerpt(source.AsSpan(line.Start, line.Span.Length), at.Start - line.Start),
						Declaration = declaration,
						Declares    = declares,
					});
				}
			}
			
			void AddHit(ReadOnlySpan<char> line, string file, int number, bool disk)
			{
				if(FirstMatch(rx, line) is not { } hit)
					
					return;
				
				matches.Add(new MatchResult {
					
					File = file,
					Line = number,
					Text = Excerpt(line, hit.Index),
					Disk = disk,
				});
			}
		}
	}
	
	/// <summary>
	///     Whether a <c>filePattern</c> can only name C# files: each of its expansions ends in
	///     <c>.cs</c>. A pattern that is not expanded here is refused by the matcher made from it.
	/// </summary>
	static bool NamesOnlyCSharp(string filePattern) =>
		GlobBraces.TryExpand(filePattern, "filePattern", out var patterns, out _)
		&& patterns.All(pattern => pattern.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
	;
	
	// What a C# file of a solution set is parsed with: the newest syntax, since no project was
	// loaded to say which. A member inside #if is then under its type and not under itself.
	static readonly CSharpParseOptions DiskParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
	
	// How long one match of the pattern may run in a search that reads disk files.
	static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);
	
	// The most files the pattern may time out on before the search stops and says so.
	const int MaxPatternTimeouts = 5;
	
	// The most hits a search that reads disk files collects before it stops and says so.
	const int MaxDiskHits = 100_000;
	
	// The most characters of a line a hit shows, and how many of them come before the match.
	const int ExcerptLength = 400;
	const int ExcerptLead   = 100;
	
	/// <summary>
	///     The text of a hit: the line without its surrounding whitespace, or, of a line longer
	///     than <see cref="ExcerptLength"/>, that many characters around the match, with an
	///     ellipsis at each cut. A hit in a minified file is one line of a megabyte, and the
	///     answer is read by a model.
	/// </summary>
	internal static string Excerpt(ReadOnlySpan<char> line, int matchIndex)
	{
		var trimmed = line.Trim();
		
		if(trimmed.Length <= ExcerptLength)
			
			return trimmed.ToString();
		
		// The match's position in the trimmed line; a match within the leading whitespace starts it.
		var lead  = line.Length - line.TrimStart().Length;
		var index = Math.Max(0, matchIndex - lead);
		var start = Math.Clamp(index - ExcerptLead, 0, trimmed.Length - ExcerptLength);
		
		return string.Concat(
			start > 0 ? "…" : "",
			trimmed.Slice(start, ExcerptLength),
			start + ExcerptLength < trimmed.Length ? "…" : "");
	}
	
	/// <summary>The files a search over disk files left out, by reason (#327): counted, never left out unseen.</summary>
	sealed class SkippedFiles
	{
		public int Binary;
		public int Unreadable;
		public int LongLine;
		public int OutsideWorkspace;
		public int PatternTimeout;
		
		bool Any => Binary + Unreadable + LongLine + OutsideWorkspace + PatternTimeout > 0;
		
		public SearchSkipped? Report() => Any ? new(Binary, Unreadable, LongLine, OutsideWorkspace, PatternTimeout) : null;
		
		public string Note() => Any
			? $", skipped {Binary} binary, {Unreadable} unreadable, {LongLine} with a line too long, {OutsideWorkspace} linked out of the workspace, {PatternTimeout} on a pattern timeout"
			: "";
	}
	
	// Builds every page of one query, first and cached alike, so a cached page keeps the same fields
	// — the caution and hint included.
	static Func<PaginatedResult<MatchResult>, object> PageShape(string? caution, string? hint, SearchSkipped? skipped)
		=> page => new SearchFilesResult(
			Group(page.Items),
			page.Total,
			page.Items.Length,
			page.PageToken,
			page.HasMore)
		{
			Skipped = skipped,
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
	
	// The matches of the pattern in one line as positions in the file, in order: the line starts
	// at lineStart. All of them, as the check for a declaration's name always had them: asked
	// for only on the few lines where one of them can be such a name.
	static TextSpan[] MatchesOf(Regex regex, ReadOnlySpan<char> line, int lineStart)
	{
		var found = new List<TextSpan>();
		
		foreach(var match in regex.EnumerateMatches(line))
			found.Add(new(lineStart + match.Index, match.Length));
		
		return [..found];
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
			var source  = hits[index].Disk ? "disk" : null;
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
			
			files.Add(new(file, source, [..members]));
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
	
	// The most declaration ranges one log entry lists.
	const int LoggedRanges = 40;
	
	/// <summary>
	///     The declaration ranges one page reported, for the log: <c>ranges: Foo.cs 23-323, 562-689;
	///     Bar.cs 10-20</c>, by file name. With the <c>lines A-B</c> of the reads that follow it
	///     says whether a read took up a reported range; the entry's preview of the answer is cut
	///     after its first group and cannot. Read from the answer itself, so that a page served
	///     from the cache is noted as the first page was.
	/// </summary>
	static string RangesNote(object answer)
	{
		if(answer is not SearchFilesResult page)
			
			return "";
		
		var note   = new System.Text.StringBuilder();
		var listed = 0;
		var more   = 0;
		
		foreach(var file in page.Files) {
			
			var named = false;
			
			foreach(var group in file.In) {
				
				if(group.Lines is null)
					continue;
				
				if(listed == LoggedRanges) {
					
					more++;
					continue;
				}
				
				// The file is named at its first range, which need not be its first group.
				if(named)
					note.Append(", ");
				else
					note.Append(note.Length == 0 ? "ranges: " : "; ").Append(Path.GetFileName(file.File)).Append(' ');
				
				note.Append(group.Lines);
				
				named = true;
				listed++;
			}
		}
		
		if(more > 0)
			note.Append($" (+{more} more)");
		
		return note.ToString();
	}
	
	// One hit as it is kept between pages: the line, and the declaration it is in.
	private sealed class MatchResult
	{
		public required string File { get; init; }
		public required int Line { get; init; }
		public required string Text { get; init; }
		public EnclosingDeclaration? Declaration { get; init; }
		public bool Declares { get; init; }
		public bool Disk { get; init; }
	}
}
