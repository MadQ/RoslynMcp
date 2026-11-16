using Microsoft.CodeAnalysis;

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
}
