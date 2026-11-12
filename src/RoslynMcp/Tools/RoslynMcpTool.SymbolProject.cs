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
	/// </summary>
	protected bool TryResolveSymbolProjectArg(
		string? projectPath,
		string symbolName,
		string? containingType,
		out string resolved,
		[NotNullWhen(false)] out ToolResult? error)
	{
		if(!TryResolveProjectArg(projectPath, out resolved, out error))
			
			return false;
		
		if(DeclaringProjectsOf(resolved, symbolName, containingType) is not { } declared)
			
			return true;
		
		if(declared.Candidates.Length == 1) {
			
			resolved = PickProject(declared.Candidates[0]);
			
			return true;
		}
		
		error = declared.Candidates.Length == 0
			? SymbolNotFoundError(symbolName)
			: AmbiguousProjectError(declared.Candidates, symbolName, declared.RootPath)
		;
		
		return false;
	}
	
	/// <summary>
	///     The same for a tool that can also be pointed at a position: with a
	///     <paramref name="filePath"/> the file decides the project, as for every file-anchored
	///     tool, and the name is not looked up.
	/// </summary>
	protected bool TryResolveSymbolProjectArg(
		string? projectPath,
		string symbolName,
		string? containingType,
		string? filePath,
		out string resolved,
		[NotNullWhen(false)] out ToolResult? error)
		=> string.IsNullOrWhiteSpace(filePath)
			? TryResolveSymbolProjectArg(projectPath, symbolName, containingType, out resolved, out error)
			: TryResolveProjectArg(projectPath, filePath, out resolved, out error)
	;
	
	/// <summary>
	///     The same for the tools that change code by a symbol's name, which may ask the user: where
	///     elicitation is on (<see cref="ProjectConfig.EffectiveElicit"/>) and the client supports
	///     it, declarations in several projects are put to the user before the call fails with the
	///     candidates. Returns the project path to go on with, or the error.
	/// </summary>
	protected async Task<(string Resolved, ToolResult? Error)> ResolveSymbolProjectArgAsync(
		string? projectPath,
		string symbolName,
		string? containingType,
		string? filePath,
		McpServer server,
		CancellationToken cancellationToken)
	{
		if(!string.IsNullOrWhiteSpace(filePath))
			
			return TryResolveProjectArg(projectPath, filePath, out var byFile, out var fileError) ? (byFile, null) : (byFile, fileError);
		
		if(!TryResolveProjectArg(projectPath, out var resolved, out var error))
			
			return (resolved, error);
		
		if(DeclaringProjectsOf(resolved, symbolName, containingType) is not { } declared)
			
			return (resolved, null);
		
		var candidates = declared.Candidates;
		
		if(candidates.Length == 1)
			
			return (PickProject(candidates[0]), null);
		
		if(candidates.Length == 0)
			
			return (resolved, SymbolNotFoundError(symbolName));
		
		if(ProjectConfig.EffectiveElicit(declared.RootPath, logger)) {
			
			ISymbol[] symbols = [..candidates.Select(candidate => candidate.Symbol)];
			
			if(await TryElicitSymbolChoice(server, symbols, symbolName, declared.RootPath, cancellationToken) is { } chosen
				&& candidates.FirstOrDefault(candidate => SymbolEqualityComparer.Default.Equals(candidate.Symbol, chosen)) is { Project: not null } pick)
				
				return (PickProject(pick), null);
		}
		
		return (resolved, AmbiguousProjectError(candidates, symbolName, declared.RootPath));
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
	///     fault through its structured mapping.
	/// </summary>
	(DeclaringProject[] Candidates, string RootPath)? DeclaringProjectsOf(string projectPath, string symbolName, string? containingType)
	{
		try {
			
			if(ResolvedSolutionPath(projectPath) is null)
				
				return null;
			
			var solution = ResolveWithRetry("TryResolveSymbolProjectArg", () => workspace.GetSolution(projectPath));
			
			if(solution.Projects.Any(project => project.FilePath is null))
				
				return null;
			
			var declared = DeclaringProjects(solution, symbolName, containingType);
			
			// Declared nowhere in source, the name may still be a symbol of a referenced assembly
			// (IDisposable, for its implementations). Such a symbol is the same one from every
			// project that can see it, so the first that can stands for all, which is no
			// preference between declarations.
			if(declared.Length == 0 && FirstProjectSeeing(solution, symbolName, containingType) is { } seeing)
				declared = [seeing];
			
			return (declared, workspace.GetRootPath(projectPath));
		}
		catch(Exception ex) when(!IsFatal(ex)) {
			
			logger.LogInfo("TryResolveSymbolProjectArg", $"{ex.GetType().Name}: {ex.Message}");
			
			return null;
		}
	}
	
	/// <summary>
	///     Finds the source declarations of a name in a solution as <see cref="FindSymbol"/> finds a
	///     name in one compilation: inside <paramref name="containingType"/> when one is given;
	///     otherwise a type of that name before any member of it. Searched through Roslyn's
	///     declaration index, which needs no compilation of every project. The targets of a
	///     multi-targeted project are one project here: one project file.
	/// </summary>
	static DeclaringProject[] DeclaringProjects(Solution solution, string symbolName, string? containingType)
	{
		var simpleName = SimpleNameOf(symbolName);
		
		// Sync over async, like the compilation fetch: the tools that call this are not all async,
		// and no synchronization context is in play.
		ISymbol[] found = [..
			SymbolFinder.FindSourceDeclarationsAsync(solution, simpleName, ignoreCase: false).GetAwaiter().GetResult()
				.Where(symbol => symbol.Kind != SymbolKind.Namespace)
		];
		
		if(containingType is not null) {
			
			var typeName = SimpleNameOf(containingType);
			
			found = [..found.Where(symbol => symbol.ContainingType?.Name == typeName)];
		}
		else if(found.Any(symbol => symbol is INamedTypeSymbol)) {
			
			found = [..found.Where(symbol => symbol is INamedTypeSymbol)];
		}
		
		// A qualified name narrows further, where it leaves anything.
		if(symbolName.Contains('.') && found.Where(symbol => symbol.ToDisplayString().EndsWith(symbolName, StringComparison.Ordinal)).ToArray() is { Length: > 0 } qualified)
			found = qualified;
		
		var byProject = new List<DeclaringProject>();
		
		foreach(var symbol in found) {
			
			if(solution.GetProject(symbol.ContainingAssembly)?.FilePath is not { } file)
				continue;
			
			if(!byProject.Any(taken => SecurityBoundary.PathComparer.Equals(taken.Project, file)))
				byProject.Add(new DeclaringProject(file, symbol));
		}
		
		return [..byProject];
	}
	
	/// <summary>
	///     The first project of the solution whose compilation resolves the name, for a name with no
	///     source declaration; <see langword="null"/> when none does. Compiles project after project
	///     until one answers, which is the cost of a name that is in no source file.
	/// </summary>
	static DeclaringProject? FirstProjectSeeing(Solution solution, string symbolName, string? containingType)
	{
		foreach(var project in solution.Projects.DistinctBy(candidate => candidate.FilePath, SecurityBoundary.PathComparer)) {
			
			if(project.FilePath is { } file
				&& project.GetCompilationAsync().GetAwaiter().GetResult() is { } compilation
				&& FindSymbol(compilation, symbolName, containingType) is { } symbol)
				
				return new DeclaringProject(file, symbol);
		}
		
		return null;
	}
	
	/// <summary>The last part of a possibly qualified or generic metadata name: <c>Ns.Outer`1</c> → <c>Outer</c>.</summary>
	static string SimpleNameOf(string name)
	{
		var simple = name[(name.LastIndexOf('.') + 1)..];
		var arity  = simple.IndexOf('`');
		
		return arity < 0 ? simple : simple[..arity];
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
