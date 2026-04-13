
using System.Collections.Generic;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Analyzers;

/// <summary>
///     Offers quick refactorings to convert between <c>scope.Outcome</c>, <c>scope.Error</c>,
///     <c>scope.Failed</c>, and <c>scope.Record</c> call sites.
/// </summary>
/// <remarks>
///     Conversions to/from <c>scope.Record</c> are inherently lossy: Record is a void side-effect
///     call that drops the return value, while the terminal methods return a result. The action
///     titles include a warning so users understand what will change before approving the preview.
/// </remarks>
[ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(ToolScopeRefactoringProvider)), Shared]
public sealed class ToolScopeRefactoringProvider : CodeRefactoringProvider
{
	
	public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
	{
		var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken)
			.ConfigureAwait(false)
			;
		
		if(root is null)
			
			return;
		
		// Find the innermost invocation that spans the cursor.
		var node = root.FindNode(context.Span, getInnermostNodeForTie: true)
		;
		var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
		
		if(invocation is null)
			
			return;
		
		if(invocation.Expression is not MemberAccessExpressionSyntax ma)
			
			return;
		
		if(ma.Expression is not IdentifierNameSyntax receiver || receiver.Identifier.Text != "scope")
			
			return;
		
		var currentName = ma.Name.Identifier.Text;
		
		if(currentName is not ("Outcome" or "Error" or "Failed" or "Record"))
			
			return;
		
		// Infer the detail name from the enclosing [McpServerTool] method.
		var method = invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>()
		;
		var detailName = ToolScopeHelpers.InferDetailName(method);
		
