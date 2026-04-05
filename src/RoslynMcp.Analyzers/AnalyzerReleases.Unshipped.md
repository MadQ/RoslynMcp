; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RMCP007 | RoslynMcp.Tools | Error | [McpServerTool] method is missing a [Description] attribute
RMCP008 | RoslynMcp.Tools | Error | Parameter on a [McpServerTool] method is missing a [Description] attribute (CancellationToken exempt)
RMCP009 | RoslynMcp.Tools | Error | string projectPath parameter uses an inline description string instead of the ProjectPathDescription constant
