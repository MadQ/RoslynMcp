## Release 0.7.4

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RMCP006 | RoslynMcp.Tools | Warning | scope.Outcome/Failed first argument contains placeholder text 'TODO'

### Changed Rules

Rule ID | New Category | New Severity | Old Category | Old Severity | Notes
--------|--------------|--------------|--------------|--------------|-------
RMCP005 | RoslynMcp.Tools | Error | RoslynMcp.Tools | Warning | BeginTool name mismatch is factually incorrect, not cosmetic

## Release 0.7.3

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RMCP003 | RoslynMcp.Tools | Error | [McpServerTool] method must begin with 'using var scope = BeginTool(...)'
RMCP004 | RoslynMcp.Tools | Error | [McpServerTool] method return must go through scope.Outcome/Error/Failed
RMCP005 | RoslynMcp.Tools | Warning | BeginTool name argument must match [McpServerTool(Name = ...)]

## Release 0.2.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RMCP001 | Style | Warning | Prefer 'nint' over 'IntPtr'
RMCP002 | Style | Warning | Prefer 'nuint' over 'UIntPtr'
