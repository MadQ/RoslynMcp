using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Tools.SignatureChange;

/// <summary>
///     Phase 1 editor: adds parameters to a method in non-breaking mode.
///     Creates a forwarding overload with [Obsolete] attribute so existing
///     call sites continue to work without modification.
/// </summary>
internal sealed class AddParameterEditor : SignatureEditor
{
	public override bool CanHandle(IMethodSymbol method)
		=> method.MethodKind == MethodKind.Ordinary && !method.IsExtern;
	
	public override string? Validate(IMethodSymbol method, SignatureChangeRequest request)
	{
		if(request.AddParameters.Length == 0)
			
			return "No parameters to add.";
		
		if(method.IsAbstract)
			
			return "Cannot add parameters to abstract methods — override chain would break.";
		
		// Check for name collisions with existing parameters.
		var existingNames = method.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
		;
		
		foreach(var p in request.AddParameters)
			if(existingNames.Contains(p.Name))
				
				return $"Parameter '{p.Name}' already exists on '{method.Name}'.";
		
		return null;
	}
	
	public override async Task<SignatureChangeResult> ApplyAsync(
		IMethodSymbol method,
		MethodDeclarationSyntax declaration,
		SignatureChangeRequest request,
		Solution solution,
		Compilation compilation,
		CancellationToken cancellationToken)
	{
		// Match the file's line-ending style and the declaration's indentation so synthesized
		// nodes blend into the surrounding code without a formatting pass — Formatter would
		// impose its default options on projects that carry no .editorconfig.
		var eolTrivia = declaration.GetTrailingTrivia().LastOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia))
		;
		var eol          = eolTrivia.IsKind(SyntaxKind.EndOfLineTrivia) ? eolTrivia : SyntaxFactory.CarriageReturnLineFeed;
		var indentTrivia = declaration.GetLeadingTrivia().LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia));
		var indent       = indentTrivia.IsKind(SyntaxKind.WhitespaceTrivia) ? indentTrivia : SyntaxFactory.Whitespace("");
		
		// Append to the existing parameter list in place so its original formatting survives.
		var newParamList = declaration.ParameterList
		;
		
		foreach(var p in request.AddParameters) {
			
			// The trailing space separates the type from the parameter name.
			var typeSyntax = SyntaxFactory.ParseTypeName(p.Type + " ");
			
			if(typeSyntax.ContainsDiagnostics)
				
				return SignatureChangeResult.Failed(solution, $"Parameter type '{p.Type}' for '{p.Name}' is not valid C#.");
			
			var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier(p.Name))
				.WithType(typeSyntax)
			;
			
			if(p.DefaultValue is not null) {
				
				var defaultExpr = SyntaxFactory.ParseExpression(p.DefaultValue);
				
				if(defaultExpr.ContainsDiagnostics)
					
					return SignatureChangeResult.Failed(solution, $"Default value '{p.DefaultValue}' for '{p.Name}' is not valid C#.");
				
				param = param.WithDefault(SyntaxFactory.EqualsValueClause(
					SyntaxFactory.Token(SyntaxKind.EqualsToken).WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(SyntaxFactory.Space),
					defaultExpr));
			}
			
			// Space after the comma AddParameters inserts (skipped when the list was empty).
			if(newParamList.Parameters.Count > 0)
				param = param.WithLeadingTrivia(SyntaxFactory.Space);
			
			newParamList = newParamList.AddParameters(param);
		}
		
		var updatedMethod = declaration.WithParameterList(newParamList);
		
		// Build the forwarding overload (old signature → calls new method with defaults).
		var forwardingArgs = new List<ArgumentSyntax>()
		;
		
		foreach(var existingParam in declaration.ParameterList.Parameters)
			forwardingArgs.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(existingParam.Identifier)));
		
		foreach(var p in request.AddParameters) {
			
			var defaultExpr = SyntaxFactory.ParseExpression(p.DefaultValue ?? "default");
			
			if(defaultExpr.ContainsDiagnostics)
				
				return SignatureChangeResult.Failed(solution, $"Default value '{p.DefaultValue}' for '{p.Name}' is not valid C#.");
			
			forwardingArgs.Add(SyntaxFactory.Argument(defaultExpr));
		}
		
		// NormalizeWhitespace canonicalizes the single-line call — spaces after argument commas.
		var forwardingCall = SyntaxFactory.InvocationExpression(
			SyntaxFactory.IdentifierName(method.Name),
			SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(forwardingArgs))
		).NormalizeWhitespace();
		
		var deprecationMessage = $"Use {method.Name}({string.Join(", ", newParamList.Parameters.Select(p => p.Type?.ToString().Trim()))}) instead.";
		
		var obsoleteAttr = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
			SyntaxFactory.Attribute(
				SyntaxFactory.ParseName("System.Obsolete"),
				SyntaxFactory.AttributeArgumentList(SyntaxFactory.SingletonSeparatedList(
					SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(
						SyntaxKind.StringLiteralExpression,
						SyntaxFactory.Literal(deprecationMessage)
					))
				))
			)
		)).WithTrailingTrivia(eol, indent);
		
		// Expression-bodied stub: old signature => NewCall(...);. The XML doc comment stays on
		// the updated method only — the declaration's leading trivia must be stripped BEFORE the
		// attribute list is added, while it is still attached to what is currently the first
		// token; added afterwards, the [Obsolete] attribute would render above the doc comment.
		var forwardingMethod = declaration
			.WithLeadingTrivia()
		;
		
		forwardingMethod = forwardingMethod
			.WithAttributeLists(forwardingMethod.AttributeLists.Add(obsoleteAttr))
			// Strip the close paren's trailing trivia so the arrow follows on the same line even
			// when the original declaration wrapped before its body.
			.WithParameterList(declaration.ParameterList.WithTrailingTrivia())
			.WithBody(null)
			.WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
				SyntaxFactory.Token(SyntaxKind.EqualsGreaterThanToken).WithLeadingTrivia(SyntaxFactory.Space).WithTrailingTrivia(SyntaxFactory.Space),
				forwardingCall))
			.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
			.WithLeadingTrivia(eol, indent)
			.WithTrailingTrivia(declaration.GetTrailingTrivia())
		;
		
		// Apply to the syntax tree.
		var tree    = declaration.SyntaxTree
		;
		var root    = await tree.GetRootAsync(cancellationToken);
		var newRoot = root.ReplaceNode(declaration, new SyntaxNode[] { updatedMethod, forwardingMethod });
		
		var docId = solution.GetDocumentIdsWithFilePath(tree.FilePath).FirstOrDefault();
		
		if(docId is null)
			
			return SignatureChangeResult.Failed(solution, "Could not resolve document in solution.");
		
		var newSolution = solution.WithDocumentSyntaxRoot(docId, newRoot);
		var diff        = await SolutionDiff.BuildAsync(solution, newSolution, cancellationToken);
		
		return new SignatureChangeResult {
			
			Success            = true,
			BaseSolution       = solution,
			NewSolution        = newSolution,
			Diff               = diff,
			ParametersAdded    = [.. request.AddParameters.Select(p => $"{p.Type} {p.Name}")],
			DeprecationMessage = deprecationMessage,
			FilesAffected      = 1
		};
	}
}
