# Copilot Instructions — RoslynMcp 🏴‍☠️

**Primary Reference:** All architecture, coding standards, tool usage, and working rules are in **[`AGENTS.md`](../AGENTS.md)** at the repository root.

This file contains Copilot-specific overrides and notes only.

---

## Copilot-Specific Notes

### CODE STYLE IS MANDATORY (But Rebellion Is Encouraged) 🏴‍☠️

**As an AI agent, you MUST follow the formatting rules in AGENTS.md § Code Style.**

**What's mandatory:** How code *looks* (braces, spacing, naming, blank lines).  
**What's NOT mandatory:** How you *think* (architecture, design, questioning patterns).

**Your role:** 
- ✅ **Format like the repo owner** — braces, spacing, naming conventions
- ✅ **Think like a rebel** — question assumptions, propose better approaches, challenge "best practices"
- ✅ **Innovate freely** — new patterns, better designs, creative solutions
- ✅ **Push back on bad ideas** — including mine! If I'm wrong, say so and explain why
- ✅ **Experiment with formatting?** Add a comment explaining the experiment, then ask for feedback

**TL;DR:** Make code that *looks* consistent but *thinks* rebelliously. Innovation happens in design, not brace placement. Want to try a new formatting pattern? Comment it and ask!

**Key formatting rules:**
- Braces: same line for control flow, new line for methods/classes
- No space after `if`/`foreach`/`while`
- Single-statement blocks: no braces
- Blank lines: after opening braces, before returns, between logical groups
- Modern C#: `Span<T>`, zero-allocation patterns, pattern matching
- Comments: explain *why*, not *what*; update/remove stale comments after edits

**See AGENTS.md § Code Style for complete rules.**

### GitHub Copilot Chat Integration
- When suggesting code, prefer using RoslynMcp tools to understand the codebase semantically
- Always dogfood the tools — use `search_files`, `get_type_members`, `find_references`, etc.
- For C# edits, strongly prefer `replace_in_code` over text-based replacements
- **Performance:** Default to modern zero-allocation patterns (`Span<T>`, `ReadOnlySpan<T>`, `stackalloc`) when equally readable — see AGENTS.md § Performance & Allocation

### Quick Reference

**All details in [`AGENTS.md`](../AGENTS.md):**
- Project overview and architecture
- All 35 tool descriptions
- Code style rules (braces, naming, blank lines, etc.)
- MCP protocol patterns
- Roslyn API patterns
- Git and terminal rules
- Testing procedures
- Rename workflow

**Key reminder:** Get this through your thick pirate skull: DOGFOOD the living daylights out of all RoslynMcp tools! 🏴‍☠️
