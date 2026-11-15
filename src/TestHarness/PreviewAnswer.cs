/// <summary>
///     The answer of a preview tool as the harness reads it (#491): a preview that got a token is
///     one line of JSON and then the diff as raw text, in a block of its own.
/// </summary>
static class PreviewAnswer
{
	/// <summary>
	///     The diff of an answer whose blocks were joined into <paramref name="wholeAnswer"/>: what
	///     follows the header's line. Empty when the answer is the header alone, as it is for a
	///     preview without a token.
	/// </summary>
	internal static string Diff(string wholeAnswer)
	{
		var headerEnd = wholeAnswer.IndexOf('\n');
		
		return headerEnd < 0 ? string.Empty : wholeAnswer[(headerEnd + 1)..];
	}
}
