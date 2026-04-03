namespace RoslynMcp.Tools;

/// <summary>
///     Abstract base for all pure-error result types.
///     Provides a generic constraint on <see cref="ToolScope.Error{T}(T)"/> so the compiler
///     enforces that only error-bearing types reach the error path.
/// </summary>
internal abstract record ToolErrorResult
{
	public abstract string? Error { get; init; }
}

/// <summary>
///     Shared error response for all tools. Provides a consistent JSON shape
///     so agents can reliably detect and parse errors across any tool.
///     Serializes to: <c>{ "error": "...", "hint": "..." }</c>
/// </summary>
internal sealed record ErrorResult(string Error, string? Hint = null) : ToolErrorResult;
