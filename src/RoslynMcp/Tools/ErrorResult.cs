using System.Text.Json.Serialization;

namespace RoslynMcp.Tools;

/// <summary>Marker interface for all error-bearing result types. Enables type-safe error routing through <see cref="RoslynMcpTool.ToolScope"/>.</summary>
internal interface IToolError { }

/// <summary>
///     Abstract base record for all tool results — success and error alike.
///     <see cref="Error"/>, <see cref="Hint"/>, and <see cref="Caution"/> are <see langword="null"/> on
///     success results and omitted from serialized JSON when null.
/// </summary>
internal abstract record ToolResult
{
	/// <summary>Compacts sorted, distinct 1-based line numbers into inclusive ranges.</summary>
	protected static LineRange[] CompactLineRanges(IEnumerable<int> sortedDistinctLines)
	{
		using var enumerator = sortedDistinctLines.GetEnumerator();

		if(!enumerator.MoveNext())
			return [];

		var ranges   = new List<LineRange>();
		var start    = enumerator.Current;
		var previous = start;

		while(enumerator.MoveNext()) {

			var current = enumerator.Current;

			if(current == previous + 1) {

				previous = current;

				continue;
			}

			ranges.Add(new LineRange(start, previous));
			start    = current;
			previous = current;
		}

		ranges.Add(new LineRange(start, previous));

		return [..ranges];
	}

	[JsonPropertyName("error")]
	[JsonPropertyOrder(-10)]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Error { get; init; }
	
	[JsonPropertyName("hint")]
	[JsonPropertyOrder(-9)]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Hint { get; init; }
	
	[JsonPropertyName("_caution")]
	[JsonPropertyOrder(int.MaxValue)]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Caution { get; init; }
}

/// <summary>
///     Shared error response for all tools. Provides a consistent JSON shape
///     so agents can reliably detect and parse errors across any tool.
///     Serializes to: <c>{ "error": "...", "hint": "..." }</c>
/// </summary>
internal record ErrorResult : ToolResult, IToolError
{
	public ErrorResult() { }
	
	// Compat constructor: used by all existing call sites and by ToolScopeCodeFixProvider,
	// which synthesizes new ErrorResult(<arg>) via Roslyn SyntaxFactory.
	public ErrorResult(string error, string? hint = null)
	{
		Error = error;
		Hint  = hint;
	}
}

/// <summary>
///     A file that holds more than <see cref="FileReader"/> reads whole (#230). <c>error</c> is the
///     stable code <c>file_too_large</c>; the file name and limit are in <c>message</c>.
///     Serializes to: <c>{ "error": "file_too_large", "message": "...", "limit_bytes": N, "hint": "..." }</c>
/// </summary>
internal sealed record FileTooLargeResult : ErrorResult
{
	public FileTooLargeResult(FileTooLargeException ex)
		: base("file_too_large", $"Files over {FileReader.Describe(ex.Limit)} are not read whole; use a tool outside RoslynMcp for this one.")
	{
		Message    = ex.Message;
		LimitBytes = ex.Limit;
	}
	
	[JsonPropertyName("message")]
	[JsonPropertyOrder(-8)]
	public string Message { get; init; }
	
	[JsonPropertyName("limit_bytes")]
	[JsonPropertyOrder(-7)]
	public long LimitBytes { get; init; }
}

/// <summary>
///     One candidate in an <see cref="AmbiguousSymbolResult"/>. <see cref="ContainingType"/> and
///     <see cref="FilePath"/>+<see cref="Line"/> are the exact values an agent copies into the
///     retry call's disambiguation parameters.
/// </summary>
internal sealed record SymbolCandidate(
	[property: JsonPropertyName("symbol")]         string  Symbol,
	[property: JsonPropertyName("kind")]           string  Kind,
	[property: JsonPropertyName("containingType")] string? ContainingType,
	[property: JsonPropertyName("filePath")]       string? FilePath,
	[property: JsonPropertyName("line")]           int     Line
);

/// <summary>
///     Ambiguous-name failure carrying the candidate list in machine-usable form, so the calling
///     agent can self-recover by re-calling with a candidate's containingType or filePath+line
///     instead of the workflow stalling. Serializes to:
///     <c>{ "error": "...", "candidates": [ { symbol, kind, containingType, filePath, line } ], "hint": "..." }</c>
/// </summary>
internal sealed record AmbiguousSymbolResult : ErrorResult
{
	public AmbiguousSymbolResult(string error, SymbolCandidate[] candidates, string hint) : base(error, hint)
	{
		Candidates = candidates;
	}
	
	[JsonPropertyName("candidates")]
	[JsonPropertyOrder(-8)]
	public SymbolCandidate[] Candidates { get; init; }
}
