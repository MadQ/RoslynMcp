using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Tools.SignatureChange;

/// <summary>
///     Coordinates signature changes: validates the request, selects the appropriate
///     <see cref="SignatureEditor"/>, and delegates the transformation.
///     Decoupled from MCP protocol — the tool class handles tokens, diffs, and approval.
/// </summary>
internal sealed class SignatureChangePlanner
{
	// Editors are tried in order — first one that CanHandle wins.
	// Phase 2 adds RemoveParameterEditor, BreakingModeEditor, etc.
	static readonly SignatureEditor[] Editors = [
		new AddParameterEditor()
	];
	
	public async Task<SignatureChangeResult> PrepareAsync(
		IMethodSymbol method,
		SignatureChangeRequest request,
		Solution solution,
		Compilation compilation,
		CancellationToken cancellationToken)
	{
		// Find an editor that can handle this method kind.
		var editor = Editors.FirstOrDefault(e => e.CanHandle(method))
		;
		
		if(editor is null)
			
			return SignatureChangeResult.Failed(solution,$"No editor available for {method.MethodKind} method '{method.Name}'.");
		
		// Validate the request against the method.
		var validationError = editor.Validate(method, request)
		;
		
		if(validationError is not null)
			
			return SignatureChangeResult.Failed(solution, validationError);
		
		// Find the declaration syntax.
		// For partial methods, DeclaringSyntaxReferences may have multiple entries (one per partial part).
		// We take the first — this is intentional: the forwarding overload will be inserted alongside
		// the first declared part. Partial methods are rarely targets for signature extension, and taking
		// the first partial part is a reasonable default that avoids ambiguity.
		var declRef = method.DeclaringSyntaxReferences.FirstOrDefault()
		;
		
		if(declRef is null)
			
			return SignatureChangeResult.Failed(solution, "Method is defined in metadata, not source.");
		
		var declaration = await declRef.GetSyntaxAsync(cancellationToken) as MethodDeclarationSyntax;
		
		if(declaration is null)
			
			return SignatureChangeResult.Failed(solution, "Symbol resolves to a non-method syntax node.");
		
		// Delegate to the editor.
		
		return await editor.ApplyAsync(method, declaration, request, solution, compilation, cancellationToken);
	}
}
