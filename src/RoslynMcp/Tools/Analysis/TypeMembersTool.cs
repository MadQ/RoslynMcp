using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class TypeMembersTool : RoslynMcpTool
{
	public TypeMembersTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_get_type_members", ReadOnly = true, Title = "Get Type Members", OpenWorld = false, Idempotent = true)]
	[Description(
		"Use this to inspect a type's API surface — all fields, properties, methods, events, and (for enums) member values, " +
		"with full signatures, modifiers, parameter types, return types, and XML doc summaries. " +
		"This is the right first tool when you need to understand what a type can do before generating code that uses it. " +
		"By default returns only members declared directly on the type; set includeInherited=true to also include " +
		"members from base types (up to but not including System.Object). " +
		"Property accessors (get/set) and event accessors (add/remove) are excluded — properties and events themselves appear. " +
		"Results are returned in declaration order, paged with default take=50 and max take=200. " +
		"For the full source code of a specific member, use roslyn_get_member_body. " +
		"For XML documentation only, use roslyn_get_symbol_documentation."),]
	public object GetTypeMembers(
		[Description("The type to inspect, as a simple name (e.g. 'WorkspaceManager') or fully-qualified name (e.g. 'RoslynMcp.WorkspaceManager'). Use roslyn_list_types to discover available type names.")]
		string typeName,
		
		[Description(ProjectPathDescription)]
		string projectPath,
		
		[Description("Optional member kind filter: 'field', 'property', 'method', 'event'. Omit to return all kinds. 'enum' filters to enum member fields.")]
		string? memberKind = null,
		
		[Description("When true, includes members declared on base types (up to but not including System.Object). Default: false (declared members only).")] bool includeInherited = false,
		
		[Description("Number of members to skip. Default: 0.")] int skip = 0,
		[Description("Maximum members to return. Default: 50, max: 200.")] int take = 50,
		[Description("Token from a previous response to get the next page without re-executing the query.")] string? page_token = null)
	{
		using var scope = BeginTool("roslyn_get_type_members", typeName);
		
		
		var cachedPage = TryServeCachedPage<object?>(scope, page_token, ref skip, ref take, 200);
		if(cachedPage is not null)
			
			return cachedPage;
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return error;
		
		var type = FindType(compilation, typeName);
		
		if(type is null)
			
			return scope.Failed("type not found", new ErrorResult($"Type '{typeName}' not found in the project."));
		
		IEnumerable<ISymbol> members = type.GetMembers();
		
		if(includeInherited) {
			
			// Walk the base type chain and collect inherited members.
			var current = type.BaseType
			;
			
			while(current is not null && current.SpecialType != SpecialType.System_Object) {
				
				members = members.Concat(current.GetMembers());
				current = current.BaseType;
			}
		}
		
		var allMembers = (object?[]) [..
			members
				.Where(m => !m.IsImplicitlyDeclared)
				.Where(m => memberKind is null || MatchesKind(m, memberKind))
				.Select(FormatMember)
				.Where(m => m is not null)
		]
		;
		
		var result = PaginateAndStore(allMembers, ref skip, take);
		
		return scope.Outcome($"{result.Items.Length}/{result.Total} member(s)", new TypeMembersResult(
			Type_name:     type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
			Type_kind:     type.TypeKind.ToString().ToLowerInvariant(),
			Total_members: result.Total,
			Skip: skip, Take: take,
			Members:    result.Items,
			Page_token: result.PageToken,
			Has_more:   result.HasMore,
			_caution:   AdhocCaution(projectPath)
		));
	}
	
	private static INamedTypeSymbol? FindType(Compilation compilation, string typeName)
	{
		// Try global namespace lookup first (handles simple names).
		var direct = compilation.GetTypeByMetadataName(typeName)
		;
		
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
			
			IMethodSymbol m   => SymbolFormatter.FormatMethod(m),
			IPropertySymbol p => SymbolFormatter.FormatProperty(p),
			IFieldSymbol f    => SymbolFormatter.FormatField(f),
			IEventSymbol e    => SymbolFormatter.FormatEvent(e),
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
