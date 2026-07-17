# RoslynMcp: Style Preservation — Implementation Plan

## Goal

Allow AI agents to respect an author's formatting preferences when editing C# code — blank line patterns, column alignment, comment placement, brace style, etc. — without requiring the agent to reason about trivia directly.

---

## Phase 1 — `roslyn_get_style_profile`

A new analysis tool that reads a file (or type) and returns inferred style rules as structured, named properties. Uses `roslyn_get_trivia` internally; never exposes raw trivia spans to the agent.

### New file: `src/RoslynMcp/Tools/Analysis/GetStyleProfileTool.cs`

**Tool name:** `roslyn_get_style_profile`

**Parameters:**
- `filePath` — the file to analyze
- `projectPath` — standard project path parameter
- `typeName` (optional) — scope analysis to a specific type's members rather than the whole file

**Output shape:**
```json
{
  "file": "WorkspaceManager.cs",
  "sample_size": 12,
  "blank_lines_between_methods": 1,
  "blank_lines_between_fields": 0,
  "blank_lines_after_opening_brace": 0,
  "blank_lines_before_closing_brace": 0,
  "field_alignment": "column",
  "comment_placement": "above_declaration",
  "inline_comment_threshold": null,
  "opening_brace": "same_line",
  "trailing_comma_multiline": false,
  "notes": ["field alignment detected across 6 declarations", "insufficient method sample for confidence"]
}
```

### Inference Rules

Each property is derived by majority vote across all matching nodes in the file. A `sample_size` field records how many nodes were examined; a `notes` array flags low-confidence inferences.

| Property | How to detect |
|----------|---------------|
| `blank_lines_between_methods` | Count `EndOfLineTrivia` in leading trivia of each `MethodDeclaration` before the first non-whitespace trivia |
| `blank_lines_between_fields` | Same, for `FieldDeclaration` siblings |
| `blank_lines_after_opening_brace` | Count blank lines immediately after `{` token in method/type bodies |
| `blank_lines_before_closing_brace` | Count blank lines immediately before `}` token |
| `field_alignment` | Compare whitespace trivia widths across sibling `FieldDeclaration` nodes — if variable-width and values are column-aligned, report `"column"`; if uniform, report `"none"` |
| `comment_placement` | For declarations with `SingleLineCommentTrivia` in leading trivia: `"above_declaration"`; with trailing trivia on same line: `"inline"` |
| `opening_brace` | Check whether `{` appears on the same line as the declaration or on the next line |
| `trailing_comma_multiline` | Check last element of multi-line argument/initializer lists for trailing comma |

### Implementation Notes

- Walk the syntax tree using `DescendantNodes()` filtered by the relevant `SyntaxKind`
- For each node, inspect `GetLeadingTrivia()` and `GetTrailingTrivia()`
- Collect samples into a `StyleSampler` helper class that accumulates votes and computes the majority
- Return `null` for any property where fewer than 2 samples were found (not enough signal)

---

## Phase 2 — `preserveStyle` Flag on `roslyn_replace_in_code`

A `preserveStyle: bool` (default `false`) parameter on `roslyn_replace_in_code`. When true, after the replacement is parsed and before it is written to disk, a trivia normalization pass runs that matches the replacement's trivia structure to the surrounding context.

### Changes to: `src/RoslynMcp/Tools/Editing/ReplaceInCodeTool.cs`

**Add parameter:**
```csharp
[Description("When true, normalizes trivia (blank lines, indentation) in the replacement to match surrounding code style. Default: false.")]
bool preserveStyle = false
```

**Trivia normalization pass** (new internal helper: `StyleNormalizer`):

For simple replacements (one node replaced by one structurally similar node):
1. Compute leading blank lines of the original node → apply the same count to the replacement
2. Copy leading comment trivia from the original to the replacement (only if the replacement has no comments of its own)
3. Preserve the original's indentation depth on all lines of the replacement

