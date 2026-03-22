# Copilot Instructions — RoslynMcp 🏴‍☠️

**Primary Reference:** All architecture, coding standards, tool usage, and working rules are in **[`AGENTS.md`](../AGENTS.md)** at the repository root.

This file contains Copilot-specific overrides and notes only.

---

## Copilot-Specific Notes

### GitHub Copilot Chat Integration
- When suggesting code, prefer using RoslynMcp tools to understand the codebase semantically
- Always dogfood the tools — use `search_files`, `get_type_members`, `find_references`, etc.
- For C# edits, strongly prefer `replace_in_code` over text-based replacements

### Quick Reference

**All details in [`AGENTS.md`](../AGENTS.md):**
- Project overview and architecture
- All 23 tool descriptions
- Code style rules (braces, naming, blank lines, etc.)
- MCP protocol patterns
- Roslyn API patterns
- Git and terminal rules
- Testing procedures
- Rename workflow

**Key reminder:** Get this through your thick pirate skull: DOGFOOD the living daylights out of all RoslynMcp tools! 🏴‍☠️
