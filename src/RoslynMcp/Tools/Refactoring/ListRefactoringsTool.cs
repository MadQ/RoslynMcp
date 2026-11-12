using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ListRefactoringsTool : RefactoringToolBase
{
	public ListRefactoringsTool(WorkspaceResolver workspace, CodeActionHost codeActionHost, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, codeActionHost, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_list_refactorings", ReadOnly = true, Title = "List Refactorings", OpenWorld = false, Idempotent = true)]
	[Description(
		"Lists the code refactorings Roslyn offers at a caret or selection in a C# file — Move type to file, Extract method, " +
		"Introduce local, Inline temporary, Convert to interpolated string, Invert if, Use expression body and the rest of what " +
		"Visual Studio shows under Ctrl+. — from Roslyn's own providers, the project's analyzer packages and any providers the " +
		"server is configured with. Each action has an index for roslyn_preview_refactoring, its title, provider and source. " +
		"An empty list means nothing applies at that location, which is a normal answer, not an error. Actions a headless host " +
		"cannot run (ones that need a dialog) are listed under unavailable with the reason; a provider that failed is listed " +
		"under providerFailures and the others still answer. providers is about the provider sources behind the list, not about " +
		"the actions: how many sources there were and how many refactoring and fix providers they loaded (per source, before " +
		"duplicates and denied providers are dropped), and under problems only the sources with a problem — one that failed to " +
		"load, or one this workspace cannot have — each with its first problem and a count of further ones. Look there when an " +
		"expected refactoring is missing; roslyn_info has every source the server has loaded and kept, for all projects, with all " +
		"problems (a source whose load failed only for the moment is named here and nowhere else). " +
		"Step 1 of the three-step workflow: list, roslyn_preview_refactoring, " +
		"roslyn_apply_refactoring. For a fix to a specific diagnostic use roslyn_preview_code_fix instead.")]
	public async Task<object> ListRefactorings(
		[Description("Relative or absolute path to the C# file.")] string filePath,
		[Description("1-based line of the caret, or of the start of the selection.")] int line,
		[Description("1-based column of the caret, or of the start of the selection. Put the caret on the identifier or keyword the refactoring is about.")] int column,
		CancellationToken cancellationToken,
		[Description(OptionalProjectPathDescription)] string? projectPath = null,
		[Description("1-based line where the selection ends. Omit for a caret. Extract method and the other selection-based refactorings need one.")] int? endLine = null,
		[Description("1-based column where the selection ends. With endLine but no endColumn, the selection runs to the end of endLine.")] int? endColumn = null)
	{
		using var scope = BeginTool("roslyn_list_refactorings", filePath, new { line, column, endLine, endColumn });
		
		if(!TryResolveProjectArg(projectPath, filePath, out projectPath, out var projectError))
			
			return scope.Error(projectError);
		
		var (target, error) = await LocateAsync(projectPath, filePath, line, column, endLine, endColumn, cancellationToken);
		
		if(target is null)
			return scope.Error(error!);
		
		var query = await codeActionHost.GetRefactoringsAsync(target.Document, target.Span, cancellationToken);
		
		if(!query.Settings.Enabled)
			return scope.Error(ProvidersDisabled());
		
		var result = new ListRefactoringsResult(
			TryMakeRelative(target.FullPath, target.RootPath) ?? target.FullPath,
			line,
			column,
			endLine,
			endColumn,
			[..query.Actions.Select(Choice)],
			query.Unavailable.IsEmpty ? null : [..query.Unavailable.Select(action => new UnavailableActionInfo(action.Title, action.Provider, action.Reason))],
			ProviderSummaryInfo.From(query.Sources),
			query.Failures.IsEmpty ? null : [..query.Failures.Select(failure => new ProviderFailureInfo(failure.Provider, failure.Error))])
		{
			Hint    = query.Actions.IsEmpty ? LocationHint : null,
			Caution = AdhocCaution(projectPath),
		};
		
		// In the log, not in the answer: the refactorings the server keeps back because they need a
		// dialog, where one would have applied here. It is the only place their demand shows (#449).
		if(!query.DeniedWithActions.IsEmpty)
			scope.Record($"denied with actions: {string.Join(", ", query.DeniedWithActions)}");
		
		return scope.Outcome($"{query.Actions.Length} action(s), {query.Unavailable.Length} unavailable, {query.Failures.Length} provider failure(s)", result);
	}
}
