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
///               (<c>CancellationToken</c> and <c>McpServer</c> parameters are exempt — SDK-injected
///               infrastructure, never surfaced in the agent-facing tool schema).
///     RMCP009 — A <c>projectPath</c> parameter does not use the description constant matching its shape
///               (<c>ProjectPathDescription</c> for required <c>string</c>, <c>OptionalProjectPathDescription</c>
///               for <c>string? = null</c>), or is nullable without the <c>= null</c> default.
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
			"CancellationToken and McpServer parameters are exempt — they are SDK-injected infrastructure " +
			"and are not surfaced in the agent's tool schema. All other parameters require a description."
	);
	
	private static readonly DiagnosticDescriptor Rule009 = new(
		"RMCP009",
		"projectPath must use the description constant matching its shape",
		"[McpServerTool] method '{0}': '{1}' must be declared exactly so and use [Description({2})]",
		Category,
		DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description:
			"A required 'string projectPath' parameter must use [Description(ProjectPathDescription)]; an optional one " +
			"must be declared 'string? projectPath = null' and use [Description(OptionalProjectPathDescription)] — both " +
			"shared constants defined in RoslynMcpTool. Inline description strings drift and diverge across tools, and " +
			"a nullable projectPath without '= null' is still advertised to agents as required."
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
			
			if(IsSdkInjectedParam(param, context.SemanticModel))
				continue;
			
			// RMCP008 — every non-injected parameter must have [Description]
			if(!HasDescriptionAttribute(param.AttributeLists)) {
				
				context.ReportDiagnostic(Diagnostic.Create(Rule008, param.Identifier.GetLocation(), methodName, param.Identifier.Text));
				continue;
			}
			
			// RMCP009 — projectPath must use the description constant matching its shape: a required
			// 'string projectPath' takes ProjectPathDescription; an optional 'string? projectPath = null'
			// takes OptionalProjectPathDescription. The MCP schema marks a parameter optional only when it
			// has a C# default, so a nullable projectPath without '= null' would still be advertised as
			// required — that mismatch is reported too.
			var shape = ProjectPathShape(param);
			
			if(shape is not ProjectPathKind.None) {
				
				var optional = shape is ProjectPathKind.Optional;
				
				var (expectedConstant, shapeText) = optional
					? ("OptionalProjectPathDescription", "string? projectPath = null")
					: ("ProjectPathDescription",         "string projectPath")
				;
				
				if(optional && param.Default is null || !UsesDescriptionConstant(param, expectedConstant))
					context.ReportDiagnostic(Diagnostic.Create(Rule009, param.Identifier.GetLocation(), methodName, shapeText, expectedConstant));
			}
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
	
	private enum ProjectPathKind { None, Required, Optional }
	
	/// <summary>
	///     Classifies a <c>projectPath</c> parameter: <c>string</c> is required, <c>string?</c> is optional,
	///     anything else (including a non-string <c>projectPath</c>) is not checked.
	/// </summary>
	private static ProjectPathKind ProjectPathShape(ParameterSyntax param)
	{
		if(param.Identifier.Text != "projectPath")
			
			return ProjectPathKind.None;
		
		return param.Type switch {
			
			PredefinedTypeSyntax { Keyword.RawKind: (int) SyntaxKind.StringKeyword }
				=> ProjectPathKind.Required,
			NullableTypeSyntax { ElementType: PredefinedTypeSyntax { Keyword.RawKind: (int) SyntaxKind.StringKeyword } }
				=> ProjectPathKind.Optional,
			_   => ProjectPathKind.None
		};
	}
	
	private static bool UsesDescriptionConstant(ParameterSyntax param, string constantName)
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
			
			// The argument must be a bare identifier naming the expected constant — not a string literal.
			var args = attr.ArgumentList?.Arguments
			;
			
			if(args is null or { Count: 0 })
				
				return false;
			
			return args.Value[0].Expression is IdentifierNameSyntax constant && constant.Identifier.Text == constantName;
		}
		
		return false;
	}
	
	// The MCP SDK binds these parameter types itself and excludes them from the generated tool
	// schema, so a [Description] on them would never reach an agent.
	private static bool IsSdkInjectedParam(ParameterSyntax param, SemanticModel semanticModel)
	{
		if(param.Type is null)
			
			return false;
		
		var typeInfo = semanticModel.GetTypeInfo(param.Type);
		
		return typeInfo.Type?.ToDisplayString()
			is "System.Threading.CancellationToken"
			or "ModelContextProtocol.Server.McpServer";
	}
}
