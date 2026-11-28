using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

// The project a symbol named by a tool call is declared in, for a call that names no project
// (#296): projectPath omitted, or given as a solution.
internal abstract partial class RoslynMcpTool
{
	/// <summary>
	///     What a tool that names a symbol is looking for. It decides which declarations of the
	///     name count, across the projects of a solution, before any project is picked: a tool
	///     that works on a method must not be sent to the project of a type that shares its name.
	/// </summary>
	protected enum SymbolSought
	{
		/// <summary>A type of the name before any member of it, as <see cref="FindSymbol"/> finds a name in one compilation.</summary>
		Any,
		
		/// <summary>
		///     Every declaration of the name, types and members alike, as <see cref="FindSymbols"/>
		///     collects them: for the tools that search or rename by name, where a type must not
		///     win over a member of the same name in another project without the caller knowing.
		/// </summary>
		All,
		
		/// <summary>Types only: the name is a type's by the tool's own contract.</summary>
		Type,
		
		/// <summary>Methods only.</summary>
		Method,
		
		/// <summary>Anything but a type: a method, property, field or event, for the tool that reads a member's body (the call graph).</summary>
		Member,
	}
	
	/// <summary>
	///     Normalizes an optional <c>projectPath</c> argument for a tool that looks a symbol up by
	///     name. A project is taken as given. A solution, the default one or one passed, names no
	///     project, so the symbol's declaration is looked for in every project of it, and the call
	///     goes on with the one project that declares it. A source symbol is visible from every
	///     project that references its own, so only the declaring project counts.
	///     <para>
	///         No declaration is a "symbol not found" error. Declarations in several projects are
	///         never resolved by preference: the call fails with the candidates, one per project,
	///         each with the project to pass back. Within one project nothing changes; several
	///         matches there are the tool's own to handle, as before.
	///     </para>
	///     <para>
	///         With a <paramref name="filePath"/>, for a tool that can also be pointed at a
	///         position, the file decides the project, as for every file-anchored tool, and the
	///         name is not looked up. Returns the project path to go on with, or the error.
	///         Awaited, with the call's token: the lookup reads the declaration index of the
	///         solution, and for a name outside it compiles project after project, which a
	///         cancelled call must not sit through (#444).
	///     </para>
	/// </summary>
	protected Task<(string Resolved, ToolResult? Error)> ResolveSymbolProjectArgAsync(
		string? projectPath,
		string symbolName,
		string? containingType,
		SymbolSought sought,
		string? filePath,
		CancellationToken cancellationToken)
		=> ResolveSymbolProjectArgAsync(projectPath, symbolName, containingType, sought, filePath, null, cancellationToken)
	;
	
	/// <summary>
	///     The same for the tools that change code by a symbol's name, which may ask the user: with
	///     a <paramref name="server"/>, where elicitation is on
	///     (<see cref="ProjectConfig.EffectiveElicit"/>) and the client supports it, declarations
	///     in several projects are put to the user before the call fails with the candidates.
	/// </summary>
	protected async Task<(string Resolved, ToolResult? Error)> ResolveSymbolProjectArgAsync(
		string? projectPath,
		string symbolName,
		string? containingType,
		SymbolSought sought,
		string? filePath,
		McpServer? server,
		CancellationToken cancellationToken)
	{
		// Per name: a call that asks several names must not carry one name's landing to the next.
		CallLanding.Current?.Clear();
		
		if(CallLanding.Current is { } asked)
			asked.Symbol = symbolName;
		
		if(!string.IsNullOrWhiteSpace(filePath))
			
			return TryResolveProjectArg(projectPath, filePath, out var byFile, out var fileError) ? (byFile, null) : (byFile, fileError);
		
		if(!TryResolveProjectArg(projectPath, out var resolved, out var error))
			
			return (resolved, error);
		
		// The root of a solution set names no solution (#501): the name is looked for in the
		// solutions of the set that are loaded.
		if(workspace.SolutionSetContext(resolved) is { } set)
			
			return await ResolveSymbolInSetAsync(set, resolved, symbolName, containingType, sought, cancellationToken);
		
		LandProjectInOwnSolution(resolved);
		
		if(await DeclaringProjectsOfAsync(resolved, symbolName, containingType, sought, indexOnly: false, cancellationToken) is not { } found)
			
			return (resolved, null);
		
		var (candidates, rootPath) = found;
		
		// Whichever project is picked is answered from this solution, also where another loaded
		// solution holds the same project file (#501).
		if(CallLanding.Current is { } landing && ResolvedSolutionPath(resolved) is { } within)
			landing.Solution = Path.GetFullPath(within);
		
		if(candidates.Length == 1)
			
			return (PickProject(candidates[0]), null);
		
		if(candidates.Length == 0)
			
			return (resolved, SymbolNotFoundError(symbolName, resolved));
		
		if(server is not null && ProjectConfig.EffectiveElicit(rootPath, logger)) {
			
			ISymbol[] symbols = [..candidates.Select(candidate => candidate.Symbol)];
			
			if(await TryElicitSymbolChoice(server, symbols, symbolName, rootPath, cancellationToken) is { } chosen
				&& candidates.FirstOrDefault(candidate => SymbolEqualityComparer.Default.Equals(candidate.Symbol, chosen)) is { } pick)
				
				return (PickProject(pick), null);
		}
		
		return (resolved, AmbiguousProjectError(candidates, symbolName, rootPath));
	}
	
