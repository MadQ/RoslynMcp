using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoslynMcp.Analyzers;

// RS1038: This assembly references Microsoft.CodeAnalysis.Workspaces (required for CodeFixProvider).
// The analyzer itself does not use Workspaces and works correctly during command-line builds.
#pragma warning disable RS1038

/// <summary>
///     RMCP010 — a string literal names one of the compilation's own tools without its <c>roslyn_</c> prefix.
/// </summary>
/// <remarks>
///     Hints and messages are read by agents, which call what they read: "call apply_rename" gets an unknown-tool
///     error (#326). The tool names are the compilation's own <c>[McpServerTool(Name = "roslyn_…")]</c> values, so a
///     new tool is covered the moment it exists and a project without tools (the TestHarness, whose test labels
///     use short names) is never checked. Only names with an underscore after the prefix are matched: one-word
///     names (<c>info</c>, <c>respawn</c>) are ordinary words in a message. Comments are not string literals, so
///     they are never checked.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ToolNamePrefixAnalyzer : DiagnosticAnalyzer
{
	private const string Category = "RoslynMcp.Tools";
	private const string Prefix   = "roslyn_";
	
	private static readonly DiagnosticDescriptor Rule010 = new(
		"RMCP010",
		"Tool name without its roslyn_ prefix",
		"String names tool '{0}' without its prefix; agents can only call '" + Prefix + "{0}'",
		Category,
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		description:
			"Agent-facing text (hints, messages, descriptions) must name tools exactly as they are called. A bare " +
			"name such as 'apply_rename' sends an agent that follows the text literally to an unknown tool. The " +
			"names checked are the [McpServerTool(Name = \"roslyn_…\")] values of the compilation itself."
	);
	
	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule010];
	
	public override void Initialize(AnalysisContext context)
	{
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.EnableConcurrentExecution();
		
		context.RegisterCompilationStartAction(start => {
			
			var bareNames = CollectBareToolNames(start.Compilation, start.CancellationToken);
			
			if(bareNames.Count == 0)
				
				return;
			
			// One alternation over every name: a bare name is one with no word character on either side, so
			// 'roslyn_apply_rename' (an underscore before it) and 'apply_renames' never match.
			var pattern = new Regex(
				@"(?<!\w)(?:" + string.Join("|", bareNames.OrderByDescending(n => n.Length).Select(Regex.Escape)) + @")(?!\w)",
				RegexOptions.CultureInvariant
			);
			
			start.RegisterSyntaxNodeAction(
				node => AnalyzeText(node, pattern),
				SyntaxKind.StringLiteralExpression,
				SyntaxKind.Utf8StringLiteralExpression,
				SyntaxKind.InterpolatedStringText
			);
		});
	}
	
	// The tool names of this compilation without their prefix, multi-word names only.
	private static HashSet<string> CollectBareToolNames(Compilation compilation, CancellationToken cancellationToken)
	{
		var names = new HashSet<string>(StringComparer.Ordinal);
		
		foreach(var tree in compilation.SyntaxTrees) {
			
			// Down to member declarations only: a tool method is a member of a type, so method bodies, where
			// nearly all of the syntax is, are never walked.
			var members = tree.GetRoot(cancellationToken)
				.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax)
				.OfType<MethodDeclarationSyntax>()
			;
			
			foreach(var method in members) {
				
				var name = ToolScopeHelpers.GetMcpToolName(method);
				
				if(name is null || !name.StartsWith(Prefix, StringComparison.Ordinal))
					
					continue;
				
				var bare = name.Substring(Prefix.Length);
				
				if(bare.IndexOf('_') >= 0)
					names.Add(bare);
			}
		}
		
		return names;
	}
	
	private static void AnalyzeText(SyntaxNodeAnalysisContext context, Regex pattern)
	{
		var text = context.Node switch {
			
			LiteralExpressionSyntax literal          => literal.Token.ValueText,
			InterpolatedStringTextSyntax interpolated => interpolated.TextToken.ValueText,
			_                                        => null
		};
		
		if(string.IsNullOrEmpty(text))
			
			return;
		
		foreach(Match match in pattern.Matches(text))
			context.ReportDiagnostic(Diagnostic.Create(Rule010, context.Node.GetLocation(), match.Value));
	}
}
