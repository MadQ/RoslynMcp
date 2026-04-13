using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using RoslynSymbolFinder = Microsoft.CodeAnalysis.FindSymbols.SymbolFinder;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class FindCallersTool : RoslynMcpTool
{
	public FindCallersTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_find_callers", ReadOnly = true, Title = "Find Callers", OpenWorld = false, Idempotent = true)]
	[Description(
		"Returns all methods that call the named symbol — the inverse of roslyn_find_references, " +
		"and semantically impossible with text search alone. " +
		"Each result includes the calling method's fully qualified name, file path, and 1-based line number of the call site. " +
		"Without containingType, searches ALL symbols matching the name; provide containingType to narrow to one type. " +
		"By default returns only direct callers (isDirect=true); set isDirect=false to also include indirect calls via interface dispatch or delegates. " +
		"Results are paged; pass page_token from a previous response to get subsequent pages. " +
		"Pair with roslyn_get_call_graph to trace what the callee itself depends on.")]
	public async Task<object> FindCallers(
		[Description("Symbol name to find callers of, e.g. 'GetCompilation', 'ProcessOrder'.")] string symbolName,
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken,
		[Description("Optional containing type to disambiguate when multiple types share a method name, e.g. 'WorkspaceManager'.")] string? containingType = null,
		[Description("When true (default), returns only direct callers. Set false to include indirect calls via interface dispatch or delegates.")] bool isDirect = true,
		[Description("Number of callers to skip. Default: 0.")] int skip = 0,
		[Description("Maximum number of callers to return. Default: 50, max: 200.")] int take = 50,
		[Description("Token from a previous response to get the next page without re-executing the query.")] string? page_token = null)
	{
		using var scope = BeginTool("roslyn_find_callers", symbolName, new { containingType, isDirect, skip, take });
		
		if(scope.TryServeCachedPage<CallerEntry>(page_token, ref skip, ref take, 200, out var cached))
			
			return scope.Outcome("cached page", cached);
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return scope.Error(error!);
		
		var solution = workspace.GetSolution(projectPath);
		var rootPath = workspace.GetRootPath(projectPath);
		
		var symbols = FindSymbols(compilation, symbolName, containingType);
		
		if(symbols.Length == 0)
			
			return scope.Failed("symbol not found", SymbolNotFoundError(symbolName));
		
		var allCallers = new List<CallerEntry>();
		
		foreach(var sym in symbols) {
			
			var callerInfos = await RoslynSymbolFinder.FindCallersAsync(sym, solution, cancellationToken);
			
			allCallers.AddRange(
				callerInfos
					.Where(c => !isDirect || c.IsDirect)
					.SelectMany(c => c.Locations.Select(loc => {
						
						var span = loc.GetLineSpan();
						var file = span.Path is { Length: > 0 } p
							? Path.GetRelativePath(rootPath, p)
							: "?";
						
						return new CallerEntry(
							Caller: FormatSymbolName(c.CallingSymbol),
							File:   file,
							Line:   span.StartLinePosition.Line + 1
						);
					}))
			);
		}
		
		var allResults = allCallers
			.DistinctBy(c => (c.Caller, c.File, c.Line))
			.OrderBy(c => c.File)
			.ThenBy(c => c.Line)
			.ToArray()
		;
		
		if(allResults.Length == 0)
			
			return scope.Outcome("no callers found", new FindCallersResult(
				SymbolSearched: symbolName,
				TotalCallers:   0,
				Skip: skip, Take: take,
				Callers:   [],
				PageToken: null,
				HasMore:   false)
			{
				Caution = AdhocCaution(projectPath)
			});
		
		var result = PaginateAndStore(allResults, ref skip, take);
		
		return scope.Outcome($"{result.Items.Length}/{result.Total} caller(s)", new FindCallersResult(
			SymbolSearched: symbolName,
			TotalCallers:   result.Total,
			Skip: skip, Take: take,
			Callers:   result.Items,
			PageToken: result.PageToken,
			HasMore:   result.HasMore)
		{
			Caution = AdhocCaution(projectPath)
		});
	}
}
