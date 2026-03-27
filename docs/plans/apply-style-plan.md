# RoslynMcp: `roslyn_apply_style` — Implementation Plan

## Overview

The natural write-side complement to `roslyn_get_style_profile`. Applies an inferred (or explicitly provided) style profile to an entire file, using the same two-phase preview/apply pattern as `roslyn_preview_rename` / `roslyn_apply_rename`.

**Target release:** v1.1.0 (after `get_style_profile` and `StyleNormalizer` are battle-tested in v1.0.0)

---

## Why Two-Phase

A whole-file trivia rewrite touching potentially hundreds of nodes is at least as risky as a rename affecting dozens of files. The two-phase pattern is non-negotiable here:

1. Agent calls `roslyn_preview_style` → sees a diff of every whitespace/blank-line change
2. Agent (or user) reviews — confirms the profile looks right, no unintended changes
3. Agent calls `roslyn_apply_style` with the token to commit

The existing `ApprovalStore` and `SolutionDiff` infrastructure extends directly to this use case.

---

## New Tools

### `roslyn_preview_style`

Applies a style profile to a file in-memory and returns a unified diff + confirmation token.

**Parameters:**
- `filePath` — the file to reformat
- `projectPath` — standard project path parameter
- `profile` (optional) — explicit style profile JSON; if omitted, inferred from the file itself via `get_style_profile`
- `confidence` (optional, default `2`) — minimum sample count required before a property is applied; lower = more aggressive, higher = more conservative
- `propertiesOnly` (optional) — array of property names to apply (e.g., `["blank_lines_between_methods", "field_alignment"]`); omit to apply all high-confidence properties

**Behaviour:**
1. Resolve or infer the style profile
2. Run `StyleNormalizer` across all matching nodes in the file
3. Build a unified diff between original and normalized source
4. Register with `ApprovalStore`, return token + diff

**Response shape:**
```json
{
  "token": "a3f9c1d2e8b0",
  "diff": "--- WorkspaceManager.cs\n+++ WorkspaceManager.cs\n...",
  "profile_used": { ... },
  "nodes_affected": 34,
  "properties_applied": ["blank_lines_between_methods", "field_alignment"],
  "properties_skipped": ["opening_brace"],
  "message": "Review the diff, then call roslyn_apply_style with token 'a3f9c1d2e8b0' and approval 'y' or 'session'."
}
```

---

### `roslyn_apply_style`

Commits or rejects a pending style preview by token.

**Parameters:**
- `token` — confirmation token from `roslyn_preview_style`
- `approval` — `'y'` (apply once), `'session'` (apply and auto-approve this profile for this file for the session), `'n'` (reject)
- `projectPath` — standard project path parameter

**Behaviour:** Identical to `roslyn_apply_rename` — consumes the token from `ApprovalStore`, writes changed documents to disk, invalidates the compilation.

The `session` approval is especially well-suited here: once a style profile is reviewed and approved for a file, future applications of the same profile to that file can skip the review step.

---

## Implementation Notes

### Dependency on Prior Work

`roslyn_apply_style` cannot ship until:
- `roslyn_get_style_profile` is working and battle-tested (v1.0.0)
- `StyleNormalizer` handles the column alignment rebalancing case correctly (v1.0.0)
- Both have been validated on real projects with varied styles

### Scoping by Confidence

Not all inferred style properties should be applied unconditionally. The `confidence` threshold parameter controls this: a property with only 1 sample (e.g., a file with a single method) should not drive a reformat. Default threshold of 2 is conservative; agents can lower it explicitly.

The `properties_skipped` field in the response makes it transparent which properties were not applied and why.

### Column Alignment: The Hard Case

Rebalancing column-aligned fields file-wide is the most complex operation:
- Must find every field declaration group (contiguous sibling fields)
- Recompute column widths per group
- Rewrite whitespace trivia on all fields in the group, not just changed ones
- Must not disturb unrelated nodes

Recommend: ship v1.1.0 with column alignment rebalancing scoped to **single type bodies** only. Full file-wide rebalancing can follow in v1.2.0 once the per-type case is validated.

### `PendingOperation` Extension

`ApprovalStore` stores `PendingOperation(NewSolution, Diff, SymbolKey, PreConfirmed)`. For style operations, `SymbolKey` becomes a **file path key** — e.g., `"style::WorkspaceManager.cs"`. This allows `session` approval to be scoped per-file, which is the right granularity.

---

## Relationship to Existing Tools

| Tool | Role |
|------|------|
| `roslyn_get_trivia` | Low-level primitive; internal engine for style inference |
| `roslyn_get_style_profile` | Reads and infers style rules; agent-facing |
| `preserveStyle` on `replace_in_code` | Incremental style preservation during targeted edits |
| `roslyn_preview_style` | Whole-file style normalization preview |
| `roslyn_apply_style` | Commits a previewed style normalization |

---

## Release Sequencing

| Version | Work |
|---------|------|
| v0.4.0 | Bug fixes, `get_member_body`, `AGENTS.md` |
| v1.0.0 | `get_style_profile`, `StyleNormalizer`, `preserveStyle` flag on `replace_in_code` |
| v1.1.0 | `roslyn_preview_style`, `roslyn_apply_style` (column alignment scoped to single type bodies) |
| v1.2.0 | Full file-wide column alignment rebalancing |

---

## Open Questions

- **Profile source:** Should `preview_style` accept a profile inferred from a *different* file? E.g., "apply the style of `WorkspaceManager.cs` to `WorkspaceResolver.cs`." Useful for bringing new files into line with established ones. Low implementation cost once the profile is serializable.
- **Selective application:** Should the agent be able to apply style to a single named type or method within a file, rather than the whole file? Reduces risk, easier to review. Worth considering as the default scope.
- **`.editorconfig` interaction:** If a `.editorconfig` exists, should inferred profile properties that conflict with it be flagged? Or should `.editorconfig` take precedence?
