using System.Text.Json.Serialization;

namespace RoslynMcp.Tools;

/// <summary>
///     One refactoring an agent can preview: the index goes back into <c>roslyn_preview_refactoring</c>.
///     <see cref="Provider"/> is the provider's type name and <see cref="ExportName"/> its export name,
///     when it has one. A <c>deny</c> entry names it by either, or by the stem of the type name
///     (<c>InvertIf</c> for <c>CSharpInvertIfCodeRefactoringProvider</c>).
/// </summary>
internal sealed record RefactoringActionChoice(
	int     Index,
	string  Title,
	string  Provider,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExportName,
	string  Source,
	string? EquivalenceKey);

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
		=> new(report.Source, KindName(report.Kind), report.Origin, report.Refactorings, report.Fixes, report.Problems.IsEmpty ? null : [..report.Problems]);
	
	internal static string KindName(ProviderSourceKind kind) => kind switch {
		
		ProviderSourceKind.BuiltIn       => "built-in",
		ProviderSourceKind.Bundled       => "bundled",
		ProviderSourceKind.Project       => "project",
		ProviderSourceKind.GlobalConfig  => "global-config",
		ProviderSourceKind.ProjectConfig => "project-config",
		_                                => kind.ToString(),
	};
}

/// <summary>
///     The provider sources behind one list of refactorings, at the size an answer can carry on every
///     call: how many sources there were and what they yielded together, and in full only the sources
///     that have something to say. The whole report went out with every list at first, some 1,300
///     tokens on a project with seventeen sources where the actions themselves took ninety each, and
///     it was the same report call after call (#414). What must travel with the answer is why a
///     provider that was expected is not offered — a source that failed, or one this workspace mode
///     cannot have — so those keep their text; a source that loaded says nothing an agent acts on,
///     and <c>roslyn_info</c> lists them all.
/// </summary>
internal sealed record ProviderSummaryInfo(
	int Sources,
	int Refactorings,
	int Fixes,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProviderProblemInfo[]? Problems)
{
	public static ProviderSummaryInfo From(IReadOnlyCollection<ProviderSourceReport> reports)
	{
		var problems = reports
			.Where(report => !report.Problems.IsEmpty)
			.Select(report => new ProviderProblemInfo(report.Source, ProviderSourceInfo.KindName(report.Kind), report.Refactorings, report.Fixes, [..report.Problems]))
			.ToArray()
		;
		
		return new(reports.Count, reports.Sum(report => report.Refactorings), reports.Sum(report => report.Fixes), problems.Length > 0 ? problems : null);
	}
}

/// <summary>A provider source with a problem: what it still yielded, and what went wrong. No origin path — <c>roslyn_info</c> has it.</summary>
internal sealed record ProviderProblemInfo(string Source, string Kind, int Refactorings, int Fixes, string[] Problems);

internal sealed record ProviderFailureInfo(string Provider, string Error);

internal sealed record ListRefactoringsResult(
	string File,
	int    Line,
	int    Column,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EndLine,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EndColumn,
	RefactoringActionChoice[] Actions,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UnavailableActionInfo[]? Unavailable,
	ProviderSummaryInfo Providers,
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
