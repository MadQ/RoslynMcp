using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ListTypesTool : RoslynMcpTool
{
	public ListTypesTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_list_types", ReadOnly = true, Title = "List Types", OpenWorld = false, Idempotent = true)]
	[Description(
		"Use this to enumerate types defined in the project — classes, interfaces, enums, structs, and records. " +
		"Without a namespaceFilter, only source-defined types are returned (not the thousands of types from " +
		"referenced assemblies), which keeps the output manageable. " +
		"Provide namespaceFilter to scope results to a specific namespace and its sub-namespaces; " +
		"provide kindFilter to restrict by type kind. " +
		"Returns an alphabetically ordered list of fully-qualified type names with total count and paging metadata. " +
		"For a type's members, use roslyn_get_type_members. To find implementations of an interface, use roslyn_find_implementations.")]
	public object ListTypes(
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Optional namespace prefix filter, e.g. 'RoslynMcp.Tools'. Returns types in this namespace and all sub-namespaces. Omit for all source-defined types.")] string? namespaceFilter = null,
		[Description("Optional type kind filter: 'class', 'interface', 'enum', 'struct', 'record'. Omit for all kinds.")] string? kindFilter = null,
		[Description("Number of types to skip. Default: 0.")] int skip = 0,
		[Description("Maximum types to return. Default: 100, max: 500.")] int take = 100,
		[Description("Token from a previous response to get the next page without re-executing the query.")] string? page_token = null)
	{
		using var scope = BeginTool("roslyn_list_types", namespaceFilter);
		
		
		if(scope.TryServeCachedPage<string>(page_token, ref skip, ref take, 500, out var cached))
			return scope.Outcome("cached page", cached);
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return scope.Error(error!);
		
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
			return scope.Outcome("no types", new[] { "No types found matching the filters." });
		
		var result = PaginateAndStore(allResults, ref skip, take);
		
		return scope.Outcome($"{result.Items.Length}/{result.Total} type(s)", new ListTypesResult(
			TotalTypes: result.Total,
			Skip: skip, Take: take,
			Types:      result.Items,
			PageToken: result.PageToken,
			HasMore:   result.HasMore,
			Caution:   AdhocCaution(projectPath)
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
		
		return ns.Equals(filter, StringComparison.Ordinal) || ns.StartsWith(filter + ".", StringComparison.Ordinal);
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
