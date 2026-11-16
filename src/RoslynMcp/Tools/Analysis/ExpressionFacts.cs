using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RoslynMcp.Tools;

/// <summary>
///     The rules two tools state a compiler fact by: <c>roslyn_get_symbol_info</c> for one position
///     and <c>roslyn_annotate_source</c> for a member (#355). Shared, so that the two never give an
///     agent two answers to one question.
/// </summary>
internal static class ExpressionFacts
{
	// The nullable marker is the whole point of these strings: 'string?' and 'string' differ here.
	internal static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.MinimallyQualifiedFormat.WithMiscellaneousOptions(
		SymbolDisplayFormat.MinimallyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
	);
	
	// A flow state on an int or a void call is true and useless. A pointer is no value type either,
	// but nullable reference analysis does not apply to it.
	internal static bool CanBeNull(ITypeSymbol? type)
		=> type is not (null or IPointerTypeSymbol or IFunctionPointerTypeSymbol) && (!type.IsValueType || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
	;
	
	// An error type means the code does not bind, and nothing said about it is a fact.
	internal static ITypeSymbol? Bound(ITypeSymbol? type) => type is { TypeKind: not TypeKind.Error } ? type : null;
	
	// A type that can hold a null reference, as flow analysis has it at a point: nullable where
	// the value may be null, not nullable where it cannot, and unmarked where nullable analysis
	// is off. An unconstrained type parameter is neither a reference nor a value type and counts.
	internal static ITypeSymbol? Flowed(ITypeSymbol? type, NullableFlowState flow)
		=> type is { IsReferenceType: true } or ITypeParameterSymbol { IsValueType: false }
			? type.WithNullableAnnotation(flow switch {
				
				NullableFlowState.MaybeNull => NullableAnnotation.Annotated,
				NullableFlowState.NotNull   => NullableAnnotation.NotAnnotated,
				_                           => NullableAnnotation.None
			})
			: type
	;
	
	/// <summary>
	///     The type of a local as an agent should read it. The declared type of an implicitly typed
	///     local of a reference type is always nullable, whatever it is given, so for those the
	///     type is taken from where the value comes from: what a <c>var</c>'s initializer yields at
	///     that point, the element type of a <c>foreach</c>, and for a designation what
	///     <see cref="DesignatedType"/> finds. A local with a written type, or one declared in
	///     another tree than the model's, keeps its declared type.
	/// </summary>
	internal static ITypeSymbol LocalType(SemanticModel model, ILocalSymbol local, CancellationToken cancellationToken)
	{
		if(local.DeclaringSyntaxReferences is not [var reference] || reference.SyntaxTree != model.SyntaxTree)
			
			return local.Type;
		
		var implied = reference.GetSyntax(cancellationToken) switch {
			
			VariableDeclaratorSyntax { Initializer.Value: { } value, Parent: VariableDeclarationSyntax { Type.IsVar: true } }
				=> Flowed(local.Type, model.GetTypeInfo(value, cancellationToken).Nullability.FlowState),
			ForEachStatementSyntax { Type.IsVar: true } loop
				=> model.GetForEachStatementInfo(loop).ElementType,
			SingleVariableDesignationSyntax designation when IsImplicitlyTyped(designation)
				=> DesignatedType(model, designation, local, cancellationToken),
			_   => null
		};
		
		return implied ?? local.Type;
	}
	
	// `out var x`, `var (a, b) = …` and the pattern `var x`: the designation has no type written.
	internal static bool IsImplicitlyTyped(SingleVariableDesignationSyntax designation)
	{
		SyntaxNode? parent = designation.Parent;
		
		while(parent is ParenthesizedVariableDesignationSyntax)
			parent = parent.Parent;
		
		return parent is DeclarationExpressionSyntax { Type.IsVar: true } or VarPatternSyntax;
	}
	
	// What a designation is given. `out var x` takes the parameter's type, nullable too when the
	// method says the value may be null on some outcome (TryGetValue's [MaybeNullWhen(false)]).
	// The pattern `e is var x` takes what e yields at that point. A deconstruction has no one
	// place to read from and keeps its declared type, which for a reference is always nullable:
	// that may say too much, never too little.
	internal static ITypeSymbol? DesignatedType(SemanticModel model, SingleVariableDesignationSyntax designation, ILocalSymbol local, CancellationToken cancellationToken)
	{
		if(designation.Parent is DeclarationExpressionSyntax { Parent: ArgumentSyntax argument }
			&& model.GetOperation(argument, cancellationToken) is IArgumentOperation { Parameter: { } parameter })
			
			return parameter.GetAttributes().Any(attribute => attribute.AttributeClass?.Name is "MaybeNullWhenAttribute" or "MaybeNullAttribute")
				? Flowed(parameter.Type, NullableFlowState.MaybeNull)
				: parameter.Type;
		
		if(designation.Parent is VarPatternSyntax { Parent: IsPatternExpressionSyntax tested })
			
			return Flowed(local.Type, model.GetTypeInfo(tested.Expression, cancellationToken).Nullability.FlowState);
		
		return local.Type;
	}
}
