using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using RoslynSymbolFinder = Microsoft.CodeAnalysis.FindSymbols.SymbolFinder;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class FindReferencesTool : RoslynMcpTool
{
    public FindReferencesTool(WorkspaceResolver workspace, FileLogger logger) : base(workspace, logger) { }

    [McpServerTool(Name = "roslyn_find_references", ReadOnly = true)]
    [Description(
        "Finds all references to a named symbol (type, method, field, property) across the project. " +
        "Useful before renaming or refactoring to see every call site. Results are paged; use skip/take for large result sets.")]
    public async Task<object> FindReferences(
        [Description("The symbol name to find, e.g. 'WindowKey', 'RestoreFromPlacements', 'trackedWindows'.")] string symbolName,
        [Description("Optional type name to narrow the search, e.g. 'WindowTracker'.")] string? containingType = null,
        [Description("Number of references to skip (for paging). Default: 0.")] int skip = 0,
        [Description("Maximum number of references to return. Default: 50, max: 200.")] int take = 50,
        [Description(ProjectPathDescription)] string? projectPath = null)
    {
        using var scope = BeginTool("roslyn_find_references", symbolName);
        if(!TryGetCompilation(projectPath, out var compilation, out var error))
            return error;

        take = Math.Clamp(take, 1, 200);

        var solution    = workspace.GetSolution(projectPath);
        var rootPath    = workspace.GetRootPath(projectPath);

        // Find the symbol declaration.
        var symbol = FindSymbol(compilation, symbolName, containingType);

        if(symbol is null)
            return scope.Failed("symbol not found", new { error = $"Symbol '{symbolName}' not found." });

        var refs = await RoslynSymbolFinder.FindReferencesAsync(symbol, solution);

        var allResults = refs
            .SelectMany(r => r.Locations)
            .OrderBy(l => l.Location.SourceTree?.FilePath)
            .ThenBy(l => l.Location.GetLineSpan().StartLinePosition.Line)
            .Select(l => {
                var span = l.Location.GetLineSpan();
				var file = span.Path is { Length: > 0 } p
					? Path.GetRelativePath(rootPath, p)
					: "?";

                var line = span.StartLinePosition.Line + 1;

                return $"{file}:{line}";
            })
            .Distinct()
            .ToArray()
        ;

        if(allResults.Length == 0)
            return new { total_references = 0, skip, take, references = new[] { $"No references found for '{symbolName}'." } };

        var page = allResults.AsSpan(skip, Math.Min(take, allResults.Length - skip)).ToArray();

        return scope.Outcome($"{page.Length}/{allResults.Length} reference(s)", new {
            total_references = allResults.Length,
            skip,
            take,
            references = page
        });
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