	/// <summary>
	///     The project a name is declared in, for a call that names neither a file nor a solution in
	///     a session whose root is a solution set (#501). Only the solutions that are loaded are
	///     searched, and none is loaded for the search: a session works on two or three of twenty.
	///     <para>
	///         Declared in one project of one loaded solution, the call lands there, as a call
	///         that named a file of it would. Declared in several projects, the call fails with
	///         the candidates. Declared in one project that several loaded solutions hold (a
	///         contracts project its consumers reference as a project), the call fails with those
	///         solutions: the answer differs by solution, a reference search most of all, and none
	///         is preferred. Declared in none, the answer says which solutions were searched and
	///         which of the others have source that mentions the name.
	///     </para>
	///     <para>
	///         With several solutions loaded only the declaration index is asked. A name outside
	///         it (a type of a referenced assembly, a constructor by its metadata name) would cost
	///         a compilation of every project of every loaded solution, a misspelled name included.
	///         With one solution loaded the lookup is the one a single solution always had.
	///         The user is not asked to choose here, whatever the elicitation setting: the choice
	///         is between solutions as often as between symbols, and the candidates say both.
	///     </para>
	/// </summary>
	async Task<(string Resolved, ToolResult? Error)> ResolveSymbolInSetAsync((string Root, SecurityBoundary Boundary) set, string resolved, string symbolName, string? containingType, SymbolSought sought, CancellationToken cancellationToken)
	{
		var root = set.Root;
		
		var loaded    = workspace.LoadedSetMembers();
		var indexOnly = loaded.Length > 1;
		var found     = new List<(string Solution, DeclaringProject Declaring)>();
		var searched  = new List<string>();
		
		foreach(var solution in loaded) {
			
			// One pushed out of the cache since the list was read is not loaded again for this:
			// the lookup loads nothing.
			if(!workspace.IsLoaded(solution) || await DeclaringProjectsOfAsync(solution, symbolName, containingType, sought, indexOnly, cancellationToken) is not { } inSolution)
				continue;
			
			searched.Add(solution);
			found.AddRange(inSolution.Candidates.Select(declaring => (solution, declaring)));
		}
		
		string Relative(string path) => Path.GetRelativePath(root, path);
		
		if(found.Count == 0) {
			
			var (mentioning, cutShort) = SolutionsMentioning(root, set.Boundary, SimpleNameOf(symbolName), loaded, cancellationToken);
			var complete               = cutShort is null;
			
			// Each part says only what was covered: a hint that sends an agent after a typo for a
			// name that was not looked for, or in files that were not reached, costs it the search.
			var hint = mentioning.Length > 0
				? "found_solutions are the solutions not loaded whose source mentions the name: pass one as projectPath, or name a file with filePath, and the call lands in that file's solution."
				: complete
					? "No source file of a solution that is not loaded mentions the name."
					: $"No mention of the name was found in the other solutions' source, which was not all searched: {cutShort}.";
			
			if(mentioning.Length > 0 && !complete)
				hint += $" Their source was not all searched ({cutShort}), so there may be more.";
			
			if(indexOnly && searched.Count > 0)
				hint += " With several solutions loaded, only declarations in source are looked for: for a type of a referenced assembly (IDisposable) or a constructor by its metadata name, pass one of searched_solutions as projectPath.";
			
			return (resolved, new PathErrorResult(
				searched.Count == 0
					? $"'{symbolName}' was not looked for: none of the solutions below the root is loaded, and a symbol is looked for in the loaded ones."
					: $"'{symbolName}' is declared in none of the {searched.Count} loaded solution(s) searched.",
				ProvidedPath: root,
				FoundSolutions: mentioning.Length > 0 ? [..mentioning.Select(Relative)] : null,
				SearchedSolutions: searched.Count > 0 ? [..searched.Select(Relative)] : null)
			{
				Error = "symbol_not_in_loaded_solutions",
				Hint  = hint,
			});
		}
		
		DeclaringProject[] projects = [..found.Select(entry => entry.Declaring).DistinctBy(declaring => declaring.Project, SecurityBoundary.PathComparer)];
		
		if(projects.Length > 1)
			
			return (resolved, AmbiguousProjectError(projects, symbolName, root, "the loaded solutions"));
		
		string[] holding = [..found.Select(entry => entry.Solution).Distinct(SecurityBoundary.PathComparer)];
		
		if(holding.Length > 1)
			
			return (resolved, new PathErrorResult(
				$"'{symbolName}' is declared in {Path.GetFileName(projects[0].Project)}, which {holding.Length} loaded solutions hold. The answer differs by solution, so none is picked.",
				ProvidedPath: root,
				FoundSolutions: [..holding.Select(Relative)])
			{
				Error = "symbol_in_shared_project",
				Hint  = "Pass one of found_solutions as projectPath: the solution whose uses of the symbol are meant.",
			});
		
		if(CallLanding.Current is { } landing)
			(landing.ReportRoot, landing.Solution, landing.NotSearched) = (root, holding[0], workspace.SolutionSetMembers().Count(member => !searched.Contains(member, SecurityBoundary.PathComparer)));
		
		activeScope.Value?.Record($"lands in {Path.GetFileName(holding[0])}");
		
		return (PickProject(projects[0]), null);
	}
	
