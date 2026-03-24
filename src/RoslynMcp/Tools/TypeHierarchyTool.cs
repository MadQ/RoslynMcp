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
        "and derived types found in the project. Use this to understand polymorphism and type relationships.")]
    public async Task<object> GetTypeHierarchy(
        [Description("The type name, e.g. 'WindowTracker' or 'RoslynMcp.WorkspaceManager'.")] string typeName,
        [Description(ProjectPathDescription)] string? projectPath = null)
    {
        using var _ = BeginTool("roslyn_get_type_hierarchy");
        if(!TryGetCompilation(projectPath, out var compilation, out var error))
            return error;

        var type        = FindType(compilation, typeName);

        if(type is null)
            return new { error = $"Type '{typeName}' not found in the project." };

        var baseTypes   = GetBaseTypeChain(type);
        var interfaces  = type.AllInterfaces
            .Select(i => i.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))
            .Order()
            .ToArray();

        var solution    = workspace.GetSolution(projectPath);
        var derivedRefs = await RoslynSymbolFinder.FindDerivedClassesAsync(type, solution);
        var derived     = derivedRefs
            .Select(d => d.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))
            .Order()
            .ToArray();

        return new {
            type_name       = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            type_kind       = type.TypeKind.ToString().ToLowerInvariant(),
            base_types      = baseTypes,
            interfaces      = interfaces,
            derived_types   = derived
        };
    }

    private static INamedTypeSymbol? FindType(Compilation compilation, string typeName)
    {
        // Try metadata name lookup first (handles fully-qualified names).
        var direct = compilation.GetTypeByMetadataName(typeName);

        if(direct is not null)
            return direct;

        // Fall back to simple name search.
        return compilation.GlobalNamespace
            .Accept(new SimpleNameFinder<INamedTypeSymbol>(typeName));
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