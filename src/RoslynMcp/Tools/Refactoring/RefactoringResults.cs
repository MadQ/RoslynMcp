using System.Text.Json.Serialization;

namespace RoslynMcp.Tools;

/// <summary>One refactoring an agent can preview: the index goes back into <c>roslyn_preview_refactoring</c>.</summary>
internal sealed record RefactoringActionChoice(int Index, string Title, string Provider, string Source, string? EquivalenceKey);

/// <summary>An action a provider offered that the headless host cannot run, and why.</summary>
internal sealed record UnavailableActionInfo(string Title, string Provider, string Reason);

/// <summary>A provider source as the tools report it: where it came from, what it yielded, what went wrong.</summary>
internal sealed record ProviderSourceInfo(
	string  Source,
	string  Kind,
	string? Origin,
	int     Refactorings,
	int     Fixes,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Problems)
{
	public static ProviderSourceInfo From(ProviderSourceReport report)
		=> new(report.Source, Describe(report.Kind), report.Origin, report.Refactorings, report.Fixes, report.Problems.IsEmpty ? null : [..report.Problems]);
	
	static string Describe(ProviderSourceKind kind) => kind switch {
		
		ProviderSourceKind.BuiltIn       => "built-in",
		ProviderSourceKind.Bundled       => "bundled",
		ProviderSourceKind.Project       => "project",
		ProviderSourceKind.GlobalConfig  => "global-config",
		ProviderSourceKind.ProjectConfig => "project-config",
		_                                => kind.ToString(),
	};
}

internal sealed record ProviderFailureInfo(string Provider, string Error);

internal sealed record ListRefactoringsResult(
	string File,
	int    Line,
	int    Column,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EndLine,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EndColumn,
	RefactoringActionChoice[] Actions,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UnavailableActionInfo[]? Unavailable,
	ProviderSourceInfo[] ProviderSources,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProviderFailureInfo[]? ProviderFailures
) : ToolResult;

internal sealed record PreviewRefactoringResult : ToolResult, IToolError
{
	public PreviewRefactoringResult(string? token, string? diff, RefactoringActionChoice? action, string message, string? error)
	{
		Token   = token;
		Diff    = diff;
		Action  = action;
		Message = message;
		Error   = error;
	}
	
	public string?                  Token   { get; }
	public string?                  Diff    { get; }
	public RefactoringActionChoice? Action  { get; }
	public string                   Message { get; }
	
	/// <summary>The choices at the location, on an invalid index.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public RefactoringActionChoice[]? Actions { get; init; }
	
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string[]? CreatedFiles { get; init; }
	
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string[]? DeletedFiles { get; init; }
}
