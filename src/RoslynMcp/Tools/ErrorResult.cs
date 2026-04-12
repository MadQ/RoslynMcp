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
