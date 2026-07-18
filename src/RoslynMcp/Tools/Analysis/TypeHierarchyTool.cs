using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using RoslynSymbolFinder = Microsoft.CodeAnalysis.FindSymbols.SymbolFinder;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class TypeHierarchyTool : RoslynMcpTool
{
	public TypeHierarchyTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_get_type_hierarchy", ReadOnly = true, Title = "Get Type Hierarchy", OpenWorld = false, Idempotent = true)]
	[Description(
		"Use this to understand where a type fits in the inheritance graph — its base type chain up to object, " +
		"all interfaces it implements, and all types in the project that derive from it or implement it. " +
		"Useful before refactoring a type to understand blast radius, or when navigating an unfamiliar class hierarchy. " +
		"Base types (the chain up to object/ValueType) are always returned in full; interfaces and derived types " +
		"are combined into a single paged list — use skip/take to paginate large hierarchies. " +
		"For interface types, derived entries are concrete implementations; for class types, they are subclasses. " +
		"The type_kind field in the response indicates which lookup was used. " +
		"For just the concrete implementations of an interface, roslyn_find_implementations is more direct.")]
	public async Task<object> GetTypeHierarchy(
		[Description("The type to query, as a simple name (e.g. 'WorkspaceManager') or fully-qualified name (e.g. 'RoslynMcp.WorkspaceManager'). Simple names are resolved by scanning the global namespace.")] string typeName,
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken,
		[Description("Number of items to skip in the paged interfaces-and-derived list. Default: 0.")] int skip = 0,
		[Description("Maximum items to return from the paged interfaces-and-derived list. Default: 50, max: 200.")] int take = 50,
		[Description("Token from a previous response to get the next page without re-executing the query.")] string? page_token = null)
	{
		using var scope = BeginTool("roslyn_get_type_hierarchy", typeName, new { skip, take });
		
		if(scope.TryServeCachedPage<string>(page_token, ref skip, ref take, 200, out var cached))
			
			return scope.Outcome("cached page", cached);
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return scope.Error(error!);
		
		var type = FindType(compilation, typeName);
		
		if(type is null)
			
			return scope.Failed("type not found", new ErrorResult($"Type '{typeName}' not found in the project."));
		
		var baseTypes   = GetBaseTypeChain(type);
		
		string[] allInterfaces = [..
			type.AllInterfaces
				.Select(i => i.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))
				.Order()
		];
		
		var solution    = workspace.GetSolution(projectPath);
		
		// FindDerivedClassesAsync only finds subclasses — for interfaces, use FindImplementationsAsync.
		var derivedRefs = type.TypeKind == TypeKind.Interface
			? (await RoslynSymbolFinder.FindImplementationsAsync(type, solution, cancellationToken: cancellationToken)).OfType<INamedTypeSymbol>()
			: await RoslynSymbolFinder.FindDerivedClassesAsync(type, solution, cancellationToken: cancellationToken)
		;
		string[] allDerived = [..
			derivedRefs
				.Select(d => d.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))
				.Order()
		];
		
		// Page both interfaces
		var combined = allInterfaces.Concat(allDerived).ToArray();
		var result   = PaginateAndStore(combined, ref skip, take);
		
		return scope.Outcome($"{result.Items.Length} interface(s)/derived", new TypeHierarchyResult(
			TypeName:           type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
			TypeKind:           type.TypeKind.ToString().ToLowerInvariant(),
			BaseTypes:          baseTypes,
			TotalInterfaces:    allInterfaces.Length,
			TotalDerivedTypes: allDerived.Length,
			Skip: skip,
			Take: take,
			InterfacesAndDerived: result.Items,
			PageToken:          result.PageToken,
			HasMore:            result.HasMore)
		{
			Caution = AdhocCaution(projectPath)
		});
	}
	
	private static INamedTypeSymbol? FindType(Compilation compilation, string typeName)
	{
		// Try metadata name lookup first (handles fully-qualified names).
		var direct = GetTypeByMetadataNameOrBest(compilation, typeName)
		;
		
		if(direct is not null)
			
			return direct;
		
		// Fall back to simple name search.
		
		return compilation.GlobalNamespace
			.Accept(new SimpleNameFinder<INamedTypeSymbol>(typeName))
		;
	}
	
	private static string[] GetBaseTypeChain(INamedTypeSymbol type)
	{
		var chain = new List<string>();
		var current = type.BaseType;
		
		while(current is not null) {
			
			chain.Add(current.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
			current = current.BaseType;
		}
		
		return [.. chain];
	}
}