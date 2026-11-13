using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class GetSymbolDefinitionTool : RoslynMcpTool
{
	public GetSymbolDefinitionTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_get_symbol_definition", ReadOnly = true, Title = "Get Symbol Definition", OpenWorld = false, Idempotent = true)]
	[Description(
		"Returns the declaration location and full signature of a symbol (type, method, property, field, event) — " +
		"file path (project-relative), line, column, formatted signature, and extracted XML doc summary as plain text. " +
		"Use this to locate a symbol before reading its source with roslyn_get_member_body, or to verify " +
		"its signature without opening the file. " +
		"For full parsed documentation (parameters, returns, remarks), use roslyn_get_symbol_documentation. " +
		"Returns a structured metadata error if the symbol is defined in a compiled assembly rather than " +
		"project source — the definition is not available as source in that case. " +
		"Several names can be asked in one call (symbolName 'A|B|C'): the answer is then definitions[], one entry per name with " +
		"its definition or its own error, and one unknown name does not fail the others.")]
	public async Task<object> GetSymbolDefinition(
		[Description("The symbol name, e.g. 'WorkspaceManager', 'GetCompilation', 'RootPath'." + SeveralNamesDescription)] string symbolName,
		CancellationToken cancellationToken,
		[Description(OptionalProjectPathDescription)] string? projectPath = null,
		[Description("Optional containing type to disambiguate when multiple types have a member with the same name, e.g. 'WorkspaceManager'. Applies to every name of a list.")] string? containingType = null)
	{
		using var scope = BeginTool("roslyn_get_symbol_definition", symbolName, new { containingType, projectPath });
		
		if(!TryStripChatSymbolRef(ref symbolName, out var refError) || !TryStripChatSymbolRefOptional(ref containingType, out refError))
			
			return scope.Error(refError!);
		
		var names = SplitSymbolNames(symbolName);
		
		if(names.Length <= 1) {
			
			var (result, failure) = await DefineAsync(names is [var only] ? only : symbolName, containingType, projectPath, cancellationToken);
			
			return failure is null ? scope.Outcome(symbolName, result) : scope.Failed(failure, result);
		}
		
		// Several names (#459): each is one lookup, with its own project, and its own answer.
		var entries = new List<NamedDefinition>(names.Length);
		var found   = 0;
		
		foreach(var name in names) {
			
			var (result, failure) = await DefineAsync(name, containingType, projectPath, cancellationToken);
			
			found += failure is null ? 1 : 0;
			entries.Add(failure is null ? new(name, result, null) : new(name, null, result));
		}
		
		var answer = new SymbolDefinitionsResult([..entries], found, names.Length - found);
		
		return found > 0
			? scope.Outcome($"{found}/{names.Length} name(s) defined", answer)
			: scope.Failed($"none of {names.Length} name(s) found", answer);
	}
	
	/// <summary>
	///     The definition of one name, or what went wrong with it: <paramref name="Failure"/> is null
	///     on success and the reason for the log otherwise, with the error as the result.
	/// </summary>
	async Task<(object Result, string? Failure)> DefineAsync(string symbolName, string? containingType, string? projectPath, CancellationToken cancellationToken)
	{
		var (resolvedProject, projectError) = await ResolveSymbolProjectArgAsync(projectPath, symbolName, containingType, SymbolSought.Any, null, cancellationToken);
		
		if(projectError is not null)
			
			return (projectError, projectError.Error ?? "project not resolved");
		
		projectPath = resolvedProject;
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return (error!, "compilation not available");
		
		if(!TryResolveRoot(projectPath, out var rootPath, out var rootError))
			
			return (rootError, rootError.Error ?? "root not resolved");
		
		var symbol = FindSymbol(compilation, symbolName, containingType);
		
		if(symbol is null)
			
			return (SymbolNotFoundError(symbolName, projectPath), "symbol not found");
		
		var location = symbol.Locations.FirstOrDefault(loc => loc.IsInSource);
		
		if(location is null)
			
			return (new MetadataSymbolResult(
				FormatSymbolName(symbol),
				symbol.Kind.ToString().ToLowerInvariant(),
				"metadata")
			{
				Error = "This symbol is defined in metadata (compiled assembly), not source code."
			}, "defined in metadata");
		
		var span      = location.GetLineSpan();
		var filePath  = span.Path;
		var relative  = string.IsNullOrEmpty(filePath) ? "?" : Path.GetRelativePath(rootPath, filePath);
		var signature = SymbolFormatter.FormatSignature(symbol);
		var docXml    = symbol.GetDocumentationCommentXml();
		var docSummary = SymbolFormatter.ExtractDocSummary(docXml);
		
		return (new SymbolDefinitionResult(
			FormatSymbolName(symbol),
			symbol.Kind.ToString().ToLowerInvariant(),
			relative,
			span.StartLinePosition.Line + 1,
			span.StartLinePosition.Character + 1,
			signature,
			docSummary)
		{
			Caution = AdhocCaution(projectPath)
		}, null);
	}
}
