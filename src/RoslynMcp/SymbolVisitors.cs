using Microsoft.CodeAnalysis;

namespace RoslynMcp;

/// <summary>Walks the namespace tree to find a named type by simple or dotted name.</summary>
internal sealed class SimpleNameFinder<T>(string name) : SymbolVisitor<T?>
	where T : class, ISymbol
{
	private readonly bool   isDotted   = name.Contains('.');
	private readonly string simpleName = name.Contains('.')
		? name[(name.LastIndexOf('.') + 1)..]
		: name
	;
	
	public override T? VisitNamespace(INamespaceSymbol symbol)
	{
		foreach(var m in symbol.GetMembers()) {
			
			var r = m.Accept(this);
			
			if(r is not null)
				
				return r;
		}
		
		return null;
	}
	
	public override T? VisitNamedType(INamedTypeSymbol symbol)
	{
		if(symbol is T t && symbol.Name == simpleName)
			// When a dotted name was provided, verify the full qualification matches.
			if(!isDotted || symbol.ToDisplayString().EndsWith(name, StringComparison.Ordinal))
				
				return t;
		
		foreach(var n in symbol.GetTypeMembers()) {
			
			var r = n.Accept(this);
			
			if(r is not null)
				
				return r;
		}
		
		return null;
	}
}



/// <summary>Walks all types to collect ALL symbols (types and members) matching a simple name.</summary>
/// <remarks>
///     Intentionally does not extend <see cref="SymbolVisitor{T}"/>: that API returns a single T per visit,
///     making multi-result accumulation awkward. A shared accumulator list is simpler and equally correct.
/// </remarks>
internal sealed class AllSymbolsFinder(string name)
{
	readonly List<ISymbol> results = [];
	
	public IReadOnlyList<ISymbol> Results => results;
	
	public void Visit(INamespaceSymbol ns)
	{
		foreach(var m in ns.GetMembers()) 
			if(m is INamespaceSymbol childNs)
				Visit(childNs);
			else if(m is INamedTypeSymbol type)
				VisitType(type);
	
	}
	
	void VisitType(INamedTypeSymbol type)
	{
		if(type.Name == name)
			results.Add(type);
		
		foreach(var member in type.GetMembers(name))
			results.Add(member);
		
		foreach(var nested in type.GetTypeMembers())
			VisitType(nested);
	}
}

/// <summary>Walks all types to find the first symbol (type or member) matching a simple name.</summary>
internal sealed class AnySymbolFinder(string name) : SymbolVisitor<ISymbol?>
{
	public override ISymbol? VisitNamespace(INamespaceSymbol symbol)
	{
		foreach(var m in symbol.GetMembers()) {
			
			var r = m.Accept(this);
			
			if(r is not null)
				
				return r;
		}
		
		return null;
	}
	
	public override ISymbol? VisitNamedType(INamedTypeSymbol symbol)
	{
		if(symbol.Name == name)
			
			return symbol;
		
		var member = symbol.GetMembers(name).FirstOrDefault();
		
		if(member is not null)
			
			return member;
		
		foreach(var n in symbol.GetTypeMembers()) {
			
			var r = n.Accept(this);
			
			if(r is not null)
				
				return r;
		}
		
		return null;
	}
}
