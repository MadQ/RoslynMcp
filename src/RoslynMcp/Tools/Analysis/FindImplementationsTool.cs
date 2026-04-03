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
		"Finds all types that implement an interface or abstract class, or all methods that override an abstract/virtual member. " +
		"Use this to discover concrete implementations of abstractions. Results are paged; use skip/take for large result sets.")]
	public async Task<object> FindImplementations(
		[Description("The symbol name, e.g. 'IDisposable', 'SymbolVisitor', 'Accept'.")] string symbolName,
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken,
		[Description("Optional containing type to narrow the search, e.g. 'SymbolVisitor' when searching for 'Accept'.")] string? containingType = null,
		[Description("Number of implementations to skip (for paging). Default: 0.")] int skip = 0,
		[Description("Maximum number of implementations to return. Default: 50, max: 200.")] int take = 50,
		[Description("Token from a previous response to get the next page without re-executing the query.")] string? page_token = null)
	{
		using var scope = BeginTool("roslyn_find_implementations", symbolName);


		var cachedPage = TryServeCachedPage<string>(scope, page_token, ref skip, ref take, 200);
		if(cachedPage is not null)
			return cachedPage;

		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return error;
		
		var symbol      = FindSymbol(compilation, symbolName, containingType);
		
		if(symbol is null)
			return scope.Failed("symbol not found", new ErrorResult($"Symbol '{symbolName}' not found.", Hint: "Use get_type_members or find_references to verify the name."));
		
		var solution = workspace.GetSolution(projectPath);
		
		// Handle type symbols (interface or abstract class).
		if(symbol is INamedTypeSymbol typeSymbol) {

			if((typeSymbol.TypeKind is TypeKind.Interface or TypeKind.Class) && typeSymbol.IsAbstract) {

					var impls = await RoslynSymbolFinder.FindImplementationsAsync(typeSymbol, solution, cancellationToken: cancellationToken);
				var allResults = impls
					.OfType<INamedTypeSymbol>()
					.Select(t => t.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))
					.Order()
					.ToArray()
				;

				var typeKind = typeSymbol.TypeKind.ToString().ToLowerInvariant();
				var typeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

				if(allResults.Length == 0)
					return new FindImplementationsResult(typeKind, typeName, 0, skip, take, ["No implementations found."]);

				var result = PaginateAndStore(allResults, ref skip, take);

				return scope.Outcome($"{result.Items.Length}/{result.Total} implementation(s)", new FindImplementationsResult(
					Symbol_type:  typeKind,
					Symbol_name:  typeName,
					Total_implementations: result.Total,
					Skip: skip, Take: take,
					Implementations: result.Items,
					Page_token:      result.PageToken,
					Has_more:        result.HasMore,
					_caution:        AdhocCaution(projectPath)
				));
			}
			
			return scope.Error(new ErrorResult($"'{symbolName}' is not an interface or abstract class."));
		}

		// Handle method symbols (abstract or virtual).
		if(symbol is IMethodSymbol methodSymbol) {

			if(methodSymbol.IsAbstract || methodSymbol.IsVirtual || methodSymbol.IsOverride) {

					var overrides = await RoslynSymbolFinder.FindOverridesAsync(methodSymbol, solution, cancellationToken: cancellationToken);
				var allResults = overrides
					.OfType<IMethodSymbol>()
					.Select(m => FormatMethod(m))
					.Order()
					.ToArray()
				;

				var methodDisplay = FormatMethod(methodSymbol);

				if(allResults.Length == 0)
					return new FindOverridesResult("method", methodDisplay, 0, skip, take, ["No overrides found."]);

				var result = PaginateAndStore(allResults, ref skip, take);

				return scope.Outcome($"{result.Items.Length}/{result.Total} override(s)", new FindOverridesResult(
					Symbol_type: "method",
					Symbol_name: methodDisplay,
					Total_overrides: result.Total,
					Skip: skip, Take: take,
					Overrides:  result.Items,
					Page_token: result.PageToken,
					Has_more:   result.HasMore,
					_caution:   AdhocCaution(projectPath)
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