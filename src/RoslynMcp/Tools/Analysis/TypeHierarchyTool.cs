using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using RoslynSymbolFinder = Microsoft.CodeAnalysis.FindSymbols.SymbolFinder;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class TypeHierarchyTool : RoslynMcpTool
{
	public TypeHierarchyTool(WorkspaceResolver workspace, FileLogger logger) : base(workspace, logger) { }
	
	[McpServerTool(Name = "roslyn_get_type_hierarchy", ReadOnly = true)]
	[Description(
		"Returns the inheritance hierarchy for a type: base types (chain to object/ValueType), implemented interfaces, " +
		"and derived types found in the project. Use this to understand polymorphism and type relationships. " +
		"Derived types and interfaces are paged; use skip/take for large hierarchies.")]
	public async Task<object> GetTypeHierarchy(
		[Description("The type name, e.g. 'WindowTracker' or 'RoslynMcp.WorkspaceManager'.")] string typeName,
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Number of derived types/interfaces to skip (for paging). Default: 0.")] int skip = 0,
		[Description("Maximum number of derived types/interfaces to return. Default: 50, max: 200.")] int take = 50)
	{
		using var scope = BeginTool("roslyn_get_type_hierarchy", typeName);
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return error;
		
		take = Math.Clamp(take, 1, 200);
		
		var type        = FindType(compilation, typeName);
		
		if(type is null)
			return scope.Failed("type not found", new { error = $"Type '{typeName}' not found in the project." });
		
		var baseTypes   = GetBaseTypeChain(type);
		string[] allInterfaces = [..
			type.AllInterfaces
				.Select(i => i.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))
				.Order()
		];
		
		var solution    = workspace.GetSolution(projectPath);
		var derivedRefs = await RoslynSymbolFinder.FindDerivedClassesAsync(type, solution);
		string[] allDerived = [..
			derivedRefs
				.Select(d => d.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))
				.Order()
		];
		
		// Page both interfaces
		var combined = allInterfaces.Concat(allDerived).ToArray()
		;
		var page     = combined.AsSpan(skip, Math.Min(take, combined.Length - skip)).ToArray();
		
		return scope.Outcome($"{page.Length} interface(s)/derived", new {
			type_name           = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
			type_kind           = type.TypeKind.ToString().ToLowerInvariant(),
			base_types          = baseTypes,
			total_interfaces    = allInterfaces.Length,
			total_derived_types = allDerived.Length,
			skip,
			take,
			interfaces_and_derived = page,
			_caution            = AdhocCaution(projectPath)
		});
	}
	
	private static INamedTypeSymbol? FindType(Compilation compilation, string typeName)
	{
		// Try metadata name lookup first (handles fully-qualified names).
		var direct = compilation.GetTypeByMetadataName(typeName);
		
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