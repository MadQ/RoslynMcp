# Agent Instructions for RoslynMcp

Copy the section below into your project's `CLAUDE.md`, `AGENTS.md`, `.github/copilot-instructions.md`, or equivalent agent instruction file. This ensures your AI agent uses RoslynMcp tools instead of falling back to grep, file reads, and text edits.

If your agent still reaches for a built-in tool, ask it *why* it chose that tool instead of the roslyn equivalent. The answer usually reveals a gap in the instructions -- update them with stronger wording. If a tool description is unclear or missing a use case, [open an issue](https://github.com/MadQ/RoslynMcp/issues) or submit a PR.

---

## Copy-Paste Instructions

```markdown
## RoslynMcp Tool Preferences

ALWAYS prefer roslyn_* MCP tools over built-in tools when working with C# code.
These tools use the Roslyn compiler for semantic understanding -- they are more
accurate than grep, Read, or Edit for C# projects. Use them FIRST; fall back to
built-in tools only if a roslyn tool fails.

### Reading Code

- `roslyn_get_member_body` — Read a single method, property, or type body.
  Use this INSTEAD OF reading the entire file. Returns only the code you need.
- `roslyn_read_file` — Read a file from the Roslyn workspace (in-memory, always
  up-to-date for `.cs` files). Use INSTEAD OF Read for .cs files. For non-.cs
  files, it reads from disk.
- `roslyn_get_file_outline` — Get the structure of a file (types, members,
  signatures). Use INSTEAD OF reading a file to understand its layout.
- `roslyn_get_line_count` — Get line counts for one or more files. Use INSTEAD
  OF Read + counting lines.
- `roslyn_get_trivia` (**EXPERIMENTAL**) — Inspect whitespace, blank lines,
  comment placement, and indentation trivia in a C# file. Use when you need to
  understand the formatting context at a specific location before inserting code.

### Discovering Code

- `roslyn_search_files` — Regex search across all files in the project. Use
  INSTEAD OF Grep for code search within a .NET project.
- `roslyn_semantic_search` — Search filtered by semantic context: comments,
  strings, identifiers, XML docs, or code-only. Use when you need to find
  matches in a specific context (e.g., "TODO" in comments only).
- `roslyn_find_string_literal` — Search C# string literals by pattern, returning
  both the raw source text and the decoded value (quotes stripped, escape
  sequences resolved). Supports glob matching (`useGlob: true`) and decoded
  value matching (e.g., search for a tab character, not `\\t`). Use INSTEAD OF
  `roslyn_semantic_search` `context:"strings"` when you need glob patterns,
  decoded-value matching, or the separate `text`/`value` fields per match.
- `roslyn_list_files` — List files in the project with glob filtering. Use
  INSTEAD OF Glob for .NET project files.
- `roslyn_list_types` — List all types in the project, optionally filtered by
  namespace. Use to discover available types.

### Navigating Symbols

- `roslyn_find_references` — Find all references to a symbol across the entire
  solution. Use INSTEAD OF Grep for symbol usage search -- it understands
  overloads, namespaces, and cross-project references.
- `roslyn_find_callers` — Find all methods that call a named symbol. The inverse
  of `roslyn_find_references`. Semantically impossible with text search alone.
  Filter by `isDirect` to exclude interface dispatch or delegate calls.
- `roslyn_find_unused` — Find private/internal source symbols with zero direct
  static references. Use before dead-code cleanup; attributed symbols and
  inheritance/interface-dispatched members are conservatively excluded.
- `roslyn_get_call_graph` — Find all methods invoked within a named method body.
  Answers "what does this method depend on?" by walking the Roslyn IOperation
  tree — finds actual invocations, not text patterns. Pair with
  `roslyn_find_callers` to trace the full call chain in both directions.
- `roslyn_find_implementations` — Find implementations of an interface or
  overrides of a virtual/abstract method. Grep cannot do this.
- `roslyn_get_symbol_definition` — Jump to a symbol's declaration. Returns file,
  line, and the declaration source.
- `roslyn_get_symbol_info` — Resolve the symbol at a specific file/line/column.
  Returns type, kind, containing type, and declaration location.
- `roslyn_get_symbol_documentation` — Get XML documentation for a symbol.
- `roslyn_get_symbols_in_scope` — List all symbols visible at a specific
  location. Useful for understanding what's available in a method body.

### Understanding Types

- `roslyn_get_type_members` — List all members of a type with full signatures.
  Use INSTEAD OF reading the file and scanning for members.
- `roslyn_find_overloads` — List all overloads for a method on a containing
  type. Use before editing, calling, renaming, or changing a method that may
  have overloads.
- `roslyn_get_type_dependencies` — List direct type dependencies from a type
  declaration and member signatures. Use before extracting, moving, or
  refactoring a type.
- `roslyn_get_type_hierarchy` — Show base types, interfaces, and derived types.
  Grep cannot reliably determine inheritance chains.
- `roslyn_get_usings` — Extract using directives from a file.
- `roslyn_get_project_info` — Get project metadata: target framework, packages,
  project references.

### Editing Code

- `roslyn_check_syntax` — Validate a C# snippet for syntax (and optionally
  semantic) errors without writing to disk. Use as a pre-flight check BEFORE
  calling `roslyn_replace_in_code` or `roslyn_write_file` to catch mistakes
  early without a write round-trip. Set `includeSemantics: true` to validate
  against project-defined types and all referenced assemblies.
- `roslyn_replace_in_code` — Syntax-aware find-and-replace. Targets specific
  node kinds (MethodDeclaration, IdentifierName, etc.) so replacements are
  precise. Use INSTEAD OF Edit for C# files when you need structural awareness.
- `roslyn_replace_in_file` — Text-level find-and-replace with regex support.
  Use for non-C# files or when you need regex. Use INSTEAD OF Edit when
  replacing patterns across a file.
- `roslyn_insert_lines` — Insert lines at a specific location (by line number
  or anchor pattern). Use when ADDING new lines rather than replacing existing
  content -- no need to construct surrounding-context patterns.
- `roslyn_write_file` — Write or create files atomically with automatic
  crash-safe backup snapshots. Use for wholesale file rewrites or creating new files.
  Returns a backup token usable with `roslyn_local_history` to undo.
- `roslyn_local_history` — List, preview, and apply crash-safe file backup
  snapshots created automatically before destructive writes. Use to undo destructive writes.

### Refactoring

- `roslyn_preview_rename` — Generate a diff showing what a semantic rename would
  change across the entire solution. Always preview before applying.
- `roslyn_apply_rename` — Apply a previously previewed rename. Writes changes to
  disk across all affected files.
- `roslyn_change_signature` — Add parameters to a method with automatic
  forwarding overload generation. Preview before applying.
- `roslyn_apply_signature_change` — Apply a previously previewed signature
  change.

### Building and Diagnostics

- `roslyn_build_project` — Smart build: checks Roslyn diagnostics first, only
  invokes MSBuild if clean. Use INSTEAD OF running `dotnet build` in a terminal.
  NEVER run `dotnet build` directly.
- `roslyn_get_diagnostics` — Get compiler errors and warnings without building.
  Use `severity: "errors"` for fast error-only checks during editing.
- `roslyn_restore_packages` — Run `dotnet restore`. Use when NuGet packages need
  updating.
- `roslyn_clean_solution` — Run `dotnet clean`. Use when build artifacts need
  clearing.

### Diagnostics & Info

- `roslyn_info` — Server version, PID, uptime, MSBuild discovery method, and
  log markers. Use to verify the server is running and check its configuration.
```

---

## Compact Version

If the full instructions are too long for your agent's context, use this shorter version:

```markdown
## RoslynMcp

ALWAYS use roslyn_* tools FIRST for C# code. They use the Roslyn compiler and
are more accurate than grep/Read/Edit.

- Reading: `roslyn_get_member_body` (single method) > `roslyn_read_file` > Read
- Structure: `roslyn_get_file_outline` > reading the whole file
- Search: `roslyn_search_files` / `roslyn_semantic_search` / `roslyn_find_string_literal` (string literals) > Grep
- References: `roslyn_find_references` > Grep (semantic, cross-project)
- Callers: `roslyn_find_callers` (who calls X?) + `roslyn_get_call_graph` (what does X call?)
- Types: `roslyn_get_type_members` / `roslyn_get_type_hierarchy` > reading files
- Syntax check: `roslyn_check_syntax` (pre-flight before writes, optional semantic validation)
- Editing: `roslyn_replace_in_code` (C#) / `roslyn_replace_in_file` (any) > Edit
- Insert: `roslyn_insert_lines` (by line or anchor) > Edit with context patterns
- Write/Undo: `roslyn_write_file` (create/rewrite) + `roslyn_local_history` (undo)
- Rename: `roslyn_preview_rename` + `roslyn_apply_rename` > find-and-replace
- Build: `roslyn_build_project` > NEVER `dotnet build` in terminal
- Diagnostics: `roslyn_get_diagnostics` for fast error checks
- Info: `roslyn_info` for server version, PID, uptime, MSBuild discovery
```

---

## Subagents and Delegated Tasks

If your AI tool supports spawning subagents (e.g., Claude Code's Agent tool, background workers, or task delegation), those subagents **do not inherit your agent instructions**. They will default to Bash `find`/`grep`/`sed` chains that trigger permission prompts and waste resources.

When delegating C# work to a subagent, include this block **verbatim** in the prompt:

```
MANDATORY TOOL CONSTRAINTS — do NOT violate these:
- You MUST use roslyn_* MCP tools for ALL C# file operations.
- Do NOT use Bash find, grep, cat, sed, awk, xargs, or wc on .cs files.
- Do NOT use cd — the CWD is already correct.
- Do NOT use the Read tool for .cs files — use roslyn_read_file instead.
- Do NOT use the Grep tool for .cs files — use roslyn_search_files instead.
- Do NOT use the Glob tool — use roslyn_list_files instead.

Specific alternatives for common tasks:
- Count tools:    roslyn_search_files with pattern 'Name = "roslyn_'
- Read .cs file:  roslyn_read_file (not Read, not cat)
- Search code:    roslyn_search_files or roslyn_semantic_search (not Grep, not grep)
- File structure: roslyn_get_file_outline (not Read on the whole file)
- List files:     roslyn_list_files (not Glob, not find)
- Read method:    roslyn_get_member_body (not Read on the whole file)
- Find usages:    roslyn_find_references (not Grep)
- Edit C#:        roslyn_replace_in_code or roslyn_replace_in_file (not Edit)
- Insert lines:   roslyn_insert_lines (not Edit)
- Build:          roslyn_build_project (NEVER dotnet build in terminal)
- Diagnostics:    roslyn_get_diagnostics
- Server info:    roslyn_info (version, PID, uptime, MSBuild discovery)

Violation triggers permission prompts that block the user.
```

This is easy to forget — the main agent follows the instructions perfectly, then delegates to a subagent that reverts to grep and file reads. If you notice a subagent using built-in tools on C# files, that's the cause.

**Resource note:** Each subagent spawns its own MCP server process with its own Roslyn workspace (~100MB+ RAM, ~10s load time). Limit parallel subagents on large solutions to avoid resource exhaustion. See [#85](https://github.com/MadQ/RoslynMcp/issues/85) for ongoing work on shared workspaces and subagent specialization.
