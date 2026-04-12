using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using RoslynSymbolFinder = Microsoft.CodeAnalysis.FindSymbols.SymbolFinder;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class FindImplementationsTool : RoslynMcpTool
{
	public FindImplementationsTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_find_implementations", ReadOnly = true, Title = "Find Implementations", OpenWorld = false, Idempotent = true)]
	[Description(
		"Use this to discover all concrete implementations of an interface or abstract class, or all overrides of an " +
		"abstract or virtual method, across the entire project. " +
		"This is the right tool when you need to know every type that fulfills a contract, or every method that overrides " +
		"a base implementation — useful for impact analysis before refactoring. " +
		"For interface and abstract class symbols, returns implementing types. " +
		"For abstract or virtual method symbols, returns overriding methods with full signatures. " +
		"Results are alphabetically ordered and paged. " +
		"For the full inheritance graph of a type (base chain, all interfaces, all derived), use roslyn_get_type_hierarchy instead.")]
	public async Task<object> FindImplementations(
		[Description("The symbol name, e.g. 'IDisposable', 'SymbolVisitor', 'Accept'.")] string symbolName,
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken,
		[Description("Optional containing type to narrow the search, e.g. 'SymbolVisitor' when searching for 'Accept'.")] string? containingType = null,
		[Description("Number of implementations to skip (for paging). Default: 0.")] int skip = 0,
		[Description("Maximum number of implementations to return. Default: 50, max: 200.")] int take = 50,
		[Description("Token from a previous response to get the next page without re-executing the query.")] string? page_token = null)
	{
		using var scope = BeginTool("roslyn_find_implementations", symbolName, new { containingType, skip, take });
		
		if(scope.TryServeCachedPage<string>(page_token, ref skip, ref take, 200, out var cached))
			return scope.Outcome("cached page", cached);
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return scope.Error(error!);
		
		var symbol      = FindSymbol(compilation, symbolName, containingType);
		
		if(symbol is null)
			return scope.Failed("symbol not found", SymbolNotFoundError(symbolName));
		
		var solution = workspace.GetSolution(projectPath);
		
		// Handle type symbols (interface or abstract class).
		if(symbol is INamedTypeSymbol typeSymbol) {
			if((typeSymbol.TypeKind is TypeKind.Interface or TypeKind.Class) && typeSymbol.IsAbstract) {
					
				var impls	   = await RoslynSymbolFinder.FindImplementationsAsync(typeSymbol, solution, cancellationToken: cancellationToken);
				var allResults = impls
					.OfType<INamedTypeSymbol>()
					.Select(t => t.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))
					.Order()
					.ToArray()
				;
				
				var typeKind = typeSymbol.TypeKind.ToString().ToLowerInvariant();
				var typeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
				
				if(allResults.Length == 0)
					return scope.Outcome("no implementations", new FindImplementationsResult(typeKind, typeName, 0, skip, take, ["No implementations found."]));
				
				var result = PaginateAndStore(allResults, ref skip, take);
				
				return scope.Outcome($"{result.Items.Length}/{result.Total} implementation(s)", new FindImplementationsResult(
					SymbolType:  typeKind,
					SymbolName:  typeName,
					TotalImplementations: result.Total,
					Skip: skip, Take: take,
					Implementations: result.Items,
					PageToken:      result.PageToken,
					HasMore:        result.HasMore,
					Caution:        AdhocCaution(projectPath)
				));
			}
			
			return scope.Error(new ErrorResult($"'{symbolName}' is not an interface or abstract class."));
		}
		
		// Handle method symbols (abstract or virtual).
		if(symbol is IMethodSymbol methodSymbol) {
			if(methodSymbol.IsAbstract || methodSymbol.IsVirtual || methodSymbol.IsOverride) {
					
				var overrides  = await RoslynSymbolFinder.FindOverridesAsync(methodSymbol, solution, cancellationToken: cancellationToken);
				var allResults = overrides
					.OfType<IMethodSymbol>()
					.Select(m => FormatMethod(m))
					.Order()
					.ToArray()
				;
				
				var methodDisplay = FormatMethod(methodSymbol);
				
				if(allResults.Length == 0)
					return scope.Outcome("no overrides", new FindOverridesResult("method", methodDisplay, 0, skip, take, ["No overrides found."]));
				
				var result = PaginateAndStore(allResults, ref skip, take);
				
				return scope.Outcome($"{result.Items.Length}/{result.Total} override(s)", new FindOverridesResult(
					SymbolType: "method",
					SymbolName: methodDisplay,
					TotalOverrides: result.Total,
					Skip: skip, Take: take,
					Overrides:  result.Items,
					PageToken: result.PageToken,
					HasMore:   result.HasMore,
					Caution:   AdhocCaution(projectPath)
				));
			}
			
			return scope.Error(new ErrorResult($"'{symbolName}' is not an abstract, virtual, or override method."));
		}
		
		return scope.Error(new ErrorResult($"'{symbolName}' is not a type or method — cannot find implementations."));
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