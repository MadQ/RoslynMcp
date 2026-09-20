using Microsoft.CodeAnalysis;

namespace RoslynMcp;

internal enum WorkspaceTextDocumentKind
{
	None,
	Source,
	Additional,
	AnalyzerConfig
}

internal sealed record WorkspaceTextDocumentInfo(
	string                    FullPath,
	WorkspaceTextDocumentKind Kind,
	DocumentId[]              DocumentIds)
{
	public bool IsTracked => Kind is not WorkspaceTextDocumentKind.None;
	
	public bool SupportsIncrementalTextChange =>
		Kind is WorkspaceTextDocumentKind.Source or WorkspaceTextDocumentKind.Additional;
}
