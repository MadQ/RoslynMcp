# Session Handoff

**2026-04-04 (UTC)**

---

## Executive Summary

Shipped several quality and correctness fixes across the server. Key deliverables: git branch/commit metadata in `roslyn_local_history` backups (#122), `ToolResults.cs` naming normalization to idiomatic C# (#124), abstract `ToolErrorResult` base record with type-safe error flow (#125), and a `BuildLiteralRegex` newline-normalization bug fix (#126). Also fixed a missing `[McpServerTool]` attribute on `ListFilesTool` (tool count now 35, not 33), corrected README tool count, and did GitHub issue triage (milestones assigned, #105 closed as already fixed).

## Current State

- **Branch:** `dev`
- **Compiler:** 0 errors, 0 warnings
- **Version:** `v0.7.2-alpha` (not yet bumped for these changes)
- **Stash:** 27 files of pre-existing style changes stashed as `"style: semicolons-on-own-lines + blank line pass (suspended — resume later)"` — do NOT pop until style pass suspension is lifted
- **pub.ps1:** Convenience publish script at repo root — re-run after server-side C# changes.

## Completed This Session

### #122 — Git branch/commit context in backup metadata (commit `0242c07`)
- `BackupStore` now records `gitBranch` and `gitCommit` at snapshot time.
- `roslyn_local_history` list response includes `_caution` when any backup was taken on a different branch than the current one.
- Tool descriptions for `roslyn_local_history` and `roslyn_write_file` updated with branch-agnostic caution notes.

### #124 — `ToolResults.cs` naming normalization (commit `1fce983`)
- All 51 result records use positional syntax + `[property: JsonPropertyName("snake_case")]`.
- PascalCase C# names throughout.
- `MetadataSymbolResult.Message` → `Error`; `SymbolDocumentationEmptyResult.Message` → `Error`.

### #125 — Abstract `ToolErrorResult` base record (commit `d1c6512`)
- `ToolErrorResult` abstract base record added.
- `ExtractDetail<T>()` switch deleted.
- `ToolScope.Error<T>()` gains `where T : ToolErrorResult` constraint.
- 5 result types now derive from `ToolErrorResult`.

### #126 — `BuildLiteralRegex` newline bug + `ToolErrorResult.Error` non-nullable (commits `b3117d2`, `f755c5e`, `10faedc`)
- `.Replace("\n",...)` was a no-op (C# string literal `\n` != literal backslash-n in pattern strings); fixed to `.Replace(@"\n",...)`.
- `ToolErrorResult.Error` made `string` (non-nullable); dead `?? "error"` fallback removed.

### ListFilesTool attribute fix
- `[McpServerTool(Name = "roslyn_list_files"...)]` attribute was missing from the method — added it.
- Tool count is now **35** (was incorrectly 33).

### README.md
- "33 tools" in body text corrected to "35 tools".

### GitHub issue triage
- #114, #117, #99 → v0.8.0 milestone
- #118 → v0.9.0
- #86 → v1.0.0
- #105 closed — was already fixed in commit `3e115df` (SolutionDiff infinite loop).
- Milestones assigned to 5 previously unassigned issues.

## Open Issues

| # | Milestone | Title |
|---|-----------|-------|
| #118 | v0.9.0 | LogViewer — NDJSON log viewer (**NOTE:** may already be shipped in v0.7.2; verify before closing) |
| #117 | v0.8.0 | Expose version/MSBuild properties in `roslyn_get_project_info` |
| #114 | v0.8.0 | Tighten `IsUnderRoot` diagnostic filter |
| #110 | v1.0.0 | `roslyn_apply_code_fix` |
| #99  | v0.8.0 | Making agents reliably choose roslyn_* tools |
| #86  | v1.0.0 | Shared workspace service via named pipes |
| #37  | v1.0.0-beta | Column alignment rebalancing |
| #36  | v1.0.0-beta | `roslyn_preview_style` / `roslyn_apply_style` |
| #35  | v0.9.0 | `preserveStyle` flag for `replace_in_code` |
| #34  | v0.9.0 | `roslyn_get_style_profile` |
| #33  | v0.8.0 | `roslyn_find_unused` |
| #32  | v0.8.0 | Call graph tools |
| #9   | v0.9.0 | Security: filesystem access boundaries |

## Known Issues / Technical Debt

- **Working tree rollback bug:** After commits, disk files sometimes silently revert to pre-commit state. Always run `git status` after commits; fix with `git checkout -- <files>`. Occurred after #124 and #125.
- **#118 status unclear:** LogViewer shipped in v0.7.2-alpha — the open issue may track further improvements. Verify before acting.
- **CHANGELOG.md / ROADMAP.md / AGENTS.md:** A parallel agent in this session was syncing entries for #122–#126. Verify these are up to date before the next release.

## Suggested Next Steps (v0.8.0 order)

1. **#114** — Tighten `IsUnderRoot` filter (small, targeted)
2. **#117** — Expose more MSBuild/SDK properties (small, tidy)
3. **#32** — Call graph tools (bigger, novel)
4. **#33** — `roslyn_find_unused` (bigger, novel)
5. **#99** — Agent tool selection (documentation/hooks work)

## Context for Resuming

- CWD: `J:\Projects\RoslynMcp` — never `cd`
- Style passes suspended — do NOT run `Test-CodeStyle.ps1 -Fix` or pop the style stash
- Use `roslyn_get_diagnostics` for error checks; `roslyn_build_project` for full build validation
- `pub.ps1` at repo root: re-run after server-side C# changes to update the published exe
- Dogfood roslyn_* tools always; terminal is last resort
- `gh.exe` globally available

