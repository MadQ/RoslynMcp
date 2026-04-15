# Roslyn MCP Tool Suggestions

## New Tools

**`roslyn_get_member_body`** ✅ *Shipped (v0.4.0)*
The single highest-value addition for token reduction. Given a method/property name and optionally a containing type, return just that member's source — the declaration line through the closing brace. Right now getting one method from a 600-line file means reading the whole file. This collapses that to 20-30 lines. Roslyn makes it trivial: find the symbol, get its `DeclaringSyntaxReferences`, slice the span.

**`roslyn_find_callers`**
The inverse of `find_references` but more useful. Instead of "where is this symbol mentioned", return "which methods contain a call to this method" — caller name, file, line, and optionally one line of call context. `SymbolFinder.FindCallersAsync` exists exactly for this. Grep can find text references; it can't tell you the containing method name without extra work.

**`roslyn_get_call_graph`** ✅ *Shipped (v0.7.6)*
Given a method, list every method it calls (direct calls only, one level deep). Roslyn's `IOperation` tree makes this precise — you walk `IInvocationOperation` nodes in the method body. Essential for "what does this method depend on?" without reading it. Pair with `find_callers` for full dependency tracing.

**`roslyn_find_unused`**
Find `private` or `internal` symbols with zero references within the project. Dead code detection. `SymbolFinder.FindReferencesAsync` over all private/internal symbols, filter to those with empty `Locations`. High value for refactoring sessions. Token-efficient because the output is just a list of names.

**`roslyn_get_type_dependencies`**
Given a type, return all types it directly references: field types, parameter types, return types, base type, interfaces, generic constraints. Answers "what does this type couple to?" in one call. Useful before extracting or moving a type.

**`roslyn_find_overloads`**
Given a method name and containing type, return all overloads with their full signatures. Currently `find_references` finds only the first symbol by that name. This fills the gap cleanly.

**`roslyn_check_syntax`**
Given a string of C#, parse it and return syntax/semantic errors. The "validate before you write" tool. Pairs naturally with `replace_in_code` — compose the replacement, validate it, then apply. Roslyn can do this in-memory with no file I/O.

---

## Enhancements to Existing Tools

**`roslyn_list_types`** — default `namespaceFilter` to the project's root namespace (derivable from the compilation), or at minimum add a `projectOnly: true` default that excludes referenced assemblies. The current default is a trap.

**`roslyn_find_references`** — when no `containingType` is given and multiple symbols share the name, search all of them and union the results, with a `symbols_searched` field in the response listing which ones were found. Right now it silently picks one.

**`roslyn_get_type_members`** — add `includeInherited: bool` (default false). ✅ *Shipped* Currently only shows declared members; you can't see what a class inherits without walking the hierarchy manually. Roslyn's `GetMembers()` vs base type traversal handles this.

**`roslyn_read_file`** — add a `symbolName` parameter: "give me the lines containing `ParseDocumentation`." Internally does the same work as `get_member_body` but surfaces it as a read operation. Lets you skip the line-number lookup step entirely.

**`roslyn_get_project_info`** — add `directOnly: true` (default true) to filter package references to those directly declared in the `.csproj`. ✅ *Shipped* Transitive packages are rarely what you want.

**`roslyn_get_diagnostics`** — add `severity` filter (`errors`, `warnings`, `all`). ✅ *Shipped* Errors-only is the most common ask and avoids noise.

**`roslyn_semantic_search`** — beyond the duplicate fix, add a `containingKind` filter: search only within method bodies, only within class declarations, only within attributes, etc. ✅ *Shipped* `SyntaxKind` makes this precise. "Find all TODO comments inside method bodies" becomes one call instead of filter-and-check.

**`roslyn_get_symbol_info`** — return structured JSON like every other tool.

---

## Priority Ranking

Ranked by (token reduction × semantic value that grep can't match):

| Priority | Item | Reason |
|----------|------|--------|
| 1 | `roslyn_get_member_body` (new) | Biggest token reduction, used constantly |
| 2 | `roslyn_find_callers` (new) | Semantically impossible with text search alone |
| 3 | `roslyn_get_call_graph` (new) | Same; essential for impact analysis before edits |
| 4 | Fix `roslyn_find_references` ambiguity | Correctness before new features |
| 5 | `roslyn_find_unused` (new) | High value for refactoring, low output cost |
