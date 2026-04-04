; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------
RMCP003 | RoslynMcp.Tools | Error    | ToolScopeAnalyzer: first statement must be `using var scope = BeginTool(...)`
RMCP004 | RoslynMcp.Tools | Error    | ToolScopeAnalyzer: every value-bearing return must go through a scope terminal
RMCP005 | RoslynMcp.Tools | Warning  | ToolScopeAnalyzer: BeginTool name must match [McpServerTool(Name = "...")]
