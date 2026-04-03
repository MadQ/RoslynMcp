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
	
	[McpServerTool(Name = "roslyn_find_references", ReadOnly = true, Title = "Find References", OpenWorld = false, Idempotent = true)]
	[Description(
		"Finds all usages (call sites, type references, field accesses) of a named symbol across the project — " +
		"essential before renaming or refactoring to understand full impact. " +
		"Without containingType, searches ALL symbols matching the name and returns the union of their references, " +
		"ensuring no call sites are missed when multiple types share a member name. " +
		"Provide containingType to search only the symbol declared on that specific type. " +
		"Returns references as project-relative file:line strings (e.g. 'src/Foo.cs:42'). " +
		"Use roslyn_find_implementations instead to discover subtypes or method overrides. " +
		"Results are paged; use skip/take for large result sets.")]
	public async Task<object> FindReferences(
		[Description("The symbol name to find, e.g. 'WindowKey', 'RestoreFromPlacements', 'trackedWindows'.")] string symbolName,
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken,
		[Description("Optional type name to disambiguate when multiple types have a member with the same name, e.g. 'WindowTracker'. Without this, all symbols matching symbolName are searched and their references are combined.")] string? containingType = null,
		[Description("Number of references to skip. Default: 0.")] int skip = 0,
		[Description("Maximum number of references to return. Default: 50, max: 200.")] int take = 50,
		[Description("Token from a previous response to get the next page without re-executing the query.")] string? page_token = null)
	{
		using var scope = BeginTool("roslyn_find_references", symbolName);
		
		
		var cachedPage = TryServeCachedPage<string>(scope, page_token, ref skip, ref take, 200);
		if(cachedPage is not null)
			
			return cachedPage;
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return error;
		
		var solution = workspace.GetSolution(projectPath);
		var rootPath = workspace.GetRootPath(projectPath);
		
		// When containingType is specified, search one symbol. Otherwise search ALL
		// symbols matching the name — prevents silently incomplete results when
		// multiple types have members with the same name.
		ISymbol[] symbols
		;
		
		if(containingType is not null) {
			
			var symbol = FindSymbol(compilation, symbolName, containingType);
			symbols = symbol is not null ? [symbol] : [];
		}
		else {
			
			var finder = new AllSymbolsFinder(symbolName);
			finder.Visit(compilation.Assembly.GlobalNamespace);
			symbols = [.. finder.Results];
		}
		
		if(symbols.Length == 0)
			
			return scope.Failed("symbol not found", new ErrorResult($"Symbol '{symbolName}' not found.", Hint: "Use get_type_members or find_references to verify the name."));
		
		var allLocations = new List<string>();
		
		foreach(var sym in symbols) {
			
			var refs = await RoslynSymbolFinder.FindReferencesAsync(sym, solution, cancellationToken: cancellationToken);
			
			allLocations.AddRange(
				refs.SelectMany(r => r.Locations)
					.Select(l => {
						
						var span = l.Location.GetLineSpan();
						var file = span.Path is { Length: > 0 } p
							? Path.GetRelativePath(rootPath, p)
							: "?";
						
						return $"{file}:{span.StartLinePosition.Line + 1}";
					})
			);
		}
		
		var allResults = allLocations
			.Distinct()
			.Order()
			.ToArray()
		;
		
		if(allResults.Length == 0)
			
			return new FindReferencesResult(0, [], skip, take, [$"No references found for '{symbolName}'."], "", false, AdhocCaution(projectPath));
		
		string[] symbolsSearched = [.. symbols.Select(s => FormatSymbolName(s)).Distinct()];
		
		var result = PaginateAndStore(allResults, ref skip, take);
		
		return scope.Outcome($"{result.Items.Length}/{result.Total} reference(s) across {symbolsSearched.Length} symbol(s)", new FindReferencesResult(
			Total_references: result.Total,
			Symbols_searched: symbolsSearched,
			Skip: skip, Take: take,
			References: result.Items,
			Page_token: result.PageToken,
			Has_more:   result.HasMore,
			_caution:   AdhocCaution(projectPath)
		));
	}

}
