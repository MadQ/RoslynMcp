using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class GetSymbolDefinitionTool : RoslynMcpTool
{
	public GetSymbolDefinitionTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_get_symbol_definition", ReadOnly = true)]
	[Description(
		"Returns the definition location and signature of a symbol (type, method, property, field, event). " +
		"Shows where the symbol is declared, its full signature, and XML doc summary. " +
		"Use this to navigate to a symbol's definition without reading multiple files.")]
	public object GetSymbolDefinition(
		[Description("The symbol name, e.g. 'WorkspaceManager', 'GetCompilation', 'RootPath'.")] string symbolName,
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Optional containing type to narrow the search, e.g. 'WorkspaceManager'.")] string? containingType = null)
	{
		using var scope = BeginTool("roslyn_get_symbol_definition", symbolName);
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return error;
		
		var rootPath = workspace.GetRootPath(projectPath);
		var symbol      = FindSymbol(compilation, symbolName, containingType);
		
		if(symbol is null)
			return scope.Failed("symbol not found", new ErrorResult($"Symbol '{symbolName}' not found.", Hint: "Use get_type_members or find_references to verify the name."));
		
		var location = symbol.Locations.FirstOrDefault(loc => loc.IsInSource);
		
		if(location is null)
			return new MetadataSymbolResult(
				FormatSymbolName(symbol),
				symbol.Kind.ToString().ToLowerInvariant(),
				"metadata",
				"This symbol is defined in metadata (compiled assembly), not source code."
			);
		
		var span      = location.GetLineSpan();
		var filePath  = span.Path;
		var relative  = string.IsNullOrEmpty(filePath) ? "?" : Path.GetRelativePath(rootPath, filePath);
		var signature = SymbolFormatter.FormatSignature(symbol);
		var docXml    = symbol.GetDocumentationCommentXml();
		var docSummary = ExtractDocSummary(docXml);
		
		return new SymbolDefinitionResult(
			FormatSymbolName(symbol),
			symbol.Kind.ToString().ToLowerInvariant(),
			relative,
			span.StartLinePosition.Line + 1,
			span.StartLinePosition.Character + 1,
			signature,
			docSummary,
			AdhocCaution(projectPath)
		);
	}
	
	
	
	
	
	
	
	
	
	
	private static string? ExtractDocSummary(string? xml)
	{
		if(string.IsNullOrWhiteSpace(xml))
			return null;
		
		try {
		
			var doc = System.Xml.Linq.XDocument.Parse(xml);
			var summary = doc.Root?.Element("summary")?.Value.Trim();
			
			return string.IsNullOrWhiteSpace(summary) ? null : summary;
		}
		catch(System.Xml.XmlException) {
		
			// Malformed XML documentation — return null rather than failing the whole tool call.
			return null;
		}
	}
}
