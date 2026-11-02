using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class SymbolInfoTool : RoslynMcpTool
{
	// The nullable marker is the whole point of these strings: 'string?' and 'string' differ here.
	static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.MinimallyQualifiedFormat.WithMiscellaneousOptions(
		SymbolDisplayFormat.MinimallyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
	);
	
	public SymbolInfoTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_get_symbol_info", ReadOnly = true, Title = "Get Symbol Info", OpenWorld = false, Idempotent = true)]
	[Description(
		"Returns what the compiler knows at a specific location in a file: the resolved symbol (kind, name, containing type, " +
		"return type) plus the facts text cannot show about the enclosing expression. " +
		"Use this when you have a line and column and need to identify exactly what is there, disambiguate an overloaded name, " +
		"or check nullability or a conversion instead of guessing. Works on declarations as well as references. " +
		"A position on a method name describes the call: 'type' is what the invocation returns. A delegate in call position " +
		"describes the delegate. The last member of a ?. chain describes the whole conditional access: 's?.Trim()' is 'string?'. " +
		"An earlier link keeps its own type: in 's?.Trim().Length', Trim is a NotNull 'string'. " +
		"'type' is flow-adjusted, so 'string? s' reads 'string' after a null check. " +
		"nullable_flow is NotNull or MaybeNull, per the compiler's flow analysis at this point; it applies to references and " +
		"nullable values. nullable_context is 'disabled' where nullable analysis is off, so a missing nullable_flow there does not " +
		"mean not-null. converted_type and conversion appear when an implicit conversion applies, and constant_value when the " +
		"expression is a compile-time constant. " +
		"On failed overload resolution, candidates and candidate_reason list what the compiler considered. The compiler does not " +
		"say why a successful overload won; set includeOverloads=true to see the others on a method name in code. " +
		"Fields that do not apply are omitted. " +
		"For looking up a symbol by name without a position, use roslyn_get_symbol_definition instead.")]
	public async Task<object> GetSymbolInfo(
		[Description("Relative file path, e.g. 'Core/WindowTracker.cs'.")] string filePath,
		[Description("1-based line number.")] int line,
		[Description("1-based column number.")] int column,
		[Description(OptionalProjectPathDescription)] string? projectPath = null,
		[Description("Also list every overload in the method group, on a method name at a call or used as a value. Can be long, e.g. for Console.WriteLine. At a declaration, use roslyn_find_overloads instead. Default: false.")] bool includeOverloads = false)
	{
		using var scope = BeginTool("roslyn_get_symbol_info", $"{filePath}:{line}", new { column, includeOverloads });
		
		if(!TryResolveProjectArg(projectPath, filePath, out projectPath, out var projectError))
			
			return scope.Error(projectError);
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return scope.Error(error!);
		
		var tree = FindSyntaxTree(compilation, filePath);
		
		if(tree is null)
			
			return scope.Failed("file not found", new ErrorResult($"File '{filePath}' not found in the compilation."));
		
		var text     = await tree.GetTextAsync();
		var position = GetPosition(text, line, column);
		
		if(position < 0)
			
			return scope.Error(new ErrorResult($"Line {line}, column {column} is out of range."));
		
		var model = compilation.GetSemanticModel(tree);
		var token = (await tree.GetRootAsync()).FindToken(position);
		var node  = token.Parent;
		
		if(node is null)
			
			return scope.Error(new ErrorResult("No node at that position."));
		
		var info   = model.GetSymbolInfo(node);
		var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
		
		// GetSymbolInfo answers for references only. Limited to the declared name, because a brace or
		// keyword of a declaration has the same parent.
		var isDeclaration = symbol is null && token.IsKind(SyntaxKind.IdentifierToken);
		
		if(isDeclaration)
			symbol = model.GetDeclaredSymbol(node);
		
		var (expression, callee) = EnclosingExpression(node, symbol);
		var typeInfo             = model.GetTypeInfo(expression ?? node);
		
		// The null literal has no type of its own, only the one it converts to.
		var anyType = typeInfo.Type ?? typeInfo.ConvertedType;
		
		if(symbol is null && anyType is null)
			
			return scope.Error(new ErrorResult("No symbol resolved at that position."));
		
		var result = symbol is null
			? new SymbolInfoResult("type", anyType!.ToDisplayString(), null, null)
			: new SymbolInfoResult(
				symbol.Kind.ToString().ToLowerInvariant(),
				symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
				symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
				symbol switch {
					
					IMethodSymbol   m => m.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
					IPropertySymbol p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
					IFieldSymbol    f => f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
					ILocalSymbol    l => l.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
					_                 => null
				})
		;
		
		var enriched = expression is not null
			? AddExpressionFacts(result, model, expression, typeInfo, symbol)
			: result with { Type = isDeclaration ? DeclaredType(symbol) : null }
		;
		
		return scope.Outcome(symbol?.Name ?? "type", enriched with {
			
			Candidates      = info.CandidateReason == CandidateReason.None ? null : Signatures(info.CandidateSymbols),
			CandidateReason = info.CandidateReason == CandidateReason.None ? null : info.CandidateReason.ToString(),
			Overloads       = includeOverloads && callee is not null ? Signatures(model.GetMemberGroup(callee)) : null,
			Caution         = AdhocCaution(projectPath)
		});
	}
	
	/// <summary>
	///     Climbs from the node under the position to the expression an agent means by it. A method
	///     name has no type of its own, so 'a.Bar' in 'a.Bar(x)' resolves to the whole call, and the
	///     value of 's?.Trim()' is the conditional access: the call inside it never sees a null, so
	///     stopping there reports NotNull for something that can be null. An earlier link of a chain
	///     stays itself: it is not what the expression yields, and in 's?.Trim().Length' the whole
	///     has a different type altogether. A delegate in call position stays itself too, because
	///     whether it can be null is the question. Also returns the name's
	///     expression, which is where <c>GetMemberGroup</c> answers; the invocation gets nothing back.
	/// </summary>
	static (ExpressionSyntax? Expression, ExpressionSyntax? Callee) EnclosingExpression(SyntaxNode node, ISymbol? symbol)
	{
		if(node is not ExpressionSyntax current)
			
			return (null, null);
		
		if(current.Parent is MemberAccessExpressionSyntax access && access.Name == current)
			current = access;
		
		else if(current.Parent is MemberBindingExpressionSyntax binding && binding.Name == current)
			current = binding;
		
		var callee = current;
		
		if(symbol is IMethodSymbol && current.Parent is InvocationExpressionSyntax invocation && invocation.Expression == current)
			current = invocation;
		
		while(current.Parent is ConditionalAccessExpressionSyntax conditional && conditional.WhenNotNull == current)
			current = conditional;
		
		return (current, callee);
	}
	
	static SymbolInfoResult AddExpressionFacts(SymbolInfoResult result, SemanticModel model, ExpressionSyntax expression, TypeInfo typeInfo, ISymbol? symbol)
	{
		// An error type means the code does not bind, and '?' is not a fact about it.
		var type       = typeInfo.Type is { TypeKind: not TypeKind.Error } ? typeInfo.Type : null;
		var converted  = typeInfo.ConvertedType is { TypeKind: not TypeKind.Error } ? typeInfo.ConvertedType : null;
		var conversion = model.GetConversion(expression);
		var constant   = model.GetConstantValue(expression);
		
		// A type name is not a value, so it has no null state, and Roslyn gives it NotNull or None
		// depending on where it stands. None would read as nullable analysis being off.
		var nullable   = symbol is not ITypeSymbol && CanBeNull(type ?? converted);
		var flow       = typeInfo.Nullability.FlowState;
		
		return result with {
			
			Type            = type?.ToDisplayString(TypeFormat),
			ConvertedType   = SymbolEqualityComparer.IncludeNullability.Equals(type, converted) ? null : converted?.ToDisplayString(TypeFormat),
			NullableFlow    = nullable && flow != NullableFlowState.None ? flow.ToString() : null,
			NullableContext = nullable && flow == NullableFlowState.None ? "disabled" : null,
			Conversion      = conversion.Exists && !conversion.IsIdentity ? conversion.ToString() : null,
			ConstantValue   = constant.HasValue ? SymbolDisplay.FormatPrimitive(constant.Value!, quoteStrings: true, useHexadecimalNumbers: false) : null
		};
	}
	
	// A flow state on an int or a void call is true and useless. A pointer is no value type either,
	// but nullable reference analysis does not apply to it.
	static bool CanBeNull(ITypeSymbol? type)
		=> type is not (null or IPointerTypeSymbol or IFunctionPointerTypeSymbol) && (!type.IsValueType || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
	;
	
	static string? DeclaredType(ISymbol? symbol) => symbol switch {
		
		ILocalSymbol     l => l.Type.ToDisplayString(TypeFormat),
		IParameterSymbol p => p.Type.ToDisplayString(TypeFormat),
		IFieldSymbol     f => f.Type.ToDisplayString(TypeFormat),
		IPropertySymbol  p => p.Type.ToDisplayString(TypeFormat),
		IEventSymbol     e => e.Type.ToDisplayString(TypeFormat),
		_                  => null
	};
	
	static string[]? Signatures(IEnumerable<ISymbol> symbols)
	{
		// The full form, because ref and out are exactly what can tell two overloads apart.
		var signatures = symbols
			.Select(symbol => symbol is IMethodSymbol method ? SymbolFormatter.FormatFullMethodSignature(method) : SymbolFormatter.FormatSignature(symbol))
			.ToArray()
		;
		
		return signatures.Length == 0 ? null : signatures;
	}
}
