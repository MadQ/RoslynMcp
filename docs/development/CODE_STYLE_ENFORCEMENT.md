# Code Style Enforcement

RoslynMcp includes automated style auditing and fixing to ensure all quirky rules from `AGENTS.md § Code Style` are consistently followed.

## Quick Start

```powershell
# Audit all C# files
.\scripts\Test-CodeStyle.ps1

# Audit a single file
.\scripts\Test-CodeStyle.ps1 -FilePath "src\RoslynMcp\Tools\MyTool.cs"

# Auto-fix violations
.\scripts\Test-CodeStyle.ps1 -Fix

# Audit with verbose output
.\scripts\Test-CodeStyle.ps1 -Verbose
```

## Rules Checked

### Auto-Fixable Rules ✅

1. **IndentedBlankLines** (Quirky!)
   - Blank lines inside blocks must be indented to match surrounding scope
   - Example: A blank line inside a `try` block at 3-tab depth should have 3 tabs
   - **Why:** Visual consistency when viewing with visible whitespace

2. **BlankLineAfterOpeningBrace**
   - Multi-statement blocks need a blank line after the opening brace
   - Single-statement blocks are fine without (and shouldn't use braces anyway)

3. **NoSpaceAfterControlFlow**
   - No space after `if`/`foreach`/`while`/`switch` keywords
   - ✅ `if(condition)` 
   - ❌ `if (condition)`

4. **BlankLineBeforeReturn** (Info)
   - Return statements should have a blank line before them
   - Exceptions: after opening brace, consecutive returns

### Manual Fix Required (Info/Warning)

5. **SingleStatementBraces** (Info)
   - Single-statement blocks shouldn't use braces
   - ✅ `if(x) return;`
   - ❌ `if(x) { return; }`

6. **CastSpacing** (Info)
   - Space between cast and operand
   - ✅ `(int) value`
   - ❌ `(int)value`

7. **TodoComments** (Warning)
   - TODO comments must include the actual concern, not just "fix"
   - ✅ `// TODO: How should this handle null values?`
   - ❌ `// TODO: fix`

8. **CommentPronouns** (Info)
   - No personal pronouns (we/I/our/my/us) in code comments
   - Describe code objectively, not subjectively
   - ✅ `// The method processes input asynchronously.`
   - ❌ `// We process input asynchronously here.`

9. **CommentSpacing** (Info)
   - One space after a period in comments, never two
   - ✅ `// First step. Second step.`
   - ❌ `// First step.  Second step.`

10. **SemicolonPlacement** (Info, Experimental)
    - Semicolons on their own line for multi-line expressions
    - Applies to: fluent chains, LINQ, ternaries, arrow bodies
    - **Experimental:** "Just recently started test-driving this one"

11. **ColumnAlignment** (Info)
    - Consecutive assignment groups and field declaration groups should be column-aligned
    - Reports lines where `=` or the identifier column deviates from the group majority
    - Assignment group example (aligned — good):
      ```csharp
      error   = "foo",
      message = "bar",
      hint    = "baz"
      ```
    - Field declaration group example (aligned — good):
      ```csharp
      readonly string? logPath;
      readonly object  writeLock = new();
      ```
    - Not auto-fixable — requires intent to determine the correct column

### Comments That Can't Be Checked (Manual Review)

**The "Obvious Comment" Problem:**

The script **cannot** detect obvious comments (comments that just restate what the code does). These require semantic understanding and should be **deleted during code review**, not "fixed":

```csharp
// BAD: Obvious comment (delete it!)
// Do the thing
DoTheThing();

// GOOD: Explains WHY, not WHAT
// Retry connection because network may be temporarily unavailable
DoTheThing();

// ALSO GOOD: Short label/annotation (no punctuation needed)
// Retry logic
DoTheThing();
```

**Per AGENTS.md:**
> **Comments:** explain *why*, not *what* — after any edit, re-evaluate nearby comments and update or remove stale ones

**AI agents and humans:** When you see obvious comments during code review, **delete them**. Don't add punctuation, don't fix spacing — just remove the noise.

## Integration Points

### For AI Agents (Primary Use Case)

AI agents should run this **before finishing a task** to ensure consistency:

1. After making changes: `.\scripts\Test-CodeStyle.ps1 -Fix`
2. Verify fixes didn't break anything
3. Commit the cleaned-up code

**Why this matters for AI:** Agents can't "see" formatting quirks like indented blank lines. This tool catches them automatically.

### For Human Contributors (Optional)

**This is a development aid, not a gate.** Run it when you want consistency, ignore it when you're experimenting.

```powershell
# Quick cleanup before committing
.\scripts\Test-CodeStyle.ps1 -Fix

# Or just audit to see what's different
.\scripts\Test-CodeStyle.ps1
```

**No pre-commit hooks. No CI/CD enforcement.** Style consistency is valuable, but blocking commits over formatting is counterproductive. The AI agents will clean up violations when they touch the code anyway.

### Editor Integration (Optional)

Some editors can run scripts on save. Configure at your own discretion. The script is fast enough for interactive use (~100ms per file).

## Style Guide Reference

Complete style rules: **[AGENTS.md § Code Style](../../AGENTS.md#code-style)**

### Quick Reference: Quirky Rules

These are the unusual rules that standard linters don't catch:

| Rule | Example | Notes |
|------|---------|-------|
| **Indented blank lines** | Blank line with 3 tabs inside 3-tab block | Maintains visual nesting |
| **No space after control flow** | `if(x)` not `if (x)` | Personal preference |
| **Cast spacing** | `(int) value` | Readability |
| **TODO details** | `// TODO: What about edge case X?` | Forces thinking |

### Why These Rules?

From `AGENTS.md`:

> **Consistency is overrated. Embrace diversity.** *(For everyone—AI and humans)*
> 
> Code should *look* consistent (formatting). Code should *think* rebelliously (design).

The style rules ensure **visual consistency** so humans and AI agents can focus on **design decisions** rather than formatting debates.

**But:** These rules are descriptive, not prescriptive. They describe how the codebase currently looks, not how you **must** format your code. If you find a better way, discuss it and update the rules. The script makes it easy to apply changes consistently once a decision is made.

**AI agents** benefit most from automated enforcement because they can't "see" formatting quirks. Humans can spot indented blank lines at a glance; AI needs to be told explicitly.

## Exit Codes

- `0` = All checks passed
- `1` = Violations found
- `2` = Error during execution

## Adding New Rules

To add a new style check:

1. Add a `Test-RuleName` function in `Test-CodeStyle.ps1`
2. Call it from `Invoke-StyleAudit`
3. (Optional) Add a `Repair-RuleName` function for auto-fix
4. Update this document

## Related

- **[AGENTS.md § Code Style](../../AGENTS.md#code-style)** — Complete style guide
- **[scripts/Fix-BlankLineIndentation.ps1](../../scripts/Fix-BlankLineIndentation.ps1)** — Legacy fixer (now part of Test-CodeStyle.ps1)
- **[scripts/Convert-IndentationToTabs.ps1](../../scripts/Convert-IndentationToTabs.ps1)** — Convert spaces to tabs

---

**Last Updated:** 2026-07-18