	// How long the search for solutions that mention a name may take: it runs on an error path,
	// over source no workspace holds.
	static readonly TimeSpan MentionBudget = TimeSpan.FromSeconds(2);
	
	/// <summary>
	///     The solutions of the set, <paramref name="loaded"/> ones left out, that have a source
	///     file mentioning <paramref name="name"/> as a whole word: where a symbol that no loaded
	///     solution declares could be. A text scan from disk, since nothing is loaded for it, of
	///     the files a search of the root would read (the same pruning, and no file a link leads
	///     out of the root to); read line by line, so a hit ends its file and a cancelled call or
	///     the end of <see cref="MentionBudget"/> ends the scan inside a file too. A solution found
	///     is not read further. It narrows twenty solutions to the few worth naming and proves
	///     nothing: a mention is not a declaration. <c>CutShort</c> is why not every file was
	///     searched, or null when all were: the caller's answer must not claim more.
	/// </summary>
	(string[] Solutions, string? CutShort) SolutionsMentioning(string root, SecurityBoundary boundary, string name, string[] loaded, CancellationToken cancellationToken)
	{
		var started  = System.Diagnostics.Stopwatch.GetTimestamp();
		var skip     = new HashSet<string>(loaded, SecurityBoundary.PathComparer);
		var hits     = new List<string>();
		var listing  = new SolutionSet.Listing(root, logger);
		var rules    = IgnoreRules.ForRoot(root, logger);
		var timedOut = false;
		var unread   = 0;
		
		// The solution above a file is the same for every file of a directory.
		var owners   = new Dictionary<string, string?>(SecurityBoundary.PathComparer);
		
		bool OutOfTime() => System.Diagnostics.Stopwatch.GetElapsedTime(started) > MentionBudget;
		
		try {
			
			var files = WorkspaceWalker.EnumerateFiles(root, "*.cs", recursive: true,
				(ref System.IO.Enumeration.FileSystemEntry directory) => listing.IsPruned(WorkspaceWalker.RelativeParent(ref directory), directory.FileName));
			
			foreach(var file in files) {
				
				cancellationToken.ThrowIfCancellationRequested();
				
				if(timedOut = OutOfTime())
					break;
				
				var directory = Path.GetDirectoryName(file)!;
				
				if(!owners.TryGetValue(directory, out var owner))
					owners[directory] = owner = SolutionSet.Above(file, root, rules, out _) is { } above ? Path.GetFullPath(above) : null;
				
				if(owner is null || skip.Contains(owner) || !boundary.IsPathAllowed(file))
					continue;
				
				try {
					
					using var reader = FileReader.OpenLines(file);
					
					if(reader.LooksBinary())
						continue;
					
					while(reader.Read(out var line)) {
						
						if((reader.LineNumber & 0xFFF) == 0) {
							
							cancellationToken.ThrowIfCancellationRequested();
							
							if(timedOut = OutOfTime())
								break;
						}
						
						if(!MentionsWord(line, name))
							continue;
						
						hits.Add(owner);
						skip.Add(owner);
						break;
					}
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
					
					// A file that cannot be read, or holds a line past the limit, was not searched.
					unread++;
				}
				
				if(timedOut)
					break;
			}
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			// The walk itself failed part way: what it did not list was not read.
			unread++;
		}
		
		// Why not everything was searched, in the answer's words: the time running out and a file
		// that cannot be read are two things, and a retry helps for neither in the same way.
		var cutShort = (timedOut, unread) switch {
			
			(true, > 0)  => $"the {MentionBudget.TotalSeconds:N0} seconds it is given ran out, and {unread} file(s) or directories could not be read",
			(true, _)    => $"the {MentionBudget.TotalSeconds:N0} seconds it is given ran out",
			(false, > 0) => $"{unread} file(s) or directories could not be read",
			_            => null,
		};
		
		return ([..hits.Order(StringComparer.OrdinalIgnoreCase)], cutShort);
	}
	
