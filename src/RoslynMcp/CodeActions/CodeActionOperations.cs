using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;

namespace RoslynMcp;

/// <summary>
///     Turns a code action into the solution it proposes. The server applies documents and nothing
///     else, so the action must come down to one <see cref="ApplyChangesOperation"/>; Roslyn's
///     editor-only operations (open a document, navigate to a position) are ignored, anything else is
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
			
			unsupported |= !IsNavigation(operation);
		}
		
		if(applyChanges is null || duplicate || unsupported) {
			
			var returnedShape = operations.Length == 0
				? "no operations"
				: string.Join(", ", operations.Select(operation => operation.GetType().Name))
			;
			
			throw new UnsupportedCodeActionOperationException(
				$"The code action returned an unsupported operation shape: {returnedShape}. " +
				"Exactly one ApplyChangesOperation is required; navigation operations are ignored.");
		}
		
		return applyChanges.ChangedSolution;
	}
	
	// Roslyn's editor operations: opening or navigating to a document means nothing to a headless host.
	static bool IsNavigation(CodeActionOperation operation)
		=> operation is OpenDocumentOperation
			|| operation.GetType().Name.EndsWith("NavigationOperation", StringComparison.Ordinal);
}

internal sealed class UnsupportedCodeActionOperationException(string message) : Exception(message);
