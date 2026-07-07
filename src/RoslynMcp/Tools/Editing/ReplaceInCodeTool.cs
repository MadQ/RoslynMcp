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
		"Specify nodeKind (e.g., MethodDeclaration, PropertyDeclaration, ClassDeclaration) and an optional textPattern " +
		"that filters by the declared name of the node — not body content. " +
		"Validates that the replacement text is syntactically valid C# before writing; rejects changes that would introduce errors. " +
		"If multiple nodes match and force is false (default), returns the match list without applying — narrow textPattern or set force=true to proceed. " +
		"Supports dryRun=true to preview which nodes would be replaced without writing. " +
		"Set verbose=true to also include each matched node's original_text in the response (omitted by default to save tokens; always included on error)."
	)]
	public async Task<object> ReplaceInCode(
		[Description("Relative path to the C# file from the workspace root.")] string filePath,
		[Description("Syntax node kind to match (e.g., 'MethodDeclaration', 'FieldDeclaration', 'IdentifierName'). Common aliases accepted: 'method', 'field', 'property', 'class', 'interface', 'struct', 'enum', 'identifier'.")] string nodeKind,
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken,
		[Description("Optional pattern to filter matched nodes. For declaration nodes (Method/Property/Field/Class etc.) matches the DECLARED NAME. For other nodes matches full text.")] string? textPattern = null,
		[Description("Replacement text for the matched node. Must be valid C# syntax for the target node kind. Default: empty string — omitting this deletes the matched node.")] string replacement = "",
		[Description("Preview changes without writing. Returns what would change. Default: false.")] bool dryRun = false,
		[Description("Apply even when multiple nodes match. Default: false — returns matches for review instead.")] bool force = false,
		[Description("When false (default), omits original node text from the response on success to reduce token usage. Set true to include the full original text of each matched node in changed_nodes[].original_text. Error responses always include the text regardless of this flag.")] bool verbose = false
	)
	{
		using var scope    = BeginTool("roslyn_replace_in_code", filePath, new { nodeKind, textPattern, replacement = replacement.Length > 120 ? replacement[..120] + "…" : replacement, dryRun, force });
		
		var       rootPath = workspace.GetRootPath(projectPath);
		var       boundary = workspace.GetSecurityBoundary(projectPath);
		
		var fullPath = ResolveFilePath(filePath, rootPath, boundary);
		
		if(fullPath is null)
			
			return scope.Failed("file not found", new ErrorResult($"File not found: {filePath}"));
		
		if(!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			
			return scope.Error(new ErrorResult("File must be a C# source file (.cs)"));
		
		if(!TryParseSyntaxKind(nodeKind, out var kind))
			
			return scope.Error(new ErrorResult($"Unknown node kind: {nodeKind}.", "Examples: MethodDeclaration, FieldDeclaration, IdentifierName."));
		
		SourceText sourceText;
		SyntaxTree syntaxTree;
		
		// Prefer the in-memory workspace document to avoid races with concurrent edits.
		var solution = workspace.GetSolution(projectPath		   )
		;
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
		
		if(dryRun)
			
			return scope.Outcome("dry run", new ReplaceInCodeResult(false, matchedNodes.Length, changedNodeInfo, $"Dry run: {matchedNodes.Length} node(s) would be replaced."));
		
		// Safety guard: multiple matches require explicit opt-in via force=true.
		if(matchedNodes.Length > 1 && !force)
			
			return scope.Outcome("multiple matches", new ReplaceInCodeResult(false, matchedNodes.Length, changedNodeInfo, $"Matched {matchedNodes.Length} nodes — set force=true to replace all, or narrow textPattern to target one."));
		
		// Empty replacement = delete matched node(s). Documented behavior: omitting replacement removes the node.
		if(string.IsNullOrWhiteSpace(replacement)) {
			
			var deletedRoot = root.RemoveNodes(matchedNodes, SyntaxRemoveOptions.KeepNoTrivia)!;
			var deletedTree = CSharpSyntaxTree.Create((CSharpSyntaxNode) deletedRoot, path: fullPath);
			var deleteErrors = deletedTree.GetDiagnostics()
				.Where(d => d.Severity == DiagnosticSeverity.Error)
				.ToArray()
			;
			
			if(deleteErrors.Length > 0) {
				
				return scope.Error(new ReplaceInCodeSyntaxError(
					string.Join("; ", deleteErrors.Select(d => d.GetMessage())),
					BuildNodeInfo(true))
				{
					Error = "Deletion would introduce syntax errors"
				});
			}
			
			var deletedText  = deletedRoot.ToFullString();
			var deletedBytes = FileWriter.Utf8NoBom.GetBytes(deletedText);
			
			var (_, deleteBackupErr) = await SaveBackupsAsync(
			backups, fullPath, projectPath, "roslyn_replace_in_code", deletedBytes);
		
		if(deleteBackupErr is not null)
			
			return scope.Error(deleteBackupErr);
		
		// When the document is workspace-tracked, let Roslyn write it via TryApplyChanges
			// (MSBuild only — handles FSW suppression and encoding). Fall back to direct I/O
			// for untracked files (e.g. AdhocWorkspace or files outside the project).
			if(docIds.Length > 0) {
				
				var newDoc      = solution.GetDocument(docIds[0])!.WithSyntaxRoot(deletedRoot);
				var newSolution = newDoc.Project.Solution;
				workspace.ApplyChanges(projectPath, newSolution);
				
				if(await TryRecoverTruncation(filePath, fullPath, projectPath, deletedBytes) is { } truncErr)
					
					return scope.Error(truncErr);
			}
			else {
				
				try {
					
					await workspace.WriteAndInvalidate(projectPath, fullPath,
						() => FileWriter.WriteAllTextAsync(fullPath, deletedText));
					
					if(CheckForTruncation(filePath, fullPath, deletedText.Length) is { } truncErr)
						
						return scope.Error(truncErr);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
					return scope.Error(new ErrorResult($"Write failed — '{filePath}' may be in an inconsistent state: {ex.Message}.", BackupRecoveryHint(filePath)));
				}
			}
			
			return scope.Outcome($"deleted {matchedNodes.Length} node(s)", new ReplaceInCodeResult(true, matchedNodes.Length, changedNodeInfo));
		}
		
		// Parse and validate the replacement text.
		SyntaxNode? replacementNode
		;
		
		try {
			
			replacementNode = kind switch {
				
				SyntaxKind.MethodDeclaration or
				SyntaxKind.FieldDeclaration or
				SyntaxKind.PropertyDeclaration or
				SyntaxKind.ClassDeclaration or
				SyntaxKind.InterfaceDeclaration or
				SyntaxKind.StructDeclaration or
				SyntaxKind.RecordDeclaration or
				SyntaxKind.EnumDeclaration
					=> (SyntaxNode?) SyntaxFactory.ParseMemberDeclaration(replacement),
				
				SyntaxKind.UsingDirective or
				SyntaxKind.LocalDeclarationStatement or
				SyntaxKind.ExpressionStatement or
				SyntaxKind.ReturnStatement or
				SyntaxKind.IfStatement or
				SyntaxKind.ForEachStatement or
				SyntaxKind.WhileStatement or
				SyntaxKind.ThrowStatement
					=> SyntaxFactory.ParseStatement(replacement),
				
				_ => (SyntaxNode?) SyntaxFactory.ParseExpression(replacement)
			};
			
			if(replacementNode is null)
				
				return scope.Error(new ErrorResult("Failed to parse replacement text — parser returned null"));
			
			if(replacementNode.ContainsDiagnostics)
				
				return scope.Error(new ReplaceInCodeSyntaxError(
					string.Join("; ", replacementNode.GetDiagnostics().Select(d => d.GetMessage())))
				{
					Error = "Replacement text contains syntax errors"
				});
		}
		catch(Exception ex) {
			
			return scope.Error(new ReplaceInCodeSyntaxError(
				ex.Message)
			{
				Error = "Failed to parse replacement text as valid C# syntax"
			});
		}
		
		// Apply replacements
		var newRoot = root.ReplaceNodes(
			matchedNodes,
			(originalNode, _) => replacementNode.WithTriviaFrom(originalNode)
		);
		
		// Validate the new tree has no new errors
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
				Error = "Replacement would introduce syntax errors"
			});
		
		var newText  = newRoot.ToFullString();
		var newBytes = FileWriter.Utf8NoBom.GetBytes(newText);
		
		var (_, replaceBackupErr) = await SaveBackupsAsync(
			backups, fullPath, projectPath, "roslyn_replace_in_code", newBytes);
		
		if(replaceBackupErr is not null)
			
			return scope.Error(replaceBackupErr);
		
		// When the document is workspace-tracked, let Roslyn write it via TryApplyChanges
		// (MSBuild only — handles FSW suppression and encoding). Fall back to direct I/O
		// for untracked files (e.g. AdhocWorkspace or files outside the project).
		if(docIds.Length > 0) {
			
			var newDoc      = solution.GetDocument(docIds[0])!.WithSyntaxRoot(newRoot);
			var newSolution = newDoc.Project.Solution;
			workspace.ApplyChanges(projectPath, newSolution);
			
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
		
		return scope.Outcome($"replaced {matchedNodes.Length} node(s)", new ReplaceInCodeResult(true, matchedNodes.Length, changedNodeInfo));
	}
	
	
	private static string GetDeclaredName(SyntaxNode node) => node switch {
		
		MethodDeclarationSyntax     m => m.Identifier.Text,
		PropertyDeclarationSyntax   p => p.Identifier.Text,
		FieldDeclarationSyntax      f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? string.Empty,
		ClassDeclarationSyntax      c => c.Identifier.Text,
		InterfaceDeclarationSyntax  i => i.Identifier.Text,
		StructDeclarationSyntax     s => s.Identifier.Text,
		EnumDeclarationSyntax       e => e.Identifier.Text,
		RecordDeclarationSyntax     r => r.Identifier.Text,
		_                             => node.ToString()
	};
	
	private static bool TryParseSyntaxKind(string kindName, out SyntaxKind kind)
	{
		// Try exact match first
		if(Enum.TryParse<SyntaxKind>(kindName, ignoreCase: true, out kind))
			
			return true;
		
		// Common aliases/shortcuts
		kind = kindName.ToLowerInvariant() switch {
			
			"method" => SyntaxKind.MethodDeclaration,
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