	// Whether a line holds a name with no identifier character on either side of it.
	static bool MentionsWord(ReadOnlySpan<char> line, string name)
	{
		if(name.Length == 0)
			
			return false;
		
		for(var from = 0; from <= line.Length - name.Length; ) {
			
			var found = line[from..].IndexOf(name, StringComparison.Ordinal);
			
			if(found < 0)
				
				return false;
			
			var at     = from + found;
			var before = at == 0 || !IsIdentifierPart(line[at - 1]);
			var after  = at + name.Length == line.Length || !IsIdentifierPart(line[at + name.Length]);
			
			if(before && after)
				
				return true;
			
			from = at + 1;
		}
		
		return false;
		
		static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';
	}
	
	/// <summary>
	///     Says in the answer where a search or a change stopped, for a tool whose answer is only
	///     as wide as its workspace (#501): the references, callers and implementations of a symbol,
	///     a rename, a signature change. In a session whose root is a solution set two things can
	///     be outside it. The symbol's project can be part of other solutions than the one the
	///     call is answered from, and code there that uses the symbol is in no answer from here;
	///     which solutions those are is read from the solution files, with nothing loaded, so a
	///     solution that uses the project without listing it is not seen, which the note says.
	///     And a call that landed by the symbol's name searched the loaded solutions only: one
	///     that is not loaded may declare the same name. Joined to the answer's caution, whatever
	///     else that says; nothing is said where neither applies.
	/// </summary>
	protected void NoteSolutionEdge(string projectPath, bool changes = false)
	{
		try {
			
			if(workspace.SolutionSetRoot is not { } root || CallLanding.Current is not { Solution: { } solution } landing)
				
				return;
			
			var here  = Path.GetRelativePath(root, solution);
			
			// Named, since a call that asks several names joins the notes of all of them.
			var asked = landing.Symbol is { } symbol ? $"'{symbol}'" : "the symbol";
			
			if(landing.NotSearched > 0)
				activeScope.Value?.AddCaution($"Of the loaded solutions, {here} declares {asked}; {landing.NotSearched} other solution(s) under the root are not loaded and were not searched for it.");
			
			if(workspace.Resolve(projectPath) is not (var project, not ResolutionKind.Solution and not ResolutionKind.SolutionSet) || !project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
				
				return;
			
			string[] others = [..workspace.SolutionSetMembers()
				.Where(member => !SecurityBoundary.PathComparer.Equals(member, solution)
					&& workspace.ProjectsListedBy(member).Contains(project, SecurityBoundary.PathComparer))
				.Select(member => Path.GetRelativePath(root, member))];
			
			if(others.Length == 0)
				
				return;
			
			var named = Path.GetFileName(project);
			var also  = string.Join(", ", others);
			
			activeScope.Value?.AddCaution(changes
				? $"This change to {asked} is made in {here} only. {named} is also a project of {also}, going by the solution files: code there that uses the symbol is not changed, and may no longer compile once this is applied."
				: $"The search for {asked} covers {here} only. {named} is also a project of {also}, going by the solution files: uses there are not in this answer. Ask again with one of them as projectPath.");
		}
		catch(Exception ex) when(!IsFatal(ex)) {
			
			// A note on an answer the call already has: it must not fail the call.
			logger.LogInfo("NoteSolutionEdge", $"{ex.GetType().Name}: {ex.Message}");
		}
	}

	/// <summary>
	///     Settles a project file named outright on its own solution, in a session whose root is a
	///     solution set (#501): the one above it, loaded if it is not. A project file can be part
	///     of several loaded solutions, and the index would answer it from whichever was loaded
	///     last, so the same call gave another answer after an unrelated load. Its own solution is
	///     the one the file system names, and where a path to the project alone loads it anyway
	///     when nothing is loaded. Nothing is settled where the project has no solution above it,
	///     or that solution does not hold it: the index answers then, as before.
	/// </summary>
	protected void LandProjectInOwnSolution(string projectPath)
	{
		try {
			
			if(workspace.SolutionSetRoot is not { } root || CallLanding.Current is not { Solution: null } landing)
				
				return;
			
			if(workspace.Resolve(projectPath) is not (var project, not ResolutionKind.Solution and not ResolutionKind.SolutionSet and not ResolutionKind.Adhoc)
				|| !project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
				|| !SecurityBoundary.StartsWithPath(project, root)
				|| SolutionSet.Above(project, root, IgnoreRules.ForRoot(root, logger), out _) is not { } own)
				
				return;
			
			own = Path.GetFullPath(own);
			
			ResolveWithRetry("LandProjectInOwnSolution", () => workspace.GetSolution(own));
			
			// The solution alone, and not the root paths are relative to: a call that names a
			// path takes and reports paths relative to what it named, as always.
			if(workspace.Holds(own, project))
				landing.Solution = own;
		}
		catch(Exception ex) when(!IsFatal(ex)) {
			
			// The tool's own resolution guard reports a path or a load that fails.
			logger.LogInfo("LandProjectInOwnSolution", $"{ex.GetType().Name}: {ex.Message}");
		}
	}
	
	/// <summary>One declaration of a looked-up name, and the file of the project that declares it.</summary>
	sealed record DeclaringProject(string Project, ISymbol Symbol);
	
	string PickProject(DeclaringProject declaring)
	{
		activeScope.Value?.Record($"→ {Path.GetFileName(declaring.Project)}");
		
		return declaring.Project;
	}
	
	/// <summary>
	///     The projects of the solution a resolved <paramref name="projectPath"/> stands for that
	///     declare the name, one entry per project; <see langword="null"/> when the path is not a
	///     solution, when the workspace has no project files to tell projects by (an adhoc
	///     workspace), or when the lookup fails — the tool's own resolution guard then reports the
	///     fault through its structured mapping. A cancelled call is not such a failure.
	/// </summary>
	async Task<(DeclaringProject[] Candidates, string RootPath)?> DeclaringProjectsOfAsync(string projectPath, string symbolName, string? containingType, SymbolSought sought, bool indexOnly, CancellationToken cancellationToken)
	{
		try {
			
			if(ResolvedSolutionPath(projectPath) is null)
				
				return null;
			
			var solution = ResolveWithRetry("ResolveSymbolProjectArg", () => workspace.GetSolution(projectPath));
			
			if(solution.Projects.Any(project => project.FilePath is null))
				
				return null;
			
			var declared = await DeclaringProjectsAsync(solution, symbolName, containingType, sought, cancellationToken);
			
			// Not in the declaration index, the name may still be a symbol: one of a referenced
			// assembly (IDisposable, for its implementations), which is the same symbol from every
			// project that can see it, or one the index does not list by that name.
			if(declared.Length == 0 && !indexOnly)
				declared = await ProjectsResolvingAsync(solution, symbolName, containingType, sought, cancellationToken);
			
			return (declared, workspace.GetRootPath(projectPath));
		}
		catch(Exception ex) when(ex is not OperationCanceledException && !IsFatal(ex)) {
			
			logger.LogInfo("ResolveSymbolProjectArg", $"{ex.GetType().Name}: {ex.Message}");
			
			return null;
		}
	}
	
	/// <summary>
	///     Finds the source declarations of a name in a solution, through Roslyn's declaration
	///     index, which needs no compilation of every project: inside
	///     <paramref name="containingType"/> when one is given, and of the kind the tool is after
	///     (<see cref="SymbolSought"/>). The targets of a multi-targeted project are one project
	///     here: one project file.
	/// </summary>
	static async Task<DeclaringProject[]> DeclaringProjectsAsync(Solution solution, string symbolName, string? containingType, SymbolSought sought, CancellationToken cancellationToken)
	{
		ISymbol[] found = [..
			(await SymbolFinder.FindSourceDeclarationsAsync(solution, SimpleNameOf(symbolName), ignoreCase: false, cancellationToken))
				.Where(symbol => symbol.Kind != SymbolKind.Namespace)
		];
		
		// A qualified name must match as one. Where it matches nothing here — a type of a
		// referenced assembly (System.Threading.Timer, beside a Timer in source), a form the
		// display string does not reproduce, a namespace mistyped — there is no source
		// declaration of that name, and the caller's fallback asks each compilation, which reads
		// a name exactly as the tool itself will. Keeping the matches of the last part instead
		// would hand the call to a declaration it did not name.
		found = [..found.Where(symbol => IsNamed(symbol, symbolName))];
		
		if(containingType is not null)
			found = [..found.Where(symbol => symbol.ContainingType is { } type && IsNamed(type, containingType))];
		
		found = sought switch {
			
			SymbolSought.Type   => [..found.OfType<INamedTypeSymbol>()],
			SymbolSought.Method => [..found.OfType<IMethodSymbol>()],
			SymbolSought.Member => [..found.Where(symbol => symbol is not INamedTypeSymbol)],
			SymbolSought.All    => found,
			
			// A type of the name before any member of it, and only without a containing type,
			// which a top-level type does not have.
			_ => containingType is null && found.Any(symbol => symbol is INamedTypeSymbol)
				? [..found.OfType<INamedTypeSymbol>()]
				: found,
		};
		
		var byProject = new List<DeclaringProject>();
		
		foreach(var symbol in found) {
			
			if(solution.GetProject(symbol.ContainingAssembly, cancellationToken)?.FilePath is not { } file)
				continue;
			
			if(!byProject.Any(taken => SecurityBoundary.PathComparer.Equals(taken.Project, file)))
				byProject.Add(new DeclaringProject(file, symbol));
		}
		
		return [..byProject];
	}
	
	/// <summary>
	///     The projects whose compilation resolves the name, for a name the declaration index does
	///     not have; empty when none does. The projects whose own assembly declares the symbol
	///     come before one that merely sees it: a constructor or an explicit interface
	///     implementation is in the index under another name, and its declaring project is the
	///     one a tool can read its source from. Compiles every project, to find each that declares
	///     the name, and stops early only for a symbol of a referenced assembly whose containing
	///     type, if one was given, is no type in source either; that is what a name outside the
	///     index costs here, a misspelled one included.
	/// </summary>
	static async Task<DeclaringProject[]> ProjectsResolvingAsync(Solution solution, string symbolName, string? containingType, SymbolSought sought, CancellationToken cancellationToken)
	{
		var declaring = new List<DeclaringProject>();
		
		DeclaringProject? seeing = null;
		
		// Whether a symbol of a referenced assembly ends the search (see below). The index is
		// asked once. It costs no compilation the loop would not do: where it finds the name it
		// may compile the projects that hold it, and those are compiled below in any case.
		var typeInSource = containingType is not null
			&& (await SymbolFinder.FindSourceDeclarationsAsync(solution, SimpleNameOf(containingType), ignoreCase: false, cancellationToken)).Any(symbol => symbol is INamedTypeSymbol)
		;
		
		foreach(var project in solution.Projects.DistinctBy(candidate => candidate.FilePath, SecurityBoundary.PathComparer)) {
			
			if(project.FilePath is not { } file || await project.GetCompilationAsync(cancellationToken) is not { } compilation)
				continue;
			
			// Read as the tool itself reads the name in one compilation, and of the kind it is
			// after: a type-first answer would hide a member of the same name from a tool that
			// wants the member (a constructor named as its type, for the call graph).
			var symbol = sought switch {
				
				SymbolSought.Type   => FindSymbol(compilation, symbolName, containingType) as INamedTypeSymbol,
				SymbolSought.Method => FindSymbols(compilation, symbolName, containingType).FirstOrDefault(candidate => candidate is IMethodSymbol),
				SymbolSought.Member => FindSymbols(compilation, symbolName, containingType).FirstOrDefault(candidate => candidate is not INamedTypeSymbol),
				
				// Any and All alike: nothing of the name is in the index here, type or member,
				// so there is no member for a type-first answer to hide.
				_                   => FindSymbol(compilation, symbolName, containingType),
			};
			
			if(symbol is null)
				continue;
			
			// A symbol of a referenced assembly: no project declares it, and every project that
			// sees it sees it alike, so the first one stands for all. Asked for by its own name
			// (IDisposable), that ends the search: a source type of the name would have been in
			// the index. Asked for inside a containing type, another project may still declare a
			// type of that name with such a member in source (a constructor, which the index
			// lists under another name), and that is then what was asked for, whichever project
			// comes first. So the search goes on only where a type of that name is in source at
			// all; for a member of a framework type (WriteLine in Console) it ends here too.
			if(!symbol.Locations.Any(location => location.IsInSource)) {
				
				if(!typeInSource && declaring.Count == 0)
					
					return [new DeclaringProject(file, symbol)];
				
				seeing ??= new DeclaringProject(file, symbol);
				continue;
			}
			
			if(SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, compilation.Assembly))
				declaring.Add(new DeclaringProject(file, symbol));
			else
				seeing ??= new DeclaringProject(file, symbol);
		}
		
		// Every project that declares it, for the caller to tell one from several: here too a
		// project is never preferred. One that only sees it stands in when none declares it.
		return declaring.Count > 0 ? [..declaring] : seeing is not null ? [seeing] : [];
	}
	
