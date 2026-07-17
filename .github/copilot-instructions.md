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
- Always dogfood the tools — use `roslyn_search_files`, `roslyn_get_type_members`, `roslyn_find_references`, etc.
- For C# edits, strongly prefer `roslyn_replace_in_code` over text-based replacements
- **Performance:** Default to modern zero-allocation patterns (`Span<T>`, `ReadOnlySpan<T>`, `stackalloc`) when equally readable — see AGENTS.md § Performance & Allocation

### ⚠️ Zero-Byte File Check — Required Before Every Commit
A known working-tree rollback issue silently empties `.cs` files on disk while the in-memory Roslyn workspace still shows the correct content. Always verify before committing:
```powershell
Get-ChildItem src\RoslynMcp -Recurse -Filter *.cs | Where-Object { $_.Length -lt 50 } | Select-Object FullName, Length
```
Any result is a red flag. Before reaching for git, check the RoslynMcp backup store — `roslyn_write_file` snapshots files before every write. **Use PowerShell directly, not `roslyn_*` tools** (the workspace is equally stale when a file is 0 bytes on disk):
```powershell
$f = "MyFile.cs"
$dir = Join-Path $env:LOCALAPPDATA "RoslynMcp\backups"
Get-ChildItem $dir -Recurse |
    Where-Object { $_.Name -like "${f}_*.bak" -and $_.Length -gt 0 } |
    Sort-Object LastWriteTime -Descending |
    Select-Object Name, LastWriteTime, Length
```
Backup naming: `{originalFileName}_{unixMs}_{nonce}.pre.bak` (pre-write) or `{originalFileName}_{unixMs}_{nonce}.post.bak` (post-write). Filter `Length -gt 0` and sort descending — some backups may themselves be 0 bytes. **Read and validate the content before copying** — confirm it contains the expected class/type names and is the correct version, not a stale draft. Cross-reference `LastWriteTime` against `git log` to pick the right snapshot. Only after validation: `Copy-Item $best.FullName "path\to\MyFile.cs"`. **After copying, read the restored file with `roslyn_get_file_outline` or `roslyn_get_member_body` and reason explicitly about whether it reflects the correct state for the work in progress** — backups predate the write that caused the zeroing, so in-session edits may be missing and need to be re-applied. If no usable backup exists, fall back to `git checkout <sha> -- path/to/file.cs`. Also run the size check after merges. See `docs/process/WORKING_TREE_ROLLBACK.md` for the full procedure.

### Quick Reference

**All details in [`AGENTS.md`](../AGENTS.md):**
- Project overview and architecture
- All 42 tool descriptions (40 public + 2 debug-only)
- Code style rules (braces, naming, blank lines, etc.)
- MCP protocol patterns
- Roslyn API patterns
- Git and terminal rules
- Testing procedures
- Rename workflow

**Key reminder:** Get this through your thick pirate skull: DOGFOOD the living daylights out of all RoslynMcp tools! 🏴‍☠️
