using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class FileOutlineTool : RoslynMcpTool
{
    public FileOutlineTool(WorkspaceResolver workspace, FileLogger logger) : base(workspace, logger) { }

    [McpServerTool(Name = "roslyn_get_file_outline", ReadOnly = true)]
    [Description(
        "Returns a structured outline of a file: types and their members (method signatures, properties, fields) without bodies. " +
        "Use this to understand file structure without reading the entire content — saves tokens.")]
    public async Task<object> GetFileOutline(
        [Description("Relative file path, e.g. 'Core/WindowTracker.cs'.")] string filePath,
        [Description(ProjectPathDescription)] string? projectPath = null)
    {
        using var scope = BeginTool("roslyn_get_file_outline", filePath);
        if(!TryGetCompilation(projectPath, out var compilation, out var error))
            return error;

        var rootPath   = workspace.GetRootPath(projectPath);
        var normalized = filePath.Replace('/', Path.DirectorySeparatorChar);

        var tree = compilation.SyntaxTrees
            .FirstOrDefault(t => t.FilePath.EndsWith(normalized, StringComparison.OrdinalIgnoreCase));

        if(tree is null) {
            scope.Failed("file not found"); return new { error = $"File '{filePath}' not found in the compilation." };
        }

        var root  = await tree.GetRootAsync();
        var model = compilation.GetSemanticModel(tree);
        var types = ExtractTypes(root, model);

        return new {
            file  = Path.GetRelativePath(rootPath, tree.FilePath),
            types = types
        };
    }

    private static TypeOutline[] ExtractTypes(SyntaxNode root, SemanticModel model)
    {
        var results = new List<TypeOutline>();

        // Walk all type declarations: class, struct, interface, enum, record.
        foreach(var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()) {

            var symbol = model.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;

            if(symbol is null)
                continue;

            var kind    = symbol.TypeKind.ToString().ToLowerInvariant();
            var name    = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var members = ExtractMembers(symbol);

            results.Add(new TypeOutline(kind, name, members));
        }

        return [.. results];
    }

    private static MemberOutline[] ExtractMembers(INamedTypeSymbol type)
    {
        var results = new List<MemberOutline>();

        foreach(var member in type.GetMembers()) {

            if(member.IsImplicitlyDeclared)
                continue;

            var kind = member.Kind.ToString().ToLowerInvariant();
            var signature = member switch {
                IMethodSymbol m => FormatMethod(m),
                IPropertySymbol p => FormatProperty(p),
                IFieldSymbol f => FormatField(f),
                IEventSymbol e => FormatEvent(e),
                _ => member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            };

            results.Add(new MemberOutline(kind, signature));
        }

        return [.. results];
    }

    private static string FormatMethod(IMethodSymbol method)
    {
        // Skip special methods (property getters/setters, event add/remove).
        if(method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet
            or MethodKind.EventAdd or MethodKind.EventRemove)
            return string.Empty;

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

    private static string FormatModifiers(ISymbol symbol)
    {
        var parts = new List<string>();

        if(symbol.IsStatic)
            parts.Add("static");
        if(symbol.IsAbstract && symbol.ContainingType?.TypeKind != TypeKind.Interface)
            parts.Add("abstract");
        if(symbol.IsVirtual)
            parts.Add("virtual");
        if(symbol.IsOverride)
            parts.Add("override");
        if(symbol.IsSealed && symbol.Kind != SymbolKind.NamedType)
            parts.Add("sealed");

        // Accessibility
        var access = symbol.DeclaredAccessibility switch {
            Accessibility.Public    => "public",
            Accessibility.Private   => "private",
            Accessibility.Protected => "protected",
            Accessibility.Internal  => "internal",
            _                       => null
        };

        if(access is not null)
            parts.Insert(0, access);

        return parts.Count > 0 ? string.Join(" ", parts) + " " : string.Empty;
    }

    private sealed record TypeOutline(string Kind, string Name, MemberOutline[] Members);
    private sealed record MemberOutline(string Kind, string Signature);
}
