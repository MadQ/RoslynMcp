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
		"Replaces a whole C# syntax node — a method, a property, a type, a statement — named by its kind and its name, with no text of the old node to quote: " +
		"the tool for rewriting a member, replacing one member by several, or deleting one. For a change of a few lines inside a member, roslyn_replace_in_file costs less and checks the syntax of a C# file as well. " +
		"Uses Roslyn syntax tree parsing to find, validate, and replace " +
		"C# syntax nodes by kind, preserving surrounding formatting trivia and the file's line-ending style " +
		"(replacement text is normalized to CRLF or LF to match the file — normalizeLineEndings=true by default). " +
		"Works on C# files only; for non-C# files or literal text replacement, use roslyn_replace_in_file instead; " +
		"for inserting new lines without replacing existing content, use roslyn_insert_lines instead. " +
		"Specify nodeKind (e.g., MethodDeclaration, ConstructorDeclaration, PropertyDeclaration, ClassDeclaration) and an optional textPattern " +
		"that filters by the declared name of the node — not body content. A name written with its return type, its parameter list or its type ('void Foo', 'Foo(int a)', 'Type.Foo') is read as the name. " +
		"When nothing matches, candidates lists the nodes of that kind in the file with their names and positions. " +
		"Validates that the replacement text is syntactically valid C# before writing; rejects changes that would introduce errors, quoting the parser's message and its line in the replacement text. " +
		"The replacement must be exactly one node of the matched kind — each matched type member is parsed inside a type of the same name, so constructors, destructors, and operators are accepted as-is. " +
		"One exception: a member of a class, struct, interface or record may be replaced by several members, written one after another " +
		"(a method and a new overload beside it, a method and the helper it now calls): the first takes the old member's place, the rest follow as written. " +
		"The same holds for a type or delegate declared in a namespace or at file level, which several type declarations may replace. " +
		"The node keeps its place: the blank lines before it and the indentation of the first line of the replacement text come from the file, further lines are written as given. " +
		"A comment in the replacement text before the node (a doc comment, say) replaces the comment attached to the node, and one at the end of its last line replaces the one there (after the comma, for an enum member); a replacement without a comment keeps the existing ones, so a body-only edit need not repeat the doc comment. " +
		"Only the node and its comments are written: preprocessor directives and anything on the lines below the node in the replacement text are not, and _caution names whatever was left out, with the line of the node. " +
		"In a list of members, what stands between two of them (blank lines, comments, a directive) is written as given. " +
		"To pick one of several similar nodes, such as the third if statement in a method, pass line (and column for several on one line): where the node starts, as a read of the file or a dry run's changed_nodes shows it. A declaration starts at its first attribute or modifier, not at its name. These narrow nodeKind and textPattern. " +
		"A position is only as good as the read it came from: after an edit moves lines it can land on a different node of the same kind, so re-read first, or add textPattern so that only a node with that text can match. " +
		"If multiple nodes match and force is false (default), returns the match list without applying — narrow textPattern, pass line and column, or set force=true to proceed. " +
		"Supports dryRun=true to preview which nodes would be replaced without writing; the replacement is parsed and validated exactly as for a real write, so a passing dry run reflects the same syntax checks the write would run. " +
		"Set verbose=true to also include each matched node's original_text in the response (omitted by default to save tokens; always included on error). " +
		"A write also returns backup_token, when the backup was saved: pass it to roslyn_local_history (action: apply) to put the file back as it was before the edit."
	)]
	public async Task<object> ReplaceInCode(
		[Description("Relative path to the C# file from the workspace root.")] string filePath,
		[Description("Syntax node kind to match (e.g., 'MethodDeclaration', 'ConstructorDeclaration', 'FieldDeclaration', 'IdentifierName'). Common aliases accepted: 'method', 'constructor', 'ctor', 'field', 'property', 'class', 'interface', 'struct', 'enum', 'identifier'.")] string nodeKind,
		CancellationToken cancellationToken,
		[Description(OptionalProjectPathDescription)] string? projectPath = null,
		[Description("Optional pattern to filter matched nodes. For declaration nodes (Method/Constructor/Property/Field/Class etc.) matches the DECLARED NAME ('Foo'; 'void Foo', 'Foo(int a)' and 'Type.Foo' are read as that name). For other nodes matches full text.")] string? textPattern = null,
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
		
		var fullPath = ResolveFilePath(filePath, rootPath, boundary, out var ambiguousFile);
		
		if(ambiguousFile is not null)
			
			return scope.Error(ambiguousFile);
		
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
		
		var plan = Plan(await syntaxTree.GetRootAsync(cancellationToken), sourceText, fullPath, kind, textPattern, replacement, dryRun, force, verbose, normalizeLineEndings, line, column, validateFile: true);
		
		foreach(var note in plan.Notes)
			scope.Record(note);
		
		if(plan.Stop is { } stop)
			
			return stop.Kind is EditStopKind.Failed ? scope.Failed(stop.Detail, stop.Answer)
				: stop.Kind is EditStopKind.Error   ? scope.Error(stop.Answer)
				: scope.Outcome(stop.Detail, stop.Answer)
			;
		
		var newRoot = plan.NewRoot!;
		var newText = plan.NewText!;
		
		if(dryRun)
			
			return scope.Outcome("dry run", new ReplaceInCodeResult(false, plan.Count, plan.Nodes, $"Dry run: {plan.Count} node(s) would be {plan.Verb}.") { Caution = plan.Caution });
		
		var newBytes = FileWriter.Utf8NoBom.GetBytes(newText);
		
		var (backupToken, backupErr) = await SaveBackupsAsync(
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
				
				return scope.Error(NamingBackup(applyError, backupToken));
			
			if(await TryRecoverTruncation(filePath, fullPath, projectPath, newBytes) is { } truncErr)
				
				return scope.Error(NamingBackup(truncErr, backupToken));
		}
		else {
			
			try {
				
				await workspace.WriteAndInvalidate(projectPath, fullPath,
					() => FileWriter.WriteAllTextAsync(fullPath, newText));
				
				if(CheckForTruncation(filePath, fullPath, newText.Length) is { } truncErr)
					
					return scope.Error(NamingBackup(truncErr, backupToken));
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				return scope.Error(NamingBackup(new ErrorResult($"Write failed — '{filePath}' may be in an inconsistent state: {ex.Message}.", BackupRecoveryHint(filePath)), backupToken));
			}
		}
		
		return scope.Outcome(plan.Detail, new ReplaceInCodeResult(true, plan.Count, plan.Nodes) { BackupToken = backupToken, Caution = plan.Caution });
	}
	
	/// <summary>
	///     The core of the tool (#475): which nodes of <paramref name="root"/> the arguments name,
	///     and the tree and text with them replaced or deleted, validated as the text that would
	///     be written. Nothing is read or written. The tool calls it for its one edit, and
	///     <c>roslyn_multi_edit</c> for each such edit of a batch, against the file as it was; for
	///     the batch the result is also given as the one change to the text it amounts to.
	///     <paramref name="validateFile"/> is the tool's own check that the file as this edit
	///     leaves it has no syntax error; a batch checks the file as all its edits leave it
	///     instead, and so passes false. The replacement text itself must parse either way.
	/// </summary>
	internal static CodeReplacePlan Plan(
		SyntaxNode root, SourceText sourceText, string fullPath, SyntaxKind kind, string? textPattern, string replacement,
		bool dryRun, bool force, bool verbose, bool normalizeLineEndings, int line, int column, bool validateFile)
	{
		static CodeReplacePlan Stopped(EditStopKind stopKind, string detail, ToolResult answer, List<string> notes) => new() { Stop = new(stopKind, detail, answer), Notes = notes };
		
		var syntaxTree   = root.SyntaxTree;
		var logNotes     = new List<string>();
		var matchedNodes = root.DescendantNodes()
			.Where(n => n.IsKind(kind))
			.ToArray()
		;
		
		// Filter by text pattern if provided.
		// For declaration nodes, match against the declared name only — not the full body.
		// This prevents "ParseDocumentation" from matching every method that *calls* it.
		var ofKind = matchedNodes;
		
		// Set when textPattern matched nothing as written and was read as the name in it.
		string? readAsName = null;
		
		if(!string.IsNullOrWhiteSpace(textPattern)) {
			
			matchedNodes = matchedNodes
				.Where(n => {
					
					var name = GetDeclaredName(n);
					
					return name.Contains(textPattern, StringComparison.OrdinalIgnoreCase);
				})
				.ToArray()
			;
			
			// A declaration is found by its name, and an agent writes the name as it read it: with
			// the return type before it, the parameter list behind it, or its type in front
			// ('void Foo', 'Foo(int a)', 'Type.Foo'). Taken as written first, so nothing that
			// matched before matches differently; only a pattern that found nothing is read again
			// as the name it holds (#476).
			if(matchedNodes.Length == 0 && EnclosingDeclaration.NameSegments(textPattern) is { Length: > 0 } segments) {
				
				matchedNodes = ofKind
					.Where(n => DeclaredName(n) is { } name
						&& name.Equals(segments[^1], StringComparison.OrdinalIgnoreCase)
						&& (segments.Length == 1
							|| n.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text.Equals(segments[^2], StringComparison.OrdinalIgnoreCase) == true
							|| string.Equals(EnclosingDeclaration.ExplicitInterface(n), segments[^2], StringComparison.OrdinalIgnoreCase)))
					.ToArray()
				;
				
				if(matchedNodes.Length > 0) {
					
					readAsName = string.Join('.', segments);
					logNotes.Add($"textPattern read as the name '{readAsName}'");
				}
			}
		}
		
		var named = matchedNodes;
		
		// One calculation for filtering and for changed_nodes, because the tool promises that a
		// position it reports can be passed back.
		LinePosition StartOf(SyntaxNode n) => syntaxTree.GetLineSpan(n.Span).StartLinePosition;
		
		if(line > 0) {
			
			matchedNodes = matchedNodes
				.Where(n => StartOf(n) is var start && start.Line + 1 == line && (column == 0 || start.Character + 1 == column))
				.ToArray()
			;
		}
		
		if(matchedNodes.Length == 0) {
			
			// What is there instead (#476): the server knows every node of the kind in the file,
			// and "no matching nodes" alone sent the agent to the text tool for the rest of its
			// session. With a position that missed, the nodes the kind and the pattern do match;
			// otherwise every node of the kind.
			var missedByPosition = line > 0 && named.Length > 0;
			var there            = missedByPosition ? named : ofKind;
			
			ReplaceInCodeCandidate[] candidates = [..there.Take(MaxCandidates).Select(n => {
				
				var start = StartOf(n);
				var name  = DeclaredName(n) ?? Brief(n.ToString());
				
				return new ReplaceInCodeCandidate(name, start.Line + 1, start.Character + 1);
			})];
			
			var position = line > 0 ? $" at line {line}{(column > 0 ? $", column {column}" : "")}" : "";
			var rest     = there.Length > candidates.Length ? $" (the first {candidates.Length} of {there.Length})" : "";
			
			var message = (there.Length, missedByPosition) switch {
				
				(0, _)    => $"No matching nodes found{position}: the file holds no {kind}.",
				(_, true) => $"No matching nodes found{position}. The nodes that match nodeKind and textPattern start where candidates says{rest}: a position is only as good as the read it came from.",
				_         => $"No matching nodes found{position}. candidates lists the {kind} nodes of the file{rest}."
					+ (!string.IsNullOrWhiteSpace(textPattern) && ofKind.Any(n => DeclaredName(n) is not null)
						? " textPattern is compared with the declared name only, not with the signature or the body."
						: ""),
			};
			
			return Stopped(EditStopKind.Failed, "No matching nodes found.", new ReplaceInCodeResult(false, 0, [], message) { Candidates = candidates.Length > 0 ? candidates : null }, logNotes);
		}
		
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
		// A pattern read as a name has lost its parameter list, so 'Foo(int a)' names every
		// overload of Foo. Several such nodes are never replaced, with force or without: one
		// signature would replace them all. A dry run with force is told the same, since its
		// verdict is the write's; one without lists the nodes, as every dry run does.
		if(matchedNodes.Length > 1 && readAsName is not null && (force || !dryRun))
			
			return Stopped(EditStopKind.NotApplied, "multiple matches", new ReplaceInCodeResult(false, matchedNodes.Length, changedNodeInfo,
				$"textPattern matched nothing as written and was read as the name '{readAsName}', which {matchedNodes.Length} nodes have; its parameter list was not compared. Nothing {(dryRun ? "would be" : "was")} replaced: pass line and column to target one, as changed_nodes lists them."), logNotes);
		
		if(matchedNodes.Length > 1 && !force && !dryRun) {
			
			var narrowing = (line, column) switch {
				
				(0, _) => "narrow textPattern or pass line and column to target one",
				(_, 0) => "add column or narrow textPattern to target one",
				_      => "narrow textPattern: these nodes start at the same position. If the text cannot tell them apart, replace the enclosing node or use roslyn_replace_in_file"
			};
			
			return Stopped(EditStopKind.NotApplied, "multiple matches", new ReplaceInCodeResult(false, matchedNodes.Length, changedNodeInfo, $"Matched {matchedNodes.Length} nodes — set force=true to replace all, or {narrowing}."), logNotes);
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
			
			// The members that follow the first, where one member is replaced by several (#463), by
			// the annotation their first member carries: ReplaceSyntax puts one node in a node's
			// place, so the others are inserted behind it afterwards.
			var followers = new List<(SyntaxAnnotation First, SyntaxNode[] Others)>();

			foreach(var matchedNode in matchedNodes) {
				
				var (parsed, parseError) = ParseReplacement(matchedNode, effectiveReplacement);
				
				if(parsed is null)
					
					return Stopped(EditStopKind.Error, "", new ReplaceInCodeSyntaxError(parseError ?? "Failed to parse replacement text — parser returned null", BuildNodeInfo(true))
					{
						Error = "Replacement text contains syntax errors"
					}, logNotes);
				
				var notes = new List<string>();
				
				var placed = WithTriviaOf(matchedNode, parsed, replacementTokens, notes);
				
				if(placed.Length > 1) {
					
					var mark = new SyntaxAnnotation();
					
					followers.Add((mark, placed[1..]));
					placed[0] = placed[0].WithAdditionalAnnotations(mark);
					logNotes.Add($"1 member replaced by {placed.Length}");
				}
				
				replacementNodes[matchedNode] = placed[0];

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
			
			// A first member that is not in the tree was inside another matched node, whose
			// replacement took its place whole: ReplaceSyntax hands the outer node's replacement out
			// as it is. Its followers went with it, and there is nothing to insert behind.
			foreach(var (first, rest) in followers)
				if(newRoot.GetAnnotatedNodes(first).FirstOrDefault() is { } anchor)
					newRoot = newRoot.InsertNodesAfter(anchor, rest);
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
		
		if(validateFile && newDiagnostics.Length > 0)
			
			return Stopped(EditStopKind.Error, "", new ReplaceInCodeSyntaxError(
				"In the file as it would be: " + string.Join("; ", newDiagnostics.Select(d => $"line {d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()} ({d.Id})")),
				BuildNodeInfo(true))
			{
				Error = isDeletion ? "Deletion would introduce syntax errors" : "Replacement would introduce syntax errors"
			}, logNotes);
		
		var verb    = isDeletion ? "deleted" : "replaced";
		var caution = unapplied.Count > 0
			? string.Join(" ", unapplied.Select(entry => $"{entry.Key} (Node at line {string.Join(", ", entry.Value)}.)"))
			: null
		;
		
		// The answer says which node was taken, since the pattern did not: a signature's
		// parameter list is not compared, so 'Foo(string s)' finds a lone 'Foo(int a)'.
		if(readAsName is not null) {
			
			var taken = $"textPattern matched nothing as written and was read as the name '{readAsName}': the node at line {string.Join(", ", matchedNodes.Select(n => StartOf(n).Line + 1))} was taken, and a parameter list or return type in the pattern was not compared.";
			
			caution = caution is null ? taken : $"{taken} {caution}";
		}
		
		// One change, from the first character that differs to the last. What the edit claims is
		// more: its nodes whole, so that a text edit inside a replaced node is seen as touching
		// it, also where the replacement differs from the node in one character only.
		var change = EditPlan.Between(sourceText.ToString(), newText);
		var claim  = TextSpan.FromBounds(
			Math.Min(change.Span.Start, matchedNodes.Min(n => n.SpanStart)),
			Math.Max(change.Span.End,   matchedNodes.Max(n => n.Span.End)));
		
		return new() {
			
			Changes = [change],
			Claims  = [claim],
			Detail  = $"{verb} {matchedNodes.Length} node(s)",
			Places  = matchedNodes.Length,
			Caution = caution,
			Notes   = logNotes,
			NewRoot = newRoot,
			NewText = newText,
			Count   = matchedNodes.Length,
			Nodes   = changedNodeInfo,
			Verb    = verb,
		};
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
	///     <para>
	///         Several members in place of one (#463) are the old node spread over a list: the first
	///         has its place and its comments, as a single replacement would; the end of the old
	///         node's last line, with whatever stood there, comes behind the last; and everything
	///         between the two is written as the replacement text has it.
	///     </para>
	/// </summary>
	private static SyntaxNode[] WithTriviaOf(SyntaxNode original, ParsedReplacement parsed, Dictionary<SyntaxToken, SyntaxToken> tokens, List<string> notes)
	{
		var node = parsed.Node.WithLeadingTrivia(LeadingTrivia(original.GetLeadingTrivia(), parsed.Node.GetLeadingTrivia(), notes));
		
		if(parsed.HasLeftover)
			notes.Add("What the replacement text holds below its node (a comment or a directive on a line after it) was not applied: it is not part of the node.");
		
		if(parsed.More is [.. var between, var last]) {
			
			// Only where no separator stands between members, so the old node's own trailing trivia
			// is the end of its line.
			var closing = TrailingTrivia(original.GetTrailingTrivia(), parsed.Trailing, EndsFile(original.GetLastToken()), notes);
			
			return [node, ..between, last.WithTrailingTrivia(closing)];
		}
		
		var separator = SeparatorAfter(original);
		
		if(separator.RawKind == 0)
			return [node.WithTrailingTrivia(TrailingTrivia(original.GetTrailingTrivia(), parsed.Trailing, EndsFile(original.GetLastToken()), notes))];
		
		tokens[separator] = separator.WithTrailingTrivia(TrailingTrivia(separator.TrailingTrivia, parsed.Trailing, EndsFile(separator), notes));
		
		return [node.WithTrailingTrivia(original.GetTrailingTrivia())];
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
	
	// What a declaration outside a type is, in the words of a message.
	private static string Describe(SyntaxNode declaration) => declaration switch {
		
		BaseNamespaceDeclarationSyntax                     => "namespace declaration",
		GlobalStatementSyntax                              => "top-level statement",
		BaseTypeDeclarationSyntax or DelegateDeclarationSyntax => "type declaration",
		_                                                  => "member declaration",
	};
	
	/// <summary>
	///     A replacement parsed: the node, the trivia after it up to the end of its line — past the
	///     comma, for an enum member — and whether the text went on below that line.
	/// </summary>
	private sealed record ParsedReplacement(SyntaxNode Node, SyntaxTriviaList Trailing, bool HasLeftover)
	{
		/// <summary>
		///     The declarations after <see cref="Node"/>, where the text replaces one member of a
		///     type by several (#463) or one type outside a type by several (#466).
		///     <see cref="Trailing"/> is then what follows the last of them.
		/// </summary>
		public SyntaxNode[] More { get; init; } = [];
	}
	
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
	
	
	private static string GetDeclaredName(SyntaxNode node) => DeclaredName(node) ?? node.ToString();
	
	// The name a declaration is found by, or null for a node that declares nothing and is found by
	// its text.
	private static string? DeclaredName(SyntaxNode node) => node switch {
		
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
		_                               => null
	};
	
	// How many nodes an answer that found none lists, and how much of a node without a name.
	const int MaxCandidates = 30;
	const int MaxBrief      = 60;
	
	// The start of a node's text on one line, for a node that has no name to be listed by.
	private static string Brief(string text)
	{
		var firstLine = text.AsSpan();
		var end       = firstLine.IndexOfAny('\r', '\n');
		
		if(end >= 0)
			firstLine = firstLine[..end];
		
		return firstLine.Length > MaxBrief ? $"{firstLine[..MaxBrief]}..." : firstLine.ToString();
	}
	
	/// <summary>
	///     The parser's errors as an answer quotes them (#476): <c>line N: message (id)</c>, the line
	///     counted in the replacement text as the agent wrote it. <paramref name="prefixLength"/> is
	///     what a wrapper put before that text, so that a position in the parsed text becomes one in
	///     the replacement.
	/// </summary>
	private static string QuoteErrors(IEnumerable<Diagnostic> errors, string replacement, int prefixLength)
		=> string.Join("; ", errors.Select(error => {
			
			if(error.Location.Kind == LocationKind.None)
				
				return $"{error.GetMessage()} ({error.Id})";
			
			var position = Math.Clamp(error.Location.SourceSpan.Start - prefixLength, 0, replacement.Length);
			var line     = 1 + replacement.AsSpan(0, position).Count('\n');
			
			return $"line {line}: {error.GetMessage()} ({error.Id})";
		}))
	;
	
	/// <summary>
	///     Parses <paramref name="replacement"/> in the grammatical position of
	///     <paramref name="original"/>, so validation asks the right question of the parser. Each
	///     type member is parsed inside a dummy type that borrows THAT member's enclosing type name
	///     (and kind, for enum members) — <c>SyntaxFactory.ParseMemberDeclaration</c> alone has no
	///     enclosing type, so it reads <c>Widget(int size) { }</c> as a method missing its return
	///     type, and dispatching on a fixed list of node kinds sent constructors to
	///     <c>ParseExpression</c> outright (#267). Statements parse as statements, a using directive
	///     as a compilation unit, everything else as an expression. Exactly one node must come out,
	///     except for a member of a class, struct, interface or record, which several members may
	///     replace (#463), and for a type outside a type, which several types may replace (#466).
	///     A replacement that closes the wrapper type early is refused, and so is a second node
	///     anywhere else.
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
		SyntaxNode[]     more = [];
		
		// The characters a wrapper puts before the replacement text, which the parser's positions count.
		var prefix = 0;

		switch(original) {
			
			case MemberDeclarationSyntax { Parent: BaseTypeDeclarationSyntax enclosingType }: {
				
				var keyword = enclosingType is EnumDeclarationSyntax ? "enum" : "class";
				var header  = $"{keyword} {enclosingType.Identifier.Text}\n{{\n";
				var unit    = SyntaxFactory.ParseCompilationUnit($"{header}{replacement}\n}}");
				
				prefix = header.Length;
				
				var members = unit.Members.Count == 1
					? unit.Members[0] switch {
						
						EnumDeclarationSyntax e => e.Members.Cast<MemberDeclarationSyntax>().ToArray(),
						TypeDeclarationSyntax t => t.Members.ToArray(),
						_                       => [],
					}
					: [];
				
				// More than the wrapper came out: the text closed it and went on.
				if(unit.Members.Count != 1)
					
					return (null, "Replacement closes the enclosing type: it must hold only members of that type.");
				
				// One member may become several (#463): a method and a new overload beside it is one
				// edit to an agent, and "exactly one" sent that edit to the text tool. Not an enum's:
				// the commas between its members are the list's, not the members'.
				if(members.Length == 0)
					
					return (null, "Replacement must be a member declaration — parsed none.");

				if(members.Length > 1 && enclosingType is EnumDeclarationSyntax)
					
					return (null, $"Replacement must be exactly one enum member — parsed {members.Length}.");
				
				node        = members[0];
				more        = members[1..];
				diagnostics = unit.GetDiagnostics();
				
				var wrapper   = (BaseTypeDeclarationSyntax) unit.Members[0];
				var separator = wrapper is EnumDeclarationSyntax { Members.SeparatorCount: > 0 } wrapperEnum ? wrapperEnum.Members.GetSeparator(0) : default;
				
				// What follows the text's last member: of one member, that member.
				trailing    = members[^1].GetTrailingTrivia().AddRange(separator.TrailingTrivia);
				hasLeftover = HasText(BelowTheLine(trailing)) || HasText(wrapper.CloseBraceToken.LeadingTrivia);

				// The parser accepts any Identifier(...) { } inside a type as a constructor; only the
				// compiler objects later (CS1520) when the name differs from the type. Catch it here,
				// or a forced batch across differently named types writes a Widget constructor into
				// Gadget with a clean syntax check.
				var enclosingTypeName = enclosingType.Identifier.ValueText;
				
				foreach(var member in members) {
					
					var declaredTypeName = member switch {
						
						ConstructorDeclarationSyntax c => c.Identifier.ValueText,
						DestructorDeclarationSyntax  d => d.Identifier.ValueText,
						_                              => null,
					};
					
					if(declaredTypeName is not null && declaredTypeName != enclosingTypeName)
						
						return (null, $"Constructor or destructor name '{declaredTypeName}' must match the enclosing type '{enclosingTypeName}'.");
				}
				
				break;
			}
			
			// A method or a field standing directly in a namespace: what a misplaced brace leaves
			// behind, and a file without one syntax error. Its text is a member's, and as a file it
			// would read as a top-level statement ('void F() { }' is a local function there), so it
			// keeps the member parser, one for one.
			case MemberDeclarationSyntax when original is not (BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or BaseNamespaceDeclarationSyntax or GlobalStatementSyntax):
				
				node        = SyntaxFactory.ParseMemberDeclaration(replacement);
				diagnostics = node?.GetDiagnostics() ?? [];
				trailing    = node?.GetTrailingTrivia() ?? default;
				hasLeftover = node is not null && GoesOnBelow(node, replacement);
				break;
			
			case MemberDeclarationSyntax: {
				
				// Outside a type: a type, a delegate, a namespace or a top-level statement, in a file
				// or a namespace. Parsed as a file, which is what such text is. ParseMemberDeclaration
				// gave one member and an "Unexpected token" for a second, null for a namespace, and
				// a constructor for the statement 'A();' (#466).
				var whole = SyntaxFactory.ParseCompilationUnit(replacement);
				
				diagnostics = whole.GetDiagnostics();
				
				// Text that does not parse keeps the parser's own words: one declaration with a typo
				// in it can come out as two members, and its error says more than a count would.
				if(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray() is { Length: > 0 } parseErrors)
					
					return (null, QuoteErrors(parseErrors, replacement, 0));
				
				if(whole.Usings.Count > 0 || whole.Externs.Count > 0 || whole.AttributeLists.Count > 0)
					
					return (null, "Replacement must hold declarations only: a using directive, an extern alias or an assembly attribute is not part of the node it replaces.");
				
				var declared = whole.Members.ToArray();
				
				if(declared.Length == 0)
					
					return (null, "Replacement must be a declaration — parsed none.");
				
				// A type takes a list, as a member of a type does (#463): its parent keeps its
				// declarations in the same kind of list. Of types only: types beside a type is the
				// edit a list is for. A namespace and a top-level statement stay one for one, and are
				// refused in a type's list. A file-scoped namespace takes every type after it into
				// itself, and a statement has its own place in a file, before the types (which the
				// parser checks: such a list written the other way round keeps the parser's error).
				if(original is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax) {
					
					if(declared.FirstOrDefault(member => member is not (BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)) is { } other)
						
						return (null, $"A type declaration is replaced by type declarations only (classes, structs, interfaces, records, enums, delegates): the replacement holds a {Describe(other)}.");
				}
				else if(declared.Length > 1 || Describe(original) != Describe(declared[0])) {
					
					var wanted = Describe(original);
					
					return (null, declared.Length > 1
						? $"Replacement must be exactly one {wanted} — parsed {declared.Length} declarations."
						: $"Replacement must be a {wanted} — parsed a {Describe(declared[0])}.");
				}
				
				// 'namespace N;' for a block namespace is a sound edit of a file that holds that one
				// namespace and nothing else. Anywhere else it is no syntax error either, so nothing
				// below would stop it: a second namespace of a file, or one inside another, written
				// file-scoped compiles to an error and takes whatever follows it into itself.
				else if(declared[0] is FileScopedNamespaceDeclarationSyntax
					&& original is not FileScopedNamespaceDeclarationSyntax
					&& original.Parent is not CompilationUnitSyntax { Members.Count: 1 })
					
					return (null, "A file-scoped namespace ('namespace N;') can replace a block namespace only where that namespace is the file's only declaration.");
				
				node        = declared[0];
				more        = declared[1..];
				trailing    = declared[^1].GetTrailingTrivia();
				hasLeftover = HasText(BelowTheLine(trailing)) || HasText(whole.EndOfFileToken.LeadingTrivia);
				break;
			}
			
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
			.ToArray()
		;
		
		return errors.Length > 0
			? (null, QuoteErrors(errors, replacement, prefix))
			: (new ParsedReplacement(node, trailing, hasLeftover) { More = more }, null)
		;
	}
	
	// For a node parsed from the replacement text itself: whether the text goes on after the
	// node's line. The parser either hangs the rest onto the node as trivia or leaves it
	// unread, so both are looked at.
	private static bool GoesOnBelow(SyntaxNode node, string replacement)
		=> HasText(BelowTheLine(node.GetTrailingTrivia()))
			|| node.FullSpan.End < replacement.Length && !string.IsNullOrWhiteSpace(replacement[node.FullSpan.End..])
	;
	
	internal static bool TryParseSyntaxKind(string kindName, out SyntaxKind kind)
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

/// <summary>
///     The plan of one <c>roslyn_replace_in_code</c> edit: the tree and the text with the nodes
///     replaced or deleted, and what its answer reports. <see cref="EditPlan.Changes"/> holds the one
///     change to the original text the edit amounts to.
/// </summary>
internal sealed record CodeReplacePlan : EditPlan
{
	public SyntaxNode?             NewRoot { get; init; }
	public string?                 NewText { get; init; }
	public int                     Count   { get; init; }
	public ReplaceInCodeNodeInfo[] Nodes   { get; init; } = [];
	public string                  Verb    { get; init; } = "replaced";
}