	// Namespaces, containing types and the name, without type arguments: the form a qualified name
	// is given in once its generic arity is dropped (Ns.Outer`1 → Ns.Outer).
	static readonly SymbolDisplayFormat QualifiedName = new(
		typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
		genericsOptions:        SymbolDisplayGenericsOptions.None,
		memberOptions:          SymbolDisplayMemberOptions.IncludeContainingType)
	;
	
	/// <summary>
	///     Whether a symbol answers to a name as a caller gives it: the simple name, or a qualified
	///     one, whole or from any of its dots on (<c>Outer.Inner</c> for <c>Ns.Outer.Inner</c>). A
	///     generic arity in the given name is ignored.
	/// </summary>
	static bool IsNamed(ISymbol symbol, string given)
	{
		var name = WithoutArity(given);
		
		// A leading dot is part of a name (.ctor), not a qualifier.
		if(name.LastIndexOf('.') <= 0)
			
			return symbol.Name == name;
		
		var qualified = symbol.ToDisplayString(QualifiedName);
		
		return qualified == name || qualified.EndsWith("." + name, StringComparison.Ordinal);
	}
	
	/// <summary>The last part of a possibly qualified or generic metadata name: <c>Ns.Outer`1</c> → <c>Outer</c>.</summary>
	static string SimpleNameOf(string name)
	{
		var plain = WithoutArity(name);
		
		// A leading dot is a name of its own (.ctor), not a qualifier.
		var dot = plain.LastIndexOf('.');
		
		return dot <= 0 ? plain : plain[(dot + 1)..];
	}
	
