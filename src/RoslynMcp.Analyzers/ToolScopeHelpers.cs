using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Analyzers;

/// <summary>
///     Shared syntactic helpers for <see cref="ToolScopeAnalyzer"/>,
///     <see cref="ToolScopeCodeFixProvider"/>, and <see cref="ToolScopeRefactoringProvider"/>.
/// </summary>
internal static class ToolScopeHelpers
{

	/// <summary>
	///     Returns <see langword="true"/> when the method carries a <c>[McpServerTool]</c> attribute,
	///     regardless of whether a <c>Name</c> argument is present.
	/// </summary>
	internal static bool HasMcpServerToolAttribute(MethodDeclarationSyntax method)
	{
		foreach(var attrList in method.AttributeLists)
		foreach(var attr in attrList.Attributes) {

			var attrName = attr.Name switch {

				IdentifierNameSyntax id              => id.Identifier.Text,
				QualifiedNameSyntax { Right: var r } => r.Identifier.Text,
				_                                    => null
			};

			if(attrName is "McpServerTool" or "McpServerToolAttribute")
				return true;
		}

		return false;
	}

	/// <summary>
	///     Extracts the <c>Name</c> argument from <c>[McpServerTool(Name = "...")]</c> on a method.
	///     Returns <see langword="null"/> when the attribute is absent or has no <c>Name</c> argument.
	/// </summary>
	internal static string? GetMcpToolName(MethodDeclarationSyntax method)
	{
		foreach(var attrList in method.AttributeLists)
		foreach(var attr in attrList.Attributes) {

			var attrName = attr.Name switch {

				IdentifierNameSyntax id              => id.Identifier.Text,
				QualifiedNameSyntax { Right: var r } => r.Identifier.Text,
				_                                    => null
			};

			if(attrName is not ("McpServerTool" or "McpServerToolAttribute"))
				continue;

			if(attr.ArgumentList is null)
				return null;

			foreach(var arg in attr.ArgumentList.Arguments) {

				if(arg.NameEquals?.Name.Identifier.Text != "Name")
					continue;

				if(arg.Expression is LiteralExpressionSyntax lit
					&& lit.IsKind(SyntaxKind.StringLiteralExpression))
					return lit.Token.ValueText;

				return null;
			}

			return null;
		}

		return null;
	}

	/// <summary>
	///     Returns the inferred detail label for a tool method — the tool name with the
	///     <c>roslyn_</c> prefix stripped (e.g. <c>roslyn_info</c> → <c>"info"</c>).
	///     Falls back to <c>"TODO"</c> when the attribute or <c>Name</c> argument is absent.
	/// </summary>
	internal static string InferDetailName(MethodDeclarationSyntax? method)
	{
		if(method is null)
			return "TODO";

		var toolName = GetMcpToolName(method);

		if(toolName is null)
			return "TODO";

		const string prefix = "roslyn_";

		return toolName.StartsWith(prefix, StringComparison.Ordinal)
			? toolName.Substring(prefix.Length)
			: toolName
			;
	}
}
