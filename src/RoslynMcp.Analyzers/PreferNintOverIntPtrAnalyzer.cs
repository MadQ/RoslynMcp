using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoslynMcp.Analyzers;

// RS1038: This assembly references Microsoft.CodeAnalysis.Workspaces (required for CodeFixProvider).
// The analyzer itself does not use Workspaces and works correctly during command-line builds.
// Code fixes are only invoked in IDEs where Workspaces are available.
#pragma warning disable RS1038

/// <summary>
/// Analyzer that suggests using <c>nint</c> and <c>nuint</c> instead of <c>IntPtr</c> and <c>UIntPtr</c> for native-sized integers.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferNintOverIntPtrAnalyzer : DiagnosticAnalyzer
{
	private const string Category = "Style";
	
	private static readonly DiagnosticDescriptor NintRule = new(
		"RMCP001",
		"Prefer 'nint' over 'IntPtr'",
		"Consider using 'nint' instead of 'IntPtr' for native-sized integers",
		Category,
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		description: "The project style guide prefers 'nint' over 'IntPtr' for native-sized signed integer handles. " +
					 "'nint' is the modern C# keyword for native-sized signed integers."
	);
	
	private static readonly DiagnosticDescriptor NuintRule = new(
		"RMCP002",
		"Prefer 'nuint' over 'UIntPtr'",
		"Consider using 'nuint' instead of 'UIntPtr' for native-sized integers",
		Category,
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		description: "The project style guide prefers 'nuint' over 'UIntPtr' for native-sized unsigned integer handles. " +
					 "'nuint' is the modern C# keyword for native-sized unsigned integers."
	);
	
	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
		[NintRule, NuintRule];
	
	public override void Initialize(AnalysisContext context)
	{
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.EnableConcurrentExecution();
		
		context.RegisterSyntaxNodeAction(AnalyzeIdentifierName, SyntaxKind.IdentifierName);
	}
	
	private static void AnalyzeIdentifierName(SyntaxNodeAnalysisContext context)
	{
		var identifierName = (IdentifierNameSyntax) context.Node;
		var text = identifierName.Identifier.Text;
		
		// Check if this is "IntPtr" or "UIntPtr"
		if(text != "IntPtr" && text != "UIntPtr")
			return;
		
		// Verify it actually resolves to System.IntPtr or System.UIntPtr
		var symbolInfo = context.SemanticModel.GetSymbolInfo(identifierName);
		if(symbolInfo.Symbol is not INamedTypeSymbol typeSymbol)
			return;
		
		DiagnosticDescriptor? rule = typeSymbol.SpecialType switch
		{
			SpecialType.System_IntPtr => NintRule,
			SpecialType.System_UIntPtr => NuintRule,
			_ => null
		};
		
		if(rule is null)
			return;
		
		// Don't warn if this is in a using directive or namespace declaration
		if(identifierName.Parent is UsingDirectiveSyntax or QualifiedNameSyntax)
			return;
		
		var diagnostic = Diagnostic.Create(rule, identifierName.GetLocation());
		context.ReportDiagnostic(diagnostic);
	}
}
