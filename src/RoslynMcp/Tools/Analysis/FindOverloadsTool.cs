using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class FindOverloadsTool : RoslynMcpTool
{
	public FindOverloadsTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_find_overloads", ReadOnly = true, Title = "Find Overloads", OpenWorld = false, Idempotent = true)]
	[Description(
		"Given a method name and containing type, returns all overloads with full signatures. " +
		"Use this before editing, calling, renaming, or changing a method that may have multiple overloads. " +
		"Requires containingType so results stay scoped to one type instead of mixing unrelated methods across the project.")]
	public object FindOverloads(
		[Description("Method name to find overloads for, e.g. 'Save', 'ProcessOrder'.")] string methodName,
		[Description("Containing type that declares the method, e.g. 'UserService' or 'MyApp.Services.UserService'.")] string containingType,
		[Description(ProjectPathDescription)] string projectPath)
	{
		using var scope = BeginTool("roslyn_find_overloads", methodName, new { containingType });
		
		if(!TryStripChatSymbolRef(ref methodName, out var refError) || !TryStripChatSymbolRef(ref containingType, out refError))
			
			return scope.Error(refError!);
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return scope.Error(error!);
		
		var type = FindType(compilation, containingType);
		
		if(type is null)
			
			return scope.Failed("type not found", new ErrorResult($"Type '{containingType}' not found in the project."));
		
		var overloads = type.GetMembers(methodName)
			.OfType<IMethodSymbol>()
			.Where(m => !m.IsImplicitlyDeclared)
			.Where(m => m.MethodKind == MethodKind.Ordinary)
			.Select(SymbolFormatter.FormatFullMethodSignature)
			.ToArray()
		;
		
		if(overloads.Length == 0)
			
			return scope.Outcome("no overloads found", new FindOverloadsResult(
				ContainingType: type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
				MethodName: methodName,
				TotalOverloads: 0,
				Overloads: [])
			{
				Caution = AdhocCaution(projectPath)
			});
		
		return scope.Outcome($"{overloads.Length} overload(s)", new FindOverloadsResult(
			ContainingType: type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
			MethodName: methodName,
			TotalOverloads: overloads.Length,
			Overloads: overloads)
		{
			Caution = AdhocCaution(projectPath)
		});
	}
	
	private static INamedTypeSymbol? FindType(Compilation compilation, string typeName)
	{
		var direct = GetTypeByMetadataNameOrBest(compilation, typeName);
		
		if(direct is not null)
			
			return direct;
		
		return compilation.GlobalNamespace
			.Accept(new SimpleNameFinder<INamedTypeSymbol>(typeName))
		;
	}
	
	private sealed record FindOverloadsResult(
		[property: JsonPropertyName("containing_type")]
		string ContainingType,
		[property: JsonPropertyName("method_name")]
		string MethodName,
		[property: JsonPropertyName("total_overloads")]
		int TotalOverloads,
		[property: JsonPropertyName("overloads")]
		string[] Overloads
	) : ToolResult;
}
