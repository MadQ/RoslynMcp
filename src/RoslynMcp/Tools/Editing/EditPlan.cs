using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.Tools;

/// <summary>How an edit that writes nothing ends: which scope terminal its answer goes through.</summary>
internal enum EditStopKind
{
	/// <summary>A failure with a reason of its own for the log (<c>scope.Failed</c>).</summary>
	Failed,
	/// <summary>A structured error (<c>scope.Error</c>).</summary>
	Error,
	/// <summary>Not a failure: the edit was not made and the answer says why (<c>scope.Outcome</c>), as when several places match.</summary>
	NotApplied,
}

/// <summary>An edit that ends without a write: the answer to give, and the detail for the log.</summary>
internal sealed record EditStop(EditStopKind Kind, string Detail, ToolResult Answer);

/// <summary>
///     What an edit tool's core found out about one edit, before anything is written (#475): why
///     it cannot be made (<see cref="Stop"/>), or the changes it makes to the text of the file
///     <em>as it was read</em>.
///     <para>
///         Each edit tool is a core that turns a text and its arguments into such a plan, and a
///         wrapper that reads, writes and backs up. <c>roslyn_multi_edit</c> runs the same cores
///         over one read of each file and applies all the changes at once, from the bottom of
///         the file up, so that no edit moves the place another one names. That is the whole
///         trick, and it is why a plan speaks in spans of the original text and not in a new
///         text: a new text can only be the base of the next edit, spans can be put together.
///     </para>
/// </summary>
internal abstract record EditPlan
{
	/// <summary>Set when the edit ends here and nothing is to be written.</summary>
	public EditStop? Stop { get; init; }
	
	/// <summary>The changes, as spans of the original text. Empty when <see cref="Stop"/> is set.</summary>
	public TextChange[] Changes { get; init; } = [];
	
	/// <summary>What the edit does, in a few words, for the log and for a batch's answer.</summary>
	public string Detail { get; init; } = "";
	
	/// <summary>What the answer must warn of although the edit is made.</summary>
	public string? Caution { get; init; }
	
	/// <summary>Annotations for the log entry (<c>scope.Record</c>).</summary>
	public List<string> Notes { get; init; } = [];
	
	/// <summary>
	///     The text with <paramref name="changes"/> made. The spans are those of
	///     <paramref name="original"/> and must not overlap (<see cref="FirstOverlap"/>); they are
	///     applied in the order of their position, and changes at one position in the order given.
	/// </summary>
	public static string Apply(string original, IEnumerable<TextChange> changes)
	{
		var builder  = new System.Text.StringBuilder(original.Length);
		var position = 0;
		
		// An insertion at the start of a replaced span goes before it, or the span's end would
		// already be behind. The sort is stable: two insertions at one position keep the order
		// they were given in.
		foreach(var change in changes.OrderBy(change => change.Span.Start).ThenBy(change => change.Span.Length)) {
			
			builder.Append(original, position, change.Span.Start - position);
			builder.Append(change.NewText);
			position = change.Span.End;
		}
		
		builder.Append(original, position, original.Length - position);
		
		return builder.ToString();
	}
	
	/// <summary>
	///     The one change that turns <paramref name="original"/> into <paramref name="changed"/>:
	///     what is left between their common start and their common end. For an edit that is made
	///     on a syntax tree, whose result is a whole new text.
	/// </summary>
	public static TextChange Between(string original, string changed)
	{
		var limit  = Math.Min(original.Length, changed.Length);
		var prefix = 0;
		
		while(prefix < limit && original[prefix] == changed[prefix])
			prefix++;
		
		var suffix = 0;
		
		while(suffix < limit - prefix && original[^(suffix + 1)] == changed[^(suffix + 1)])
			suffix++;
		
		return new TextChange(TextSpan.FromBounds(prefix, original.Length - suffix), changed[prefix..(changed.Length - suffix)]);
	}
	
	/// <summary>
	///     The first two of <paramref name="changes"/> that touch the same text, by their index in
	///     the list, or null. Two changes overlap when their spans share a character, and an
	///     insertion when it falls inside another change's span; an insertion at the edge of a
	///     span, or two at one position, do not.
	/// </summary>
	public static (int First, int Second)? FirstOverlap(IReadOnlyList<TextChange> changes)
	{
		var order = Enumerable.Range(0, changes.Count)
			.OrderBy(index => changes[index].Span.Start)
			.ThenBy(index => changes[index].Span.Length)
		;
		
		// Against every earlier change that still reaches this far, not only the one before: a
		// long span can hold several short ones.
		var reach      = -1;
		var reachIndex = -1;
		
		foreach(var index in order) {
			
			var span = changes[index].Span;
			
			if(span.Start < reach)
				
				return (Math.Min(reachIndex, index), Math.Max(reachIndex, index));
			
			if(span.End > reach) {
				
				reach      = span.End;
				reachIndex = index;
			}
		}
		
		return null;
	}
}
