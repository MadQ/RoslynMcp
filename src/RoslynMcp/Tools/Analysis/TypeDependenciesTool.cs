using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class TypeDependenciesTool : RoslynMcpTool
{
	public TypeDependenciesTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_get_type_dependencies", ReadOnly = true, Title = "Get Type Dependencies", OpenWorld = false, Idempotent = true)]
	[Description(
		"Given a type, returns all types it directly references through its declaration and member signatures: " +
		"field types, parameter types, return types, base type, interfaces, and generic constraints. " +
		"Answers what the type couples to before extracting, moving, or refactoring it. " +
		"Does not inspect nested types, attributes, method bodies, local variables, or called methods.")]
	public object GetTypeDependencies(
		[Description("The type to inspect, as a simple name (e.g. 'WorkspaceManager') or fully-qualified name (e.g. 'RoslynMcp.WorkspaceManager'). Simple names are resolved by scanning the global namespace.")] string typeName,
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Number of dependencies to skip. Default: 0.")] int skip = 0,
		[Description("Maximum dependencies to return. Default: 50, max: 200.")] int take = 50,
		[Description("Token from a previous response to get the next page without re-executing the query.")] string? page_token = null)
	{
		using var scope = BeginTool("roslyn_get_type_dependencies", typeName, new { skip, take });
		
		if(!TryStripChatSymbolRef(ref typeName, out var refError))
			
			return scope.Error(refError!);
		
		if(scope.TryServeCachedPage<TypeDependencyEntry>(page_token, ref skip, ref take, 200, out var cached))
			
			return scope.Outcome("cached page", cached);
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return scope.Error(error!);
		
		var type = FindType(compilation, typeName);
		
		if(type is null)
			
			return scope.Failed("type not found", new ErrorResult($"Type '{typeName}' not found in the project."));
		
		var collector = new DependencyCollector();
		
		if(IsReportableBaseType(type.BaseType))
			collector.Add(type.BaseType!, "base_type", null);
		
		foreach(var interfaceType in type.Interfaces)
			collector.Add(interfaceType, "interface", null);
		
		AddTypeParameterConstraints(collector, type.TypeParameters, "generic_constraint", null);
		
		foreach(var member in type.GetMembers()) {
			
			if(member.IsImplicitlyDeclared)
				continue;
			
			switch(member) {
				
				case IFieldSymbol field:
					collector.Add(field.Type, "field", field.Name);
					break;
				
				case IPropertySymbol property:
					collector.Add(property.Type, "property", property.Name);
					break;
				
				case IEventSymbol eventSymbol:
					collector.Add(eventSymbol.Type, "event", eventSymbol.Name);
					break;
				
				case IMethodSymbol method when IsDependencyBearingMethod(method):
					AddMethodDependencies(collector, method);
					break;
			}
		}
		
		var allDependencies = collector.ToArray();
		var result          = PaginateAndStore(allDependencies, ref skip, take);
		
		return scope.Outcome($"{result.Items.Length}/{result.Total} dependency/dependencies", new TypeDependenciesResult(
			TypeName:          type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
			TypeKind:          type.TypeKind.ToString().ToLowerInvariant(),
			TotalDependencies: result.Total,
			Skip: skip, Take: take,
			Dependencies: result.Items,
			PageToken:    result.PageToken,
			HasMore:      result.HasMore)
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
	
	private static bool IsReportableBaseType(INamedTypeSymbol? baseType)
		=> baseType is not null
			&& baseType.SpecialType is not SpecialType.System_Object
			&& baseType.SpecialType is not SpecialType.System_ValueType
			&& baseType.SpecialType is not SpecialType.System_Enum;
	
	private static bool IsDependencyBearingMethod(IMethodSymbol method)
		=> method.MethodKind is
			MethodKind.Ordinary or
			MethodKind.Constructor or
			MethodKind.UserDefinedOperator or
			MethodKind.Conversion;
	
	private static void AddMethodDependencies(DependencyCollector collector, IMethodSymbol method)
	{
		if(method.MethodKind != MethodKind.Constructor && !method.ReturnsVoid)
			collector.Add(method.ReturnType, "method_return", method.Name);
		
		var dependencyKind = method.MethodKind == MethodKind.Constructor
			? "constructor_parameter"
			: "method_parameter";
		
		foreach(var parameter in method.Parameters)
			collector.Add(parameter.Type, dependencyKind, method.Name);
		
		AddTypeParameterConstraints(collector, method.TypeParameters, "generic_constraint", method.Name);
	}
	
	private static void AddTypeParameterConstraints(DependencyCollector collector, IEnumerable<ITypeParameterSymbol> typeParameters, string dependencyKind, string? member)
	{
		foreach(var typeParameter in typeParameters)
		foreach(var constraintType in typeParameter.ConstraintTypes)
			collector.Add(constraintType, dependencyKind, member ?? typeParameter.Name);
	}
	
	private sealed class DependencyCollector
	{
		private readonly HashSet<string> keys = [];
		private readonly List<TypeDependencyEntry> entries = [];
		
		public void Add(ITypeSymbol type, string dependencyKind, string? member)
		{
			foreach(var dependencyType in FlattenType(type)) {
				
				var typeName = dependencyType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
				var key      = $"{typeName}|{dependencyKind}|{member}";
				
				if(!keys.Add(key))
					continue;
				
				entries.Add(new TypeDependencyEntry(typeName, dependencyKind, member));
			}
		}
		
		public TypeDependencyEntry[] ToArray()
			=> [..
				entries
					.OrderBy(e => e.DependencyKind, StringComparer.Ordinal)
					.ThenBy(e => e.Member, StringComparer.Ordinal)
					.ThenBy(e => e.TypeName, StringComparer.Ordinal)
			];
	}
	
	private static IEnumerable<ITypeSymbol> FlattenType(ITypeSymbol type)
	{
		if(type is ITypeParameterSymbol)
			yield break;
		
		yield return type;
		
		switch(type) {
			
			case IArrayTypeSymbol arrayType:
				foreach(var nestedType in FlattenType(arrayType.ElementType))
					yield return nestedType;
				break;
			
			case IPointerTypeSymbol pointerType:
				foreach(var nestedType in FlattenType(pointerType.PointedAtType))
					yield return nestedType;
				break;
			
			case INamedTypeSymbol namedType:
				foreach(var typeArgument in namedType.TypeArguments)
				foreach(var nestedType in FlattenType(typeArgument))
					yield return nestedType;
				break;
		}
	}
}
