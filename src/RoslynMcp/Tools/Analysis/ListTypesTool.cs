using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ListTypesTool : RoslynMcpTool
{
    public ListTypesTool(WorkspaceResolver workspace, FileLogger logger) : base(workspace, logger) { }

    [McpServerTool(Name = "roslyn_list_types", ReadOnly = true)]
    [Description(
        "Lists all types (classes, interfaces, enums, structs, records) in the project. " +
        "Optionally filter by namespace or type kind. Use this to discover what's available in the codebase.")]
    public string[] ListTypes(
        [Description("Optional namespace filter, e.g. 'RoslynMcp.Tools'. Types in this namespace and its sub-namespaces are returned.")] string? namespaceFilter = null,
        [Description("Optional type kind filter: 'class', 'interface', 'enum', 'struct'. Omit for all types.")] string? kindFilter = null,
        [Description(ProjectPathDescription)] string? projectPath = null)
    {
        using var scope = BeginTool("roslyn_list_types", namespaceFilter);
        if(!TryGetCompilation(projectPath, out var compilation, out var error))
            return [error.ToString()!];


        var allTypes    = new List<INamedTypeSymbol>();

        // Walk namespace tree.
        CollectTypes(compilation.GlobalNamespace, allTypes);

        var filtered = allTypes
            .Where(t => !t.IsImplicitlyDeclared)
            .Where(t => MatchesNamespace(t, namespaceFilter))
            .Where(t => MatchesKind(t, kindFilter))
            .Select(t => FormatType(t))
            .Order();

        // Materialize only when needed for response.
        var results = filtered.ToArray();

        return results.Length > 0
            ? scope.Outcome($"{results.Length} type(s)", results)
            : ["No types found matching the filters."];
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

    private static string FormatType(INamedTypeSymbol type)
    {
        var kind = type.TypeKind.ToString().ToLowerInvariant();
        var name = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

        return $"{kind}: {name}";
    }
}
