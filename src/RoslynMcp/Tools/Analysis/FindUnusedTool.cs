using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using RoslynSymbolFinder = Microsoft.CodeAnalysis.FindSymbols.SymbolFinder;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class FindUnusedTool : RoslynMcpTool
{
	private const string StaticAnalysisCaution = "Only direct static references in the loaded solution are considered. Dynamic, reflection-based, external project, and external friend-assembly usage cannot be detected; internal symbols may still be used outside the loaded solution.";
	
	public FindUnusedTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_find_unused", ReadOnly = true, Title = "Find Unused Symbols", OpenWorld = false, Idempotent = true)]
	[Description(
		"Finds private, internal, and effectively internal source symbols with zero direct static references across the loaded solution. " +
		"Checks types, methods, constructors, fields, properties, and events. " +
		"Conservatively skips generated symbols, attributed symbols, members of attributed or generated types, partial methods, protected members, interface implementations, and inheritance-dispatched members. " +
		"Results are paged and include declaration locations, confidence, and a reason. Dynamic or reflection-based usage cannot be detected.")]
	public async Task<object> FindUnused(
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken,
		[Description("Number of unused symbols to skip. Default: 0.")] int skip = 0,
		[Description("Maximum number of unused symbols to return. Default: 50, max: 200.")] int take = 50,
		[Description("Token from a previous response to get the next page without repeating the reference analysis.")] string? page_token = null)
	{
		using var scope = BeginTool("roslyn_find_unused", projectPath, new { skip, take });
		
		if(scope.TryServeCachedPage<UnusedSymbolEntry>(page_token, ref skip, ref take, 200, out var cached))
			
			return scope.Outcome("cached page", cached);
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return scope.Error(error!);
		
		var solution            = workspace.GetSolution(projectPath);
		var rootPath            = workspace.GetRootPath(projectPath);
		var hasFriendAssemblies = HasFriendAssemblies(compilation.Assembly);
		var candidates          = new List<ISymbol>();
		var seen                = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
		
		CollectCandidates(compilation.Assembly.GlobalNamespace, candidates, seen);
		
		var unused = new List<UnusedCandidate>();
		
		foreach(var symbol in candidates) {
			
			cancellationToken.ThrowIfCancellationRequested();
			
			var references = await RoslynSymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken: cancellationToken);
			
			if(references.SelectMany(r => r.Locations).Any())
				continue;
			
			var declaration = symbol.Locations.First(l => l.IsInSource);
			var span        = declaration.GetLineSpan();
			var file        = span.Path is { Length: > 0 }
				? Path.GetRelativePath(rootPath, span.Path)
				: "?";
			
			unused.Add(new UnusedCandidate(symbol, new UnusedSymbolEntry(
				Kind:          GetKind(symbol),
				Name:          FormatName(symbol),
				Accessibility: symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
				Confidence:    GetConfidence(symbol),
				Reason:        GetReason(symbol, hasFriendAssemblies),
				File:          file,
				Line:          span.StartLinePosition.Line + 1)));
		}
		
		var unusedSymbols = unused
			.Select(u => u.Symbol)
			.ToHashSet(SymbolEqualityComparer.Default)
		;
		
		var allResults = unused
			.Where(u => !HasUnusedContainingType(u.Symbol, unusedSymbols))
			.Select(u => u.Entry)
			.OrderBy(e => e.File, StringComparer.Ordinal)
			.ThenBy(e => e.Line)
			.ThenBy(e => e.Name, StringComparer.Ordinal)
			.ToArray()
		;
		
		var result = PaginateAndStore(allResults, ref skip, take);
		
		return scope.Outcome($"{result.Items.Length}/{result.Total} unused symbol(s)", new FindUnusedResult(
			TotalUnused: result.Total,
			Skip: skip, Take: take,
			Unused:    result.Items,
			PageToken: result.PageToken,
			HasMore:   result.HasMore)
		{
			Caution = BuildCaution(projectPath)
		});
	}
	
	private static void CollectCandidates(INamespaceSymbol ns, List<ISymbol> candidates, HashSet<ISymbol> seen)
	{
		foreach(var member in ns.GetMembers()) {
			
			if(member is INamespaceSymbol childNamespace)
				CollectCandidates(childNamespace, candidates, seen);
			
			else if(member is INamedTypeSymbol type)
				CollectType(type, candidates, seen);
		}
	}
	
	private static void CollectType(INamedTypeSymbol type, List<ISymbol> candidates, HashSet<ISymbol> seen)
	{
		AddCandidate(type, candidates, seen);
		
		foreach(var member in type.GetMembers()) {
			
			if(member is INamedTypeSymbol nestedType) {
				
				CollectType(nestedType, candidates, seen);
				continue;
			}
			
			AddCandidate(member, candidates, seen);
		}
	}
	
	private static void AddCandidate(ISymbol symbol, List<ISymbol> candidates, HashSet<ISymbol> seen)
	{
		if(IsCandidate(symbol) && seen.Add(symbol))
			candidates.Add(symbol);
	}
	
	private static bool IsCandidate(ISymbol symbol)
	{
		if(!IsEffectivelyPrivateOrInternal(symbol))
			
			return false;
		
		if(HasProtectedAccessibility(symbol))
			
			return false;
		
		if(symbol.IsImplicitlyDeclared || !symbol.Locations.Any(l => l.IsInSource))
			
			return false;
		
		if(IsGenerated(symbol) || HasGeneratedContainingType(symbol))
			
			return false;
		
		if(symbol.GetAttributes().Length > 0 || HasAttributedContainingType(symbol))
			
			return false;
		
		return symbol switch {
			
			INamedTypeSymbol type => IsSupportedType(type),
			IMethodSymbol method   => IsCandidateMethod(method),
			IFieldSymbol field     => field.AssociatedSymbol is null && field.ContainingType?.TypeKind != TypeKind.Enum,
			IPropertySymbol property => !property.IsAbstract && !property.IsVirtual && !property.IsOverride && !ImplementsInterfaceMember(property),
			IEventSymbol eventSymbol => !eventSymbol.IsAbstract && !eventSymbol.IsVirtual && !eventSymbol.IsOverride && !ImplementsInterfaceMember(eventSymbol),
			_ => false
		};
	}
	
	private static bool IsEffectivelyPrivateOrInternal(ISymbol symbol)
	{
		if(symbol.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal)
			
			return true;
		
		if(symbol.DeclaredAccessibility != Accessibility.Public)
			
			return false;
		
		for(var containingType = symbol.ContainingType; containingType is not null; containingType = containingType.ContainingType)
			if(containingType.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal)
				
				return true;
		
		return false;
	}
	
	private static bool HasProtectedAccessibility(ISymbol symbol)
		=> symbol.DeclaredAccessibility is Accessibility.Protected or Accessibility.ProtectedAndInternal or Accessibility.ProtectedOrInternal;
	
	private static bool IsGenerated(ISymbol symbol)
		=> symbol.Locations.Any(l => l.IsInSource && IsGenerated(l.SourceTree));
	
	private static bool HasGeneratedContainingType(ISymbol symbol)
	{
		for(var containingType = symbol.ContainingType; containingType is not null; containingType = containingType.ContainingType)
			if(IsGenerated(containingType))
				
				return true;
		
		return false;
	}
	
	private static bool IsGenerated(SyntaxTree? sourceTree)
	{
		if(sourceTree is null)
			
			return false;
		
		return IsGeneratedPath(sourceTree.FilePath) || HasAutoGeneratedHeader(sourceTree);
	}
	
	private static bool IsGeneratedPath(string? path)
	{
		if(path is null)
			
			return false;
		
		var fileName = Path.GetFileName(path);
		
		return fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
			fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
			fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase);
	}
	
	private static bool HasAutoGeneratedHeader(SyntaxTree sourceTree)
	{
		var text = sourceTree.GetText();
		var length = Math.Min(text.Length, 1024);
		
		return text.ToString(TextSpan.FromBounds(0, length)).Contains("<auto-generated", StringComparison.OrdinalIgnoreCase);
	}
	
	private static bool HasAttributedContainingType(ISymbol symbol)
	{
		for(var containingType = symbol.ContainingType; containingType is not null; containingType = containingType.ContainingType)
			if(containingType.GetAttributes().Length > 0)
				
				return true;
		
		return false;
	}
	
	private static bool IsSupportedType(INamedTypeSymbol type)
		=> type.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.Enum or TypeKind.Delegate;
	
	private static bool IsCandidateMethod(IMethodSymbol method)
	{
		if(method.MethodKind is not MethodKind.Ordinary and not MethodKind.Constructor)
			
			return false;
		
		if(method.IsAbstract || method.IsVirtual || method.IsOverride || method.IsExtern)
			
			return false;
		
		if(method.PartialDefinitionPart is not null || method.PartialImplementationPart is not null)
			
			return false;
		
		if(IsConventionMethod(method))
			
			return false;
		
		return !ImplementsInterfaceMember(method);
	}
	
	private static bool IsConventionMethod(IMethodSymbol method)
		=> method is { IsStatic: true, Name: "Main" };
	
	private static bool ImplementsInterfaceMember(ISymbol symbol)
	{
		var containingType = symbol.ContainingType;
		
		if(containingType is null)
			
			return false;
		
		foreach(var interfaceType in containingType.AllInterfaces)
		foreach(var interfaceMember in interfaceType.GetMembers()) {
			
			var implementation = containingType.FindImplementationForInterfaceMember(interfaceMember);
			
			if(SymbolEqualityComparer.Default.Equals(implementation, symbol))
				
				return true;
		}
		
		return false;
	}
	
	private static bool HasUnusedContainingType(ISymbol symbol, HashSet<ISymbol> unusedSymbols)
	{
		for(var containingType = symbol.ContainingType; containingType is not null; containingType = containingType.ContainingType)
			if(unusedSymbols.Contains(containingType))
				
				return true;
		
		return false;
	}
	
	private static bool HasFriendAssemblies(IAssemblySymbol assembly)
		=> assembly.GetAttributes().Any(a => a.AttributeClass?.Name == "InternalsVisibleToAttribute");
	
	private static string GetConfidence(ISymbol symbol)
	{
		if(symbol is IMethodSymbol { MethodKind: MethodKind.Constructor })
			
			return "medium";
		
		return symbol.DeclaredAccessibility == Accessibility.Private
			? "high"
			: "medium";
	}
	
	private static string GetReason(ISymbol symbol, bool hasFriendAssemblies)
	{
		if(symbol is IMethodSymbol { MethodKind: MethodKind.Constructor })
			
			return "Constructor has zero direct static references, but reflection or dependency injection may activate it.";
		
		if(symbol.DeclaredAccessibility == Accessibility.Private)
			
			return "Private symbol has zero direct static references in the loaded solution.";
		
		if(symbol.DeclaredAccessibility == Accessibility.Internal)
			return hasFriendAssemblies
				? "Internal symbol has zero direct static references in the loaded solution, but friend assemblies may use it."
				: "Internal symbol has zero direct static references in the loaded solution.";
		
		if(HasEffectivelyInternalContainingType(symbol))
			return "Public member is effectively internal because a containing type is private or internal, and it has zero direct static references in the loaded solution.";
		
		return "Symbol has zero direct static references in the loaded solution.";
	}
	
	private static bool HasEffectivelyInternalContainingType(ISymbol symbol)
	{
		for(var containingType = symbol.ContainingType; containingType is not null; containingType = containingType.ContainingType)
			if(containingType.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal)
				
				return true;
		
		return false;
	}
	
	private static string FormatName(ISymbol symbol)
	{
		if(symbol is INamedTypeSymbol type)
			
			return type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
		
		var containingType = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
		
		if(containingType is null)
			
			return symbol.Name;
		
		if(symbol is not IMethodSymbol method)
			
			return $"{containingType}.{symbol.Name}";
		
		var methodName = method.MethodKind == MethodKind.Constructor
			? method.ContainingType.Name
			: method.Name;
		var typeParameters = method.TypeParameters.Length == 0
			? string.Empty
			: $"<{string.Join(", ", method.TypeParameters.Select(p => p.Name))}>";
		var parameters = string.Join(", ", method.Parameters.Select(p =>
			p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
		));
		
		return $"{containingType}.{methodName}{typeParameters}({parameters})";
	}
	
	private static string GetKind(ISymbol symbol)
		=> symbol switch {
			
			INamedTypeSymbol type => type.TypeKind.ToString().ToLowerInvariant(),
			IMethodSymbol { MethodKind: MethodKind.Constructor } => "constructor",
			_ => symbol.Kind.ToString().ToLowerInvariant()
		};
	
	private string BuildCaution(string projectPath)
	{
		var adhocCaution = AdhocCaution(projectPath);
		
		return adhocCaution is null
			? StaticAnalysisCaution
			: $"{StaticAnalysisCaution} {adhocCaution}";
	}
	
	private sealed record UnusedCandidate(ISymbol Symbol, UnusedSymbolEntry Entry);
}
