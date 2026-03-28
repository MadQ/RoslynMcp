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
		var existingNames = method.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

		foreach(var p in request.AddParameters) {

			if(existingNames.Contains(p.Name))
				return $"Parameter '{p.Name}' already exists on '{method.Name}'.";
		}

		return null;
	}

	public override async Task<SignatureChangeResult> ApplyAsync(
		IMethodSymbol method,
		MethodDeclarationSyntax declaration,
		SignatureChangeRequest request,
		Solution solution,
		Compilation compilation)
	{
		// Build the new parameter list (existing + added).
		var newParams = new List<ParameterSyntax>(declaration.ParameterList.Parameters);

		foreach(var p in request.AddParameters) {

			var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier(p.Name))
				.WithType(SyntaxFactory.ParseTypeName(p.Type + " "))
			;

			if(p.DefaultValue is not null)
				param = param.WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression(p.DefaultValue)));

			newParams.Add(param);
		}

		var updatedMethod = declaration.WithParameterList(
			SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(newParams))
		);

		// Build the forwarding overload (old signature → calls new method with defaults).
		var forwardingArgs = new List<ArgumentSyntax>();

		foreach(var existingParam in declaration.ParameterList.Parameters)
			forwardingArgs.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(existingParam.Identifier)));

		foreach(var p in request.AddParameters)
			forwardingArgs.Add(SyntaxFactory.Argument(SyntaxFactory.ParseExpression(p.DefaultValue ?? "default")));

		var forwardingCall = SyntaxFactory.InvocationExpression(
			SyntaxFactory.IdentifierName(method.Name),
			SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(forwardingArgs))
		);

		var forwardingBody = method.ReturnsVoid
			? (StatementSyntax) SyntaxFactory.ExpressionStatement(forwardingCall)
			: SyntaxFactory.ReturnStatement(forwardingCall)
		;

		var deprecationMessage = $"Use {method.Name}({string.Join(", ", newParams.Select(p => p.Type?.ToString().Trim()))}) instead.";

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
		));

		var forwardingMethod = declaration
			.WithAttributeLists(declaration.AttributeLists.Add(obsoleteAttr))
			.WithBody(SyntaxFactory.Block(forwardingBody))
			.WithExpressionBody(null)
			.WithSemicolonToken(default)
		;

		// Apply to the syntax tree.
		var tree    = declaration.SyntaxTree;
		var root    = await tree.GetRootAsync();
		var newRoot = root.ReplaceNode(declaration, new SyntaxNode[] { updatedMethod, forwardingMethod });

		var docId = solution.GetDocumentIdsWithFilePath(tree.FilePath).FirstOrDefault();

		if(docId is null)
			return SignatureChangeResult.Failed(solution, "Could not resolve document in solution.");

		var newSolution = solution.WithDocumentSyntaxRoot(docId, newRoot);
		var diff        = await SolutionDiff.BuildAsync(solution, newSolution);

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
