# Roslyn MCP Tools: Honest Assessment

## Tools That Work Well

**`roslyn_get_type_members`** — genuinely excellent. One call gives full resolved signatures, doc summaries, member kinds. Replaces opening a file and skimming it.

**`roslyn_get_symbol_definition`** — fast and precise. File, line, column, signature, doc. Better than grep because it's semantic — it won't mislead you if the name appears in comments or strings.

**`roslyn_get_type_hierarchy`** — solid. Got all 26 subclasses of `RoslynMcpTool` instantly. (The `FindDerivedClassesAsync`/interfaces bug limits it for interfaces, but the tool itself is well designed.)

**`roslyn_get_symbol_info` (at position)** — very useful when you know the line/column. It resolved `GetCompilationAsync` at line 734 to `Task<Compilation?> Project.GetCompilationAsync(CancellationToken)` without reading anything. That's the real Roslyn value proposition.

**`roslyn_get_diagnostics`** — clean and fast.

**`roslyn_read_file`** — reads from the in-memory workspace (post-edit, always current), supports line ranges. The `source: "roslyn"` annotation is a nice touch. Direct substitute for `Read` for `.cs` files.

---

## Tools With Serious Problems

**`roslyn_list_types` — broken by default.** Without `namespaceFilter` it dumps **every type from every referenced assembly** — anonymous types from Roslyn internals, 53 transitive packages, all of it. 829,618 characters. The first entries were `<>f__AnonymousType0<<child>j__TPar, <tok>j__TPar>` from deep inside Roslyn's own guts. With `namespaceFilter: "RoslynMcp"` it works perfectly and returns a clean 44-type list. But the default is essentially a context-window bomb. An AI agent that calls this naively is done — and nothing in the description warns about it.

**`roslyn_semantic_search` — every result is duplicated.** Every single match appears exactly twice. The 18 unique `TryGetCompilation` hits came back as 36. `total_matches: 36`, `returned: 36`. The semantic search for `TODO|FIXME` also returned 4 results for 2 unique matches. This is a consistent, reproducible bug. It makes the `total_matches` count completely unreliable and would confuse any agent trying to reason about coverage.

**`roslyn_find_references` — misleading without `containingType`.** Without it, `AnySymbolFinder` returns the first symbol it encounters in namespace traversal order. Searching for `GetCompilation` returned 1 reference, but that was only for `WorkspaceManager.GetCompilation`. `WorkspaceResolver.GetCompilation` has 2 entirely different call sites. An agent that doesn't know to add `containingType` gets a silently incomplete picture. The tool description doesn't warn about this.

**`roslyn_get_project_info` — noisy.** It lists all 53 transitive NuGet packages, not just direct dependencies. For a real project with 30+ direct deps this becomes hundreds of packages. There's no way to filter for direct vs. transitive.

**`roslyn_get_symbol_info` — inconsistent output format.** Every other tool returns structured JSON. This one returns a pipe-delimited string: `Kind: Method | Name: ... | ContainingType: ... | Type/ReturnType: ...`. An agent parsing tool outputs programmatically would need special-case handling.

---

## Comparison to Normal Tools

Where Roslyn tools genuinely beat `Read`/`Grep`:
- Cross-file reference finding is a single call instead of grep-then-read-every-file
- `get_type_members` replaces read-file-then-parse-mentally
- `get_symbol_info` resolves an overloaded name at a position — text search can't do that at all
- `get_symbol_definition` navigates to a definition without knowing which file it's in

Where normal tools win:
- `Grep` doesn't have the duplicate-result bug
- `Read` + `Glob` don't have context-window bombs
- Normal tools work on any file type, not just `.cs`
- `Grep` finds all text matches regardless of which overload you meant, which is sometimes what you want

---

## Bottom Line

The design philosophy is right — Roslyn semantics are genuinely better than text search for C# navigation. `get_type_members`, `get_symbol_definition`, `get_symbol_info`, and `get_type_hierarchy` are all things worth reaching for first. But there are three issues that would reliably break an AI agent using these tools:

1. `list_types` needs a mandatory or defaulted namespace filter — the current default is a trap
2. `semantic_search` duplication needs fixing before it can be trusted
3. `find_references` needs to either warn about ambiguity or search all symbols with the name (with deduplication)
