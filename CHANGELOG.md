# Changelog

All notable changes to RoslynMcp will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased] — v0.3.0-alpha

### Added
- **Multi-project support** — all 24 tools accept optional `projectPath` parameter (defaults to CWD); switch projects mid-session without restarting
- **WorkspaceResolver** facade layer for consistent per-tool project resolution and structured error handling
- **RoslynMcpTool** base class — `TryGetCompilation()` / `TryGetProject()` with `[NotNullWhen]` attributes; eliminates boilerplate from every tool
- **AdhocWorkspace restored** — directories without `.csproj` now fully supported with FileSystemWatcher for incremental updates
- **Exceptions.cs** — `InvalidProjectPathException`, `ProjectNotFoundException`, `MultipleProjectsFoundException` with structured messages
- **`roslyn_semantic_search`** tool — Roslyn syntax-tree filtering by context (comments, strings, identifiers, xmldocs, code)

### Changed
- All 24 tools migrated to `RoslynMcpTool` base class pattern
- All 24 tools renamed with `roslyn_` prefix (e.g. `get_type_members` → `roslyn_get_type_members`) for unambiguous identification in agent tool lists
- All tools annotated with `ReadOnly`, `Destructive`, or `Idempotent` hints via `McpServerToolAttribute`
- **File logging** — every tool invocation, server start/stop, and workspace error logged to a rotating file; controlled via `ROSLYNMCP_LOG_PATH` env var (default `%LOCALAPPDATA%\RoslynMcp\logs\roslynmcp.log`, set to empty string to disable)
- **`ToolScope`** — `BeginTool(name, subject?)` returns a disposable scope with `Failed<T>()`, `Outcome<T>()`, `Record()` for fluent per-tool logging; extracted into `RoslynMcpTool.ToolScope.cs` via `partial` class
- **Tools reorganized** into semantic subfolders: `Analysis/` (13), `Search/` (3), `Editing/` (2), `Rename/` (2), `Build/` (3) — all remain in `RoslynMcp.Tools` namespace
- WorkspaceManager: LRU workspace cache; `GetProject()`, `GetWorkspaceInfo()`, `InvalidateFile()` added
- TestHarness: path calculation fixed (5 levels up); `projectPath` added to all 23 tests

### Planned
- `undo_last_edit` — revert most recent Roslyn-generated edit (rename, refactoring) from in-memory snapshot
- NuGet package publication
- CI/CD pipeline (GitHub Actions)
- Performance optimizations for large projects
- Additional tool: `get_nullable_flow_state`
- Additional tool: `get_call_info` (resolve method call targets)

---

## [0.2.0-alpha] - 2026-01-XX

### Added
- **23 MCP tools** covering all core agent workflows:
  - Discovery: `roslyn_search_files`, `roslyn_list_types`, `roslyn_get_file_outline`, `roslyn_get_project_info`, `roslyn_get_usings`
  - Type Understanding: `roslyn_get_type_members` (enhanced), `roslyn_get_type_hierarchy`, `roslyn_find_implementations`, `roslyn_get_symbol_documentation`
  - Navigation: `roslyn_get_symbol_info`, `roslyn_find_references`, `roslyn_get_symbol_definition`
  - Code Generation: `roslyn_get_symbols_in_scope`
  - Validation: `roslyn_get_diagnostics`, `roslyn_build_project` (smart Roslyn-first)
  - Editing: `roslyn_replace_in_file`, `roslyn_replace_in_code`, `roslyn_list_files`
  - Refactoring: `roslyn_preview_rename`, `roslyn_apply_rename`
  - Maintenance: `roslyn_clean_solution`, `roslyn_restore_packages`
  - Debug: `roslyn_respawn` (DEBUG only)

### Enhanced
- **`roslyn_get_type_members`**: Now returns full signatures with parameter types, return types, modifiers, and XML doc summaries (was just names)
- **`roslyn_build_project`**: Smart Roslyn-first behavior — checks diagnostics before running MSBuild, skips build if errors exist (huge performance win)
- **Diagnostic filtering**: NETSDK1209 and other non-actionable SDK warnings automatically filtered from output

### Added (Infrastructure)
- Comprehensive test suite: 16 tests covering all 18 tools (100% pass rate)
- TestHarness project for dogfooding (RoslynMcp tests itself)
- GitHub-ready documentation: README, CONTRIBUTING, INSTALLATION
- MIT License
- .gitattributes for consistent line endings

### Fixed
- Multi-target framework builds now require explicit `-f` flag
- Metadata symbols (external types) handled gracefully in `roslyn_find_implementations`
- Preview rename validation handles PascalCase/camelCase property names

---

## [0.1.0-alpha] - 2026-01-XX (Initial Development)

### Added
- Core MCP server infrastructure
- WorkspaceManager with MSBuildWorkspace and AdhocWorkspace support
- Initial tool set: `roslyn_get_type_members`, `roslyn_get_diagnostics`, `roslyn_find_references`, `roslyn_get_symbol_info`
- Rename tools: `roslyn_preview_rename`, `roslyn_apply_rename` with approval flow
- ApprovalStore for session-scoped rename approvals
- SolutionDiff for unified diff generation
- FileSystemWatcher integration for AdhocWorkspace
- ImplicitUsings enabled (.NET 8/10/11 multi-targeting)

### Technical Details
- Multi-targeted: net8.0, net10.0 (net11.0 auto-added when .NET 11 SDK detected)
- C# 14 preview language version
- Roslyn 5.3.0
- ModelContextProtocol 1.1.0
- Microsoft.Extensions.Hosting for DI and lifetime management

---

## Future Considerations

### Performance
- Lazy symbol loading for large workspaces
- Incremental compilation cache
- Parallel compilation for multi-project solutions

### Features
- Support for F# projects
- Support for VB.NET projects
- Cross-solution symbol search
- Project-wide rename with dependency tracking
- Code fix suggestions (Roslyn analyzers integration)

### Developer Experience
- VS Code extension for easier configuration
- GitHub Copilot Chat integration examples
- Sample MCP client for testing

---

[Unreleased]: https://github.com/MadQ/RoslynMcp/compare/v0.2.0-alpha...HEAD
[0.2.0-alpha]: https://github.com/MadQ/RoslynMcp/releases/tag/v0.2.0-alpha
[0.1.0-alpha]: https://github.com/MadQ/RoslynMcp/releases/tag/v0.1.0-alpha
