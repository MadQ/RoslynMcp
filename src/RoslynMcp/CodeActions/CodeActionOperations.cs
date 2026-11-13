using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;

namespace RoslynMcp;

/// <summary>
///     Turns a code action into the solution it proposes. The server applies documents and nothing
///     else, so the action must come down to one <see cref="ApplyChangesOperation"/>; Roslyn's
///     editor-only operations (open a document, navigate to a position, note which wrapping style was
///     picked) are ignored, anything else is
///     refused with a structured error rather than silently dropped.
/// </summary>
internal static class CodeActionOperations
{
	public static async Task<Solution> GetChangedSolutionAsync(CodeAction action, CancellationToken cancellationToken)
	{
		var operations = await action.GetOperationsAsync(cancellationToken)
			.ConfigureAwait(false)
		;
		
		ApplyChangesOperation? applyChanges = null;
		var duplicate   = false;
		var unsupported = false;
		
		foreach(var operation in operations) {
			
			if(operation is ApplyChangesOperation changes) {
				
				duplicate    |= applyChanges is not null;
				applyChanges ??= changes;
				
				continue;
			}
			
			unsupported |= !IsEditorOnly(operation);
		}
		
		if(applyChanges is null || duplicate || unsupported) {
			
			var returnedShape = operations.Length == 0
				? "no operations"
				: string.Join(", ", operations.Select(operation => operation.GetType().Name))
			;
			
			throw new UnsupportedCodeActionOperationException(
				$"The code action returned an unsupported operation shape: {returnedShape}. " +
				"Exactly one ApplyChangesOperation is required; Roslyn's editor-only operations (navigation, usage tracking) are ignored.");
		}
		
		return applyChanges.ChangedSolution;
	}
	
	// Roslyn's operations that mean something to an editor only. Opening or navigating to a document
	// means nothing to a headless host. Neither does the operation every wrapping action adds to its
	// change (WrapItemsAction.RecordCodeActionOperation, #438): its Apply moves the action's title to
	// the front of a static most-recently-used list, by which an editor orders the wrapping actions
	// next time, and touches no workspace. Without it none of Roslyn's wrapping refactorings could be
	// previewed. The type is private to Roslyn, hence the name; the namespace keeps another
	// provider's operation of the same name refused, as every unknown operation is.
	static bool IsEditorOnly(CodeActionOperation operation)
		=> operation is OpenDocumentOperation
			|| operation.GetType().Name.EndsWith("NavigationOperation", StringComparison.Ordinal)
			|| operation.GetType() is { Name: "RecordCodeActionOperation", Namespace: "Microsoft.CodeAnalysis.Wrapping" }
	;
}

internal sealed class UnsupportedCodeActionOperationException(string message) : Exception(message);
