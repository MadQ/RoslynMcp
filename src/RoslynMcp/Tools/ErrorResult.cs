namespace RoslynMcp.Tools;

/// <summary>
///     Shared error response for all tools. Provides a consistent JSON shape
///     so agents can reliably detect and parse errors across any tool.
///     Serializes to: <c>{ "error": "...", "hint": "..." }</c>
/// </summary>
internal sealed record ErrorResult(string Error, string? Hint = null);
