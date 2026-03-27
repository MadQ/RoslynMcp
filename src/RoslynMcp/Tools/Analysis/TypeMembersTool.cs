using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class TypeMembersTool : RoslynMcpTool
{
	public TypeMembersTool(WorkspaceResolver workspace, FileLogger logger) : base(workspace, logger) { }
	
	[McpServerTool(Name = "roslyn_get_type_members", ReadOnly = true)]
	[Description(
		"Returns detailed information about all members of a type (class, struct, enum, interface). " +
		"Includes full signatures with parameter types, return types, modifiers, and XML doc summaries. " +
		"For enums, returns the member names. Use this to understand a type's API surface. Results are paged; use skip/take for large types.")]
	public object GetTypeMembers(
		[Description("The simple or fully-qualified type name, e.g. 'ShowWindowCommand' or 'ScreenMon.RuleMode'.")]
		string typeName,

		[Description(ProjectPathDescription)]
		string projectPath,

		[Description("Optional filter: 'field', 'property', 'method', 'enum', 'event', or omit for all.")]
		string? memberKind = null,

		[Description("Number of members to skip (for paging). Default: 0.")] int skip = 0,
		[Description("Maximum number of members to return. Default: 50, max: 200.")] int take = 50)
	{
		using var scope = BeginTool("roslyn_get_type_members", typeName);
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return error;
		
		take = Math.Clamp(take, 1, 200);
		
		var type = FindType(compilation, typeName);
		
		if(type is null)
			return scope.Failed("type not found", new { error = $"Type '{typeName}' not found in the project." });
		
		var allMembers = type.GetMembers()
			.Where(m => !m.IsImplicitlyDeclared)
			.Where(m => memberKind is null || MatchesKind(m, memberKind))
			.Select(FormatMember)
			.Where(m => m is not null)
			.ToArray()
		;
		
		var page = allMembers.AsSpan(skip, Math.Min(take, allMembers.Length - skip)).ToArray();
		
		return scope.Outcome($"{page.Length}/{allMembers.Length} member(s)", new {
			type_name = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
			type_kind = type.TypeKind.ToString().ToLowerInvariant(),
			total_members = allMembers.Length,
			skip,
			take,
			members   = page
		});
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
	
	private static MemberInfo? FormatMember(ISymbol member)
	{
		var kind = member.Kind.ToString().ToLowerInvariant();
		
		// Skip special compiler-generated methods (property accessors, etc.).
		if(member is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove })
			return null;
		
		var signature = member switch {
			IMethodSymbol m   => FormatMethod(m),
			IPropertySymbol p => FormatProperty(p),
			IFieldSymbol f    => FormatField(f),
			IEventSymbol e    => FormatEvent(e),
			_                 => member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
		};
		
		var docXml = member.GetDocumentationCommentXml();
		var docSummary = ExtractDocSummary(docXml);
		
		return new MemberInfo(
			Kind: kind,
			Name: member.Name,
			Signature: signature,
			DocSummary: docSummary
		);
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
	
	private sealed record MemberInfo(string Kind, string Name, string Signature, string? DocSummary);
}
