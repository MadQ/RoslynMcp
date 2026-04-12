
using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Analyzers;

// Roslyn reports RS1038 for code fix providers that are not in an analyzer package.
// This project IS the analyzer package — the warning is a false positive here.
#pragma warning disable RS1038

/// <summary>
/// Code fix provider for RMCP003 (missing BeginTool scope), RMCP004 (bare return that bypasses
/// a scope terminal), RMCP005 (BeginTool name does not match [McpServerTool(Name = "...")] ),
/// and RMCP006 (placeholder 'TODO' detail in scope.Outcome/Failed).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ToolScopeCodeFixProvider)), Shared]
public sealed class ToolScopeCodeFixProvider : CodeFixProvider
{
	public override ImmutableArray<string> FixableDiagnosticIds =>
		["RMCP003", "RMCP004", "RMCP005", "RMCP006"]
		;
	
	public override FixAllProvider GetFixAllProvider() =>
		WellKnownFixAllProviders.BatchFixer
		;
	
	public override async Task RegisterCodeFixesAsync(CodeFixContext context)
	{
		var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken)
			.ConfigureAwait(false)
			;
		
		if(root is null)
			
			return;
		
		foreach(var diagnostic in context.Diagnostics) {
			
			switch(diagnostic.Id) {
				
				case "RMCP003":
				RegisterFix003(context, diagnostic, root);
				break;
				
				case "RMCP004":
				RegisterFix004(context, diagnostic, root);
				break;
				
				case "RMCP005":
				RegisterFix005(context, diagnostic, root);
				break;
				
				case "RMCP006":
				RegisterFix006(context, diagnostic, root);
				break;
			}
		}
	}
	
	// RMCP003: The diagnostic is on the method identifier token.
	// Fix: insert `using var scope = BeginTool("toolName");` as the first statement.
	private static void RegisterFix003(CodeFixContext context, Diagnostic diagnostic, SyntaxNode root)
	{
		var method = root.FindToken(diagnostic.Location.SourceSpan.Start).Parent
			as MethodDeclarationSyntax
			;
		
		if(method?.Body is null)
			
			return;
		
		context.RegisterCodeFix(
			CodeAction.Create(
				"Insert 'using var scope = BeginTool(...)'",
				ct => InsertBeginToolAsync(context.Document, method, ct),
				equivalenceKey: "RMCP003"
			),
			diagnostic
		);
	}
	
	private static async Task<Document> InsertBeginToolAsync(
		Document document,
		MethodDeclarationSyntax method,
		CancellationToken cancellationToken)
	{
		var root = await document.GetSyntaxRootAsync(cancellationToken)
			.ConfigureAwait(false)
			;
		
		if(root is null)
			
			return document;
		
		var body     = method.Body!;
		var toolName = GetMcpToolName(method) ?? "TODO: set tool name";
		
		// Mirror the leading trivia of the first existing statement so indentation is correct.
		// If the body is empty, fall back to two tabs.
		var indent = body.Statements.Count > 0
			? body.Statements[0].GetLeadingTrivia()
			: SyntaxFactory.TriviaList(
				SyntaxFactory.ElasticEndOfLine("\n"),
				SyntaxFactory.ElasticWhitespace("\t\t")
			)
			;
		
		// Detect the end-of-line style from the opening brace so the inserted statement
		// is followed by a proper line terminator and the next statement stays on its own line.
		var eolToken = body.OpenBraceToken.TrailingTrivia
			.FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia))
			;
		
		var eol = eolToken != default
			? SyntaxFactory.TriviaList(eolToken)
			: SyntaxFactory.TriviaList(SyntaxFactory.ElasticEndOfLine("\r\n"))
			;
		
		var statement = SyntaxFactory.ParseStatement($"using var scope = BeginTool(\"{toolName}\");")
			.WithLeadingTrivia(indent)
			.WithTrailingTrivia(eol)
			;
		
		var newBody = body.WithStatements(body.Statements.Insert(0, statement));
		var newRoot = root.ReplaceNode(body, newBody);
		
		return document.WithSyntaxRoot(newRoot);
	}
	
	// RMCP004: The diagnostic is on the `return` keyword token.
	// Fix (3 alternatives): wrap the return expression in a scope terminal call.
	private static void RegisterFix004(CodeFixContext context, Diagnostic diagnostic, SyntaxNode root)
	{
		var returnStatement = root.FindToken(diagnostic.Location.SourceSpan.Start).Parent
			as ReturnStatementSyntax
			;
		
		if(returnStatement?.Expression is null)
			
			return;
		
		// Walk up to the enclosing method to infer the detail name from [McpServerTool(Name)].
		var method = returnStatement.FirstAncestorOrSelf<MethodDeclarationSyntax>()
		;
		var detailName = ToolScopeHelpers.InferDetailName(method);
		
		context.RegisterCodeFix(
			CodeAction.Create(
				$"Wrap with scope.Outcome(\"{detailName}\", ...)",
				ct => WrapReturnAsync(context.Document, returnStatement, "Outcome", detailName, ct),
				equivalenceKey: "RMCP004_Outcome"
			),
			diagnostic
		);
		
		context.RegisterCodeFix(
			CodeAction.Create(
				"Wrap with scope.Error(...)",
				ct => WrapReturnAsync(context.Document, returnStatement, "Error", detailName, ct),
				equivalenceKey: "RMCP004_Error"
			),
			diagnostic
		);
		
		context.RegisterCodeFix(
			CodeAction.Create(
				$"Wrap with scope.Failed(\"failed\", ...)",
				ct => WrapReturnAsync(context.Document, returnStatement, "Failed", "failed", ct),
				equivalenceKey: "RMCP004_Failed"
			),
			diagnostic
		);
	}
	
	private static async Task<Document> WrapReturnAsync(
		Document document,
		ReturnStatementSyntax returnStatement,
		string terminal,
		string detailName,
		CancellationToken cancellationToken)
	{
		var root = await document.GetSyntaxRootAsync(cancellationToken)
			.ConfigureAwait(false)
			;
		
		if(root is null)
			
			return document;
		
		var original = returnStatement.Expression!;
		
		// null / null! can't satisfy the generic T constraint — substitute a typed ErrorResult instead.
		var result = IsNullExpression(original)
			? BuildErrorResult(returnStatement)
			: original.WithoutTrivia()
			;
		
		var wrappedExpr = terminal switch {
			
			"Outcome" => (ExpressionSyntax) SyntaxFactory.InvocationExpression(
				ScopeMemberAccess("Outcome"),
				SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] {
					
					SyntaxFactory.Argument(
						SyntaxFactory.LiteralExpression(
							SyntaxKind.StringLiteralExpression,
							SyntaxFactory.Literal(detailName)
						)
					),
					SyntaxFactory.Argument(result)
				}))
			),
			
			"Error" => SyntaxFactory.InvocationExpression(
				ScopeMemberAccess("Error"),
				SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] {
					SyntaxFactory.Argument(result)
				}))
			),
			
			_ => SyntaxFactory.InvocationExpression(
				ScopeMemberAccess("Failed"),
				SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] {
					
					SyntaxFactory.Argument(
						SyntaxFactory.LiteralExpression(
							SyntaxKind.StringLiteralExpression,
							SyntaxFactory.Literal(detailName)
						)
					),
					SyntaxFactory.Argument(result)
				}))
			)
		};
		
		var newReturn = returnStatement.WithExpression(
			wrappedExpr.WithTriviaFrom(original)
		);
		
		var newRoot = root.ReplaceNode(returnStatement, newReturn);
		
		return document.WithSyntaxRoot(newRoot);
	}
	
	private static bool IsNullExpression(ExpressionSyntax expr)
	{
		if(expr.IsKind(SyntaxKind.NullLiteralExpression))
			
			return true;
		
		return expr.IsKind(SyntaxKind.SuppressNullableWarningExpression)
			&& expr is PostfixUnaryExpressionSyntax suppress
			&& suppress.Operand.IsKind(SyntaxKind.NullLiteralExpression);
	}
	
	// Builds `new ErrorResult(<arg>)` where <arg> is the first scope.Record() string in the method,
	// or "TODO" if no Record call is found. This avoids the CS0411 type-inference failure that
	// occurs when the original return expression is null or null!.
	private static ExpressionSyntax BuildErrorResult(ReturnStatementSyntax returnStatement)
	{
		var method    = returnStatement.FirstAncestorOrSelf<MethodDeclarationSyntax>();
		var recordArg = method?.DescendantNodes()
			.OfType<InvocationExpressionSyntax>()
			.Where(inv => inv.Expression is MemberAccessExpressionSyntax ma
				&& ma.Name.Identifier.Text == "Record")
			.Select(inv => inv.ArgumentList.Arguments.FirstOrDefault()?.Expression)
			.FirstOrDefault(arg => arg is not null)
			;
		
		var errorArg = recordArg is not null
			? (ExpressionSyntax) recordArg.WithoutTrivia()
			: SyntaxFactory.LiteralExpression(
				SyntaxKind.StringLiteralExpression,
				SyntaxFactory.Literal("TODO")
			  )
			;
		
		return SyntaxFactory.ObjectCreationExpression(
			SyntaxFactory.IdentifierName("ErrorResult")).WithArgumentList(
			SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] {
				SyntaxFactory.Argument(errorArg)
			}))
		);
	}
	
	
	// RMCP005: The diagnostic is on Arguments[0] of the BeginTool call (ArgumentSyntax node).
	// Fix: replace the string literal with the value from [McpServerTool(Name = "...")].
	private static void RegisterFix005(CodeFixContext context, Diagnostic diagnostic, SyntaxNode root)
	{
		var arg = root
			.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
			.AncestorsAndSelf()
			.OfType<ArgumentSyntax>()
			.FirstOrDefault()
			;
		
		if(arg is null)
			
			return;
		
		var method = arg.Ancestors()
			.OfType<MethodDeclarationSyntax>()
			.FirstOrDefault()
			;
		
		var toolName = method is not null ? GetMcpToolName(method) : null;
		
		if(toolName is null)
			
			return;
		
		context.RegisterCodeFix(
			CodeAction.Create(
				$"Fix BeginTool name to \"{toolName}\"",
				ct => FixBeginToolNameAsync(context.Document, arg, toolName, ct),
				equivalenceKey: "RMCP005"
			),
			diagnostic
		);
	}
	
	private static async Task<Document> FixBeginToolNameAsync(
		Document document,
		ArgumentSyntax wrongArg,
		string correctName,
		CancellationToken cancellationToken)
	{
		var root = await document.GetSyntaxRootAsync(cancellationToken)
			.ConfigureAwait(false)
			;
		
		if(root is null)
			
			return document;
		
		var correctLiteral = SyntaxFactory
			.LiteralExpression(
				SyntaxKind.StringLiteralExpression,
				SyntaxFactory.Literal(correctName)
			)
			.WithTriviaFrom(wrongArg.Expression)
			;
		
		var newArg  = wrongArg.WithExpression(correctLiteral);
		var newRoot = root.ReplaceNode(wrongArg, newArg);
		
		return document.WithSyntaxRoot(newRoot);
	}
	
	// Mirrors ToolScopeAnalyzer.TryGetMcpToolName — syntactic extraction of [McpServerTool(Name = "...")].
	private static void RegisterFix006(CodeFixContext context, Diagnostic diagnostic, SyntaxNode root)
	{
		var arg = root
			.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
			.AncestorsAndSelf()
			.OfType<ArgumentSyntax>()
			.FirstOrDefault()
			;
		
		if(arg?.Expression is not LiteralExpressionSyntax lit)
			
			return;
		
		var method = arg.Ancestors()
			.OfType<MethodDeclarationSyntax>()
			.FirstOrDefault()
			;
		
		var inferredName = ToolScopeHelpers.InferDetailName(method);
		
		context.RegisterCodeFix(
			CodeAction.Create(
				$"Replace placeholder with \"{inferredName}\"",
				ct => ReplaceStringArgAsync(context.Document, lit, inferredName, ct),
				equivalenceKey: "RMCP006"
			),
			diagnostic
		);
	}
	
	private static async Task<Document> ReplaceStringArgAsync(
		Document document,
		LiteralExpressionSyntax literal,
		string newValue,
		CancellationToken cancellationToken)
	{
		var root = await document.GetSyntaxRootAsync(cancellationToken)
			.ConfigureAwait(false)
			;
		
		if(root is null)
			
			return document;
		
		var newLiteral = SyntaxFactory.LiteralExpression(
			SyntaxKind.StringLiteralExpression,
			SyntaxFactory.Literal(newValue)
		).WithTriviaFrom(literal);
		
		var newRoot = root.ReplaceNode(literal, newLiteral);
		
		return document.WithSyntaxRoot(newRoot);
	}
	
	
	private static string? GetMcpToolName(MethodDeclarationSyntax method)
		=> ToolScopeHelpers.GetMcpToolName(method);
	
	private static MemberAccessExpressionSyntax ScopeMemberAccess(string memberName) =>
		SyntaxFactory.MemberAccessExpression(
			SyntaxKind.SimpleMemberAccessExpression,
			SyntaxFactory.IdentifierName("scope"),
			SyntaxFactory.IdentifierName(memberName)
		)
		;
}
