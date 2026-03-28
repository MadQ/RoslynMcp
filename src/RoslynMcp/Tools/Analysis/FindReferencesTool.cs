using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using RoslynSymbolFinder = Microsoft.CodeAnalysis.FindSymbols.SymbolFinder;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class FindReferencesTool : RoslynMcpTool
{
	public FindReferencesTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_find_references", ReadOnly = true)]
	[Description(
		"Finds all references to a named symbol (type, method, field, property) across the project. " +
		"Useful before renaming or refactoring to see every call site. Results are paged; use skip/take for large result sets.")]
	public async Task<object> FindReferences(
		[Description("The symbol name to find, e.g. 'WindowKey', 'RestoreFromPlacements', 'trackedWindows'.")] string symbolName,
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Optional type name to narrow the search, e.g. 'WindowTracker'.")] string? containingType = null,
		[Description("Number of references to skip (for paging). Default: 0.")] int skip = 0,
		[Description("Maximum number of references to return. Default: 50, max: 200.")] int take = 50,
		[Description("Token from a previous response to get the next page without re-executing the query.")] string? page_token = null)
	{
		using var scope = BeginTool("roslyn_find_references", symbolName);

		take = Math.Clamp(take, 1, 200);

		var cachedPage = TryServeCachedPage<string>(scope, page_token, ref skip, take);
		if(cachedPage is not null)
			return cachedPage;

		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return error;

		var solution = workspace.GetSolution(projectPath);
		var rootPath = workspace.GetRootPath(projectPath);
		var symbol   = FindSymbol(compilation, symbolName, containingType);

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

				return $"{file}:{span.StartLinePosition.Line + 1}";
			})
			.Distinct()
			.ToArray()
		;

		if(allResults.Length == 0)
			return new { total_references = 0, skip, take, references = new[] { $"No references found for '{symbolName}'." } };

		var result = PaginateAndStore(allResults, ref skip, take);

		return scope.Outcome($"{result.Items.Length}/{result.Total} reference(s)", new {
			total_references = result.Total,
			skip, take,
			references = result.Items,
			page_token = result.PageToken,
			has_more   = result.HasMore,
			_caution   = AdhocCaution(projectPath)
		});
	}
	
}
