# Changelog

All notable changes to RoslynMcp will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

---

## [0.6.0-alpha] — 2026-03-28

### Fixed
- **cacheLock contention** — workspace loading (multi-second MSBuild) no longer holds the cache lock; other tool calls can still hit cached workspaces during loading. Double-checked pattern handles concurrent loads.
- **`msbuildRegistered` volatile** — double-checked locking read outside lock needs `volatile` for correctness on ARM (.NET ECMA-335 memory model)
- **FSW feedback loop** — `MSBuildWorkspace.TryApplyChanges` writes text back to disk, re-triggering the FSW. New `ApplyChangesWithFswSuppressed` method disables FSW during writes (#49)
- **SolutionDiff O(n*m) LCS memory** — replaced full DP table (~400 MB for 10K-line files) with O(n+m) greedy line matching using dictionary + binary search (#26)
- **GetTriviaTool bounds checking** — clamp `startLine`/`endLine` to valid range instead of crashing on out-of-range input
- **DiagnosticsTool single-file performance** — use `GetSemanticModel(tree).GetDiagnostics()` instead of full-project compilation for single-file queries
- **ApprovalStore eviction** — cap pending operations at 10, evict oldest. Each holds two `Solution` snapshots; previously accumulated without bound (#27)

---

## [0.5.0-alpha] — 2026-03-27

### Fixed
- **FormatModifiers** — `protected internal` and `private protected` were silently dropped in FileOutlineTool, GetSymbolDefinitionTool, TypeMembersTool. Extracted to shared `FormatModifiers` in `RoslynMcpTool` base class.
- **DiagnosticsTool** — errors now sorted before warnings (`OrderByDescending`); error path returns structured object instead of `.ToString()` on anonymous type
- **TypeHierarchyTool** — `FindDerivedClassesAsync` replaced with `FindImplementationsAsync` for interface types (was returning nothing for interfaces)
- **Rename workflow** — store base solution in `PendingOperation` so apply uses the same snapshot as preview; `SymbolKey` includes parameter types to distinguish overloaded methods (#22)
- **ReplaceInCodeTool** — added `ParseStatement` fallback for statement/directive syntax kinds; added `RecordDeclaration` to member arm
- **SimpleNameFinder** — verify namespace qualification for dotted names instead of silently discarding the prefix
- **SemanticSearchTool** — replaced per-token regex with line-level matching so multi-token patterns work in `code` context; comments/strings excluded via `DetermineContext`
- **PreviewRenameTool** — error path uses `JsonSerializer.Serialize` instead of `.ToString()` on anonymous object (#46)
- **MSBuild FileSystemWatcher** — added FSW for MSBuild workspaces; external file changes (IDE edits, `rm`, non-Roslyn agent tools) now detected. Handles modify (push SourceText) and delete (clear text). New files require server restart (#47)

---

## [0.4.0-alpha] — 2026-03-27

Folds in previously unreleased v0.3.0-alpha work (multi-project infrastructure) plus v0.4.0 bug fixes, solution-level loading, and comprehensive IO hardening.

### Added (v0.3.0 — multi-project infrastructure)
- **Multi-project support** — all 25 tools accept `projectPath` parameter; switch projects mid-session without restarting
- **WorkspaceResolver** facade layer for consistent per-tool project resolution and structured error handling
- **RoslynMcpTool** base class — `TryGetCompilation()` / `TryGetProject()` with `[NotNullWhen]` attributes; eliminates boilerplate from every tool
- **AdhocWorkspace restored** — directories without `.csproj` now fully supported with FileSystemWatcher for incremental updates
- **Exceptions.cs** — `InvalidProjectPathException`, `ProjectNotFoundException`, `MultipleProjectsFoundException` with structured messages
- **`roslyn_semantic_search`** tool — Roslyn syntax-tree filtering by context (comments, strings, identifiers, xmldocs, code)
- **Version centralization** — `Directory.Build.props` with `VersionPrefix`/`VersionSuffix`; Git commit SHA automatically appended to `InformationalVersion` for traceability

### Added (v0.4.0)
- **Solution-level workspace loading** — WorkspaceManager searches upward for .sln/.slnx and loads the full solution; cross-project references, rename, and find-implementations work across all projects
- **`.slnx` support** — parses the new XML solution format and loads each project into the same MSBuildWorkspace
- **`roslyn_debug_attach`** tool (DEBUG builds only) — launches JIT debugger dialog for mid-session VS attach
- **`Paginate<T>` helper** in `RoslynMcpTool` base class — DRY pagination with bounds checking
- **`ResolveFilePath` helper** — suffix-match fallback for file path resolution; preserves backward compat when rootPath is the solution directory
- **`GetFilesSafe` helper** — `Directory.GetFiles` with exception guarding
- **Comprehensive planning docs** — ROADMAP.md, code audit (23 bugs documented), tool assessment, style preservation plans

### Changed
- **BREAKING: `projectPath` now REQUIRED** — all 25 tools require explicit project path; no CWD fallback (prevents catastrophic drive root enumeration, Issue #9 prerequisite)
- All tools migrated to `RoslynMcpTool` base class pattern
- All tools renamed with `roslyn_` prefix (e.g. `get_type_members` → `roslyn_get_type_members`) for unambiguous identification in agent tool lists
- All tools annotated with `ReadOnly`, `Destructive`, or `Idempotent` hints via `McpServerToolAttribute`
- **File logging** — every tool invocation, server start/stop, and workspace error logged to a rotating file; controlled via `ROSLYNMCP_LOG_PATH` env var
- **`ToolScope`** — `BeginTool(name, subject?)` returns a disposable scope with `Failed<T>()`, `Outcome<T>()`, `Record()` for fluent per-tool logging
- **Tools reorganized** into semantic subfolders: `Analysis/` (16), `Search/` (3), `Editing/` (2), `Rename/` (2), `Build/` (3)
- **WorkspaceManager split** into partial classes: `WorkspaceManager.cs` (cache + API), `WorkspaceManager.Resolution.cs` (path resolution), `WorkspaceManager.Instance.cs` (workspace lifecycle)
- **Per-project compilation cache** replaces single `Compilation` field (supports multi-project solutions)
- **`GetRootPath`** returns solution directory when loaded from a solution; project directory otherwise
- **LINQ optimization** — tools use materialize-once pattern when enumerating multiple times

### Fixed (v0.3.0)
- **AdhocWorkspace safety** — protected directory enumeration with try/catch for `UnauthorizedAccessException`; skips system/hidden directories and common large folders
- **Root directory protection** — fail-fast check prevents accidental scanning of drive roots
- **stdout contamination** — server startup messages use `Console.Error.WriteLine()` to avoid corrupting MCP protocol stream
- **Server crash on startup** — `MSBuildLocator.RegisterDefaults()` moved into deferred `EnsureMSBuildRegistered()` with double-checked locking
- **`LoadMSBuildWorkspace` unhandled exception** — wrapped in try/catch with context
- **`ListTypesTool` / `DiagnosticsTool` return type** — changed `string[]` → `object` for correct MCP SDK serialization
- **`AppDomain.UnhandledException` handler** — fatal crashes now write `[FATAL]` to log file

### Fixed (v0.4.0)
- **5 unregistered tools** — FileOutlineTool, GetSymbolsInScopeTool, GetUsingsTool, GetLineCountTool, SearchFilesTool were missing `[McpServerTool]` attributes; 20% of tools were silently invisible (#15)
- **Pagination crash** — `AsSpan(skip, ...)` threw `ArgumentOutOfRangeException` when `skip >= results.Length` in 5 tools (#16)
- **AdhocWorkspace root path** — `GetRootPath` returned parent directory instead of the directory itself (#17)
- **Semantic search duplicate results** — multi-TFM projects produced duplicate matches; deduplicated by `FilePath` (#18)
- **SolutionDiff `\r\n` splitting** — diff output had spurious `\r` on Windows (#19)
- **MSBuildWorkspace comment** — corrected misleading comment claiming auto file watching (#19)
- **LRU eviction gap** — `GetSolution`/`GetProject`/`GetWorkspaceInfo` were missing eviction on cache miss
- **IO exception audit** — guarded all unprotected filesystem calls with exception filters; added `UnauthorizedAccessException` to `ReplaceInFileTool` catch blocks; guarded `SolutionDiff.ApplyToDiskAsync`, `RestorePackagesTool.FindProjectFile`, `CleanSolutionTool.FindProjectFile`

### Security
- **Removed CWD fallback** — prevents server started from drive roots from enumerating entire drives
- **ArgumentException on missing projectPath** — clear error message when agent fails to specify project

⚠️ **Known Issue:** RoslynMcp currently has unrestricted filesystem access. Only use with trusted agents and on projects you control. Filesystem security boundaries are planned for a future release. See [Issue #9](https://github.com/MadQ/RoslynMcp/issues/9).

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
- **`roslyn_build_project`**: Smart Roslyn-first behavior — checks diagnostics before running MSBuild, skips build if errors exist
- **Diagnostic filtering**: NETSDK1209 and other non-actionable SDK warnings automatically filtered from output

### Added (Infrastructure)
- Comprehensive test suite: 23 tests covering all tools (100% pass rate)
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

[Unreleased]: https://github.com/MadQ/RoslynMcp/compare/v0.6.0-alpha...HEAD
[0.6.0-alpha]: https://github.com/MadQ/RoslynMcp/compare/v0.5.0-alpha...v0.6.0-alpha
[0.5.0-alpha]: https://github.com/MadQ/RoslynMcp/compare/v0.4.0-alpha...v0.5.0-alpha
[0.4.0-alpha]: https://github.com/MadQ/RoslynMcp/compare/v0.2.0-alpha...v0.4.0-alpha
[0.2.0-alpha]: https://github.com/MadQ/RoslynMcp/releases/tag/v0.2.0-alpha
[0.1.0-alpha]: https://github.com/MadQ/RoslynMcp/releases/tag/v0.1.0-alpha
