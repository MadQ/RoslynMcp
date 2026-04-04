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
///     Enforces the BeginTool/scope lifecycle contract on all <c>[McpServerTool]</c> methods.
/// </summary>
/// <remarks>
///     RMCP003 — First statement must be <c>using var scope = BeginTool("name", ...)</c>.
///     RMCP004 — Every value-bearing return must go through <c>scope.Outcome</c>, <c>scope.Error</c>, or <c>scope.Failed</c>.
///     RMCP005 — The name arg to <c>BeginTool</c> must match <c>[McpServerTool(Name = "...")]</c>.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ToolScopeAnalyzer : DiagnosticAnalyzer
{
	private const string Category = "RoslynMcp.Tools";
	
	private static readonly DiagnosticDescriptor Rule003 = new(
		"RMCP003",
		"Missing BeginTool scope",
		"[McpServerTool] method '{0}' must start with: using var scope = BeginTool(name, ...)",
		Category,
		DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description:
			"Every [McpServerTool] method must open a ToolScope as its very first statement using " +
			"'using var scope = BeginTool(...)'. The 'using' keyword ensures Dispose() is called on " +
			"every exit path, which writes the NDJSON log entry. Without it, the invocation is " +
			"invisible in logs and timing is lost."
	);
	
	private static readonly DiagnosticDescriptor Rule004 = new(
		"RMCP004",
		"Return bypasses scope terminal",
		"[McpServerTool] method '{0}': return value must go through scope.Outcome(...), scope.Error(...), or scope.Failed(...)",
		Category,
		DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description:
			"Every return statement that produces a value in a [McpServerTool] method must go through " +
			"a ToolScope terminal: scope.Outcome(detail, value) for success, scope.Error(error) for " +
			"structured errors, or scope.Failed(reason, value) for unstructured failures. " +
			"Bare returns bypass scope logging, token estimation, and response peek capture."
	);
	
	private static readonly DiagnosticDescriptor Rule005 = new(
		"RMCP005",
		"BeginTool name mismatch",
		"[McpServerTool] method '{0}': BeginTool name \"{1}\" does not match [McpServerTool(Name = \"{2}\")]",
		Category,
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		description:
			"The first string argument to BeginTool() must exactly match the Name property of the " +
			"[McpServerTool] attribute. A mismatch means the log entry and the MCP tool registration " +
			"report different names, making log correlation impossible."
	);
	
	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule003, Rule004, Rule005];
	
	public override void Initialize(AnalysisContext context)
	{
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.EnableConcurrentExecution();
		
		context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
	}
	
	private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
	{
		var method = (MethodDeclarationSyntax) context.Node;
		
		if(!TryGetMcpToolName(method, out var attributeName))
			return;
		
		// Expression-bodied methods are intentionally skipped — they can't satisfy RMCP003 by design,
		// and they're legitimately used for simple forwarders. Flagging them would be noise.
		if(method.Body is not { } body)
			return;
		
		var methodName = method.Identifier.Text;
		var statements = body.Statements;
		
		InvocationExpressionSyntax? beginToolInvocation = null;
		
		var hasBeginTool = statements.Count > 0 
			&& IsBeginToolDeclaration(statements[0], out beginToolInvocation)
		;
		
		// RMCP003 — first statement must be `using var scope = BeginTool(...)`
		if(!hasBeginTool) {
			context.ReportDiagnostic(Diagnostic.Create(Rule003, method.Identifier.GetLocation(), methodName));
			beginToolInvocation = null;
		}
		
		// RMCP005 — BeginTool name must match the [McpServerTool(Name = "...")] attribute value
		if(hasBeginTool && attributeName is not null && beginToolInvocation is not null) {
			var nameArg = GetFirstStringArg(beginToolInvocation);
			
			if(nameArg is not null && nameArg != attributeName) {
				var argLoc = beginToolInvocation.ArgumentList.Arguments[0].GetLocation();
				context.ReportDiagnostic(Diagnostic.Create(Rule005, argLoc, methodName, nameArg, attributeName));
			}
		}
		
		// RMCP004 — every return with a value must go through a scope terminal
		foreach(var ret in body.DescendantNodes().OfType<ReturnStatementSyntax>()) {
			
			if(ret.Expression is null)
				continue;
			
			// Returns inside nested lambdas/anonymous methods/local functions are not method-level returns.
			if(IsInsideNestedFunction(ret, body))
				continue;
			
			if(!IsTerminalExpression(ret.Expression))
				context.ReportDiagnostic(Diagnostic.Create(Rule004, ret.ReturnKeyword.GetLocation(), methodName));
		}
	}
	
	// Returns true when the method has [McpServerTool].
	// Sets attributeName to the Name arg value, or null if the attribute has no Name arg.
	private static bool TryGetMcpToolName(MethodDeclarationSyntax method, out string? attributeName)
	{
		attributeName = null;
		
		foreach(var attrList in method.AttributeLists)
		foreach(var attr in attrList.Attributes) {
			
			var name = attr.Name switch {
				
				IdentifierNameSyntax id              => id.Identifier.Text,
				QualifiedNameSyntax { Right: var r } => r.Identifier.Text,
				_                                    => null
			};
			
			if(name is not ("McpServerTool" or "McpServerToolAttribute"))
				continue;
			
			attributeName = GetNamedStringArg(attr, "Name");
			
			return true;
		}
		
		return false;
	}
	
	private static bool IsBeginToolDeclaration(
		StatementSyntax stmt,
		out InvocationExpressionSyntax? invocation)
	{
		invocation = null;
		
		if(stmt is not LocalDeclarationStatementSyntax local)
			return false;
		
		// 'using' keyword is required — `using var scope = ...`
		if(!local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
			return false;
		
		var vars = local.Declaration.Variables;
		
		if(vars.Count != 1)
			return false;
		
		if(vars[0].Initializer?.Value is not InvocationExpressionSyntax inv)
			return false;
		
		var callee = inv.Expression switch {
			
			IdentifierNameSyntax id         => id.Identifier.Text,
			MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
			_                               => null
		};
		
		if(callee != "BeginTool")
			return false;
		
		invocation = inv;
		
		return true;
	}
	
	// Recursively checks whether an expression routes through a scope terminal.
	// Conditional expressions are valid if BOTH branches are terminals.
	private static bool IsTerminalExpression(ExpressionSyntax expr) => expr switch {
		
		InvocationExpressionSyntax inv   => IsScopeTerminal(inv),
		ConditionalExpressionSyntax cond => IsTerminalExpression(cond.WhenTrue) && IsTerminalExpression(cond.WhenFalse),
		_                                => false
	};
	
	private static bool IsScopeTerminal(InvocationExpressionSyntax inv)
	{
		if(inv.Expression is not MemberAccessExpressionSyntax ma)
			return false;
		
		if(ma.Name.Identifier.Text is not ("Outcome" or "Error" or "Failed"))
			return false;
		
		// Receiver must be the scope variable.
		
		return ma.Expression is IdentifierNameSyntax id && id.Identifier.Text == "scope";
	}
	
	private static bool IsInsideNestedFunction(SyntaxNode node, BlockSyntax methodBody)
	{
		var parent = node.Parent;
		
		while(parent is not null && !ReferenceEquals(parent, methodBody)) {
			
			if(parent is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax or LocalFunctionStatementSyntax)
				return true;
			
			parent = parent.Parent;
		}
		
		return false;
	}
	
	private static string? GetFirstStringArg(InvocationExpressionSyntax inv)
	{
		var first = inv.ArgumentList.Arguments.FirstOrDefault();
		
		return first?.Expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)
			? lit.Token.ValueText
			: null;
	}
	
	private static string? GetNamedStringArg(AttributeSyntax attr, string argName)
	{
		if(attr.ArgumentList is null)
			return null;
		
		foreach(var arg in attr.ArgumentList.Arguments) {
			
			if(arg.NameEquals?.Name.Identifier.Text != argName)
				continue;
			
			return arg.Expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)
				? lit.Token.ValueText
				: null;
		}
		
		return null;
	}
}
