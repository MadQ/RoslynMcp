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
		"For a solution (projectPath omitted with a solution as default workspace, or a .sln/.slnx given) every project's types are listed, " +
		"each ending in its project: 'Lib.Greeter [Lib]'. " +
		"For a type's members, use roslyn_get_type_members. To find implementations of an interface, use roslyn_find_implementations.")]
	public async Task<object> ListTypes(
		CancellationToken cancellationToken,
		[Description(OptionalProjectPathDescription)] string? projectPath = null,
		[Description("Optional namespace prefix filter, e.g. 'RoslynMcp.Tools'. Returns types in this namespace and all sub-namespaces. Omit for all source-defined types.")] string? namespaceFilter = null,
		[Description("Optional type kind filter: 'class', 'interface', 'enum', 'struct', 'record'. Omit for all kinds.")] string? kindFilter = null,
		[Description("Number of types to skip. Default: 0.")] int skip = 0,
		[Description("Maximum types to return. Default: 100, max: 500.")] int take = 100,
		[Description(PageTokenDescription)] string? page_token = null)
	{
		using var scope = BeginTool("roslyn_list_types", namespaceFilter, new { projectPath, kindFilter, skip, take });
		
		if(scope.TryServeCachedPage<string>(page_token, ref skip, ref take, 100, 500, out var cached))
			
			return scope.Outcome("cached page", cached);
		
		if(!TryResolveProjectArg(projectPath, out projectPath, out var projectError))
			
			return scope.Error(projectError);
		
		// Every project the path stands for (#296): a solution's projects one by one, where the
		// tool used to answer for the first one alone, or refuse.
		var (targets, targetError) = await GetTargetsAsync(projectPath, forFile: false, cancellationToken);
		
		if(targetError is not null)
			
			return scope.Error(targetError);
		
		var several = targets.Select(target => target.Project?.FilePath).OfType<string>().Distinct(SecurityBoundary.PathComparer).Count() > 1;
		
		// Distinct: the frameworks of a multi-targeted project list the same types.
		var allResults = targets
			.SelectMany(target => TypesOf(target.Compilation, namespaceFilter, kindFilter, several ? target.Project : null))
			.Distinct()
			.Order()
			.ToArray()
		;
		
		if(allResults.Length == 0)
			
			return scope.Outcome("no types", new ListTypesEmptyResult("No types found matching the filters."));
		
		var shape  = PageShape(AdhocCaution(projectPath));
		var result = PaginateAndStore(allResults, ref skip, take, shape);
		
		return scope.Outcome($"{result.Items.Length}/{result.Total} type(s)", shape(result));
	}
	
	/// <summary>
	///     The types of one compilation that pass the filters, formatted. With <paramref name="project"/>
	///     given, the compilation is one project of several: a type declared in another project of
	///     the solution is left to that project's own turn, and each type declared here ends in its
	///     project's name, <c>Lib.Greeter [Lib]</c>. A type from a referenced assembly, which a
	///     namespace filter can ask for, belongs to no project and is listed once, bare.
	/// </summary>
	static IEnumerable<string> TypesOf(Compilation compilation, string? namespaceFilter, string? kindFilter, Project? project)
	{
		var allTypes = new List<INamedTypeSymbol>();
		
		CollectTypes(compilation.GlobalNamespace, allTypes);
		
		foreach(var type in allTypes) {
			
			var inSource = type.Locations.Any(l => l.IsInSource);
			var own      = SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly);
			
			// Without a namespace filter, only return types defined in source — not the
			// thousands of types from referenced assemblies (the context-window bomb).
			if(type.IsImplicitlyDeclared
				|| (namespaceFilter is null && !inSource)
				|| (project is not null && inSource && !own)
				|| !MatchesNamespace(type, namespaceFilter)
				|| !MatchesKind(type, kindFilter))
				continue;
			
			var name = SymbolFormatter.FormatType(type);
			
			yield return project is not null && own ? $"{name} [{Path.GetFileNameWithoutExtension(project.FilePath)}]" : name;
		}
	}
	
	// Builds every page of one query, first and cached alike. A static factory, so the function the
	// pagination cache holds captures only what is passed in — never the compilation.
	static Func<PaginatedResult<string>, object> PageShape(string? caution)
		=> page => new ListTypesResult(
			TotalTypes: page.Total,
			Skip: page.Skip, Take: page.Take,
			Types:      page.Items,
			PageToken: page.PageToken,
			HasMore:   page.HasMore)
		{
			Caution = caution
		}
	;
	
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
