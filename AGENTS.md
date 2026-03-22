# AGENTS.md — RoslynMcp

Working rules for GitHub Copilot and any other AI agent in this repo.

**Ignore files called HumanNotes.txt** These are for human reference only and may contain notes that would confuse an AI assistant.

---

> **Coding style rules** (braces, naming, modern C#) live in
> [`.github/copilot-instructions.md`](.github/copilot-instructions.md). Don't duplicate them here.

---

## Project

| | |
|---|---|
| **Type** | Model Context Protocol (MCP) server — stdio transport |
| **Runtime** | .NET 8 / .NET 10 / .NET 11 (multi-targeted) |
| **Language** | C# 14 (`<LangVersion>preview</LangVersion>`) |
| **Version** | 0.2.0-alpha (pre-1.0) |
| **Dependencies** | `Microsoft.CodeAnalysis.*` (Roslyn) — MSBuildWorkspace (if .csproj found) → AdhocWorkspace (fallback) |

Two projects:
- `RoslynMcp/RoslynMcp.csproj` — MCP server
- `TestHarness/TestHarness.csproj` — local testing client

```
dotnet build RoslynMcp/RoslynMcp.csproj
```

---

## Architecture

| Component | Responsibility |
|-----------|----------------|
| `WorkspaceManager` | Auto-detects `.csproj` → `MSBuildWorkspace` (full resolution) or `AdhocWorkspace` (source-only); lazy compilation rebuild |
| `SearchFilesTool` | `search_files` — regex search across workspace files with paging |
| `TypeMembersTool` | `get_type_members` — enumerate members with full signatures + doc summaries |
| `DiagnosticsTool` | `get_diagnostics` — compiler errors and warnings for project or single file |
| `FindReferencesTool` | `find_references` — all references to a symbol across the project |
| `SymbolInfoTool` | `get_symbol_info` — resolve what a name at a location actually is |
| `PreviewRenameTool` | `preview_rename` — compute rename edits, return unified diff + token |
| `ApplyRenameTool` | `apply_rename` — approve/reject a pending rename by token |
| `ProjectInfoTool` | `get_project_info` — project metadata (TFM, language version, packages, etc.) |
| `BuildTool` | `build_project` — smart build: check Roslyn diagnostics first, skip if errors; run `dotnet build` if clean |
| `CleanSolutionTool` | `clean_solution` — remove all build artifacts (bin/obj directories) |
| `RestorePackagesTool` | `restore_packages` — restore NuGet packages |
| `FileOutlineTool` | `get_file_outline` — type/member structure without bodies (token saver) |
| `TypeHierarchyTool` | `get_type_hierarchy` — base types, interfaces, derived types |
| `FindImplementationsTool` | `find_implementations` — concrete implementations of interfaces/abstract members |
| `ListTypesTool` | `list_types` — enumerate types with optional filters |
| `GetUsingsTool` | `get_usings` — using directives + global usings |
| `GetSymbolDocumentationTool` | `get_symbol_documentation` — XML doc comments for symbols |
| `GetSymbolDefinitionTool` | `get_symbol_definition` — find declaration location with signature |
| `GetSymbolsInScopeTool` | `get_symbols_in_scope` — enumerate accessible symbols at a location |
| `RespawnTool` | `respawn` (DEBUG only) — hot-reload mechanism |
| `ApprovalStore` | Session-scoped approval state (`y`, `n`, `session` model) |
| `SolutionDiff` | Unified diff generation for `Solution` → `Solution` edits |

**Data flow:** stdio MCP request → tool → `WorkspaceManager.GetCompilation()` (may rebuild) → Roslyn API → JSON response.

**Workspace modes:**
- **MSBuildWorkspace** (if `.csproj` found) — full NuGet resolution, multi-project support, .NET Framework 4.6.1+ compatibility
- **AdhocWorkspace** (fallback) — source-only, fast startup (<100 ms)

---

## Rename Workflow (User-Configurable)

**All renames use two-phase flow:** `preview_rename` → review diff → `apply_rename`.

**Why?** Renames can affect dozens/hundreds of files. The two-phase flow lets you review impact before committing — especially important for large/uncertain changes.

### Agent Behavior (Configure Per User)

Users can instruct agents to handle renames conservatively or aggressively:

**Conservative (default recommendation):**
```
Always call preview_rename first. Show me the diff.
Only call apply_rename after I explicitly approve.
```

**Balanced:**
```
For small renames (1-3 files, obvious intent like typo fixes):
  - Call preview_rename, review diff yourself, auto-apply if safe
For large renames (>3 files, broad scope, uncertain impact):
  - Call preview_rename, show me the diff, wait for approval
```

**Aggressive:**
```
Call preview_rename → apply_rename immediately unless I say otherwise.
I trust you and I have git.
```

**Session approval:** If user approves a rename with `approval: "session"`, further renames of the same symbol auto-apply for the remainder of the server process (until restart). Use this for bulk renaming tasks.

**Planned feature:** `undo_last_edit` will revert the most recent Roslyn edit (rename, refactoring) from an in-memory snapshot. Useful for "wait, let me rethink that" moments mid-task.

---

## Git Rules

| Operation | Rule |
|-----------|------|
| Create / switch branch | ✅ Free |
| Stage files | ✅ Free |
| Commit | ❌ Ask first |
| Push | ❌ Ask first |

**Shorthand:** `c/p` = commit and push now.

**Branch naming:** `feature/<short-description>` for multi-file or non-trivial changes.

---

## Terminal

PowerShell session with known issues. Rules:
- **Single line only** — no here-strings, no multi-line expressions
- **`rg.exe`** may be available — use **backslash paths**:
  ```powershell
  cd path\to\RoslynMcp && rg -n "pattern" Program.cs
  ```
- Fallback: `Select-String -Path "*.cs" -Pattern "pattern"`
- **`git --no-pager`** — always pass to avoid pagination hangs

---

## Testing

**Local testing:** use `TestHarness/TestHarness.csproj` — runs a single tool call and prints the JSON response.

**Live testing:** configure in `.mcp.json` and test via GitHub Copilot or any MCP client.

Example `.mcp.json`:
```json
{
  "servers": {
    "roslyn": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "path/to/RoslynMcp/RoslynMcp.csproj", "--", "."]
    }
  }
}
```