		if(currentName == "Record") {
			
			// Record → each terminal (wraps in return; adds args appropriate to terminal)
			RegisterTerminalConversions(context, invocation, detailName, fromRecord: true);
		}
		else {
			
			// Terminal → other terminals
			RegisterTerminalConversions(context, invocation, detailName, fromRecord: false, currentName);
			
			// Terminal → Record (lossy — warns in title)
			context.RegisterRefactoring(CodeAction.Create(
				$"Convert to scope.Record(...) — drops return, verify method still compiles",
				ct => ConvertToRecordAsync(context.Document, invocation, ct),
				equivalenceKey: $"ToolScopeRefactoring_ToRecord"
			));
		}
	}
	
	private static void RegisterTerminalConversions(
		CodeRefactoringContext context,
		InvocationExpressionSyntax invocation,
		string detailName,
		bool fromRecord,
		string? skipName = null)
	{
		foreach(var targetName in new[] { "Outcome", "Error", "Failed" }) {
			
			if(targetName == skipName)
				continue;
			
			var title = fromRecord
				? $"Convert to return scope.{targetName}(...)"
				: $"Convert to scope.{targetName}(...)"
				;
			
			// Capture loop variable for closure.
			var captured = targetName
			;
			
			context.RegisterRefactoring(CodeAction.Create(
				title,
				ct => fromRecord
					? ConvertFromRecordToTerminalAsync(context.Document, invocation, captured, detailName, ct)
					: ConvertBetweenTerminalsAsync(context.Document, invocation, captured, detailName, ct),
				equivalenceKey: $"ToolScopeRefactoring_{captured}"
			));
		}
	}
	
	/// <summary>
	///     Converts <c>scope.Outcome/Error/Failed</c> to a different terminal in the same return
	///     statement, reconstructing the argument list appropriate to the target terminal.
	/// </summary>
	private static async Task<Document> ConvertBetweenTerminalsAsync(
		Document document,
		InvocationExpressionSyntax invocation,
		string targetName,
		string detailName,
		CancellationToken cancellationToken)
	{
		var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
		
		if(root is null)
			
			return document;
		
		var ma = (MemberAccessExpressionSyntax) invocation.Expression;
		var args = invocation.ArgumentList.Arguments;
		var currentName = ma.Name.Identifier.Text;
		
		// Reconstruct a best-effort argument list for the target terminal.
		// When args match the expected shape, reuse them; otherwise substitute defaults.
		ArgumentSyntax BuildStringArg(string value) => SyntaxFactory.Argument(
			SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value))
		);
		
		ArgumentSyntax? TryExtractResultArg()
		{
			// In Outcome(detail, result) and Failed(reason, result): result is arg[1]
			// In Error(result): result is arg[0]
			
			return currentName switch {
				
				"Outcome" or "Failed" => args.Count >= 2 ? args[1] : null,
				"Error"               => args.Count >= 1 ? args[0] : null,
				_                     => null
			};
		}
		
		var resultArg = TryExtractResultArg();
		var nullBang   = SyntaxFactory.Argument(
			SyntaxFactory.PostfixUnaryExpression(
				SyntaxKind.SuppressNullableWarningExpression,
				SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
			)
		);
		
		SeparatedSyntaxList<ArgumentSyntax> newArgList = targetName switch {
			
			"Outcome" => SyntaxFactory.SeparatedList(new[] {
				
				BuildStringArg(detailName),
				resultArg ?? nullBang
			}),
			
			"Error" => SyntaxFactory.SeparatedList(new[] {
				resultArg ?? nullBang
			}),
			
			// Failed(reason, result)
			_ => SyntaxFactory.SeparatedList(new[] {
				
				BuildStringArg("failed"),
				resultArg ?? nullBang
			})
		};
		
		var newMa = ma.WithName(SyntaxFactory.IdentifierName(targetName));
		var newInvocation = invocation
			.WithExpression(newMa)
			.WithArgumentList(SyntaxFactory.ArgumentList(newArgList))
			.WithTriviaFrom(invocation)
			;
		
		return document.WithSyntaxRoot(root.ReplaceNode(invocation, newInvocation));
	}
	
	/// <summary>
	///     Converts <c>scope.Record(...)</c> (a void statement) to
	///     <c>return scope.Terminal(...)</c> by wrapping in a return statement with
	///     a <c>null!</c> placeholder for the result.
	/// </summary>
	private static async Task<Document> ConvertFromRecordToTerminalAsync(
		Document document,
		InvocationExpressionSyntax invocation,
		string targetName,
		string detailName,
		CancellationToken cancellationToken)
	{
		var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
		
		if(root is null)
			
			return document;
		
		// The Record's first arg is the most meaningful payload for the error result.
		// Using null! here would cause CS0411 (T cannot be inferred from null).
		var recordArgExpr = invocation.ArgumentList.Arguments.Count > 0
			? (ExpressionSyntax) invocation.ArgumentList.Arguments[0].Expression.WithoutTrivia()
			: SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal("TODO"))
			;
		
		var errorResultArg = SyntaxFactory.Argument(
			SyntaxFactory.ObjectCreationExpression(
				SyntaxFactory.IdentifierName("ErrorResult")).WithArgumentList(
				SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] {
					SyntaxFactory.Argument(recordArgExpr)
				}))
			)
		);
		
		ArgumentSyntax BuildStringArg(string value) => SyntaxFactory.Argument(
			SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value))
		);
		
		SeparatedSyntaxList<ArgumentSyntax> newArgs = targetName switch {
			
			"Outcome" => SyntaxFactory.SeparatedList(new[] { BuildStringArg(detailName), errorResultArg }),
			"Error"   => SyntaxFactory.SeparatedList(new[] { errorResultArg }),
			_         => SyntaxFactory.SeparatedList(new[] { BuildStringArg("failed"), errorResultArg })
		};
		
		var newMa = ((MemberAccessExpressionSyntax) invocation.Expression)
			.WithName(SyntaxFactory.IdentifierName(targetName))
			;
		
		var newInvocation = invocation
			.WithExpression(newMa)
			.WithArgumentList(SyntaxFactory.ArgumentList(newArgs))
			;
		
		// If the invocation is inside an expression statement, replace the whole statement
		// with a return statement. Otherwise just replace the invocation.
		if(invocation.Parent is ExpressionStatementSyntax exprStmt) {
			
			var returnStmt = SyntaxFactory.ReturnStatement(newInvocation)
				.WithTriviaFrom(exprStmt)
				;
			
			return document.WithSyntaxRoot(root.ReplaceNode(exprStmt, returnStmt));
		}
		
		return document.WithSyntaxRoot(root.ReplaceNode(invocation, newInvocation));
	}
	
	/// <summary>
	///     Converts <c>return scope.Terminal(...)</c> to a <c>scope.Record(...)</c>
	///     expression statement, dropping the <c>return</c> keyword and the result argument.
	/// </summary>
	private static async Task<Document> ConvertToRecordAsync(
		Document document,
		InvocationExpressionSyntax invocation,
		CancellationToken cancellationToken)
	{
		var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
		
		if(root is null)
			
			return document;
		
		var ma          = (MemberAccessExpressionSyntax) invocation.Expression;
		var currentName = ma.Name.Identifier.Text;
		var args        = invocation.ArgumentList.Arguments;
		
		// For Outcome/Failed, first arg is the detail string — pass it to Record.
		// For Error, there is no detail string — pass no args.
		SeparatedSyntaxList<ArgumentSyntax> recordArgs = currentName switch {
			
			"Outcome" or "Failed" when args.Count >= 1
				=> SyntaxFactory.SeparatedList(new[] { args[0] }),
			_   => default
		};
		
		var newMa = ma.WithName(SyntaxFactory.IdentifierName("Record"));
		var newInvocation = invocation
			.WithExpression(newMa)
			.WithArgumentList(SyntaxFactory.ArgumentList(recordArgs))
			;
		
		// If the invocation is the expression of a return statement, replace the whole
		// return statement with an expression statement.
		if(invocation.Parent is ReturnStatementSyntax retStmt) {
			
			var exprStmt = SyntaxFactory.ExpressionStatement(newInvocation)
				.WithTriviaFrom(retStmt)
				;
			
			return document.WithSyntaxRoot(root.ReplaceNode(retStmt, exprStmt));
		}
		
		return document.WithSyntaxRoot(root.ReplaceNode(invocation, newInvocation));
	}
}
