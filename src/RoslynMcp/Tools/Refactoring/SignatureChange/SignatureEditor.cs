using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Tools.SignatureChange;

/// <summary>
///     Base class for signature editors. Each subclass handles a specific method kind
///     (regular, extern, operator, constructor, indexer). Phase 1 implements only
///     <see cref="AddParameterEditor"/>. Future editors slot in without changing the
///     orchestrator or tool class.
/// </summary>
internal abstract class SignatureEditor
{
	/// <summary>Can this editor handle the given method symbol?</summary>
	public abstract bool CanHandle(IMethodSymbol method);
	
	/// <summary>
	///     Validates that the requested change is safe for this method kind.
	///     Returns null if valid, or an error message if not.
	/// </summary>
	public abstract string? Validate(IMethodSymbol method, SignatureChangeRequest request);
	
	/// <summary>
	///     Produces a new solution with the signature change applied.
	///     Does not write to disk — the orchestrator handles that.
	/// </summary>
	public abstract Task<SignatureChangeResult> ApplyAsync(
		IMethodSymbol method,
		MethodDeclarationSyntax declaration,
		SignatureChangeRequest request,
		Solution solution,
		Compilation compilation,
		CancellationToken cancellationToken);
}

/// <summary>
///     Describes what changes to make to a method signature.
///     Extensible: Phase 2 adds RemoveParameters, ReorderParameters, BreakingMode.
/// </summary>
internal sealed record SignatureChangeRequest
{
	public NewParameter[] AddParameters    { get; init; } = [];
	// Phase 2:
	// public string[]?   RemoveParameters   { get; init; }
	// public Dictionary<int,int>? Reorder   { get; init; }
	// public bool         BreakingMode      { get; init; }
}

internal sealed record NewParameter(string Name, string Type, string? DefaultValue);
