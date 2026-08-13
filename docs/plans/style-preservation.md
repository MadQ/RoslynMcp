# Style Preservation in RoslynMcp: Thoughts on `roslyn_get_trivia` and Beyond

## Where Trivia Actually Lives in Roslyn's Model

Style *is* trivia in Roslyn — blank lines, column alignment of fields, comment placement above vs. inline, spacing around operators. `roslyn_get_trivia` is therefore the right instinct: you're reaching for the right layer of the AST. The issue is the gap between having the raw trivia data and knowing what to *do* with it.

---

## Where the Current Tools Fall Short

`roslyn_replace_in_code` already calls `WithTriviaFrom(originalNode)` — so the outer trivia (leading/trailing whitespace and comments on the replaced node itself) is preserved. What's lost is the *internal* trivia of the replacement: blank lines inside a method body, column alignment of a field block, spacing within expressions. The replacement text brings its own trivia and there's no reconciliation step.

For rename, trivia is actually well-preserved already since Roslyn's renamer only touches identifier tokens — the surrounding structure stays intact.

---

## What `roslyn_get_trivia` Can Realistically Contribute

As a raw data feed to an agent it's probably too low-level to be directly actionable. An agent reading trivia spans can't easily derive "this author aligns field declarations in columns" or "this author always puts a blank line before a comment but not after." The cognitive overhead of going from raw trivia → inferred pattern → applied-to-replacement is too many steps.

Where it becomes useful is as the *engine* for a higher-level tool rather than as the exposed surface itself.

---

## A More Actionable Design: `roslyn_get_style_profile`

Rather than exposing raw trivia, a tool that *analyzes* trivia and returns semantic style properties would give agents something they can actually act on:

```json
{
  "blank_lines_between_methods": 1,
  "blank_lines_between_fields": 0,
  "field_alignment": "column",
  "comment_placement": "above_declaration",
  "opening_brace": "same_line",
  "trailing_comma_in_multiline": true
}
```

This gets derived from trivia analysis internally but gives the agent named rules it can follow when composing replacement text.

---

## A `preserveStyle` Flag on Editing Tools

The most seamless approach for the common case would be a `preserveStyle: true` option on `roslyn_replace_in_code` that doesn't require the agent to know anything about style explicitly. The tool would:

1. Extract the trivia structure of the surrounding context (the nodes immediately before and after the replacement site)
2. Parse the replacement text
3. Walk the replacement tree and normalize its trivia to match the surrounding patterns — blank line counts, indentation depth, alignment if the surrounding code uses it
4. Apply the result

For genuinely *new* code (insertions rather than replacements), the reference would be the nearest similar node — inserting a new method takes its blank-line pattern from adjacent methods, a new field takes alignment from adjacent fields.

---

## The Hardest Case: Column Alignment

Things like:

```csharp
private readonly string  name    = "";
private readonly int     count   = 0;
private readonly bool    enabled = false;
```

This is where it gets genuinely hard. Alignment trivia isn't just "copy from the original node" — it's a property of the *relationship* between sibling nodes. When you insert or replace one field, all the others may need their spacing adjusted to maintain the alignment. That requires detecting the alignment pattern, recalculating column positions, and rewriting trivia on nodes you didn't otherwise touch.

Roslyn's `SyntaxEditor` and the `Formatter` can handle structural formatting, but column alignment of this kind isn't something `.editorconfig` can express or that `Formatter.Format()` will produce — it's personal style that lives outside any formal style system.

---

## Honest Assessment

`roslyn_get_trivia` as currently designed is better suited as infrastructure than as an agent-facing tool. The path forward is probably:

1. Keep it as the low-level primitive it is (useful for diagnostic/analysis work like the code style checker already in `Test-CodeStyle.ps1`)
2. Build `roslyn_get_style_profile` on top of it as the agent-facing surface
3. Add a `preserveStyle` flag to `roslyn_replace_in_code` that uses trivia analysis internally to normalize replacement text against its context

The rename tools probably don't need this — Roslyn's renamer already does the right thing. The gap is squarely in `roslyn_replace_in_code` when the replacement is structurally similar to the original but not identical.
