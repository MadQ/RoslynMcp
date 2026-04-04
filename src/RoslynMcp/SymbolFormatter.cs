using Microsoft.CodeAnalysis;

namespace RoslynMcp;

/// <summary>
///     Shared symbol formatting utilities. Used by FileOutlineTool, GetSymbolDefinitionTool,
///     TypeMembersTool, ListTypesTool, and others for consistent signature rendering.
/// </summary>
internal static class SymbolFormatter
{
	public static string FormatSignature(ISymbol symbol) => symbol switch {
		
		IMethodSymbol m    => FormatMethod(m),
		IPropertySymbol p  => FormatProperty(p),
		IFieldSymbol f     => FormatField(f),
		IEventSymbol e     => FormatEvent(e),
		INamedTypeSymbol t => FormatType(t),
		_                  => symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
	};
	
	public static string FormatMethod(IMethodSymbol method)
	{
		var returnType = method.ReturnsVoid
			? "void"
			: method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
		;
		
		var parameters = string.Join(", ", method.Parameters.Select(p =>
			$"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}"
		));
		
		var modifiers = FormatModifiers(method);
		
		return $"{modifiers}{returnType} {method.Name}({parameters})";
	}
	
	public static string FormatProperty(IPropertySymbol property)
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
	
	public static string FormatField(IFieldSymbol field)
	{
		var type      = field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
		var modifiers = FormatModifiers(field);
		
		return $"{modifiers}{type} {field.Name}";
	}
	
	public static string FormatEvent(IEventSymbol evt)
	{
		var type      = evt.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
		var modifiers = FormatModifiers(evt);
		
		return $"{modifiers}event {type} {evt.Name}";
	}
	
	public static string FormatType(INamedTypeSymbol type)
	{
		var kind      = type.TypeKind.ToString().ToLowerInvariant();
		var modifiers = FormatModifiers(type);
		var name      = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
		
		return $"{modifiers}{kind} {name}";
	}
	
	public static string FormatModifiers(ISymbol symbol)
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
		
		var access = symbol.DeclaredAccessibility switch {
			Accessibility.Public               => "public",
			Accessibility.Private              => "private",
			Accessibility.Protected            => "protected",
			Accessibility.Internal             => "internal",
			Accessibility.ProtectedOrInternal  => "protected internal",
			Accessibility.ProtectedAndInternal => "private protected",
			_                                  => null
		};
		
		if(access is not null)
			parts.Insert(0, access);
		
		return parts.Count > 0 ? string.Join(" ", parts) + " " : string.Empty;
	}
}
