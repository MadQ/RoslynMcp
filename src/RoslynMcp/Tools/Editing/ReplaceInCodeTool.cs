using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
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
	public ReplaceInCodeTool(WorkspaceResolver workspace, FileLogger logger) : base(workspace, logger) { }
	
	[McpServerTool(Name = "roslyn_replace_in_code", Destructive = true)]
	[Description(
		"**PREFER THIS TOOL for C# code edits** — semantically aware, validates syntax, preserves formatting. " +
		"Replaces C# syntax nodes matching a kind and optional text pattern. " +
		"Uses Roslyn for semantic understanding. Works on C# files only. " +
		"For text/config files or non-C# content, use replace_in_file instead. " +
		"Node kinds: MethodDeclaration, FieldDeclaration, PropertyDeclaration, ClassDeclaration, IdentifierName, etc. " +
		"textPattern matches the DECLARED NAME of declaration nodes (method/property/field/class/etc.) — not body content. " +
		"If multiple nodes match and force is false (default), the tool returns the matches without applying."
	)]
	public async Task<object> ReplaceInCode(
		[Description("Relative path to the C# file from workspace root.")] string filePath,
		[Description("Syntax node kind to match (e.g., 'MethodDeclaration', 'FieldDeclaration', 'IdentifierName').")] string nodeKind,
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Optional pattern to filter matched nodes. For declaration nodes (Method/Property/Field/Class etc.) matches the DECLARED NAME. For other nodes matches full text.")] string? textPattern = null,
		[Description("Replacement text for the matched node. Must produce valid C# syntax.")] string replacement = "",
		[Description("Preview changes without writing. Returns what would change. Default: false.")] bool dryRun = false,
		[Description("Apply even when multiple nodes match. Default: false — returns matches for review instead.")] bool force = false
	)
	{
		using var scope = BeginTool("roslyn_replace_in_code", filePath);
		var rootPath = workspace.GetRootPath(projectPath);
		
		var fullPath = Path.IsPathRooted(filePath)
			? filePath
			: Path.GetFullPath(Path.Combine(rootPath, filePath));
		
		if(!File.Exists(fullPath))
			return scope.Failed("file not found", new { error = $"File not found: {filePath}" });
		
		if(!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			return new { error = "File must be a C# source file (.cs)" };
		
		// Map string to SyntaxKind
		if(!TryParseSyntaxKind(nodeKind, out var kind))
			return new { error = $"Unknown node kind: {nodeKind}. Examples: MethodDeclaration, FieldDeclaration, IdentifierName." };
		
		SourceText sourceText;
		
		try {
			sourceText = SourceText.From(File.ReadAllText(fullPath));
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
		
			return new {
				error = "Failed to read file",
				details = ex.Message
			};
		}
		
		var syntaxTree = CSharpSyntaxTree.ParseText(sourceText, path: fullPath);
		var root = await syntaxTree.GetRootAsync();
		
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

		if(matchedNodes.Length == 0) {

			return new {
				applied      = false,
				changeCount  = 0,
				changedNodes = Array.Empty<object>(),
				message      = "No matching nodes found."
			};
		}
		
		// Build replacement syntax
		SyntaxNode? replacementNode;
		
		try {
			// Parse based on what kind of node we're replacing
			replacementNode = kind switch {
				SyntaxKind.MethodDeclaration or
				SyntaxKind.FieldDeclaration or
				SyntaxKind.PropertyDeclaration or
				SyntaxKind.ClassDeclaration or
				SyntaxKind.InterfaceDeclaration or
				SyntaxKind.StructDeclaration or
				SyntaxKind.EnumDeclaration
					=> (SyntaxNode?) SyntaxFactory.ParseMemberDeclaration(replacement),
				
				_ => (SyntaxNode?) SyntaxFactory.ParseExpression(replacement)
			};
			
			if(replacementNode is null)
				return new { error = "Failed to parse replacement text — parser returned null" };
			
			if(replacementNode.ContainsDiagnostics) {
			
				return new {
					error = "Replacement text contains syntax errors",
					details = string.Join("; ", replacementNode.GetDiagnostics().Select(d => d.GetMessage()))
				};
			}
		}
		catch(Exception ex) {
		
			return new {
				error = "Failed to parse replacement text as valid C# syntax",
				details = ex.Message
			};
		}
		
		// Collect change info before replacement.
		var changedNodeInfo = matchedNodes.Select(n => {
		
			var lineSpan = syntaxTree.GetLineSpan(n.Span);
			
			return new {
				originalText = n.ToString(),
				line = lineSpan.StartLinePosition.Line + 1,
				column = lineSpan.StartLinePosition.Character + 1
			};
		}).ToArray()
		;
		
		if(dryRun) {

			return new {
				applied      = false,
				changeCount  = matchedNodes.Length,
				changedNodes = changedNodeInfo,
				message      = $"Dry run: {matchedNodes.Length} node(s) would be replaced."
			};
		}

		// Safety guard: multiple matches require explicit opt-in via force=true.
		if(matchedNodes.Length > 1 && !force) {

			return new {
				applied      = false,
				changeCount  = matchedNodes.Length,
				changedNodes = changedNodeInfo,
				message      = $"Matched {matchedNodes.Length} nodes — set force=true to replace all, or narrow textPattern to target one."
			};
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
		
		var syntaxValid = newDiagnostics.Length == 0;
		
		if(!syntaxValid) {
		
			return new {
				error = "Replacement would introduce syntax errors",
				details = string.Join("; ", newDiagnostics.Select(d => d.GetMessage())),
				changedNodes = changedNodeInfo
			};
		}
		
		// Write the new source
		try {
			File.WriteAllText(fullPath, newRoot.ToFullString());
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
		
			return new {
				error = "Failed to write file",
				details = ex.Message
			};
		}
		
		// Invalidate workspace cache
		workspace.InvalidateFile(projectPath, fullPath);
		
		return new {
			applied = true,
			changeCount = matchedNodes.Length,
			changedNodes = changedNodeInfo,
			syntaxValid
		};
	}
	
	/// <summary>
	///     Returns the declared identifier name for declaration nodes so textPattern
	///     matches the name only — not body content that may contain the pattern as a call site.
	///     Falls back to full node text for non-declaration nodes (e.g. IdentifierName).
	/// </summary>
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
