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
		"With no projectPath, or a solution, the project is the one that declares the name: a name declared in several projects " +
		"fails with the candidates and their projects, never a union across them. " +
		"Without containingType, searches ALL symbols matching the name in the project and returns the union of their references, " +
		"ensuring no call sites are missed when multiple types share a member name. " +
		"Provide containingType to search only the symbol declared on that specific type. " +
		"Returns references as project-relative file:line strings (e.g. 'src/Foo.cs:42'). " +
		"Use roslyn_find_implementations instead to discover subtypes or method overrides. " +
		"Pass filePath+line (and optional column) to resolve the symbol at an exact source position instead of " +
		"by name — the precise way to target one specific overload, local, or parameter. " +
		"The answer also says where the symbol is declared (declarations, as file:line), so one call answers both where it is " +
		"and who uses it. " +
		"Several names can be asked in one call (symbolName 'A|B|C'): the answer is then symbols[], one entry per name with its " +
		"declarations, its total and its first references (take applies per name, and there is no page token: ask a name alone " +
		"to page through it), or with that name's own error. " +
		"Results are paged; use skip/take for large result sets.")]
	public async Task<object> FindReferences(
		[Description("The symbol name to find, e.g. 'WindowKey', 'RestoreFromPlacements', 'trackedWindows'." + SeveralNamesDescription)] string symbolName,
		CancellationToken cancellationToken,
		[Description(OptionalProjectPathDescription)] string? projectPath = null,
		[Description("Optional type name to disambiguate when multiple types have a member with the same name, e.g. 'WindowTracker'. Without this, all symbols matching symbolName are searched and their references are combined.")] string? containingType = null,
		[Description("Number of references to skip. Default: 0.")] int skip = 0,
		[Description("Maximum number of references to return. Default: 50, max: 200.")] int take = 50,
		[Description(PageTokenDescription)] string? page_token = null,
		[Description("Optional relative file path for position-based resolution, e.g. 'Core/Foo.cs'. Required when line is specified.")] string? filePath = null,
		[Description("Optional 1-based line number. When > 0, resolves the symbol at filePath:line:column instead of by name.")] int line = 0,
		[Description("1-based column for position-based resolution. Default: 1.")] int column = 1)
	{
		using var scope = BeginTool("roslyn_find_references", symbolName, new { projectPath, containingType, skip, take, filePath, line, column });
		
		if(!TryStripChatSymbolRef(ref symbolName, out var refError) || !TryStripChatSymbolRefOptional(ref containingType, out refError))
			
			return scope.Error(refError!);
		
		if(scope.TryServeCachedPage<string>(page_token, ref skip, ref take, 50, 200, out var cached))
			
			return scope.Outcome("cached page", cached);
		
		// A position names one symbol, whatever the name argument holds. Otherwise the argument is a
		// name or a list of names (#459), and a list that comes to one name ('Greet|') is that name.
		var names = line > 0 ? [] : SplitSymbolNames(symbolName);
		
		if(names is [var only])
			symbolName = only;
		
		// Several names: each is one lookup, with its own project and its own answer.
		if(names.Length > 1) {
			
			var entries = new List<NamedReferences>(names.Length);
			var found   = 0;
			
			foreach(var name in names) {
				
				var (references, failed, failure) = await ReferencesOfAsync(name, containingType, projectPath, filePath, cancellationToken);
				
				if(references is null) {
					
					// The entry's detail counts the names found; which one failed, and why, is this.
					scope.Record($"{name}: {failure}");
					entries.Add(new(name) { Failed = failed });
					
					continue;
				}
				
				found++;
				entries.Add(new(name) {
					
					Declarations    = references.Declarations,
					SymbolsSearched = references.Searched,
					TotalReferences = references.Locations.Length,
					References      = references.Locations.Length > take ? references.Locations[..take] : references.Locations,
					HasMore         = references.Locations.Length > take ? true : null,
				});
			}
			
			var answer = new SymbolsReferencesResult([..entries], found, names.Length - found) {
				
				// A cursor's position arrives as skip too, so this covers a page token sent with a list.
				Hint = skip > 0 ? "skip and page_token do not apply to a list of names: each name's first references are listed. Ask one name alone to page through its references." : null,
			};
			
			return found > 0
				? scope.Outcome($"{found}/{names.Length} name(s) found, {entries.Sum(entry => entry.TotalReferences ?? 0)} reference(s)", answer)
				: scope.Failed($"none of {names.Length} name(s) found", answer);
		}
		
		string[] allResults, declarations, symbolsSearched;
		
		// By name: when containingType is specified, one symbol; without it ALL symbols matching the
		// name, which prevents silently incomplete results when multiple types share a member name.
		if(line <= 0) {
			
			var (references, failed, failure) = await ReferencesOfAsync(symbolName, containingType, projectPath, filePath, cancellationToken);
			
			if(references is null)
				
				return scope.Failed(failure!, failed!);
			
			projectPath = references.Project;
			(allResults, declarations, symbolsSearched) = (references.Locations, references.Declarations, references.Searched);
		}
		else {
			
			// A position (line > 0) pinpoints one symbol.
			var (resolvedProject, projectError) = await ResolveSymbolProjectArgAsync(projectPath, symbolName, containingType, SymbolSought.All, filePath, cancellationToken);
			
			if(projectError is not null)
				
				return scope.Error(projectError);
			
			projectPath = resolvedProject;
			
			if(!TryGetCompilation(projectPath, out var compilation, out var error))
				
				return scope.Error(error);
			
			if(!TryResolveSolution(projectPath, out var solution, out var solutionError))
				
				return scope.Error(solutionError);
			
			if(!TryResolveRoot(projectPath, out var rootPath, out var rootError))
				
				return scope.Error(rootError);
			
			if(filePath is null)
				
				return scope.Error(new ErrorResult("filePath is required when line is specified."));
			
			var tree = FindSyntaxTree(compilation, filePath, projectPath, rootPath, out var ambiguous);
			
			if(ambiguous.Length > 0)
				
				return scope.Error(AmbiguousSourceFileError(filePath, ambiguous));
			
			if(await FindSymbolAtPosition(compilation, tree, line, column, cancellationToken) is not { } positional)
				
				return scope.Failed("symbol not found", SymbolNotFoundError(symbolName, projectPath));
			
			(allResults, declarations, symbolsSearched) = await CollectAsync([positional], solution, rootPath, cancellationToken);
		}
		
		if(allResults.Length == 0)
			
			return scope.Outcome("no references", new FindReferencesResult(0, [], skip, take, [$"No references found for '{symbolName}'."], null, false) { Declarations = declarations, Caution = AdhocCaution(projectPath) });
		
		var shape  = PageShape(symbolsSearched, declarations, AdhocCaution(projectPath));
		var result = PaginateAndStore(allResults, ref skip, take, shape);
		
		return scope.Outcome($"{result.Items.Length}/{result.Total} reference(s) across {symbolsSearched.Length} symbol(s)", shape(result));
	}
	
	/// <summary>What the search for one name found: every reference, where the symbols are declared, which symbols they were, and the project they were found in.</summary>
	sealed record Found(string[] Locations, string[] Declarations, string[] Searched, string Project);
	
	/// <summary>
	///     The references of one name, for a single-name call and for each name of a list alike: the
	///     project that declares the name (or holds <paramref name="filePath"/>), every symbol of
	///     that name in it. Or the error the call fails with for that name, and the reason for the log.
	/// </summary>
	async Task<(Found? References, object? Failed, string? Failure)> ReferencesOfAsync(string symbolName, string? containingType, string? projectPath, string? filePath, CancellationToken cancellationToken)
	{
		var (resolvedProject, projectError) = await ResolveSymbolProjectArgAsync(projectPath, symbolName, containingType, SymbolSought.All, filePath, cancellationToken);
		
		if(projectError is not null)
			
			return (null, projectError, projectError.Error ?? "project not resolved");
		
		projectPath = resolvedProject;
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return (null, error, error.Error ?? "compilation not available");
		
		if(!TryResolveSolution(projectPath, out var solution, out var solutionError))
			
			return (null, solutionError, solutionError.Error ?? "solution not resolved");
		
		if(!TryResolveRoot(projectPath, out var rootPath, out var rootError))
			
			return (null, rootError, rootError.Error ?? "root not resolved");
		
		var symbols = FindSymbols(compilation, symbolName, containingType);
		
		if(symbols.Length == 0)
			
			return (null, SymbolNotFoundError(symbolName, projectPath), "symbol not found");
		
		var (locations, declarations, searched) = await CollectAsync(symbols, solution, rootPath, cancellationToken);
		
		return (new(locations, declarations, searched, projectPath), null, null);
	}
	
	/// <summary>
	///     Every reference to the symbols, as sorted <c>file:line</c> strings, with where the symbols
	///     are declared in source and what they are called. The declarations are their own list:
	///     Roslyn's reference search leaves a symbol's declaration out, so "where is it and who uses
	///     it" took a second tool, where a text search for the name answers both at once (#459).
	/// </summary>
	static async Task<(string[] Locations, string[] Declarations, string[] Searched)> CollectAsync(ISymbol[] symbols, Solution solution, string rootPath, CancellationToken cancellationToken)
	{
		var allLocations = new List<string>();
		
		foreach(var sym in symbols) {
			
			var refs = await RoslynSymbolFinder.FindReferencesAsync(sym, solution, cancellationToken: cancellationToken);
			
			allLocations.AddRange(refs.SelectMany(r => r.Locations).Select(l => Place(l.Location, rootPath)));
		}
		
		string[] declarations = [..
			symbols.SelectMany(symbol => symbol.Locations)
				.Where(location => location.IsInSource)
				.Select(location => Place(location, rootPath))
				.Distinct()
				.Order()
		];
		
		return ([.. allLocations.Distinct().Order()], declarations, [.. symbols.Select(s => FormatSymbolName(s)).Distinct()]);
	}
	
	static string Place(Location location, string rootPath)
	{
		var span = location.GetLineSpan();
		var file = span.Path is { Length: > 0 } path ? Path.GetRelativePath(rootPath, path) : "?";
		
		return $"{file}:{span.StartLinePosition.Line + 1}";
	}

	// Builds every page of one query, first and cached alike. A static factory taking plain values, so
	// the function the pagination cache holds never captures a symbol, the compilation, or the solution.
	static Func<PaginatedResult<string>, object> PageShape(string[] symbolsSearched, string[] declarations, string? caution)
		=> page => new FindReferencesResult(
			TotalReferences: page.Total,
			SymbolsSearched: symbolsSearched,
			Skip: page.Skip, Take: page.Take,
			References: page.Items,
			PageToken: page.PageToken,
			HasMore:   page.HasMore)
		{
			Declarations = declarations,
			Caution      = caution
		}
	;

}
