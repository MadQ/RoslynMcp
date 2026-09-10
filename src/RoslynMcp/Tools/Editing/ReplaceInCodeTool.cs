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
		"C# syntax nodes by kind, preserving surrounding formatting trivia. " +
		"Works on C# files only; for non-C# files or literal text replacement, use roslyn_replace_in_file instead; " +
		"for inserting new lines without replacing existing content, use roslyn_insert_lines instead. " +
		"Specify nodeKind (e.g., MethodDeclaration, ConstructorDeclaration, PropertyDeclaration, ClassDeclaration) and an optional textPattern " +
		"that filters by the declared name of the node — not body content. " +
		"Validates that the replacement text is syntactically valid C# before writing; rejects changes that would introduce errors. " +
		"The replacement must be exactly one node of the matched kind — a type member is parsed inside a type of the same name, so constructors, destructors, and operators are accepted as-is. " +
		"If multiple nodes match and force is false (default), returns the match list without applying — narrow textPattern or set force=true to proceed. " +
		"Supports dryRun=true to preview which nodes would be replaced without writing; the replacement is parsed and validated exactly as for a real write, so a passing dry run reflects the same syntax checks the write would run. " +
		"Set verbose=true to also include each matched node's original_text in the response (omitted by default to save tokens; always included on error)."
	)]
	public async Task<object> ReplaceInCode(
		[Description("Relative path to the C# file from the workspace root.")] string filePath,
		[Description("Syntax node kind to match (e.g., 'MethodDeclaration', 'ConstructorDeclaration', 'FieldDeclaration', 'IdentifierName'). Common aliases accepted: 'method', 'constructor', 'ctor', 'field', 'property', 'class', 'interface', 'struct', 'enum', 'identifier'.")] string nodeKind,
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken,
		[Description("Optional pattern to filter matched nodes. For declaration nodes (Method/Constructor/Property/Field/Class etc.) matches the DECLARED NAME. For other nodes matches full text.")] string? textPattern = null,
		[Description("Replacement text for the matched node. Must be valid C# syntax for the target node kind. Default: empty string — omitting this deletes the matched node.")] string replacement = "",
		[Description("Preview changes without writing. Returns what would change, after the same parse and validation a real write performs. Default: false.")] bool dryRun = false,
		[Description("Apply even when multiple nodes match. Default: false — returns matches for review instead.")] bool force = false,
		[Description("When false (default), omits original node text from the response on success to reduce token usage. Set true to include the full original text of each matched node in changed_nodes[].original_text. Error responses always include the text regardless of this flag.")] bool verbose = false
	)
	{
		using var scope    = BeginTool("roslyn_replace_in_code", filePath, new { nodeKind, textPattern, replacement = replacement.Length > 120 ? replacement[..120] + "…" : replacement, dryRun, force });
		
		if(!TryResolveFileContext(projectPath, out var rootPath, out var boundary, out var resolveError))
			
			return scope.Error(resolveError);
		
		var fullPath = ResolveFilePath(filePath, rootPath, boundary);
		
		if(fullPath is null)
			
			return scope.Failed("file not found", new ErrorResult($"File not found: {filePath}"));
		
		if(!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			
			return scope.Error(new ErrorResult("File must be a C# source file (.cs)"));
		
		if(!TryParseSyntaxKind(nodeKind, out var kind))
			
			return scope.Error(new ErrorResult($"Unknown node kind: {nodeKind}.", "Examples: MethodDeclaration, ConstructorDeclaration, FieldDeclaration, IdentifierName."));
		
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
				
				using var stream = File.OpenRead(fullPath);
				sourceText = SourceText.From(stream, FileWriter.Utf8NoBom);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				return scope.Error(new ErrorResult($"Failed to read file: {ex.Message}"));
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
		
		if(matchedNodes.Length == 0)
			
			return scope.Failed("No matching nodes found.", new ReplaceInCodeResult(false, 0, [], "No matching nodes found."));
		
		// Local helper so error paths can always include the matched text (diagnostic info the agent needs),
		// while success paths omit it unless verbose=true to save tokens.
		ReplaceInCodeNodeInfo[] BuildNodeInfo(bool includeText) => matchedNodes.Select(n => {
			
			var lineSpan = syntaxTree.GetLineSpan(n.Span);
			
			return new ReplaceInCodeNodeInfo(
				includeText ? n.ToString() : null,
				lineSpan.StartLinePosition.Line + 1,
				lineSpan.StartLinePosition.Character + 1
			);
		}).ToArray();
		
		var changedNodeInfo = BuildNodeInfo(verbose);
		
		// Safety guard: multiple matches require explicit opt-in via force=true. A dry run is
		// exempt — its job is to show what would match.
		if(matchedNodes.Length > 1 && !force && !dryRun)
			
			return scope.Outcome("multiple matches", new ReplaceInCodeResult(false, matchedNodes.Length, changedNodeInfo, $"Matched {matchedNodes.Length} nodes — set force=true to replace all, or narrow textPattern to target one."));
		
		// Build the edited tree — deletion or replacement — and validate it BEFORE the dry-run
		// exit, so a dry run's verdict rests on the same checks a real write runs (#267).
		// Empty replacement = delete matched node(s). Documented behavior: omitting replacement removes the node.
		var        isDeletion = string.IsNullOrWhiteSpace(replacement);
		SyntaxNode newRoot;
		
		if(isDeletion)
			newRoot = root.RemoveNodes(matchedNodes, SyntaxRemoveOptions.KeepNoTrivia)!;
		
		else {
			
			// Parse the replacement in the grammatical position of the node it replaces — a type
			// member inside a type of the same name so constructors, destructors, and operators
			// parse as themselves; a statement as a statement; anything else as an expression.
			var (replacementNode, parseError) = ParseReplacement(matchedNodes[0], replacement);
			
			if(replacementNode is null)
				
				return scope.Error(new ReplaceInCodeSyntaxError(parseError ?? "Failed to parse replacement text — parser returned null")
				{
					Error = "Replacement text contains syntax errors"
				});
			
			newRoot = root.ReplaceNodes(
				matchedNodes,
				(originalNode, _) => replacementNode.WithTriviaFrom(originalNode)
			);
		}
		
		// Validate the new tree has no new errors.
		var newTree = CSharpSyntaxTree.Create((CSharpSyntaxNode) newRoot, path: fullPath);
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
		
		var verb = isDeletion ? "deleted" : "replaced";
		
		if(dryRun)
			
			return scope.Outcome("dry run", new ReplaceInCodeResult(false, matchedNodes.Length, changedNodeInfo, $"Dry run: {matchedNodes.Length} node(s) would be {verb}."));
		
		var newText  = newRoot.ToFullString();
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
		
		return scope.Outcome($"{verb} {matchedNodes.Length} node(s)", new ReplaceInCodeResult(true, matchedNodes.Length, changedNodeInfo));
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
	///     <paramref name="original"/>, so validation asks the right question of the parser. A type
	///     member is parsed inside a dummy type that borrows the enclosing type's name (and kind,
	///     for enum members) — <c>SyntaxFactory.ParseMemberDeclaration</c> alone has no enclosing
	///     type, so it reads <c>Widget(int size) { }</c> as a method missing its return type, and
	///     dispatching on a fixed list of node kinds sent constructors to <c>ParseExpression</c>
	///     outright (#267). Statements parse as statements, a using directive as a compilation unit,
	///     everything else as an expression. Exactly one node must come out: a replacement that
	///     smuggles in a second member, or closes the wrapper type early, is refused.
	///     Returns the node, or null plus the joined parser error messages.
	/// </summary>
	private static (SyntaxNode? Node, string? Error) ParseReplacement(SyntaxNode original, string replacement)
	{
		SyntaxNode?             node;
		IEnumerable<Diagnostic> diagnostics;
		
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
				break;
			}
			
			case MemberDeclarationSyntax:
				
				// Top-level: a type, delegate, or namespace member with no enclosing type to borrow.
				node        = SyntaxFactory.ParseMemberDeclaration(replacement);
				diagnostics = node?.GetDiagnostics() ?? [];
				break;
			
			case StatementSyntax:
				
				node        = SyntaxFactory.ParseStatement(replacement);
				diagnostics = node.GetDiagnostics();
				break;
			
			case UsingDirectiveSyntax: {
				
				var unit = SyntaxFactory.ParseCompilationUnit(replacement);
				
				if(unit.Usings.Count != 1 || unit.Members.Count != 0)
					
					return (null, "Replacement must be exactly one using directive.");
				
				node        = unit.Usings[0];
				diagnostics = unit.GetDiagnostics();
				break;
			}
			
			default:
				
				node        = SyntaxFactory.ParseExpression(replacement);
				diagnostics = node.GetDiagnostics();
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
			: (node, null)
		;
	}
	
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
