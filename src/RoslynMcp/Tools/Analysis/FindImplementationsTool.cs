using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using RoslynSymbolFinder = Microsoft.CodeAnalysis.FindSymbols.SymbolFinder;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class FindImplementationsTool : RoslynMcpTool
{
	public FindImplementationsTool(WorkspaceResolver workspace, FileLogger logger) : base(workspace, logger) { }
	
	[McpServerTool(Name = "roslyn_find_implementations", ReadOnly = true)]
	[Description(
		"Finds all types that implement an interface or abstract class, or all methods that override an abstract/virtual member. " +
		"Use this to discover concrete implementations of abstractions. Results are paged; use skip/take for large result sets.")]
	public async Task<object> FindImplementations(
		[Description("The symbol name, e.g. 'IDisposable', 'SymbolVisitor', 'Accept'.")] string symbolName,
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Optional containing type to narrow the search, e.g. 'SymbolVisitor' when searching for 'Accept'.")] string? containingType = null,
		[Description("Number of implementations to skip (for paging). Default: 0.")] int skip = 0,
		[Description("Maximum number of implementations to return. Default: 50, max: 200.")] int take = 50)
	{
		using var scope = BeginTool("roslyn_find_implementations", symbolName);
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return error;
		
		var symbol      = FindSymbol(compilation, symbolName, containingType);
		
		if(symbol is null)
			return scope.Failed("symbol not found", new { error = $"Symbol '{symbolName}' not found. Use get_type_members or find_references to verify the name." });
		
		var solution = workspace.GetSolution(projectPath);
		
		// Handle type symbols (interface or abstract class).
		if(symbol is INamedTypeSymbol typeSymbol) {
		
			if(typeSymbol.TypeKind is TypeKind.Interface or TypeKind.Class && typeSymbol.IsAbstract) {
			
				take = Math.Clamp(take, 1, 200);
				
				var impls = await RoslynSymbolFinder.FindImplementationsAsync(typeSymbol, solution);
				var allResults = impls
					.OfType<INamedTypeSymbol>()
					.Select(t => t.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))
					.Order()
					.ToArray()
				;
				
				if(allResults.Length == 0)
					return new {
						symbol_type = typeSymbol.TypeKind.ToString().ToLowerInvariant(),
						symbol_name = typeSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
						total_implementations = 0,
						skip,
						take,
						implementations = new[] { "No implementations found." }
					};
				
				var page = allResults.AsSpan(skip, Math.Min(take, allResults.Length - skip)).ToArray();
				
				return scope.Outcome($"{page.Length}/{allResults.Length} implementation(s)", new {
					symbol_type  = typeSymbol.TypeKind.ToString().ToLowerInvariant(),
					symbol_name  = typeSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
					total_implementations = allResults.Length,
					skip,
					take,
					implementations = page,
					_caution        = AdhocCaution(projectPath)
				});
			}
			
			return new { error = $"'{symbolName}' is not an interface or abstract class." };
		}
		
		// Handle method symbols (abstract or virtual).
		if(symbol is IMethodSymbol methodSymbol) {
		
			if(methodSymbol.IsAbstract || methodSymbol.IsVirtual || methodSymbol.IsOverride) {
			
				take = Math.Clamp(take, 1, 200);
				
				var overrides = await RoslynSymbolFinder.FindOverridesAsync(methodSymbol, solution);
				var allResults = overrides
					.OfType<IMethodSymbol>()
					.Select(m => FormatMethod(m))
					.Order()
					.ToArray()
				;
				
				if(allResults.Length == 0)
					return new {
						symbol_type = "method",
						symbol_name = FormatMethod(methodSymbol),
						total_overrides = 0,
						skip,
						take,
						overrides = new[] { "No overrides found." }
					};
				
				var page = allResults.AsSpan(skip, Math.Min(take, allResults.Length - skip)).ToArray();
				
				return scope.Outcome($"{page.Length}/{allResults.Length} override(s)", new {
					symbol_type = "method",
					symbol_name = FormatMethod(methodSymbol),
					total_overrides = allResults.Length,
					skip,
					take,
					overrides = page,
					_caution  = AdhocCaution(projectPath)
				});
			}
			
			return new { error = $"'{symbolName}' is not an abstract, virtual, or override method." };
		}
		
		return new { error = $"'{symbolName}' is not a type or method — cannot find implementations." };
	}
	
	private static ISymbol? FindSymbol(Compilation compilation, string name, string? inType)
	{
		if(inType is not null) {
		
			var type = compilation.GetTypeByMetadataName(inType)
				?? compilation.GlobalNamespace.Accept(new SimpleNameFinder<INamedTypeSymbol>(inType));
			
			return type?.GetMembers(name).FirstOrDefault();
		}
		
		// Global search for type or member.
		return compilation.GlobalNamespace.Accept(new AnySymbolFinder(name));
	}
	
	private static string FormatMethod(IMethodSymbol method)
	{
		var containingType = method.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
		var returnType     = method.ReturnsVoid
			? "void"
			: method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
		
		var parameters = string.Join(", ", method.Parameters.Select(p =>
			p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
		));
		
		return $"{containingType}.{method.Name}({parameters}): {returnType}";
	}
}