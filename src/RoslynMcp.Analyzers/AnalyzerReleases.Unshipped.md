; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RMCP010 | RoslynMcp.Tools | Warning | A string literal names a [McpServerTool] of the compilation without its roslyn_ prefix
RMCP011 | RoslynMcp.Tools | Error | A mode parameter of a [McpServerTool] method does not describe itself with MatchModeDescription
