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
			return scope.Failed("symbol not found", new { error = $"Symbol '{symbolName}' not found. Use get_type_members or find_references to verify the name." });
		
		var location = symbol.Locations.FirstOrDefault(loc => loc.IsInSource);
		
		if(location is null)
			return new {
				symbol_name = FormatSymbolName(symbol),
				symbol_kind = symbol.Kind.ToString().ToLowerInvariant(),
				location    = "metadata",
				message     = "This symbol is defined in metadata (compiled assembly), not source code."
			};
		
		var span      = location.GetLineSpan();
		var filePath  = span.Path;
		var relative  = string.IsNullOrEmpty(filePath) ? "?" : Path.GetRelativePath(rootPath, filePath);
		var signature = FormatSignature(symbol);
		var docXml    = symbol.GetDocumentationCommentXml();
		var docSummary = ExtractDocSummary(docXml);
		
		return new {
			symbol_name = FormatSymbolName(symbol),
			symbol_kind = symbol.Kind.ToString().ToLowerInvariant(),
			file        = relative,
			line        = span.StartLinePosition.Line + 1,
			column      = span.StartLinePosition.Character + 1,
			signature   = signature,
			doc_summary = docSummary,
			_caution    = AdhocCaution(projectPath)
		};
	}
	
	
	
	private static string FormatSignature(ISymbol symbol)
	{
		return symbol switch {
			IMethodSymbol m   => FormatMethod(m),
			IPropertySymbol p => FormatProperty(p),
			IFieldSymbol f    => FormatField(f),
			IEventSymbol e    => FormatEvent(e),
			INamedTypeSymbol t => FormatType(t),
			_                 => symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
		};
	}
	
	private static string FormatMethod(IMethodSymbol method)
	{
		var returnType = method.ReturnsVoid
			? "void"
			: method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
		
		var parameters = string.Join(", ", method.Parameters.Select(p =>
			$"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}"
		));
		
		var modifiers = FormatModifiers(method);
		
		return $"{modifiers}{returnType} {method.Name}({parameters})";
	}
	
	private static string FormatProperty(IPropertySymbol property)
	{
		var type      = property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
		var modifiers = FormatModifiers(property);
		var accessors = new List<string>();
		
		if(property.GetMethod is not null)
			accessors.Add("get");
		if(property.SetMethod is not null)
			accessors.Add("set");
		
		var accessorStr = accessors.Count > 0 ? $" {{ {string.Join("; ", accessors)}; }}" : string.Empty;
		
		return $"{modifiers}{type} {property.Name}{accessorStr}";
	}
	
	private static string FormatField(IFieldSymbol field)
	{
		var type      = field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
		var modifiers = FormatModifiers(field);
		
		return $"{modifiers}{type} {field.Name}";
	}
	
	private static string FormatEvent(IEventSymbol evt)
	{
		var type      = evt.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
		var modifiers = FormatModifiers(evt);
		
		return $"{modifiers}event {type} {evt.Name}";
	}
	
	private static string FormatType(INamedTypeSymbol type)
	{
		var kind      = type.TypeKind.ToString().ToLowerInvariant();
		var modifiers = FormatModifiers(type);
		var name      = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
		
		return $"{modifiers}{kind} {name}";
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
