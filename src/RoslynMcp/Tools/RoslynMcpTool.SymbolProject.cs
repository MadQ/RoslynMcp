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
		if(!string.IsNullOrWhiteSpace(filePath))
			
			return TryResolveProjectArg(projectPath, filePath, out var byFile, out var fileError) ? (byFile, null) : (byFile, fileError);
		
		if(!TryResolveProjectArg(projectPath, out var resolved, out var error))
			
			return (resolved, error);
		
		if(await DeclaringProjectsOfAsync(resolved, symbolName, containingType, sought, cancellationToken) is not { } found)
			
			return (resolved, null);
		
		var (candidates, rootPath) = found;
		
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
	async Task<(DeclaringProject[] Candidates, string RootPath)?> DeclaringProjectsOfAsync(string projectPath, string symbolName, string? containingType, SymbolSought sought, CancellationToken cancellationToken)
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
			if(declared.Length == 0)
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
	static AmbiguousSymbolResult AmbiguousProjectError(DeclaringProject[] candidates, string symbolName, string rootPath)
	{
		var named = AmbiguousSymbolError([..candidates.Select(candidate => candidate.Symbol)], symbolName, rootPath);
		
		return new AmbiguousSymbolResult(
			$"'{symbolName}' is declared in {candidates.Length} projects of the solution.",
			[..named.Candidates.Select((candidate, index) => candidate with { Project = candidates[index].Project })],
			"Re-call with one candidate's project as projectPath. " +
			"If the user's intent is not clear from the conversation, ask them which project they meant.");
	}
}
