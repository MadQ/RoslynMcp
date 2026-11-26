using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ReplaceInFileTool : RoslynMcpTool
{
	readonly BackupStore backups;
	
	public ReplaceInFileTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache, BackupStore backups) : base(workspace, logger, paginationCache) { this.backups = backups; }
	
	[McpServerTool(Name = "roslyn_replace_in_file", Destructive = true, Title = "Replace In File", OpenWorld = false)]
		[Description(
		"Use for text-level find/replace in any file type — C# source, JSON, XML, .csproj, Markdown, plain text. " +
		"Interpretation of the pattern is controlled by 'mode' (default literal); regex replacements support $1/$2 backreferences. " +
		"Glob mode ('*'/'?') is match-only — the replacement text is always inserted literally. " +
		"A literal pattern must match exactly once: when it matches several times, nothing is replaced and the answer lists the matches " +
		"(in a C# file, each under the declaration it is in) — make the pattern longer so that it matches once, name the declaration with inMember, " +
		"or pass replaceAll: true when every match is meant. A regex or glob pattern replaces every match. " +
		"In a C# file the result is parsed before it is written: an edit that adds a syntax error (an unbalanced brace, a missing semicolon) " +
		"is refused with the parser's message and its line, and nothing is written. Errors the file already had do not count, " +
		"and allowSyntaxErrors: true writes anyway, for an edit that is one half of a change the next edit completes. " +
		"The XML files a project is evaluated from (.csproj, .props, .targets, .slnx, .ruleset, .resx, nuget.config, packages.config) are checked the same way: " +
		"an edit that leaves a well-formed file as malformed XML is refused, quoting the XML parser, since such a file fails the next workspace load, not the edit. " +
		"To replace a whole member or statement by its kind and name, use roslyn_replace_in_code; " +
		"for inserting new lines without replacing existing content, use roslyn_insert_lines. " +
		"Returns the number of replacements made and changed_line_ranges as inclusive { start, end } 1-based line ranges; start equals end for a single changed line. " +
		"A write also returns backup_token: pass it to roslyn_local_history (action: apply) to put the file back as it was before the edit. " +
		"Literal patterns are matched case-sensitively and whitespace-exactly — verify the exact text with " +
		"roslyn_read_file or roslyn_search_files before attempting a replacement if unsure of the content. " +
		"Line endings in the replacement text are normalized to match the file's existing style by default (normalizeLineEndings=true). " +
		"Supports dryRun=true to preview what would change without writing; a dry run of a C# edit runs the same syntax check."
	)]
	public async Task<object> ReplaceInFile(
		[Description("Relative path to the file from the workspace root.")                                                ] string  filePath,
		[Description("The pattern to find in file CONTENT. Interpretation is controlled by 'mode' (default literal). This is not a filename filter.")               ] string  pattern,
		[Description("The replacement text. Supports $1/$2 backreferences when mode is 'regex'; inserted literally for 'literal' and 'glob' modes.")                ] string  replacement,
		[Description(OptionalProjectPathDescription)] string? projectPath = null,
		[Description(MatchModeDescription + "Default: 'literal'.")                                                        ] string? mode      = null,
		[Description("DEPRECATED — use mode:\"regex\" instead. Treat pattern as a regular expression. Ignored when 'mode' is set.")                                 ] bool    useRegex  = false,
		[Description("Preview replacements without writing the file. Returns what would change. Default: false.")         ] bool    dryRun    = false,
		[Description("Case-sensitive matching. Default: true.")                                                           ] bool    caseSensitive = true,
		[Description(
			"Match line endings in the replacement text to the file's existing style (CRLF or LF). " +
			"Default: true — prevents mixed line endings in the file. " +
			"Set false only if your replacement text already has the correct line endings."
		)] bool normalizeLineEndings = true,
		[Description(
			"C# files only: count only the matches inside this declaration, e.g. 'PruneExpired' or 'BackupStore.PruneExpired' " +
			"(a method, property, field, constructor or type of the file, with the comment attached to it; overloads of the name all count). " +
			"The short way to say which of several identical lines is meant. Default: the whole file."
		)] string? inMember = null,
		[Description("Replace every match of a literal pattern. Default: false — a literal pattern that matches more than once replaces nothing and the matches are listed. Regex and glob patterns always replace every match.")] bool replaceAll = false,
		[Description("C# files only: write the file even when the edit adds syntax errors; they are then named in _caution. Default: false — such an edit is refused.")] bool allowSyntaxErrors = false
	)
	{
		using var scope = BeginTool("roslyn_replace_in_file", filePath, new { pattern, mode, useRegex, caseSensitive, dryRun, normalizeLineEndings, inMember, replaceAll, allowSyntaxErrors });
		
		if(!TryResolveProjectArg(projectPath, filePath, out projectPath, out var projectError))
			
			return scope.Error(projectError);
		
		if(!TryResolveFileContext(projectPath, out var rootPath, out var boundary, out var resolveError))
			
			return scope.Error(resolveError);
		
		var fullPath = ResolveFilePath(filePath, rootPath, boundary, out var ambiguousFile);
		
		if(ambiguousFile is not null)
			
			return scope.Error(ambiguousFile);
		
		if(fullPath is null)
			
			return scope.Failed("file not found", new ErrorResult($"File not found: {filePath}"));
		
		if(!TryResolveMatchMode(mode, useRegex, "useRegex", MatchMode.Regex, MatchMode.Literal, out var matchMode, out var modeCaution, out var modeError))
			
			return scope.Error(new ErrorResult(modeError));
		
		if(string.IsNullOrWhiteSpace(inMember))
			inMember = null;
		
		// What the file is goes by the file the name leads to, as in a batch: a link named
		// 'build.txt' to a project file is a project file, and its edits are checked as one.
		var physical = boundary.ResolveAllowed(fullPath) ?? fullPath;
		var isCSharp = IsCSharpSourcePath(physical);
		
		if(inMember is not null && !isCSharp)
			
			return scope.Error(new ErrorResult("inMember names a C# declaration, and this is not a C# file (.cs).", "Leave inMember out, and make the pattern longer so that it matches once."));
		
		// In every entry, not only a failed one's arguments: whether edits to C# are one-line
		// literals or patterns across lines is what says which other tool could have made them (#449).
		// The size of what is written with them (#476): the pattern says how an edit finds its place,
		// and only the replacement says how large the edit is.
		scope.Record(matchMode.ToString().ToLowerInvariant());
		scope.Record($"replacement {replacement.Length} chars, {replacement.Count(c => c == '\n') + 1} line(s)");
		
		if(inMember is not null)
			scope.Record("inMember");
		
		if(replaceAll)
			scope.Record("replaceAll");
		
		Regex regex;
		
		try {
			regex = BuildContentRegex(pattern, matchMode, caseSensitive);
		}
		catch(ArgumentException ex) {
			return scope.Error(new ErrorResult($"Invalid regex pattern: {ex.Message}"));
		}
		
		string originalContent;
		
		try {
			originalContent = FileReader.ReadAllText(fullPath, FileReader.DocumentLimit);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			return scope.Error(ReadFailure(ex));
		}
		
		// A C# file is parsed as its project parses it, where it has one: the syntax check compares
		// the errors before and after, and a language version or a symbol the project defines
		// decides what is one. A file of no project is parsed as permissively as the parser can.
		SourceText? sourceText = null;
		SyntaxTree? tree       = null;
		
		if(isCSharp) {
			
			sourceText = SourceText.From(originalContent);
			tree       = CSharpSyntaxTree.ParseText(sourceText, ParseOptionsOf(projectPath, fullPath, physical), fullPath);
		}
		
		var plan = Plan(filePath, originalContent, tree, sourceText, regex, matchMode, replacement, normalizeLineEndings, inMember, replaceAll, dryRun, modeCaution);
		
		foreach(var note in plan.Notes)
			scope.Record(note);
		
		if(plan.Stop is { } stop)
			
			return stop.Kind is EditStopKind.Failed ? scope.Failed(stop.Detail, stop.Answer)
				: stop.Kind is EditStopKind.Error   ? scope.Error(stop.Answer)
				: scope.Outcome(stop.Detail, stop.Answer)
			;
		
		var newContent = EditPlan.Apply(originalContent, plan.Changes);
		
		// Before the dry-run exit, so that a dry run's verdict is the write's (#267).
		if(EditChecks.AddedErrors(physical, tree, originalContent, newContent) is { Length: > 0 } added) {
			
			var quoted = Quoted(added);
			
			if(!allowSyntaxErrors)
				
				return scope.Failed("would add syntax errors", new ErrorResult(
					$"The edit would add {added.Length} syntax error(s) to {filePath}, and nothing was written. In the file as it would be: {quoted}.",
					"Correct the replacement text. When this edit is one half of a change that the next edit completes, pass allowSyntaxErrors: true."));
			
			scope.Record($"{added.Length} syntax error(s) allowed");
			
			var allowed = $"The edit added {added.Length} syntax error(s): {quoted}.";
			
			modeCaution = modeCaution is null ? allowed : $"{modeCaution} {allowed}";
		}
		
		if(dryRun)
			
			return scope.Outcome("dry run", new ReplaceInFileResult(false, plan.MatchCount, plan.ChangedLines, Message: $"Dry run: {plan.MatchCount} replacement(s) would be made.") { Caution = modeCaution });
		
		var newBytes = FileWriter.Utf8NoBom.GetBytes(newContent);
		
		var (backupToken, backupErr) = await SaveBackupsAsync(
			backups, fullPath, projectPath, "roslyn_replace_in_file", newBytes);
		
		if(backupErr is not null)
			
			return scope.Error(backupErr);
		
		try {
			
			await workspace.ApplyTextChange(projectPath, fullPath, SourceText.From(newContent, FileWriter.Utf8NoBom));
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			return scope.Error(new ErrorResult($"Write failed — '{filePath}' may be in an inconsistent state: {ex.Message}.", BackupRecoveryHint(filePath)));
		}
		
		if(CheckForTruncation(filePath, fullPath, newContent.Length) is { } truncErr)
			
			return scope.Error(truncErr);
		
		return scope.Outcome(plan.Detail, new ReplaceInFileResult(true, plan.MatchCount, plan.ChangedLines) { BackupToken = backupToken, Caution = modeCaution });
	}
	
	/// <summary>
	///     The core of the tool (#475): what a pattern and a replacement do to a text, as changes to
	///     that text, with nothing read or written. The tool calls it for its one edit, and
	///     <c>roslyn_multi_edit</c> for each such edit of a batch, against the file as it was.
	///     <paramref name="tree"/> and <paramref name="sourceText"/> are those of a C# file and null
	///     for any other.
	/// </summary>
	internal static TextReplacePlan Plan(
		string filePath, string content, SyntaxTree? tree, SourceText? sourceText,
		Regex regex, MatchMode matchMode, string replacement, bool normalizeLineEndings,
		string? inMember, bool replaceAll, bool dryRun, string? modeCaution)
	{
		static TextReplacePlan Stopped(EditStopKind kind, string detail, ToolResult answer) => new() { Stop = new(kind, detail, answer) };
		
		if(inMember is not null && tree is null)
			
			return Stopped(EditStopKind.Error, "", new ErrorResult("inMember names a C# declaration, and this is not a C# file (.cs).", "Leave inMember out, and make the pattern longer so that it matches once."));
		
		// Split into lines to compute 1-based line numbers for each match position.
		var lines        = content.Split('\n')
		;
		var lineStarts   = BuildLineStartMap(lines);
		Match[] matches  = [..regex.Matches(content).Cast<Match>()];
		var inFile       = matches.Length;
		
		if(inMember is not null) {
			
			var root  = tree!.GetRoot();
			var named = EnclosingDeclaration.Named(root, sourceText!, inMember);
			
			if(named.Length == 0) {
				
				var declared = EnclosingDeclaration.All(root, sourceText!).Select(declaration => declaration.Name).Distinct().ToArray();
				
				return Stopped(EditStopKind.Failed, "inMember not found", new ErrorResult(
					$"No declaration named '{inMember}' in {filePath}.",
					declared.Length == 0
						? "The file declares nothing."
						: $"The file declares: {string.Join(", ", declared.Take(MaxListed))}{(declared.Length > MaxListed ? $", and {declared.Length - MaxListed} more" : "")}. Write a name as 'Member' or 'Type.Member'."));
			}
			
			// A match that reaches over the edge of the declaration is one of it: a snippet that
			// ends in the line break after the closing brace, or starts on the line above, is how
			// an agent quotes a member's end or its start. Left out, it was reported as a match
			// "elsewhere in the file".
			matches = [..matches.Where(match => named.Any(declaration => match.Length == 0
				? declaration.Span.Start <= match.Index && match.Index <= declaration.Span.End
				: match.Index < declaration.Span.End && match.Index + match.Length > declaration.Span.Start))];
		}
		
		int[] changedLines = [..
			matches
				.Select(m => GetLineNumber(lineStarts, m.Index))
				.Distinct()
				.Order()
		];
		
		if(matches.Length == 0) {
			
			var nothing = inMember is not null && inFile > 0
				? $"No matches in {inMember}; the pattern matches {inFile} time(s) elsewhere in the file."
				: "No matches found.";
			
			return Stopped(EditStopKind.Failed, "No matches found.", new ReplaceInFileResult(false, 0, [], nothing) { Caution = modeCaution });
		}
		
		// A literal snippet is one place to the agent that wrote it. Where it is several, replacing
		// them all changed lines nobody looked at (#476); the rule is the one of the edit tool every
		// model knows. A dry run gets the same answer, since its verdict is the write's (#267). A
		// regex or a glob says "every place like this" by being one.
		if(matchMode is MatchMode.Literal && matches.Length > 1 && !replaceAll) {
			
			var narrowing = tree is not null && inMember is null
				? "make the pattern longer so that it matches once, name the declaration with inMember"
				: "make the pattern longer so that it matches once";
			
			return Stopped(EditStopKind.NotApplied, "multiple matches", new ReplaceInFileResult(false, matches.Length, changedLines,
				$"{(dryRun ? "Dry run: matched" : "Matched")} {matches.Length} places, and {(dryRun ? "a write would replace nothing" : "nothing was replaced")}: {narrowing}, or pass replaceAll: true to replace all {matches.Length}.")
			{
				Caution = modeCaution,
				Matches = GroupMatches(matches, lines, lineStarts, tree, sourceText),
			});
		}
		
		var effectiveReplacement = normalizeLineEndings ? NormalizeLineEndings(replacement, content) : replacement;
		
		// Regex mode honors $1/$&/${name} substitution in the replacement text; literal and glob modes
		// insert the replacement VERBATIM, so a '$' (or '$1', '$&', '$$') in it stays literal in those
		// modes. Match by match and not with Regex.Replace: with inMember, only some of the pattern's
		// matches are replaced, and a batch puts the changes of several edits together.
		return new() {
			
			Changes      = [..matches.Select(match => new TextChange(new TextSpan(match.Index, match.Length), matchMode is MatchMode.Regex ? match.Result(effectiveReplacement) : effectiveReplacement))],
			Detail       = $"{matches.Length} replacement(s)",
			Places       = matches.Length,
			MatchCount   = matches.Length,
			ChangedLines = changedLines,
		};
	}
	
	/// <summary>The parser's messages as an answer quotes them: the first few, and a count of the rest.</summary>
	internal static string Quoted(string[] added)
		=> $"{string.Join("; ", added.Take(MaxQuotedErrors))}{(added.Length > MaxQuotedErrors ? $"; and {added.Length - MaxQuotedErrors} more" : "")}"
	;
	
	
	// How many names, matches and parser messages an answer lists before it counts the rest.
	const int MaxListed       = 40;
	const int MaxQuotedErrors = 5;
	const int MaxMatchText    = 200;
	
	/// <summary>
	///     The syntax errors <paramref name="newContent"/> has that the file did not have before the
	///     edit, each as <c>line N: message (id)</c> with the line of the new text; empty when the
	///     edit leaves the file with no more errors than it had.
	///     <para>
	///         A file that already has errors must stay editable, also by an edit that repairs
	///         only one of them, and a repair can uncover an error the parser did not get to
	///         before (closing an unterminated comment shows the missing semicolon below it). So
	///         an edit is at fault only when the file has more errors afterwards; which ones to
	///         quote is then found by id and message, which hold no position and so survive the
	///         lines an edit moves. An edit that trades one error for another in a file that was
	///         already broken passes: the file is no worse, and nothing here can tell that
	///         from a repair.
	///     </para>
	/// </summary>
	internal static string[] AddedSyntaxErrors(SyntaxTree before, string newContent)
	{
		static string Key(Diagnostic error) => $"{error.Id}: {error.GetMessage()}";
		
		var known  = new Dictionary<string, int>(StringComparer.Ordinal);
		var had    = 0;
		
		foreach(var error in before.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)) {
			
			known[Key(error)] = known.GetValueOrDefault(Key(error)) + 1;
			had++;
		}
		
		var after = CSharpSyntaxTree.ParseText(newContent, (CSharpParseOptions) before.Options, before.FilePath);
		var has   = after.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
		
		if(has.Length <= had)
			
			return [];
		
		var added = new List<string>();
		
		foreach(var error in has) {
			
			if(known.GetValueOrDefault(Key(error)) > 0) {
				
				known[Key(error)]--;
				continue;
			}
			
			added.Add($"line {error.Location.GetLineSpan().StartLinePosition.Line + 1}: {error.GetMessage()} ({error.Id})");
		}
		
		return [..added];
	}
	
	// The matches as a text search lists its hits (#460): by the declaration each is in, where the
	// file is C#, so that the answer shows which of them is meant and what to name in inMember.
	static SearchDeclarationGroup[] GroupMatches(Match[] matches, string[] lines, int[] lineStarts, SyntaxTree? tree, SourceText? sourceText)
	{
		var groups  = new List<SearchDeclarationGroup>();
		var hits    = new List<SearchHit>();
		var root    = tree?.GetRoot();
		EnclosingDeclaration? current = null;
		
		void Close()
		{
			if(hits.Count == 0)
				
				return;
			
			groups.Add(current is { } declaration
				? new($"{declaration.Kind} {declaration.Name}", $"{declaration.FirstLine}-{declaration.EndLine}", $"{declaration.StartLine}:{declaration.StartColumn}", [..hits])
				: new(null, null, null, [..hits]));
			
			hits.Clear();
		}
		
		foreach(var match in matches.Take(MaxListed)) {
			
			var declaration = root is null ? null : EnclosingDeclaration.At(root, sourceText!, match.Index).Declaration;
			
			if(declaration != current) {
				
				Close();
				current = declaration;
			}
			
			var line = GetLineNumber(lineStarts, match.Index);
			var text = lines[line - 1].Trim();
			
			hits.Add(new(line, text.Length > MaxMatchText ? text[..MaxMatchText] + "..." : text, null));
		}
		
		Close();
		
		return [..groups];
	}
	
	// Builds a sorted array of character offsets where each line starts.
	private static int[] BuildLineStartMap(string[] lines)
	{
		var starts  = new int[lines.Length];
		var current = 0;
		
		for(int i = 0; i < lines.Length; i++) {
			
			starts[i]  = current;
			// +1 for the '\n' we split on. Files with bare '\r' line endings (rare, pre-Mac OS X)
				// would be off by one per line; that platform is not a supported target.
			current   += lines[i].Length + 1; // +1 for the '\n' we split on
		}
		
		return starts;
	}
	
	// Returns the 1-based line number for a given character offset.
	private static int GetLineNumber(int[] lineStarts, int charOffset)
	{
		var lo = 0;
		var hi = lineStarts.Length - 1;
		
		while(lo < hi) {
			
			var mid = (lo + hi + 1) / 2;
			
			if(lineStarts[mid] <= charOffset)
				lo = mid;
			else
				hi = mid - 1;
		}
		
		return lo + 1;
	}
}

/// <summary>The plan of one <c>roslyn_replace_in_file</c> edit: its changes, and what its answer reports.</summary>
internal sealed record TextReplacePlan : EditPlan
{
	public int   MatchCount   { get; init; }
	public int[] ChangedLines { get; init; } = [];
}
