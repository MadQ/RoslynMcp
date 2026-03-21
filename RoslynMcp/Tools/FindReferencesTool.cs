using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using RoslynSymbolFinder = Microsoft.CodeAnalysis.FindSymbols.SymbolFinder;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class FindReferencesTool(WorkspaceManager workspace)
{
    [McpServerTool, Description(
        "Finds all references to a named symbol (type, method, field, property) across the project. " +
        "Useful before renaming or refactoring to see every call site.")]
    public async Task<string[]> FindReferences(
        [Description("The symbol name to find, e.g. 'WindowKey', 'RestoreFromPlacements', 'trackedWindows'.")] string symbolName,
        [Description("Optional type name to narrow the search, e.g. 'WindowTracker'.")] string? containingType = null)
    {
        var compilation = workspace.GetCompilation();
        var solution    = workspace.GetSolution();

        // Find the symbol declaration.
        var symbol = FindSymbol(compilation, symbolName, containingType);

        if(symbol is null)
            return [$"Symbol '{symbolName}' not found."];

        var refs = await RoslynSymbolFinder.FindReferencesAsync(symbol, solution);

        var results = refs
            .SelectMany(r => r.Locations)
            .OrderBy(l => l.Location.SourceTree?.FilePath)
            .ThenBy(l => l.Location.GetLineSpan().StartLinePosition.Line)
            .Select(l => {
                var span = l.Location.GetLineSpan();
                var file = span.Path is { Length: > 0 } p
                    ? Path.GetRelativePath(workspace.RootPath, p)
                    : "?";
                var line = span.StartLinePosition.Line + 1;

                return $"{file}:{line}";
            })
            .Distinct()
            .ToArray()
        ;

        return results.Length > 0 ? results : [$"No references found for '{symbolName}'."];
    }

    private static ISymbol? FindSymbol(Compilation compilation, string name, string? inType)
    {
        if(inType is not null) {
            var type = compilation.GetTypeByMetadataName(inType)
                ?? compilation.GlobalNamespace.Accept(new SimpleNameFinder<INamedTypeSymbol>(inType));

            return type?.GetMembers(name).FirstOrDefault();
        }

        return compilation.GlobalNamespace.Accept(new AnySymbolFinder(name));
    }
}