	/// <summary>A metadata name without its generic arities: <c>Ns.Outer`1.Inner`2</c> → <c>Ns.Outer.Inner</c>.</summary>
	static string WithoutArity(string name)
	{
		var tick = name.IndexOf('`');
		
		if(tick < 0)
			
			return name;
		
		var plain = new System.Text.StringBuilder(name.Length);
		
		for(var i = 0; i < name.Length; i++) {
			
			if(name[i] != '`') {
				
				plain.Append(name[i]);
				continue;
			}
			
			while(i + 1 < name.Length && char.IsAsciiDigit(name[i + 1]))
				i++;
		}
		
		return plain.ToString();
	}
	
	/// <summary>
	///     The failure for a name declared in several projects of a solution: one candidate per
	///     project, each with the project to pass back as projectPath.
	/// </summary>
	static AmbiguousSymbolResult AmbiguousProjectError(DeclaringProject[] candidates, string symbolName, string rootPath, string within = "the solution")
	{
		var named = AmbiguousSymbolError([..candidates.Select(candidate => candidate.Symbol)], symbolName, rootPath);
		
		return new AmbiguousSymbolResult(
			$"'{symbolName}' is declared in {candidates.Length} projects of {within}.",
			[..named.Candidates.Select((candidate, index) => candidate with { Project = candidates[index].Project })],
			"Re-call with one candidate's project as projectPath. " +
			"If the user's intent is not clear from the conversation, ask them which project they meant.");
	}
	
