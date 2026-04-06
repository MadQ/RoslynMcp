using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoslynMcp.Analyzers;

// RS1038: This assembly references Microsoft.CodeAnalysis.Workspaces (required for CodeFixProvider).
// The analyzer itself does not use Workspaces and works correctly during command-line builds.
#pragma warning disable RS1038

/// <summary>
///     Enforces <c>[Description]</c> completeness on all <c>[McpServerTool]</c> methods and their parameters.
/// </summary>
/// <remarks>
///     RMCP007 — The tool method itself is missing a <c>[Description]</c> attribute.
///     RMCP008 — A parameter on the tool method is missing a <c>[Description]</c> attribute
///               (<c>CancellationToken</c> parameters are exempt — infrastructure, not agent-facing).
///     RMCP009 — The <c>string projectPath</c> parameter uses an inline description string instead of the
///               <c>ProjectPathDescription</c> constant; inline text drifts and diverges.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ToolDescriptionAnalyzer : DiagnosticAnalyzer
{
	private const string Category = "RoslynMcp.Tools";

	private static readonly DiagnosticDescriptor Rule007 = new(
		"RMCP007",
		"Tool method missing [Description]",
		"[McpServerTool] method '{0}' is missing a [Description] attribute",
		Category,
		DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description:
			"Every [McpServerTool] method must have a [Description] attribute. " +
			"The description is what agents see when choosing which tool to invoke — " +
			"without it, the tool is essentially invisible to agent decision-making."
	);

	private static readonly DiagnosticDescriptor Rule008 = new(
		"RMCP008",
		"Tool parameter missing [Description]",
		"[McpServerTool] method '{0}': parameter '{1}' is missing a [Description] attribute",
		Category,
		DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description:
			"Every parameter on a [McpServerTool] method must have a [Description] attribute. " +
			"CancellationToken parameters are exempt — they are infrastructure and are not surfaced " +
			"in the agent's tool schema. All other parameters require a description."
	);

	private static readonly DiagnosticDescriptor Rule009 = new(
		"RMCP009",
		"projectPath must use ProjectPathDescription constant",
		"[McpServerTool] method '{0}': 'projectPath' parameter must use [Description(ProjectPathDescription)], not an inline string",
		Category,
		DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description:
			"The 'string projectPath' parameter on every [McpServerTool] method must use " +
			"[Description(ProjectPathDescription)] — the shared constant defined in RoslynMcpTool. " +
			"Inline description strings drift and diverge across tools; the constant is the single source of truth."
	);

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule007, Rule008, Rule009];

	public override void Initialize(AnalysisContext context)
	{
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.EnableConcurrentExecution();

		context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
	}

	private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
	{
		var method = (MethodDeclarationSyntax) context.Node;

		if(!ToolScopeHelpers.HasMcpServerToolAttribute(method))
			return;

		var methodName = method.Identifier.Text;

		// RMCP007 — the method itself must have [Description]
		if(!HasDescriptionAttribute(method.AttributeLists))
			context.ReportDiagnostic(Diagnostic.Create(Rule007, method.Identifier.GetLocation(), methodName));

		foreach(var param in method.ParameterList.Parameters) {

			if(IsCancellationToken(param, context.SemanticModel))
				continue;

			// RMCP008 — every non-CancellationToken parameter must have [Description]
			if(!HasDescriptionAttribute(param.AttributeLists)) {
				context.ReportDiagnostic(Diagnostic.Create(Rule008, param.Identifier.GetLocation(), methodName, param.Identifier.Text));
				continue;
			}

			// RMCP009 — string projectPath must use [Description(ProjectPathDescription)], not an inline string
			if(IsStringProjectPathParam(param) && !UsesProjectPathDescriptionConstant(param))
				context.ReportDiagnostic(Diagnostic.Create(Rule009, param.Identifier.GetLocation(), methodName));
		}
	}

	private static bool HasDescriptionAttribute(SyntaxList<AttributeListSyntax> attributeLists)
	{
		foreach(var attrList in attributeLists)
		foreach(var attr in attrList.Attributes) {

			var name = attr.Name switch {

				IdentifierNameSyntax id              => id.Identifier.Text,
				QualifiedNameSyntax { Right: var r } => r.Identifier.Text,
				_                                    => null
			};

			if(name is "Description" or "DescriptionAttribute")
				return true;
		}

		return false;
	}

	private static bool IsStringProjectPathParam(ParameterSyntax param) =>
		param.Identifier.Text == "projectPath"
		&& param.Type is PredefinedTypeSyntax { Keyword.RawKind: (int) SyntaxKind.StringKeyword };

	private static bool UsesProjectPathDescriptionConstant(ParameterSyntax param)
	{
		foreach(var attrList in param.AttributeLists)
		foreach(var attr in attrList.Attributes) {

			var name = attr.Name switch {

				IdentifierNameSyntax id              => id.Identifier.Text,
				QualifiedNameSyntax { Right: var r } => r.Identifier.Text,
				_                                    => null
			};

			if(name is not ("Description" or "DescriptionAttribute"))
				continue;

			// The argument must be a bare identifier named ProjectPathDescription — not a string literal.
			var args = attr.ArgumentList?.Arguments;

			if(args is null or { Count: 0 })
				return false;

			return args.Value[0].Expression is IdentifierNameSyntax { Identifier.Text: "ProjectPathDescription" };
		}

		return false;
	}

	private static bool IsCancellationToken(ParameterSyntax param, SemanticModel semanticModel)
	{
		if(param.Type is null)
			return false;

		var typeInfo = semanticModel.GetTypeInfo(param.Type);

		return typeInfo.Type?.ToDisplayString() == "System.Threading.CancellationToken";
	}
}
