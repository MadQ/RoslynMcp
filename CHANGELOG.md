# Changelog

All notable changes to RoslynMcp will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Planned
- NuGet package publication
- CI/CD pipeline (GitHub Actions)
- Performance optimizations for large projects
- Additional tool: `get_nullable_flow_state`
- Additional tool: `get_call_info` (resolve method call targets)

---

## [0.2.0-alpha] - 2025-01-XX

### Added
- **18 MCP tools** covering all core agent workflows:
  - Discovery: `search_files`, `list_types`, `get_file_outline`, `get_project_info`, `get_usings`
  - Type Understanding: `get_type_members` (enhanced), `get_type_hierarchy`, `find_implementations`, `get_symbol_documentation`
  - Navigation: `get_symbol_info`, `find_references`, `get_symbol_definition`
  - Code Generation: `get_symbols_in_scope`
  - Validation: `get_diagnostics`, `build_project` (smart Roslyn-first)
  - Refactoring: `preview_rename`, `apply_rename`
  - Debug: `respawn` (DEBUG only)

### Enhanced
- **`get_type_members`**: Now returns full signatures with parameter types, return types, modifiers, and XML doc summaries (was just names)
- **`build_project`**: Smart Roslyn-first behavior — checks diagnostics before running MSBuild, skips build if errors exist (huge performance win)
- **Diagnostic filtering**: NETSDK1209 and other non-actionable SDK warnings automatically filtered from output

### Added (Infrastructure)
- Comprehensive test suite: 16 tests covering all 18 tools (100% pass rate)
- TestHarness project for dogfooding (RoslynMcp tests itself)
- GitHub-ready documentation: README, CONTRIBUTING, INSTALLATION, TEST_RESULTS
- MIT License
- .gitattributes for consistent line endings

### Fixed
- Multi-target framework builds now require explicit `-f` flag
- Metadata symbols (external types) handled gracefully in `find_implementations`
- Preview rename validation handles PascalCase/camelCase property names

---

## [0.1.0-alpha] - 2025-01-XX (Initial Development)

### Added
- Core MCP server infrastructure
- WorkspaceManager with MSBuildWorkspace and AdhocWorkspace support
- Initial tool set: `get_type_members`, `get_diagnostics`, `find_references`, `get_symbol_info`
- Rename tools: `preview_rename`, `apply_rename` with approval flow
- ApprovalStore for session-scoped rename approvals
- SolutionDiff for unified diff generation
- FileSystemWatcher integration for AdhocWorkspace
- ImplicitUsings enabled (.NET 8/10/11 multi-targeting)

### Technical Details
- Multi-targeted: net8.0, net10.0, net11.0
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
