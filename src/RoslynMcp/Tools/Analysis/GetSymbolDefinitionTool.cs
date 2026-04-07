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
		"project source — the definition is not available as source in that case.")]
	public object GetSymbolDefinition(
		[Description("The symbol name, e.g. 'WorkspaceManager', 'GetCompilation', 'RootPath'.")] string symbolName,
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Optional containing type to disambiguate when multiple types have a member with the same name, e.g. 'WorkspaceManager'.")] string? containingType = null)
	{
		using var scope = BeginTool("roslyn_get_symbol_definition", symbolName);
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return scope.Error(error!);
		
		var rootPath = workspace.GetRootPath(projectPath);
		var symbol      = FindSymbol(compilation, symbolName, containingType);
		
		if(symbol is null)
			return scope.Failed("symbol not found", new ErrorResult($"Symbol '{symbolName}' not found.", Hint: "Use get_type_members or find_references to verify the name."));
		
		var location = symbol.Locations.FirstOrDefault(loc => loc.IsInSource);
		
		if(location is null)
			return scope.Error(new MetadataSymbolResult(
				FormatSymbolName(symbol),
				symbol.Kind.ToString().ToLowerInvariant(),
				"metadata",
				"This symbol is defined in metadata (compiled assembly), not source code."
			));
		
		var span      = location.GetLineSpan();
		var filePath  = span.Path;
		var relative  = string.IsNullOrEmpty(filePath) ? "?" : Path.GetRelativePath(rootPath, filePath);
		var signature = SymbolFormatter.FormatSignature(symbol);
		var docXml    = symbol.GetDocumentationCommentXml();
		var docSummary = SymbolFormatter.ExtractDocSummary(docXml);
		
		return scope.Outcome(symbolName, new SymbolDefinitionResult(
			FormatSymbolName(symbol),
			symbol.Kind.ToString().ToLowerInvariant(),
			relative,
			span.StartLinePosition.Line + 1,
			span.StartLinePosition.Character + 1,
			signature,
			docSummary,
			AdhocCaution(projectPath)
		));
	}
}
