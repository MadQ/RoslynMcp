using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

/// <summary>
///     Semantic C# code manipulation tool using Roslyn syntax trees.
///     Finds syntax nodes by kind, validates edits, preserves formatting.
///     Complements <see cref="ReplaceInFileTool"/> (text-level, any file type).
/// </summary>
[McpServerToolType]
internal sealed class ReplaceInCodeTool : RoslynMcpTool
{
	readonly BackupStore backups;
	
	public ReplaceInCodeTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache, BackupStore backups) : base(workspace, logger, paginationCache) { this.backups = backups; }
	
	[McpServerTool(Name = "roslyn_replace_in_code", Destructive = true, Title = "Replace In Code", OpenWorld = false)]
		[Description(
		"Prefer this tool for all C# edits — uses Roslyn syntax tree parsing to find, validate, and replace " +
		"C# syntax nodes by kind, preserving surrounding formatting trivia and the file's line-ending style " +
		"(replacement text is normalized to CRLF or LF to match the file — normalizeLineEndings=true by default). " +
		"Works on C# files only; for non-C# files or literal text replacement, use roslyn_replace_in_file instead; " +
		"for inserting new lines without replacing existing content, use roslyn_insert_lines instead. " +
		"Specify nodeKind (e.g., MethodDeclaration, ConstructorDeclaration, PropertyDeclaration, ClassDeclaration) and an optional textPattern " +
		"that filters by the declared name of the node — not body content. " +
		"Validates that the replacement text is syntactically valid C# before writing; rejects changes that would introduce errors. " +
		"The replacement must be exactly one node of the matched kind — each matched type member is parsed inside a type of the same name, so constructors, destructors, and operators are accepted as-is. " +
		"The node keeps its place: the blank lines before it and the indentation of the first line of the replacement text come from the file, further lines are written as given. " +
		"A comment in the replacement text before the node (a doc comment, say) replaces the comment attached to the node, and one at the end of its last line replaces the one there (after the comma, for an enum member); a replacement without a comment keeps the existing ones, so a body-only edit need not repeat the doc comment. " +
		"Only the node and its comments are written: preprocessor directives and anything on the lines below the node in the replacement text are not, and _caution names whatever was left out, with the line of the node. " +
		"To pick one of several similar nodes, such as the third if statement in a method, pass line (and column for several on one line): where the node starts, as a read of the file or a dry run's changed_nodes shows it. A declaration starts at its first attribute or modifier, not at its name. These narrow nodeKind and textPattern. " +
		"A position is only as good as the read it came from: after an edit moves lines it can land on a different node of the same kind, so re-read first, or add textPattern so that only a node with that text can match. " +
		"If multiple nodes match and force is false (default), returns the match list without applying — narrow textPattern, pass line and column, or set force=true to proceed. " +
		"Supports dryRun=true to preview which nodes would be replaced without writing; the replacement is parsed and validated exactly as for a real write, so a passing dry run reflects the same syntax checks the write would run. " +
		"Set verbose=true to also include each matched node's original_text in the response (omitted by default to save tokens; always included on error)."
	)]
	public async Task<object> ReplaceInCode(
		[Description("Relative path to the C# file from the workspace root.")] string filePath,
		[Description("Syntax node kind to match (e.g., 'MethodDeclaration', 'ConstructorDeclaration', 'FieldDeclaration', 'IdentifierName'). Common aliases accepted: 'method', 'constructor', 'ctor', 'field', 'property', 'class', 'interface', 'struct', 'enum', 'identifier'.")] string nodeKind,
		CancellationToken cancellationToken,
		[Description(OptionalProjectPathDescription)] string? projectPath = null,
		[Description("Optional pattern to filter matched nodes. For declaration nodes (Method/Constructor/Property/Field/Class etc.) matches the DECLARED NAME. For other nodes matches full text.")] string? textPattern = null,
		[Description("Replacement text for the matched node. Must be valid C# syntax for the target node kind. Default: empty string — omitting this deletes the matched node.")] string replacement = "",
		[Description("Preview changes without writing. Returns what would change, after the same parse and validation a real write performs. Default: false.")] bool dryRun = false,
		[Description("Apply even when multiple nodes match. Default: false — returns matches for review instead.")] bool force = false,
		[Description("When false (default), omits original node text from the response on success to reduce token usage. Set true to include the full original text of each matched node in changed_nodes[].original_text. Error responses always include the text regardless of this flag.")] bool verbose = false,
		[Description("Match line endings in the replacement text to the file's existing style (CRLF or LF). Default: true — prevents mixed line endings in the file. Set false only if your replacement text already has the correct line endings.")] bool normalizeLineEndings = true,
		[Description("Keep only nodes that start on this 1-based line. Default: 0 — any line.")] int line = 0,
		[Description("Keep only nodes that start at this 1-based column, in UTF-16 code units: a tab is one, an emoji two. A dry run's changed_nodes reports matching columns. Requires line. Default: 0 — any column.")] int column = 0
	)
	{
		using var scope    = BeginTool("roslyn_replace_in_code", filePath, new { nodeKind, textPattern, replacement = replacement.Length > 120 ? replacement[..120] + "…" : replacement, dryRun, force, normalizeLineEndings, line, column });
		
		if(!TryResolveProjectArg(projectPath, filePath, out projectPath, out var projectError))
			
			return scope.Error(projectError);
		
		if(!TryResolveFileContext(projectPath, out var rootPath, out var boundary, out var resolveError))
			
			return scope.Error(resolveError);
		
		var fullPath = ResolveFilePath(filePath, rootPath, boundary);
		
		if(fullPath is null)
			
			return scope.Failed("file not found", new ErrorResult($"File not found: {filePath}"));
		
		if(!IsCSharpSourcePath(fullPath))
			
			return scope.Error(new ErrorResult("File must be a C# source file (.cs)"));
		
		if(!TryParseSyntaxKind(nodeKind, out var kind))
			
			return scope.Error(new ErrorResult($"Unknown node kind: {nodeKind}.", "Examples: MethodDeclaration, ConstructorDeclaration, FieldDeclaration, IdentifierName."));
		
		if(line < 0 || column < 0 || (column > 0 && line == 0))
			
			return scope.Error(new ErrorResult($"Line {line}, column {column} is not a valid position.", "Both are 1-based, and column requires line."));
		
		SourceText sourceText;
		SyntaxTree syntaxTree;
		
		// Prefer the in-memory workspace document to avoid races with concurrent edits.
		if(!TryResolveSolution(projectPath, out var solution, out var solutionError))
			
			return scope.Error(solutionError);
		var docIds   = solution.GetDocumentIdsWithFilePath(fullPath);
		
		if(docIds.Length > 0) {
			
			var doc = solution.GetDocument(docIds[0])!;
			sourceText = await doc.GetTextAsync(cancellationToken);
			syntaxTree = (await doc.GetSyntaxTreeAsync(cancellationToken))!;
		}
		
		else {
			
			try {
				
				var bytes = await FileReader.ReadAllBytesAsync(fullPath, FileReader.DocumentLimit, cancellationToken);
				
				sourceText = SourceText.From(bytes, bytes.Length, FileWriter.Utf8NoBom);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				return scope.Error(ReadFailure(ex));
			}
			
			syntaxTree = CSharpSyntaxTree.ParseText(sourceText, path: fullPath);
		}
		
		var root		 = await syntaxTree.GetRootAsync(cancellationToken);
		var matchedNodes = root.DescendantNodes()
			.Where(n => n.IsKind(kind))
			.ToArray()
		;
		
		// Filter by text pattern if provided.
		// For declaration nodes, match against the declared name only — not the full body.
		// This prevents "ParseDocumentation" from matching every method that *calls* it.
		if(!string.IsNullOrWhiteSpace(textPattern)) {
			
			matchedNodes = matchedNodes
				.Where(n => {
					
					var name = GetDeclaredName(n);
					
					return name.Contains(textPattern, StringComparison.OrdinalIgnoreCase);
				})
				.ToArray()
			;
		}
		
		// One calculation for filtering and for changed_nodes, because the tool promises that a
		// position it reports can be passed back.
		LinePosition StartOf(SyntaxNode n) => syntaxTree.GetLineSpan(n.Span).StartLinePosition;
		
		if(line > 0) {
			
			matchedNodes = matchedNodes
				.Where(n => StartOf(n) is var start && start.Line + 1 == line && (column == 0 || start.Character + 1 == column))
				.ToArray()
			;
		}
		
		if(matchedNodes.Length == 0)
			
			return scope.Failed("No matching nodes found.", new ReplaceInCodeResult(false, 0, [], line > 0
				? $"No matching nodes found at line {line}{(column > 0 ? $", column {column}" : "")}. Check nodeKind, textPattern and the position: a dry run without line and column lists where the matches start."
				: "No matching nodes found."));
		
		// Local helper so error paths can always include the matched text (diagnostic info the agent needs),
		// while success paths omit it unless verbose=true to save tokens.
		ReplaceInCodeNodeInfo[] BuildNodeInfo(bool includeText) => matchedNodes.Select(n => {
			
			var start = StartOf(n);
			
			return new ReplaceInCodeNodeInfo(
				includeText ? n.ToString() : null,
				start.Line + 1,
				start.Character + 1
			);
		}).ToArray();
		
		var changedNodeInfo = BuildNodeInfo(verbose);
		
		// Safety guard: multiple matches require explicit opt-in via force=true. A dry run is
		// exempt — its job is to show what would match.
		// Nodes of one kind can start at the same place, as in 'a.b.c', and asking for a position
		// again would never end.
		if(matchedNodes.Length > 1 && !force && !dryRun) {
			
			var narrowing = (line, column) switch {
				
				(0, _) => "narrow textPattern or pass line and column to target one",
				(_, 0) => "add column or narrow textPattern to target one",
				_      => "narrow textPattern: these nodes start at the same position. If the text cannot tell them apart, replace the enclosing node or use roslyn_replace_in_file"
			};
			
			return scope.Outcome("multiple matches", new ReplaceInCodeResult(false, matchedNodes.Length, changedNodeInfo, $"Matched {matchedNodes.Length} nodes — set force=true to replace all, or {narrowing}."));
		}
		
		// Build the edited tree — deletion or replacement — and validate it BEFORE the dry-run
		// exit, so a dry run's verdict rests on the same checks a real write runs (#267).
		// Empty replacement = delete matched node(s). Documented behavior: omitting replacement removes the node.
		var        isDeletion = string.IsNullOrWhiteSpace(replacement);
		SyntaxNode newRoot;
		
		// What the replacement text held around its node that could not be put into the file. It
		// goes into the response: a part of an edit that is dropped must never be dropped unseen (#416).
		// By message, with the lines of the nodes it is true of: a forced replacement of several
		// nodes can leave some of them with their old comment, and the agent has to know which.
		var unapplied = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
		
		// Adopt the file's line-ending style before parsing, so the newlines inside the new node
		// and its comments (only the blank lines and indentation around it come from the file)
		// match the rest of the file (#268).
		var effectiveReplacement = normalizeLineEndings
			? NormalizeLineEndings(replacement, sourceText.ToString())
			: replacement
		;
		
		if(isDeletion)
			newRoot = root.RemoveNodes(matchedNodes, SyntaxRemoveOptions.KeepNoTrivia)!;
		
		else {
			
			// Parse the replacement in the grammatical position of EACH node it replaces — a type
			// member inside a type of the same name so constructors, destructors, and operators
			// parse as themselves; a statement as a statement; anything else as an expression.
			var replacementNodes  = new Dictionary<SyntaxNode, SyntaxNode>(matchedNodes.Length);
			var replacementTokens = new Dictionary<SyntaxToken, SyntaxToken>();
			
			foreach(var matchedNode in matchedNodes) {
				
				var (parsed, parseError) = ParseReplacement(matchedNode, effectiveReplacement);
				
				if(parsed is null)
					
					return scope.Error(new ReplaceInCodeSyntaxError(parseError ?? "Failed to parse replacement text — parser returned null", BuildNodeInfo(true))
					{
						Error = "Replacement text contains syntax errors"
					});
				
				var notes = new List<string>();
				
				replacementNodes[matchedNode] = WithTriviaOf(matchedNode, parsed, replacementTokens, notes);
				
				foreach(var note in notes) {
					
					if(!unapplied.TryGetValue(note, out var lines))
						unapplied[note] = lines = [];
					
					lines.Add(StartOf(matchedNode).Line + 1);
				}
			}
			
			// Tokens too: the comment after an enum member's comma belongs to the comma.
			newRoot = root.ReplaceSyntax(
				matchedNodes,           (originalNode, _) => replacementNodes[originalNode],
				replacementTokens.Keys, (originalToken, _) => replacementTokens[originalToken],
				null,                   null
			);
		}
		
		// Validate the text that will be written, parsed as the file is parsed — not the tree it was
		// put together from. The two can differ: a tree built from parts carries the diagnostics of
		// its parts, and what the parts amount to once they are text again (a directive that now
		// lacks its counterpart, a comment that swallows the rest of a line) only a parse shows.
		var newText = newRoot.ToFullString();
		var newTree = CSharpSyntaxTree.ParseText(newText, (CSharpParseOptions) syntaxTree.Options, fullPath);
		var newDiagnostics = newTree.GetDiagnostics()
			.Where(d => d.Severity == DiagnosticSeverity.Error)
			.ToArray()
		;
		
		if(newDiagnostics.Length > 0)
			
			return scope.Error(new ReplaceInCodeSyntaxError(
				string.Join("; ", newDiagnostics.Select(d => d.GetMessage())),
				BuildNodeInfo(true))
			{
				Error = isDeletion ? "Deletion would introduce syntax errors" : "Replacement would introduce syntax errors"
			});
		
		var verb    = isDeletion ? "deleted" : "replaced";
		var caution = unapplied.Count > 0
			? string.Join(" ", unapplied.Select(entry => $"{entry.Key} (Node at line {string.Join(", ", entry.Value)}.)"))
			: null
		;
		
		if(dryRun)
			
			return scope.Outcome("dry run", new ReplaceInCodeResult(false, matchedNodes.Length, changedNodeInfo, $"Dry run: {matchedNodes.Length} node(s) would be {verb}.") { Caution = caution });
		
		var newBytes = FileWriter.Utf8NoBom.GetBytes(newText);
		
		var (_, backupErr) = await SaveBackupsAsync(
			backups, fullPath, projectPath, "roslyn_replace_in_code", newBytes);
		
		if(backupErr is not null)
			
			return scope.Error(backupErr);
		
		// When the document is workspace-tracked, let Roslyn write it via TryApplyChanges
		// (MSBuild only — handles FSW suppression and encoding). Fall back to direct I/O
		// for untracked files (e.g. AdhocWorkspace or files outside the project).
		if(docIds.Length > 0) {
			
			var newDoc      = solution.GetDocument(docIds[0])!.WithSyntaxRoot(newRoot);
			var newSolution = newDoc.Project.Solution;
			
			if(!TryApplyEdit(projectPath, newSolution, out var applyError))
				
				return scope.Error(applyError);
			
			if(await TryRecoverTruncation(filePath, fullPath, projectPath, newBytes) is { } truncErr)
				
				return scope.Error(truncErr);
		}
		else {
			
			try {
				
				await workspace.WriteAndInvalidate(projectPath, fullPath,
					() => FileWriter.WriteAllTextAsync(fullPath, newText));
				
				if(CheckForTruncation(filePath, fullPath, newText.Length) is { } truncErr)
					
					return scope.Error(truncErr);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				return scope.Error(new ErrorResult($"Write failed — '{filePath}' may be in an inconsistent state: {ex.Message}.", BackupRecoveryHint(filePath)));
			}
		}
		
		return scope.Outcome($"{verb} {matchedNodes.Length} node(s)", new ReplaceInCodeResult(true, matchedNodes.Length, changedNodeInfo) { Caution = caution });
	}
	
	/// <summary>
	///     Gives the parsed replacement the trivia it has in the file: the blank lines and
	///     indentation of <paramref name="original"/>, so the node sits where the old one sat, and the
	///     comments of whichever of the two has them.
	///     <para>
	///         The trivia of the old node used to be taken whole. A replacement that began with a doc
	///         comment of its own then lost it without a word, and the old comment stayed above code
	///         it no longer described (#416). So a comment in the replacement text takes the place
	///         of the node's comments — text an agent sends is text it means to land — and a
	///         replacement without one keeps them, which is what makes an edit of the body alone
	///         cheap. The same holds for a comment at the end of the node's last line. After an
	///         enum member that line ends behind the comma, which is a token of the list and not of
	///         the member, so there the comment goes onto the file's comma, through
	///         <paramref name="tokens"/>.
	///     </para>
	///     What is not put into the file is said in <paramref name="notes"/>: a comment that cannot
	///     be placed safely, a preprocessor directive, and anything written below the node.
	/// </summary>
	private static SyntaxNode WithTriviaOf(SyntaxNode original, ParsedReplacement parsed, Dictionary<SyntaxToken, SyntaxToken> tokens, List<string> notes)
	{
		var node = parsed.Node.WithLeadingTrivia(LeadingTrivia(original.GetLeadingTrivia(), parsed.Node.GetLeadingTrivia(), notes));
		
		if(parsed.HasLeftover)
			notes.Add("What the replacement text holds below its node (a comment or a directive on a line after it) was not applied: it is not part of the node.");
		
		var separator = SeparatorAfter(original);
		
		if(separator.RawKind == 0)
			return node.WithTrailingTrivia(TrailingTrivia(original.GetTrailingTrivia(), parsed.Trailing, EndsFile(original.GetLastToken()), notes));
		
		tokens[separator] = separator.WithTrailingTrivia(TrailingTrivia(separator.TrailingTrivia, parsed.Trailing, EndsFile(separator), notes));
		
		return node.WithTrailingTrivia(original.GetTrailingTrivia());
	}
	
	// The comma after an enum member, when the file has one there; otherwise no token.
	private static SyntaxToken SeparatorAfter(SyntaxNode node)
	{
		if(node is not EnumMemberDeclarationSyntax { Parent: EnumDeclarationSyntax owner } member)
			return default;
		
		var index = owner.Members.IndexOf(member);
		
		return index < owner.Members.SeparatorCount ? owner.Members.GetSeparator(index) : default;
	}
	
	private static bool EndsFile(SyntaxToken token)
		=> token.GetNextToken(includeZeroWidth: true).IsKind(SyntaxKind.EndOfFileToken);
	
	private static SyntaxTriviaList LeadingTrivia(SyntaxTriviaList existing, SyntaxTriviaList incoming, List<string> notes)
	{
		// A directive is one half of a pair more often than not — #region, #if, a #pragma that is
		// restored — and the other half stands below the node, where it is not the node's. Half a
		// pair written is worse than none, and the node's own directive sent along would be
		// written twice.
		if(incoming.Any(IsDirectiveText))
			notes.Add("A preprocessor directive before the node in the replacement text was not applied: directives are not part of a node. Add or change them with roslyn_replace_in_file.");
		
		// From the first comment on: the whitespace before it is where the text happened to start,
		// and the file's own indentation stands in for it. What follows it is kept as written,
		// like the lines of the node itself.
		var incomingStart = IndexOf(incoming, IsComment);
		
		if(incomingStart < 0)
			return existing;
		
		var written       = WithoutDirectives(incoming.Skip(incomingStart));
		var existingStart = AttachedCommentStart(existing);
		
		// The node has no comment yet, so there is nothing to take the place of.
		if(existingStart < 0)
			return existing.AddRange(written);
		
		// A directive after the node's comment — the #if or #pragma between a doc comment and its
		// declaration — would go with the comment, and the file's conditional structure with it.
		if(existing.Skip(existingStart).Any(IsDirectiveText)) {
			
			notes.Add("The comment before the node in the replacement text was not applied: the existing comment is followed by a preprocessor directive, so it was left as it is. Edit the comment with roslyn_replace_in_file.");
			
			return existing;
		}
		
		return SyntaxFactory.TriviaList(existing.Take(existingStart).Concat(written));
	}
	
	private static SyntaxTriviaList TrailingTrivia(SyntaxTriviaList existing, SyntaxTriviaList incoming, bool endsFile, List<string> notes)
	{
		// The comment as written, and the file's own end of line. Only what stands on the node's
		// line: a parser may hang the lines below onto the node as well, and a comment down there
		// is no reason to take the one the file has on this line.
		var written = incoming.TakeWhile(trivia => !trivia.IsKind(SyntaxKind.EndOfLineTrivia)).ToArray();
		
		if(!written.Any(IsComment))
			return existing;
		var lineEnd = IndexOf(existing, trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia));
		
		if(lineEnd >= 0)
			return SyntaxFactory.TriviaList(written.Concat(existing.Skip(lineEnd)));
		
		// The last line of a file that does not end in a line break ends all the same.
		if(endsFile)
			return SyntaxFactory.TriviaList(written);
		
		// The node is followed by more code on its line, which a comment to the end of the line
		// would comment out.
		notes.Add("The comment after the node in the replacement text was not applied: the replaced node does not end its line.");
		
		return existing;
	}
	
	private static bool IsRegionDirective(SyntaxTrivia trivia)
		=> trivia.Kind() is SyntaxKind.RegionDirectiveTrivia or SyntaxKind.EndRegionDirectiveTrivia;
	
	// A directive, or the text a false #if switches off: what stands and falls with directives.
	private static bool IsDirectiveText(SyntaxTrivia trivia)
		=> trivia.IsDirective || trivia.IsKind(SyntaxKind.DisabledTextTrivia);
	
	// The trivia without its directives, and without the indentation of a directive's line: left
	// behind, that would be added to the indentation of the line after it.
	private static List<SyntaxTrivia> WithoutDirectives(IEnumerable<SyntaxTrivia> trivia)
	{
		var kept = new List<SyntaxTrivia>();
		
		foreach(var item in trivia) {
			
			if(!IsDirectiveText(item))
				kept.Add(item);
			
			else if(kept is [.., var before] && before.IsKind(SyntaxKind.WhitespaceTrivia))
				kept.RemoveAt(kept.Count - 1);
		}
		
		return kept;
	}
	
	// Whether the trivia holds anything but blank space.
	private static bool HasText(IEnumerable<SyntaxTrivia> trivia)
		=> trivia.Any(item => !item.IsKind(SyntaxKind.WhitespaceTrivia) && !item.IsKind(SyntaxKind.EndOfLineTrivia));
	
	// What a node's trailing trivia holds after the end of the node's own line.
	private static IEnumerable<SyntaxTrivia> BelowTheLine(SyntaxTriviaList trailing)
		=> trailing.SkipWhile(trivia => !trivia.IsKind(SyntaxKind.EndOfLineTrivia)).Skip(1);
	
	/// <summary>
	///     A replacement parsed: the node, the trivia after it up to the end of its line — past the
	///     comma, for an enum member — and whether the text went on below that line.
	/// </summary>
	private sealed record ParsedReplacement(SyntaxNode Node, SyntaxTriviaList Trailing, bool HasLeftover);
	
	/// <summary>
	///     Where the comment that belongs to a node begins in its leading trivia: the first comment of
	///     the lines directly above it, up to the nearest blank line. Everything before a node is its
	///     leading trivia, also a section heading three lines up or the licence text at the top of
	///     the file, and those are not the node's to lose when its doc comment is rewritten. A
	///     <c>#region</c> or <c>#endregion</c> ends them like a blank line; any other directive
	///     among them does not — <see cref="LeadingTrivia"/> decides what a directive inside the
	///     node's comment means. Returns -1 when no comment is attached.
	/// </summary>
	internal static int AttachedCommentStart(SyntaxTriviaList leading)
	{
		var start      = -1;
		var lineIsBare = false;
		
		for(var index = leading.Count - 1; index >= 0; index--) {
			
			var trivia = leading[index];
			
			if(trivia.IsKind(SyntaxKind.WhitespaceTrivia))
				continue;
			
			// A '///' comment and a directive hold the end of their own line, so one that is
			// followed by a line with nothing on it sits above a blank line like any other.
			var endsLine = trivia.IsKind(SyntaxKind.EndOfLineTrivia) || trivia.ToFullString() is [.., '\n' or '\r'];
			
			if(endsLine && lineIsBare)
				break;
			
			if(trivia.IsKind(SyntaxKind.EndOfLineTrivia)) {
				
				lineIsBare = true;
				
				continue;
			}
			
			if(IsComment(trivia))
				start = index;
			
			// #region and #endregion end the lines wherever they stand: they are headings of the
			// file's own, and what is written over one is the region's, not the comment of the
			// first member in it. Every other directive, and the text a false #if switches off,
			// is passed over, above the comment or below it, so that the caller sees it together
			// with the comment and can leave both alone.
			else if(IsRegionDirective(trivia) || !IsDirectiveText(trivia))
				break;
			
			lineIsBare = false;
		}
		
		return start;
	}
	
	private static bool IsComment(SyntaxTrivia trivia)
		=> trivia.Kind() is SyntaxKind.SingleLineCommentTrivia
			or SyntaxKind.MultiLineCommentTrivia
			or SyntaxKind.SingleLineDocumentationCommentTrivia
			or SyntaxKind.MultiLineDocumentationCommentTrivia
	;
	
	private static int IndexOf(SyntaxTriviaList list, Func<SyntaxTrivia, bool> matches)
	{
		for(var index = 0; index < list.Count; index++)
			if(matches(list[index]))
				return index;
		
		return -1;
	}
	
	
	private static string GetDeclaredName(SyntaxNode node) => node switch {
		
		MethodDeclarationSyntax      m  => m.Identifier.Text,
		ConstructorDeclarationSyntax c  => c.Identifier.Text,
		DestructorDeclarationSyntax  d  => d.Identifier.Text,
		PropertyDeclarationSyntax    p  => p.Identifier.Text,
		EventDeclarationSyntax       ev => ev.Identifier.Text,
		FieldDeclarationSyntax       f  => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? string.Empty,
		EventFieldDeclarationSyntax  ef => ef.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? string.Empty,
		EnumMemberDeclarationSyntax  em => em.Identifier.Text,
		DelegateDeclarationSyntax    dl => dl.Identifier.Text,
		ClassDeclarationSyntax       c  => c.Identifier.Text,
		InterfaceDeclarationSyntax   i  => i.Identifier.Text,
		StructDeclarationSyntax      s  => s.Identifier.Text,
		EnumDeclarationSyntax        e  => e.Identifier.Text,
		RecordDeclarationSyntax      r  => r.Identifier.Text,
		_                               => node.ToString()
	};
	
	/// <summary>
	///     Parses <paramref name="replacement"/> in the grammatical position of
	///     <paramref name="original"/>, so validation asks the right question of the parser. Each
	///     type member is parsed inside a dummy type that borrows THAT member's enclosing type name
	///     (and kind, for enum members) — <c>SyntaxFactory.ParseMemberDeclaration</c> alone has no
	///     enclosing type, so it reads <c>Widget(int size) { }</c> as a method missing its return
	///     type, and dispatching on a fixed list of node kinds sent constructors to
	///     <c>ParseExpression</c> outright (#267). Statements parse as statements, a using directive
	///     as a compilation unit, everything else as an expression. Exactly one node must come out:
	///     a replacement that smuggles in a second member, or closes the wrapper type early, is
	///     refused.
	///     Returns the node, or null plus the joined parser error messages.
	/// </summary>
	private static (ParsedReplacement? Parsed, string? Error) ParseReplacement(SyntaxNode original, string replacement)
	{
		SyntaxNode?             node;
		IEnumerable<Diagnostic> diagnostics;
		
		// What follows the node in the text: the rest of its line, and whether anything stands
		// below it. A wrapper puts both where the parser hangs them — after a comma, before the
		// closing brace — so each case reads them where they are.
		SyntaxTriviaList trailing;
		bool             hasLeftover;
		
		switch(original) {
			
			case MemberDeclarationSyntax { Parent: BaseTypeDeclarationSyntax enclosingType }: {
				
				var keyword = enclosingType is EnumDeclarationSyntax ? "enum" : "class";
				var unit    = SyntaxFactory.ParseCompilationUnit($"{keyword} {enclosingType.Identifier.Text}\n{{\n{replacement}\n}}");
				
				var members = unit.Members.Count == 1
					? unit.Members[0] switch {
						
						EnumDeclarationSyntax e => e.Members.Cast<MemberDeclarationSyntax>().ToArray(),
						TypeDeclarationSyntax t => t.Members.ToArray(),
						_                       => [],
					}
					: [];
				
				if(members.Length != 1)
					
					return (null, $"Replacement must be exactly one member declaration — parsed {members.Length}.");
				
				node        = members[0];
				diagnostics = unit.GetDiagnostics();
				
				var wrapper   = (BaseTypeDeclarationSyntax) unit.Members[0];
				var separator = wrapper is EnumDeclarationSyntax { Members.SeparatorCount: > 0 } wrapperEnum ? wrapperEnum.Members.GetSeparator(0) : default;
				
				trailing    = node.GetTrailingTrivia().AddRange(separator.TrailingTrivia);
				hasLeftover = HasText(BelowTheLine(trailing)) || HasText(wrapper.CloseBraceToken.LeadingTrivia);
				
				// The parser accepts any Identifier(...) { } inside a type as a constructor; only the
				// compiler objects later (CS1520) when the name differs from the type. Catch it here,
				// or a forced batch across differently named types writes a Widget constructor into
				// Gadget with a clean syntax check.
				var declaredTypeName = node switch {
					
					ConstructorDeclarationSyntax c => c.Identifier.ValueText,
					DestructorDeclarationSyntax  d => d.Identifier.ValueText,
					_                              => null,
				};
				
				var enclosingTypeName = enclosingType.Identifier.ValueText;
				
				if(declaredTypeName is not null && declaredTypeName != enclosingTypeName)
					
					return (null, $"Constructor or destructor name '{declaredTypeName}' must match the enclosing type '{enclosingTypeName}'.");
				break;
			}
			
			case MemberDeclarationSyntax:
				
				// Top-level: a type, delegate, or namespace member with no enclosing type to borrow.
				node        = SyntaxFactory.ParseMemberDeclaration(replacement);
				diagnostics = node?.GetDiagnostics() ?? [];
				trailing    = node?.GetTrailingTrivia() ?? default;
				hasLeftover = node is not null && GoesOnBelow(node, replacement);
				break;
			
			case StatementSyntax:
				
				node        = SyntaxFactory.ParseStatement(replacement);
				diagnostics = node.GetDiagnostics();
				trailing    = node.GetTrailingTrivia();
				hasLeftover = GoesOnBelow(node, replacement);
				break;
			
			case UsingDirectiveSyntax: {
				
				var unit = SyntaxFactory.ParseCompilationUnit(replacement);
				
				if(unit.Usings.Count != 1 || unit.Members.Count != 0)
					
					return (null, "Replacement must be exactly one using directive.");
				
				node        = unit.Usings[0];
				diagnostics = unit.GetDiagnostics();
				trailing    = node.GetTrailingTrivia();
				hasLeftover = HasText(BelowTheLine(trailing)) || HasText(unit.EndOfFileToken.LeadingTrivia);
				break;
			}
			
			default:
				
				node        = SyntaxFactory.ParseExpression(replacement);
				diagnostics = node.GetDiagnostics();
				trailing    = node.GetTrailingTrivia();
				hasLeftover = GoesOnBelow(node, replacement);
				break;
		}
		
		if(node is null)
			
			return (null, "Failed to parse replacement text — parser returned null.");
		
		var errors = diagnostics
			.Where(d => d.Severity == DiagnosticSeverity.Error)
			.Select(d => d.GetMessage())
			.ToArray()
		;
		
		return errors.Length > 0
			? (null, string.Join("; ", errors))
			: (new ParsedReplacement(node, trailing, hasLeftover), null)
		;
	}
	
	// For a node parsed from the replacement text itself: whether the text goes on after the
	// node's line. The parser either hangs the rest onto the node as trivia or leaves it
	// unread, so both are looked at.
	private static bool GoesOnBelow(SyntaxNode node, string replacement)
		=> HasText(BelowTheLine(node.GetTrailingTrivia()))
			|| node.FullSpan.End < replacement.Length && !string.IsNullOrWhiteSpace(replacement[node.FullSpan.End..])
	;
	
	private static bool TryParseSyntaxKind(string kindName, out SyntaxKind kind)
	{
		// Try exact match first
		if(Enum.TryParse<SyntaxKind>(kindName, ignoreCase: true, out kind))
			
			return true;
		
		// Common aliases/shortcuts
		kind = kindName.ToLowerInvariant() switch {
			
			"method" => SyntaxKind.MethodDeclaration,
			"constructor" => SyntaxKind.ConstructorDeclaration,
			"ctor" => SyntaxKind.ConstructorDeclaration,
			"field" => SyntaxKind.FieldDeclaration,
			"property" => SyntaxKind.PropertyDeclaration,
			"class" => SyntaxKind.ClassDeclaration,
			"interface" => SyntaxKind.InterfaceDeclaration,
			"struct" => SyntaxKind.StructDeclaration,
			"enum" => SyntaxKind.EnumDeclaration,
			"identifier" => SyntaxKind.IdentifierName,
			_ => SyntaxKind.None
		};
		
		return kind != SyntaxKind.None;
	}
}
