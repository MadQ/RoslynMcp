using Microsoft.CodeAnalysis;

namespace RoslynMcp;

/// <summary>Walks the namespace tree to find a named type by simple or dotted name.</summary>
internal sealed class SimpleNameFinder<T>(string name) : SymbolVisitor<T?>
    where T : class, ISymbol
{
    private readonly string simpleName = name.Contains('.')
        ? name[(name.LastIndexOf('.') + 1)..]
        : name;

    public override T? VisitNamespace(INamespaceSymbol symbol)
    {
        foreach(var m in symbol.GetMembers()) {
            var r = m.Accept(this);
            if(r is not null) return r;
        }

        return null;
    }

    public override T? VisitNamedType(INamedTypeSymbol symbol)
    {
        if(symbol is T t && symbol.Name == simpleName)
            return t;

        foreach(var n in symbol.GetTypeMembers()) {
            var r = n.Accept(this);
            if(r is not null) return r;
        }

        return null;
    }
}

/// <summary>Walks all types to find the first symbol (type or member) matching a simple name.</summary>
internal sealed class AnySymbolFinder(string name) : SymbolVisitor<ISymbol?>
{
    public override ISymbol? VisitNamespace(INamespaceSymbol symbol)
    {
        foreach(var m in symbol.GetMembers()) {
            var r = m.Accept(this);
            if(r is not null) return r;
        }

        return null;
    }

    public override ISymbol? VisitNamedType(INamedTypeSymbol symbol)
    {
        if(symbol.Name == name) return symbol;

        var member = symbol.GetMembers(name).FirstOrDefault();
        if(member is not null) return member;

        foreach(var n in symbol.GetTypeMembers()) {
            var r = n.Accept(this);
            if(r is not null) return r;
        }

        return null;
    }
}
