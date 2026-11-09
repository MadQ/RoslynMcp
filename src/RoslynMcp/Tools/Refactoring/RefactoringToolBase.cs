using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.Tools;

/// <summary>
///     Shared by <c>roslyn_list_refactorings</c> and <c>roslyn_preview_refactoring</c> (#389): the
///     document and span a request names, resolved the same way in both, so the index the list hands
///     out means the same action to the preview. Not a tool itself; the two deriving tools are sealed.
/// </summary>
internal abstract class RefactoringToolBase : RoslynMcpTool
{
	protected readonly CodeActionHost codeActionHost;
	
	protected RefactoringToolBase(WorkspaceResolver workspace, CodeActionHost codeActionHost, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, logger, paginationCache)
	{
		this.codeActionHost = codeActionHost;
	}
	
	protected const string LocationHint =
		"A caret (line and column) is enough for most refactorings: put it on the identifier or keyword the refactoring is about " +
		"(Move type to file: the type name; Invert if: the if keyword). Extract method, Introduce local and the like need a selection: " +
		"add endLine and endColumn.";
	
	protected sealed record RefactoringTarget(
		string           ProjectPath,
		Project          Project,
		Document         Document,
		string           FullPath,
		string           RootPath,
		SecurityBoundary Boundary,
		SourceText       Text,
		TextSpan         Span);
	
	/// <summary>
	///     The document and span for a location. A caret is an empty span; a selection runs from
	///     line/column to endLine/endColumn, or to the end of endLine when endColumn is omitted. The
	///     error, when there is one, is ready for <c>scope.Error</c>.
	/// </summary>
	protected async Task<(RefactoringTarget? Target, ToolResult? Error)> LocateAsync(
		string projectPath,
		string filePath,
		int line,
		int column,
		int? endLine,
		int? endColumn,
		CancellationToken cancellationToken)
	{
		if(!TryGetProject(projectPath, out var project, out var error))
			return (null, error);
		
		if(!TryResolveFileContext(projectPath, out var rootPath, out var boundary, out var resolveError))
			return (null, resolveError);
		
		var fullPath = ResolveFilePath(filePath, rootPath, boundary);
		
		if(fullPath is null)
			return (null, new ErrorResult("file_not_found", $"File '{filePath}' was not found under the project root."));
		
		var document = project.Documents.FirstOrDefault(candidate => SecurityBoundary.PathComparer.Equals(candidate.FilePath, fullPath));
		
		if(document is null)
			return (null, new ErrorResult("document_not_found", $"File '{filePath}' is not a C# document in the resolved project."));
		
		var text  = await document.GetTextAsync(cancellationToken);
		var start = GetPosition(text, line, column);
		
		if(start < 0)
			return (null, new ErrorResult("invalid_location", $"Line {line}, column {column} is outside '{filePath}'."));
		
		var end = start;
		
		if(endLine is not null || endColumn is not null) {
			
			var lastLine = endLine ?? line;
			
			end = endColumn is null
				? lastLine >= 1 && lastLine <= text.Lines.Count ? text.Lines[lastLine - 1].End : -1
				: GetPosition(text, lastLine, endColumn.Value)
			;
			
			if(end < 0)
				return (null, new ErrorResult("invalid_location", $"End line {lastLine}, column {(endColumn is null ? "(end of line)" : endColumn.ToString())} is outside '{filePath}'."));
			
			if(end < start)
				return (null, new ErrorResult("invalid_location", "The selection ends before it starts."));
		}
		
		return (new RefactoringTarget(projectPath, project, document, fullPath, rootPath, boundary, text, TextSpan.FromBounds(start, end)), null);
	}
	
	protected static RefactoringActionChoice Choice(RefactoringAction action, int index)
		=> new(index, action.Title, action.Provider, action.ExportName, action.Source, action.EquivalenceKey);
	
	protected static ErrorResult ProvidersDisabled()
		=> new("providers_disabled",
			"Code actions are turned off for this workspace: by --code-actions off or ROSLYNMCP_CODE_ACTIONS=off, " +
			"or by \"codeActions\": { \"enabled\": false } in .madq_roslynmcp.json or the user's config.json.");
}
