using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class TypeMembersTool : RoslynMcpTool
{
	public TypeMembersTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
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

		[Description("Include inherited members from base types. Default: false (declared only).")] bool includeInherited = false,

		[Description("Number of members to skip (for paging). Default: 0.")] int skip = 0,
		[Description("Maximum number of members to return. Default: 50, max: 200.")] int take = 50,
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
			return scope.Failed("type not found", new { error = $"Type '{typeName}' not found in the project." });
		
		IEnumerable<ISymbol> members = type.GetMembers();

		if(includeInherited) {

			// Walk the base type chain and collect inherited members.
			var current = type.BaseType;

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
		];
		
		var result = PaginateAndStore(allMembers, ref skip, take);

		return scope.Outcome($"{result.Items.Length}/{result.Total} member(s)", new {
			type_name     = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
			type_kind     = type.TypeKind.ToString().ToLowerInvariant(),
			total_members = result.Total,
			skip, take,
			members    = result.Items,
			page_token = result.PageToken,
			has_more   = result.HasMore,
			_caution   = AdhocCaution(projectPath)
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
