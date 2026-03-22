#if FALSE
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using RoslynSymbolFinder = Microsoft.CodeAnalysis.FindSymbols.SymbolFinder;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class FindImplementationsTool(WorkspaceManager workspace)
{
    [McpServerTool, Description(
        "Finds all types that implement an interface or abstract class, or all methods that override an abstract/virtual member. " +
        "Use this to discover concrete implementations of abstractions.")]
    public async Task<object> FindImplementations(
        [Description("The symbol name, e.g. 'IDisposable', 'SymbolVisitor', 'Accept'.")] string symbolName,
        [Description("Optional containing type to narrow the search, e.g. 'SymbolVisitor' when searching for 'Accept'.")] string? containingType = null)
    {
        var compilation = workspace.GetCompilation();
        var symbol      = FindSymbol(compilation, symbolName, containingType);

        if(symbol is null)
            return new { error = $"Symbol '{symbolName}' not found. Use get_type_members or find_references to verify the name." };

        var solution = workspace.GetSolution();

        // Handle type symbols (interface or abstract class).
        if(symbol is INamedTypeSymbol typeSymbol) {

            if(typeSymbol.TypeKind is TypeKind.Interface or TypeKind.Class && typeSymbol.IsAbstract) {

                var impls = await RoslynSymbolFinder.FindImplementationsAsync(typeSymbol, solution);
                var results = impls
                    .OfType<INamedTypeSymbol>()
                    .Select(t => t.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))
                    .Order()
                    .ToArray();

                return new {
                    symbol_type  = typeSymbol.TypeKind.ToString().ToLowerInvariant(),
                    symbol_name  = typeSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    implementations = results.Length > 0 ? results : ["No implementations found."]
                };
            }

            return new { error = $"'{symbolName}' is not an interface or abstract class." };
        }

        // Handle method symbols (abstract or virtual).
        if(symbol is IMethodSymbol methodSymbol) {

            if(methodSymbol.IsAbstract || methodSymbol.IsVirtual || methodSymbol.IsOverride) {

                var overrides = await RoslynSymbolFinder.FindOverridesAsync(methodSymbol, solution);
                var results   = overrides
                    .OfType<IMethodSymbol>()
                    .Select(m => FormatMethod(m))
                    .Order()
                    .ToArray();

                return new {
                    symbol_type = "method",
                    symbol_name = FormatMethod(methodSymbol),
                    overrides   = results.Length > 0 ? results : ["No overrides found."]
                };
            }

            return new { error = $"'{symbolName}' is not an abstract, virtual, or override method." };
        }

        return new { error = $"'{symbolName}' is not a type or method — cannot find implementations." };
    }

    private static ISymbol? FindSymbol(Compilation compilation, string name, string? inType)
    {
        if(inType is not null) {

            var type = compilation.GetTypeByMetadataName(inType)
                ?? compilation.GlobalNamespace.Accept(new SimpleNameFinder<INamedTypeSymbol>(inType));

            return type?.GetMembers(name).FirstOrDefault();
        }

        // Global search for type or member.
        return compilation.GlobalNamespace.Accept(new AnySymbolFinder(name));
    }

    private static string FormatMethod(IMethodSymbol method)
    {
        var containingType = method.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        var returnType     = method.ReturnsVoid
            ? "void"
            : method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        var parameters = string.Join(", ", method.Parameters.Select(p =>
            p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
        ));

        return $"{containingType}.{method.Name}({parameters}): {returnType}";
    }
}
#endif
