using System.Xml.Linq;
using System.ComponentModel;
using System.Xml;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class GetSymbolDocumentationTool(WorkspaceManager workspace)
{
    [McpServerTool, Description(
        "Returns XML documentation comments for a symbol (type, method, property, field, event). " +
        "Includes summary, parameter descriptions, return value description, and remarks. " +
        "Use this to understand API contracts without reading source files.")]
    public object GetSymbolDocumentation(
        [Description("The symbol name, e.g. 'WorkspaceManager', 'GetCompilation', 'RootPath'.")] string symbolName,
        [Description("Optional containing type to narrow the search, e.g. 'WorkspaceManager'.")] string? containingType = null)
    {
        var compilation = workspace.GetCompilation();
        var symbol      = FindSymbol(compilation, symbolName, containingType);

        if(symbol is null)
            return new { error = $"Symbol '{symbolName}' not found. Use get_type_members or find_references to verify the name." };

        var xml = symbol.GetDocumentationCommentXml();

        if(string.IsNullOrWhiteSpace(xml))
            return new {
                symbol_name = FormatSymbolName(symbol),
                symbol_kind = symbol.Kind.ToString().ToLowerInvariant(),
                documentation = (string?) null,
                message = "No documentation comments found for this symbol."
            };

        var parsed = ParseDocumentation(xml);

        return new {
            symbol_name   = FormatSymbolName(symbol),
            symbol_kind   = symbol.Kind.ToString().ToLowerInvariant(),
            summary       = parsed.Summary,
            parameters    = parsed.Parameters,
            returns       = parsed.Returns,
            remarks       = parsed.Remarks,
            example       = parsed.Example
        };
    }

    private static ISymbol? FindSymbol(Compilation compilation, string name, string? inType)
    {
        if(inType is not null) {

            var type = compilation.GetTypeByMetadataName(inType)
                ?? compilation.GlobalNamespace.Accept(new SimpleNameFinder<INamedTypeSymbol>(inType));

            return type?.GetMembers(name).FirstOrDefault();
        }

        // Global search: try as type first, then as member.
        var typeSymbol = compilation.GetTypeByMetadataName(name)
            ?? compilation.GlobalNamespace.Accept(new SimpleNameFinder<INamedTypeSymbol>(name));

        if(typeSymbol is not null)
            return typeSymbol;

        return compilation.GlobalNamespace.Accept(new AnySymbolFinder(name));
    }

    private static string FormatSymbolName(ISymbol symbol)
    {
        if(symbol is INamedTypeSymbol)
            return symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

        var containingType = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

        return containingType is not null
            ? $"{containingType}.{symbol.Name}"
            : symbol.Name;
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
            var parameters = root.Elements("param")
                .Select(e => new ParameterDoc(
                    Name: e.Attribute("name")?.Value ?? "?",
                    Description: e.Value.Trim()
                ))
                .ToArray();

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
