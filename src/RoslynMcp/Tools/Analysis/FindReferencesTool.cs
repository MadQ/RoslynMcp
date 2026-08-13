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
		"use this instead of grep for symbol searches; text search cannot resolve overloads, aliases, or cross-file semantics. " +
		"Essential before renaming or refactoring to understand full impact. " +
		"Without containingType, searches ALL symbols matching the name and returns the union of their references, " +
		"ensuring no call sites are missed when multiple types share a member name. " +
		"Provide containingType to search only the symbol declared on that specific type. " +
		"Returns references as project-relative file:line strings (e.g. 'src/Foo.cs:42'). " +
		"Use roslyn_find_implementations instead to discover subtypes or method overrides. " +
		"Pass filePath+line (and optional column) to resolve the symbol at an exact source position instead of " +
		"by name — the precise way to target one specific overload, local, or parameter. " +
		"Results are paged; use skip/take for large result sets.")]
	public async Task<object> FindReferences(
		[Description("The symbol name to find, e.g. 'WindowKey', 'RestoreFromPlacements', 'trackedWindows'.")] string symbolName,
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken,
		[Description("Optional type name to disambiguate when multiple types have a member with the same name, e.g. 'WindowTracker'. Without this, all symbols matching symbolName are searched and their references are combined.")] string? containingType = null,
		[Description("Number of references to skip. Default: 0.")] int skip = 0,
		[Description("Maximum number of references to return. Default: 50, max: 200.")] int take = 50,
		[Description("Token from a previous response to get the next page without re-executing the query.")] string? page_token = null,
		[Description("Optional relative file path for position-based resolution, e.g. 'Core/Foo.cs'. Required when line is specified.")] string? filePath = null,
		[Description("Optional 1-based line number. When > 0, resolves the symbol at filePath:line:column instead of by name.")] int line = 0,
		[Description("1-based column for position-based resolution. Default: 1.")] int column = 1)
	{
		using var scope = BeginTool("roslyn_find_references", symbolName, new { containingType, skip, take, line });
		
		if(!TryStripChatSymbolRef(ref symbolName, out var refError) || !TryStripChatSymbolRefOptional(ref containingType, out refError))
			
			return scope.Error(refError!);
		
		
		if(scope.TryServeCachedPage<string>(page_token, ref skip, ref take, 200, out var cached))
			
			return scope.Outcome("cached page", cached);
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return scope.Error(error!);
		
		var solution = workspace.GetSolution(projectPath);
		var rootPath = workspace.GetRootPath(projectPath);
		
		// Position (line > 0) pinpoints one symbol. Otherwise, when containingType is
		// specified, search one symbol; without it search ALL symbols matching the name —
		// prevents silently incomplete results when multiple types share a member name.
		ISymbol[] symbols;
		
		if(line > 0) {
			
			if(filePath is null)
				
				return scope.Error(new ErrorResult("filePath is required when line is specified."));
			
			symbols = await FindSymbolAtPosition(compilation, filePath, line, column, cancellationToken) is { } positional
				? [positional]
				: [];
		}
		else
			symbols = FindSymbols(compilation, symbolName, containingType);
		
		if(symbols.Length == 0)
			
			return scope.Failed("symbol not found", SymbolNotFoundError(symbolName));
		
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
			
			return scope.Outcome("no references", new FindReferencesResult(0, [], skip, take, [$"No references found for '{symbolName}'."], null, false) { Caution = AdhocCaution(projectPath) });
		
		string[] symbolsSearched = [.. symbols.Select(s => FormatSymbolName(s)).Distinct()];
		
		var result = PaginateAndStore(allResults, ref skip, take);
		
		return scope.Outcome($"{result.Items.Length}/{result.Total} reference(s) across {symbolsSearched.Length} symbol(s)", new FindReferencesResult(
			TotalReferences: result.Total,
			SymbolsSearched: symbolsSearched,
			Skip: skip, Take: take,
			References: result.Items,
			PageToken: result.PageToken,
			HasMore:   result.HasMore)
		{
			Caution = AdhocCaution(projectPath)
		});
	}

}