	/// <summary>The parameter text for a <c>symbolName</c> that may hold several names (#459).</summary>
	protected const string SeveralNamesDescription =
		" Several names in one call: separate them with '|' or ',' (e.g. 'InvalidateFile|RequestReload'); each is looked up on its own and gets its own result or error."
	;
	
	/// <summary>
	///     The names in a <c>symbolName</c> argument (#459). Agents ask about several symbols at once:
	///     nearly every symbol lookup in the logs was a text search for names joined by <c>|</c>,
	///     because the tools that know symbols took one name a call. So a name list is split at
	///     <c>|</c>, which is what they already write, and at a comma that is not between angle
	///     brackets (<c>Dictionary&lt;TKey, TValue&gt;</c> is one name). Order kept, repeats dropped.
	/// </summary>
	protected static string[] SplitSymbolNames(string symbolName)
	{
		var names = new List<string>();
		var depth = 0;
		var start = 0;
		
		for(var index = 0; index <= symbolName.Length; index++) {
			
			var c = index < symbolName.Length ? symbolName[index] : '|';
			
			if(c == '<')
				depth++;
			else if(c == '>' && depth > 0)
				depth--;
			
			if(c != '|' && (c != ',' || depth > 0))
				continue;
			
			if(symbolName[start..index].Trim() is { Length: > 0 } name) {
				
				// The chat prefix a client puts before a symbol it was shown comes once per name, and
				// the tools take it off the argument's start alone.
				if(HasChatSymbolRefPrefix(name, out var bare) && bare.Length > 0)
					name = bare;
				
				if(!names.Contains(name))
					names.Add(name);
			}
			
			start = index + 1;
		}
		
		return [..names];
	}

}
