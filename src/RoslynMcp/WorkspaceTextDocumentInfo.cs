using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp;

/// <summary>
///     What kind of tracked <see cref="TextDocument"/> a <see cref="DocumentId"/> from
///     <see cref="Solution.GetDocumentIdsWithFilePath"/> identifies — that method returns ids
///     for regular source documents, <c>AdditionalFiles</c> items, and analyzer-config files
///     alike, but only <see cref="Solution.WithDocumentText"/> accepts a source-document id;
///     calling it with any other kind throws <see cref="InvalidOperationException"/> (#276).
///     <c>None</c> means the path is not tracked by the workspace at all.
/// </summary>
internal enum WorkspaceTextDocumentKind
{
	None,
	Source,
	Additional,
	AnalyzerConfig
}

/// <summary>
///     The single classification surface for "what is this path, to Roslyn?" (#288). Resolved
///     from one <see cref="Solution"/> snapshot and used against that same snapshot — the ids
///     it carries are meaningless against any other, so callers must not mix snapshots.
/// </summary>
internal sealed record WorkspaceTextDocumentInfo(
	string                     FullPath,
	WorkspaceTextDocumentKind  Kind,
	ImmutableArray<DocumentId> DocumentIds)
{
	public bool IsTracked => Kind is not WorkspaceTextDocumentKind.None;
	
	/// <summary>
	///     True when <see cref="WithText"/> can update this document in place. Analyzer-config
	///     documents are tracked but excluded: <c>MSBuildWorkspace.CanApplyChange(ChangeAnalyzerConfigDocument)</c>
	///     is false as of Roslyn 5.3.0 (verified), so TryApplyChanges throws for them and callers
	///     must route .editorconfig/.globalconfig edits to a full reload instead.
	/// </summary>
	public bool SupportsIncrementalTextChange =>
		Kind is WorkspaceTextDocumentKind.Source or WorkspaceTextDocumentKind.Additional;
	
	/// <summary>
	///     Classifies <paramref name="fullPath"/> against <paramref name="solution"/>. All ids for
	///     one physical path share a kind, so the first id decides — see <see cref="WithText"/> for
	///     the defensive handling of the case where that does not hold.
	/// </summary>
	public static WorkspaceTextDocumentInfo Resolve(Solution solution, string fullPath)
	{
		var docIds = solution.GetDocumentIdsWithFilePath(fullPath);
		
		return docIds.IsEmpty
			? new WorkspaceTextDocumentInfo(fullPath, WorkspaceTextDocumentKind.None, [])
			: new WorkspaceTextDocumentInfo(fullPath, Classify(solution, docIds[0]), docIds);
	}
	
	static WorkspaceTextDocumentKind Classify(Solution solution, DocumentId id)
	{
		if(solution.GetDocument(id) is not null)
			
			return WorkspaceTextDocumentKind.Source;
		
		if(solution.GetAdditionalDocument(id) is not null)
			
			return WorkspaceTextDocumentKind.Additional;
		
		if(solution.GetAnalyzerConfigDocument(id) is not null)
			
			return WorkspaceTextDocumentKind.AnalyzerConfig;
		
		return WorkspaceTextDocumentKind.None;
	}
	
	/// <summary>
	///     The first tracked <see cref="TextDocument"/> for this path, fetched from the kind-specific
	///     accessor. Returns null when untracked, or when <paramref name="solution"/> is not the
	///     snapshot this info was resolved from.
	/// </summary>
	public TextDocument? GetDocument(Solution solution)
	{
		if(DocumentIds.IsDefaultOrEmpty)
			
			return null;
		
		var id = DocumentIds[0];
		
		return Kind switch {
			
			WorkspaceTextDocumentKind.Source         => solution.GetDocument(id),
			WorkspaceTextDocumentKind.Additional     => solution.GetAdditionalDocument(id),
			WorkspaceTextDocumentKind.AnalyzerConfig => solution.GetAnalyzerConfigDocument(id),
			_                                        => null
		};
	}
	
	/// <summary>
	///     Applies <paramref name="text"/> to every id for this path via the kind-specific
	///     <c>With*DocumentText</c> overload. Throws <see cref="InvalidOperationException"/> if a
	///     path's ids ever span mixed kinds (a regular document in one project, additional in
	///     another) so that the first id's kind does not describe every id — callers catch it and
	///     fall back to a full reload rather than letting it escape.
	/// </summary>
	public Solution WithText(Solution solution, SourceText text)
	{
		foreach(var id in DocumentIds)
			solution = Kind == WorkspaceTextDocumentKind.Additional
				? solution.WithAdditionalDocumentText(id, text)
				: solution.WithDocumentText(id, text);
		
		return solution;
	}
}
