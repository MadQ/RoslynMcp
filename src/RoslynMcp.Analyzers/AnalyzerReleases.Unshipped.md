; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------
RMCP006 | RoslynMcp.Tools | Warning  | New rule: scope terminal has placeholder 'TODO' detail string

### Changed Rules

Rule ID | New Category    | New Severity | Old Category    | Old Severity | Notes
--------|-----------------|--------------|-----------------|--------------|--------------------
RMCP005 | RoslynMcp.Tools | Error        | RoslynMcp.Tools | Warning      | Name mismatch is factually incorrect — upgraded to Error
