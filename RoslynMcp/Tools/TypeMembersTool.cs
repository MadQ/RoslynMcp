using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class TypeMembersTool(WorkspaceManager workspace)
{
    [McpServerTool, Description(
        "Returns all member names of a named type (class, struct, enum, interface) in the target project. " +
        "For enums, returns the member names — use this to verify exact enum value spelling before writing code.")]
    public string[] GetTypeMembers(
        [Description("The simple or fully-qualified type name, e.g. 'ShowWindowCommand' or 'ScreenMon.RuleMode'.")] string typeName,
        [Description("Optional filter: 'field', 'property', 'method', 'enum', 'event', or omit for all.")] string? memberKind = null)
    {
        var compilation = workspace.GetCompilation();
        var type        = FindType(compilation, typeName);

        if(type is null)
            return [$"Type '{typeName}' not found in the project."];

        var members = type.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared)
            .Where(m => memberKind is null || MatchesKind(m, memberKind))
            .Select(m => m.Name)
            .Distinct()
            .Order()
            .ToArray()
        ;

        return members.Length > 0 ? members : [$"No members found on '{typeName}' (kind filter: {memberKind ?? "none"})."];
    }

    private static INamedTypeSymbol? FindType(Compilation compilation, string typeName)
    {
        // Try global namespace lookup first (handles simple names).
        var direct = compilation.GetTypeByMetadataName(typeName);

        if(direct is not null)
            return direct;

        return compilation.GlobalNamespace
            .Accept(new SimpleNameFinder<INamedTypeSymbol>(typeName))
        ;
    }

    private static bool MatchesKind(ISymbol member, string kind)
        => kind.ToLowerInvariant() switch
        {
            "field"     => member is IFieldSymbol,
            "property"  => member is IPropertySymbol,
            "method"    => member is IMethodSymbol,
            "enum"      => member is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum },
            "event"     => member is IEventSymbol,
            _           => true
        };
}
