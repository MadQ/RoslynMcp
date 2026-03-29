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
  up-to-date). Use INSTEAD OF Read for .cs files. Also works for non-.cs files
  (falls back to disk).
- `roslyn_get_file_outline` — Get the structure of a file (types, members,
  signatures). Use INSTEAD OF reading a file to understand its layout.
- `roslyn_get_line_count` — Get line counts for one or more files. Use INSTEAD
  OF Read + counting lines.

### Discovering Code

- `roslyn_search_files` — Regex search across all files in the project. Use
  INSTEAD OF Grep for code search within a .NET project.
- `roslyn_semantic_search` — Search filtered by semantic context: comments,
  strings, identifiers, XML docs, or code-only. Use when you need to find
  matches in a specific context (e.g., "TODO" in comments only).
- `roslyn_list_files` — List files in the project with glob filtering. Use
  INSTEAD OF Glob for .NET project files.
- `roslyn_list_types` — List all types in the project, optionally filtered by
  namespace. Use to discover available types.

### Navigating Symbols

- `roslyn_find_references` — Find all references to a symbol across the entire
  solution. Use INSTEAD OF Grep for symbol usage search -- it understands
  overloads, namespaces, and cross-project references.
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
- `roslyn_get_type_hierarchy` — Show base types, interfaces, and derived types.
  Grep cannot reliably determine inheritance chains.
- `roslyn_get_usings` — Extract using directives from a file.
- `roslyn_get_project_info` — Get project metadata: target framework, packages,
  project references.

### Editing Code

- `roslyn_replace_in_code` — Syntax-aware find-and-replace. Targets specific
  node kinds (MethodDeclaration, IdentifierName, etc.) so replacements are
  precise. Use INSTEAD OF Edit for C# files when you need structural awareness.
- `roslyn_replace_in_file` — Text-level find-and-replace with regex support.
  Use for non-C# files or when you need regex. Use INSTEAD OF Edit when
  replacing patterns across a file.
- `roslyn_insert_lines` — Insert lines at a specific location (by line number
  or anchor pattern). Use when ADDING new lines rather than replacing existing
  content -- no need to construct surrounding-context patterns.

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
- Search: `roslyn_search_files` / `roslyn_semantic_search` > Grep
- References: `roslyn_find_references` > Grep (semantic, cross-project)
- Types: `roslyn_get_type_members` / `roslyn_get_type_hierarchy` > reading files
- Editing: `roslyn_replace_in_code` (C#) / `roslyn_replace_in_file` (any) > Edit
- Insert: `roslyn_insert_lines` (by line or anchor) > Edit with context patterns
- Rename: `roslyn_preview_rename` + `roslyn_apply_rename` > find-and-replace
- Build: `roslyn_build_project` > NEVER `dotnet build` in terminal
- Diagnostics: `roslyn_get_diagnostics` for fast error checks
```

---

## Subagents and Delegated Tasks

If your AI tool supports spawning subagents (e.g., Claude Code's Agent tool, background workers, or task delegation), those subagents **do not inherit your agent instructions**. They will default to built-in tools unless explicitly told otherwise.

When delegating C# work to a subagent, include this in the prompt:

```
Use roslyn_* MCP tools for ALL C# file operations — do NOT use built-in
Read/Grep/Edit/Glob/Bash tools for C# code:

Reading:     roslyn_get_member_body (single method/property) or roslyn_read_file (whole file)
Structure:   roslyn_get_file_outline (types + signatures, no bodies)
Search:      roslyn_search_files or roslyn_semantic_search (not Grep)
Files:       roslyn_list_files (not Glob)
References:  roslyn_find_references (semantic, cross-project)
Impls:       roslyn_find_implementations (interfaces, overrides)
Definition:  roslyn_get_symbol_definition (file + line + signature)
Types:       roslyn_get_type_members, roslyn_get_type_hierarchy
Editing:     roslyn_replace_in_code (C#) or roslyn_replace_in_file (any file)
Inserting:   roslyn_insert_lines (by line number or anchor pattern)
Renaming:    roslyn_preview_rename + roslyn_apply_rename
Signatures:  roslyn_change_signature + roslyn_apply_signature_change
Building:    roslyn_build_project (NEVER run dotnet build in terminal)
Diagnostics: roslyn_get_diagnostics for fast error checks
```

This is easy to forget — the main agent follows the instructions perfectly, then delegates to a subagent that reverts to grep and file reads. If you notice a subagent using built-in tools on C# files, that's the cause.

**Resource note:** Each subagent spawns its own MCP server process with its own Roslyn workspace (~100MB+ RAM, ~10s load time). Limit parallel subagents on large solutions to avoid resource exhaustion. See [#85](https://github.com/MadQ/RoslynMcp/issues/85) for ongoing work on shared workspaces and subagent specialization.
