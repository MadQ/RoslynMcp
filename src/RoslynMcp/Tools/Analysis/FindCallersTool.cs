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
		"Call this before refactoring or removing a method to discover every caller across the project — " +
		"the inverse of roslyn_find_references, and semantically impossible with text search, which cannot " +
		"distinguish overloads or track aliases. " +
		"Returns every calling line, grouped by file and, within a file, by the declaration it is in, as roslyn_search_files lists its hits: " +
		"callers[].in[] names the caller ('method Enqueue'), its lines ('108-158': pass them to roslyn_read_file to read the whole caller) " +
		"and where its node starts ('112:2': the line and column roslyn_replace_in_code takes), followed by matches[] with each calling line and its text. " +
		"So a call can be judged, its caller read in full or edited without another lookup. Two calls on one line are listed once; take counts calling lines. " +
		"Without containingType, searches ALL symbols matching the name in the project; provide containingType to narrow to one type. " +
		"With no projectPath, or a solution, the project is the one that declares the name: a name declared in several projects " +
		"fails with the candidates and their projects, never a union across them. " +
		"By default returns only direct callers (isDirect=true); set isDirect=false to also include indirect " +
		"calls via interface dispatch or delegates. " +
		"Results are paged; pass page_token from a previous response to get subsequent pages. " +
		"Pass filePath+line (and optional column) to resolve the callee at an exact source position instead of " +
		"by name — the precise way to target one specific overload. " +
		"Pair with roslyn_get_call_graph to also trace what the callee itself depends on.")]
	public async Task<object> FindCallers(
		[Description("Symbol name to find callers of, e.g. 'GetCompilation', 'ProcessOrder'.")] string symbolName,
		CancellationToken cancellationToken,
		[Description(OptionalProjectPathDescription)] string? projectPath = null,
		[Description("Optional containing type to disambiguate when multiple types share a method name, e.g. 'WorkspaceManager'.")] string? containingType = null,
		[Description("When true (default), returns only direct callers. Set false to include indirect calls via interface dispatch or delegates.")] bool isDirect = true,
		[Description("Number of calling lines to skip. Default: 0.")] int skip = 0,
		[Description("Maximum number of calling lines to return. Default: 50, max: 200.")] int take = 50,
		[Description(PageTokenDescription)] string? page_token = null,
		[Description("Optional relative file path for position-based resolution, e.g. 'Core/Foo.cs'. Required when line is specified.")] string? filePath = null,
		[Description("Optional 1-based line number. When > 0, resolves the symbol at filePath:line:column instead of by name.")] int line = 0,
		[Description("1-based column for position-based resolution. Default: 1.")] int column = 1)
	{
		using var scope = BeginTool("roslyn_find_callers", symbolName, new { projectPath, containingType, isDirect, skip, take, filePath, line, column });
		
		if(!TryStripChatSymbolRef(ref symbolName, out var refError) || !TryStripChatSymbolRefOptional(ref containingType, out refError))
			
			return scope.Error(refError!);
		
		if(scope.TryServeCachedPage<SourcePlace>(page_token, ref skip, ref take, 50, 200, out var cached))
			
			return scope.Outcome("cached page", cached);
		
		var (resolvedProject, projectError) = await ResolveSymbolProjectArgAsync(projectPath, symbolName, containingType, SymbolSought.All, filePath, cancellationToken);
		
		if(projectError is not null)
			
			return scope.Error(projectError);
		
		projectPath = resolvedProject;
		
		NoteSolutionEdge(projectPath);
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return scope.Error(error!);
		
		if(!TryResolveSolution(projectPath, out var solution, out var solutionError))
			
			return scope.Error(solutionError);
		
		if(!TryResolveRoot(projectPath, out var rootPath, out var rootError))
			
			return scope.Error(rootError);
		
		// Position (line > 0) pinpoints one symbol — the precise way to target one overload.
		ISymbol[] symbols;
		
		if(line > 0) {
			
			if(filePath is null)
				
				return scope.Error(new ErrorResult("filePath is required when line is specified."));
			
			var tree = FindSyntaxTree(compilation, filePath, projectPath, rootPath, out var ambiguous);
			
			if(ambiguous.Length > 0)
				
				return scope.Error(AmbiguousSourceFileError(filePath, ambiguous));
			
			symbols = await FindSymbolAtPosition(compilation, tree, line, column, cancellationToken) is { } positional
				? [positional]
				: [];
		}
		else
			symbols = FindSymbols(compilation, symbolName, containingType);
		
		if(symbols.Length == 0)
			
			return scope.Failed("symbol not found", SymbolNotFoundError(symbolName, projectPath));
		
		var allCallers = new List<SourcePlace>();
		
		foreach(var sym in symbols) {
			
			var callerInfos = await RoslynSymbolFinder.FindCallersAsync(sym, solution, cancellationToken);
			
			// The caller is not kept by name: the declaration a calling line is in is the caller,
			// and the answer names it once for all its calls (#487).
			allCallers.AddRange(
				callerInfos
					.Where(c => !isDirect || c.IsDirect)
					.SelectMany(c => c.Locations)
					.Select(location => SourcePlace.Of(location, rootPath, cancellationToken))
			);
		}
		
		var allResults = SourcePlace.Sorted(allCallers);
		
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
		
		var shape  = PageShape(symbolName, AdhocCaution(projectPath));
		var result = PaginateAndStore(allResults, ref skip, take, shape);
		
		// How the page grouped, and what the same places took as file:line strings (#487).
		scope.Record(SourcePlace.GroupingNote(result.Items, "calling line(s)"));
		
		return scope.Outcome($"{result.Items.Length}/{result.Total} calling line(s)", shape(result));
	}
	
	// Builds every page of one query, first and cached alike. A static factory taking plain values, so
	// the function the pagination cache holds never captures a symbol, the compilation, or the solution.
	static Func<PaginatedResult<SourcePlace>, object> PageShape(string symbolSearched, string? caution)
		=> page => new FindCallersResult(
			SymbolSearched: symbolSearched,
			TotalCallers:   page.Total,
			Skip: page.Skip, Take: page.Take,
			Callers:   SourcePlace.Group(page.Items, sayDeclares: false),
			PageToken: page.PageToken,
			HasMore:   page.HasMore)
		{
			Caution = caution
		}
	;
}
