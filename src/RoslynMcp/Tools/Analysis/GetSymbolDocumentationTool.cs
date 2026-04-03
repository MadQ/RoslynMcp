using System.Xml.Linq;
using System.ComponentModel;
using System.Xml;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class GetSymbolDocumentationTool : RoslynMcpTool
{
	public GetSymbolDocumentationTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_get_symbol_documentation", ReadOnly = true, Title = "Get Symbol Documentation", OpenWorld = false, Idempotent = true)]
	[Description(
		"Returns parsed XML documentation for a symbol (type, method, property, field, event) as structured fields: " +
		"summary, parameter descriptions, return value description, remarks, and example — not raw XML. " +
		"Use this to understand a symbol's API contract (expected inputs, outputs, side effects) before " +
		"using it, without reading its source code. " +
		"For a brief summary only, use roslyn_get_symbol_definition; for full source code when docs are " +
		"absent, use roslyn_get_member_body. " +
		"Returns a structured empty result (not an error) when no XML doc comments are found for the symbol.")]
	public object GetSymbolDocumentation(
		[Description("The symbol name, e.g. 'WorkspaceManager', 'GetCompilation', 'RootPath'.")] string symbolName,
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Optional containing type to disambiguate when multiple types have a member with the same name, e.g. 'WorkspaceManager'.")] string? containingType = null)
	{
		using var scope = BeginTool("roslyn_get_symbol_documentation", symbolName);
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return error;
		
		var symbol      = FindSymbol(compilation, symbolName, containingType);
		
		if(symbol is null)
			
			return scope.Failed("symbol not found", new ErrorResult($"Symbol '{symbolName}' not found.", Hint: "Use get_type_members or find_references to verify the name."));
		
		var xml = symbol.GetDocumentationCommentXml();
		
		if(string.IsNullOrWhiteSpace(xml))
			
			return scope.Error(new SymbolDocumentationEmptyResult(
				FormatSymbolName(symbol),
				symbol.Kind.ToString().ToLowerInvariant(),
				null,
				"No documentation comments found for this symbol."
			));
		
		var parsed = ParseDocumentation(xml);
		
		return scope.Outcome(symbolName, new SymbolDocumentationResult(
			FormatSymbolName(symbol),
			symbol.Kind.ToString().ToLowerInvariant(),
			parsed.Summary,
			parsed.Parameters,
			parsed.Returns,
			parsed.Remarks,
			parsed.Example,
			AdhocCaution(projectPath)
		));
	}
	
	
	
	private static DocumentationComment ParseDocumentation(string xml)
	{
		try {
			
			var doc = XDocument.Parse(xml);
			var root = doc.Root;
			
			if(root is null)
				
				return new DocumentationComment();
			
			var summary    = GetElementText(root, "summary");
			var returns    = GetElementText(root, "returns");
			var remarks    = GetElementText(root, "remarks");
			var example    = GetElementText(root, "example");
			ParameterDoc[] parameters = [..
					root.Elements("param")
						.Select(e => new ParameterDoc(
							Name: e.Attribute("name")?.Value ?? "?",
							Description: e.Value.Trim()
						))
				];
			
			return new DocumentationComment(summary, parameters, returns, remarks, example);
		}
		catch(XmlException) {
			
			// Malformed XML documentation — return empty rather than failing the tool call.
			
			return new DocumentationComment();
		}
	}
	
	private static string? GetElementText(XElement root, string elementName)
	{
		var element = root.Element(elementName);
		
		if(element is null)
			
			return null;
		
		var text = element.Value.Trim();
		
		return string.IsNullOrWhiteSpace(text) ? null : text;
	}
	
	private sealed record DocumentationComment(
		string? Summary = null,
		ParameterDoc[]? Parameters = null,
		string? Returns = null,
		string? Remarks = null,
		string? Example = null
	);
	
	private sealed record ParameterDoc(string Name, string Description);
}
