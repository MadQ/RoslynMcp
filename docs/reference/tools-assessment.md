# Roslyn MCP Tools: Honest Assessment

> Updated against source as of v0.8.1-beta.  
> Public tool count: **41 shipped tools**. Debug-only tools excluded from the inventory below: `roslyn_respawn`, `roslyn_debug_attach`.

## Public tool inventory (complete)

### Analysis / navigation (24)

- `roslyn_check_drift`
- `roslyn_check_syntax`
- `roslyn_find_callers`
- `roslyn_find_implementations`
- `roslyn_find_overloads`
- `roslyn_find_references`
- `roslyn_find_unused`
- `roslyn_get_call_graph`
- `roslyn_get_diagnostics`
- `roslyn_get_file_outline`
- `roslyn_get_line_count`
- `roslyn_get_member_body`
- `roslyn_get_project_info`
- `roslyn_get_symbol_definition`
- `roslyn_get_symbol_documentation`
- `roslyn_get_symbol_info`
- `roslyn_get_symbols_in_scope`
- `roslyn_get_trivia` *(experimental)*
- `roslyn_get_type_dependencies`
- `roslyn_get_type_hierarchy`
- `roslyn_get_type_members`
- `roslyn_get_usings`
- `roslyn_list_types`
- `roslyn_read_file`

### Search (4)

- `roslyn_find_string_literal`
- `roslyn_list_files`
- `roslyn_search_files`
- `roslyn_semantic_search`

### Editing (5)

- `roslyn_insert_lines`
- `roslyn_local_history`
- `roslyn_replace_in_code`
- `roslyn_replace_in_file`
- `roslyn_write_file`

### Rename / refactoring (4)

- `roslyn_apply_rename`
- `roslyn_apply_signature_change`
- `roslyn_change_signature`
- `roslyn_preview_rename`

### Build / maintenance (4)

- `roslyn_build_project`
- `roslyn_clean_solution`
- `roslyn_info`
- `roslyn_restore_packages`

## Highlighted strengths

**`roslyn_get_type_members`** — still one of the clearest wins. It gives resolved signatures, member kinds, and summaries without forcing a full-file read.

**`roslyn_get_symbol_definition`** — strong everyday navigation tool. Semantic lookup beats text search whenever a name is overloaded or appears in comments/strings.

**`roslyn_get_type_hierarchy`** — useful for understanding inheritance and interface relationships quickly. The earlier derived-type/interface limitation was fixed in v0.5.0.

**`roslyn_get_symbol_info`** — high-value when you already know the exact position and need the resolved symbol, not a guessed text match.

**`roslyn_get_diagnostics`** — practical fast-path tool for checking compiler state without dropping to terminal output parsing.

**`roslyn_read_file`** — essential glue tool. Reading `.cs` content from the Roslyn workspace instead of disk is exactly the right behavior for an MCP server.

**`roslyn_get_line_count`** — simple but useful for deciding whether to read, outline, or page through a file.

**`roslyn_get_trivia`** *(experimental)* — niche, but valid. Its value is trivia-in-context, especially when an agent needs indentation or comment placement instead of plain text.

## Previously serious problems that are now fixed

- `roslyn_list_types` defaulted to an unusable wall of framework types. Fixed in v0.7.0.
- `roslyn_semantic_search` returned duplicate results in multi-TFM projects. Fixed in v0.4.0.
- `roslyn_find_references` was misleading without `containingType`. Fixed in v0.7.0.
- `roslyn_get_project_info` was noisy about transitive packages. Fixed in v0.7.0 with `directOnly`.
- `roslyn_get_symbol_info` had an inconsistent response shape. Fixed in v0.7.0.

## Comparison to normal tools

Where Roslyn tools genuinely beat plain read/grep workflows:
- semantic definition lookup
- cross-file reference finding
- symbol-aware member/type inspection
- in-workspace `.cs` reads after edits

Where normal tools still win:
- non-C# files
- blunt text hunting when semantic disambiguation is unnecessary
- ad hoc repository-wide searches outside the Roslyn workspace

## Bottom line

The core value proposition is still correct: Roslyn-backed semantics are genuinely better than text search for C# navigation and analysis. The tool surface is now broad enough to cover most C# exploration, search, editing, rename/refactoring, and build workflows without leaving the MCP layer.

This file is now **inventory-complete** for the 41 public tools, but the qualitative commentary remains intentionally selective rather than giving every tool a score.
