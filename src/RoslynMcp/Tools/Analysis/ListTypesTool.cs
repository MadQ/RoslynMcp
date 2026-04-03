using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ListTypesTool : RoslynMcpTool
{
	public ListTypesTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_list_types", ReadOnly = true)]
	[Description(
		"Lists all types (classes, interfaces, enums, structs, records) in the project. " +
		"Optionally filter by namespace or type kind. Use this to discover what's available in the codebase.")]
	public object ListTypes(
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Optional namespace filter, e.g. 'RoslynMcp.Tools'. Types in this namespace and its sub-namespaces are returned.")] string? namespaceFilter = null,
		[Description("Optional type kind filter: 'class', 'interface', 'enum', 'struct', 'record'. Omit for all types.")] string? kindFilter = null,
		[Description("Number of types to skip (for paging). Default: 0.")] int skip = 0,
		[Description("Maximum number of types to return. Default: 100, max: 500.")] int take = 100,
		[Description("Token from a previous response to get the next page without re-executing the query.")] string? page_token = null)
	{
		using var scope = BeginTool("roslyn_list_types", namespaceFilter);


		var cachedPage = TryServeCachedPage<string>(scope, page_token, ref skip, ref take, 500);
		if(cachedPage is not null)
			return cachedPage;

		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return error;

		var allTypes = new List<INamedTypeSymbol>();

		CollectTypes(compilation.GlobalNamespace, allTypes);

		var allResults = allTypes
			.Where(t => !t.IsImplicitlyDeclared)
			// Without a namespace filter, only return types defined in source — not the
			// thousands of types from referenced assemblies (the context-window bomb).
			.Where(t => namespaceFilter is not null || t.Locations.Any(l => l.IsInSource))
			.Where(t => MatchesNamespace(t, namespaceFilter))
			.Where(t => MatchesKind(t, kindFilter))
			.Select(t => SymbolFormatter.FormatType(t))
			.Order()
			.ToArray()
		;

		if(allResults.Length == 0)
			return new[] { "No types found matching the filters." };

		var result = PaginateAndStore(allResults, ref skip, take);

		return scope.Outcome($"{result.Items.Length}/{result.Total} type(s)", new ListTypesResult(
			Total_types: result.Total,
			Skip: skip, Take: take,
			Types:      result.Items,
			Page_token: result.PageToken,
			Has_more:   result.HasMore,
			_caution:   AdhocCaution(projectPath)
		));
	}
	
	private static void CollectTypes(INamespaceSymbol ns, List<INamedTypeSymbol> collector)
	{
		foreach(var member in ns.GetMembers()) {
		
			if(member is INamedTypeSymbol type)
				collector.Add(type);
			else if(member is INamespaceSymbol childNs)
				CollectTypes(childNs, collector);
		}
	}
	
	private static bool MatchesNamespace(INamedTypeSymbol type, string? filter)
	{
		if(filter is null)
			return true;
		
		var ns = type.ContainingNamespace?.ToDisplayString();
		
		if(ns is null)
			return false;
		
		// Match exact namespace or sub-namespace.
		return ns.Equals(filter, StringComparison.Ordinal)
			|| ns.StartsWith(filter + ".", StringComparison.Ordinal);
	}
	
	private static bool MatchesKind(INamedTypeSymbol type, string? filter)
	{
		if(filter is null)
			return true;
		
		return filter.ToLowerInvariant() switch {
			"class"     => type.TypeKind == TypeKind.Class,
			"interface" => type.TypeKind == TypeKind.Interface,
			"enum"      => type.TypeKind == TypeKind.Enum,
			"struct"    => type.TypeKind == TypeKind.Struct,
			"record"    => type.IsRecord,
			_           => true
		};
	}
	
}