For field/property blocks (multiple sibling declarations):
1. After replacing one field, inspect all sibling `FieldDeclaration` nodes
2. If the siblings use column alignment, recompute column widths across all siblings including the new one and rewrite whitespace trivia to restore alignment
3. This requires rewriting trivia on *unchanged* sibling nodes — use `SyntaxEditor` for this

### `StyleNormalizer` helper class

**New file:** `src/RoslynMcp/StyleNormalizer.cs`

```csharp
internal static class StyleNormalizer
{
    // Entry point: normalize replacement node's trivia to match originalNode's context
    public static SyntaxNode Normalize(SyntaxNode original, SyntaxNode replacement, SyntaxNode fileRoot);

    // Detect and reapply column alignment across sibling field declarations
    public static SyntaxNode RebalanceFieldAlignment(SyntaxNode fileRoot, SyntaxNode changedNode);

    // Count blank lines in leading trivia
    private static int CountLeadingBlankLines(SyntaxNode node);

    // Set exact blank line count in leading trivia
    private static SyntaxNode WithLeadingBlankLines(SyntaxNode node, int count);

    // Reindent all lines of a node to a target indentation depth
    private static SyntaxNode WithIndentation(SyntaxNode node, string indentation);
}
```

### Column Alignment Detection

The most complex case. Algorithm:

1. Find all sibling `FieldDeclaration` nodes in the same type body
2. For each, extract: modifier width, type name width, identifier width, initializer position
3. Compare whitespace trivia between tokens — if the whitespace varies across siblings but the *column positions* of identifiers and initializers are consistent, classify as column-aligned
4. Compute the new maximum widths including the replacement field
5. Rewrite whitespace trivia in all siblings to restore alignment

This is the only case where nodes other than the replaced one need their trivia modified.

---

## Phase 3 — Wire `roslyn_get_trivia` into `roslyn_get_style_profile`

Once `roslyn_get_style_profile` exists, evaluate whether `roslyn_get_trivia` should:
- **Stay as-is** — remains a low-level diagnostic tool, useful for debugging style inference and for `Test-CodeStyle.ps1`
- **Be deprecated** — if `roslyn_get_style_profile` covers all agent-facing use cases, demote `roslyn_get_trivia` further (or remove it in a future release)
- **Be kept as infrastructure** — `GetStyleProfileTool` calls the same internal trivia-walking code that `GetTriviaTool` exposes; keeping both makes the internals testable from the outside

Recommendation: keep `roslyn_get_trivia` as experimental infrastructure. Remove the `[McpServerTool]` attribute only if it is confirmed to add no value beyond what `roslyn_get_style_profile` provides.

---

## Order of Execution

| Step | Effort | Dependency | Value |
|------|--------|------------|-------|
| `StyleSampler` helper (inference engine) | Medium | None | Foundational |
| `roslyn_get_style_profile` tool | Medium | StyleSampler | High — agent-facing style awareness |
| `StyleNormalizer` helper (basic trivia copy) | Medium | None | Foundational |
| `preserveStyle` flag on `roslyn_replace_in_code` (basic) | Small | StyleNormalizer | High — immediate quality improvement |
| Column alignment rebalancing | Large | StyleNormalizer | Medium — covers the hardest case |
| Evaluate / update `roslyn_get_trivia` status | Trivial | `roslyn_get_style_profile` | Low |

---

## Open Questions

- **Confidence threshold:** How many samples are needed before a property is reported vs. returning `null`? Suggested minimum: 2 samples for binary properties, 3 for numeric ones.
- **Scope of normalization:** Should `preserveStyle` also handle multi-line argument list formatting, or only member-level trivia? Start with member-level only; expand later.
- **Performance:** `StyleSampler` walks the full syntax tree. For large files this may be slow. Consider caching the style profile per file in `WorkspaceInstance`, invalidated alongside the compilation.
- **Conflicting styles:** What if a file has inconsistent style (mixed blank line counts, some aligned fields and some not)? Report the majority with a `"inconsistent": true` flag on the affected properties.
