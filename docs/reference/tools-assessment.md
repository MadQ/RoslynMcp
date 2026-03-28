# Roslyn MCP Tools: Honest Assessment

> Originally written during v0.3.0 evaluation. Updated with fix status as of v0.6.0-alpha.

## Tools That Work Well

**`roslyn_get_type_members`** — genuinely excellent. One call gives full resolved signatures, doc summaries, member kinds. Replaces opening a file and skimming it.

**`roslyn_get_symbol_definition`** — fast and precise. File, line, column, signature, doc. Better than grep because it's semantic — it won't mislead you if the name appears in comments or strings.

**`roslyn_get_type_hierarchy`** — solid. Got all 26 subclasses of `RoslynMcpTool` instantly. ~~The `FindDerivedClassesAsync`/interfaces bug limits it for interfaces.~~ **Fixed in v0.5.0 (#21).**

**`roslyn_get_symbol_info` (at position)** — very useful when you know the line/column. It resolved `GetCompilationAsync` at line 734 to `Task<Compilation?> Project.GetCompilationAsync(CancellationToken)` without reading anything. That's the real Roslyn value proposition.

**`roslyn_get_diagnostics`** — clean and fast. **v0.5.0: errors now sorted before warnings. v0.6.0: single-file queries use SemanticModel for better performance.**

**`roslyn_read_file`** — reads from the in-memory workspace (post-edit, always current), supports line ranges. The `source: "roslyn"` annotation is a nice touch. Direct substitute for `Read` for `.cs` files.

---

## Tools With Serious Problems

**`roslyn_list_types` — broken by default.** Without `namespaceFilter` it dumps every type from every referenced assembly — 829,618 characters. With `namespaceFilter` it works perfectly. The default is a context-window bomb. **Planned fix: v0.7.0 (#29) — default to project namespace.**

~~**`roslyn_semantic_search` — every result is duplicated.**~~ **Fixed in v0.4.0 (#18).** Multi-TFM projects produced one `Project` per target framework with identical source files. Deduplicated by `FilePath`.

**`roslyn_find_references` — misleading without `containingType`.** Without it, `AnySymbolFinder` returns the first symbol it encounters in namespace traversal order. An agent that doesn't specify `containingType` gets a silently incomplete picture. **Planned fix: v0.7.0 (#29) — search all matching symbols.**

**`roslyn_get_project_info` — noisy.** Lists all transitive NuGet packages, not just direct dependencies. **Planned fix: v0.7.0 (#30) — add `directOnly` parameter.**

**`roslyn_get_symbol_info` — inconsistent output format.** Returns pipe-delimited string instead of structured JSON. **Planned fix: v0.7.0 (#30) — return JSON.**

---

## Comparison to Normal Tools

Where Roslyn tools genuinely beat `Read`/`Grep`:
- Cross-file reference finding is a single call instead of grep-then-read-every-file
- `get_type_members` replaces read-file-then-parse-mentally
- `get_symbol_info` resolves an overloaded name at a position — text search can't do that at all
- `get_symbol_definition` navigates to a definition without knowing which file it's in

Where normal tools win:
- Normal tools work on any file type, not just `.cs`
- `Grep` finds all text matches regardless of which overload you meant, which is sometimes what you want
- ~~`Grep` doesn't have the duplicate-result bug~~ (fixed)
- ~~`Read` + `Glob` don't have context-window bombs~~ (mitigated with `namespaceFilter`; default fix planned)

---

## Bottom Line

The design philosophy is right — Roslyn semantics are genuinely better than text search for C# navigation. `get_type_members`, `get_symbol_definition`, `get_symbol_info`, and `get_type_hierarchy` are all things worth reaching for first. ~~Three issues that would reliably break an AI agent:~~ Status as of v0.6.0:

1. `list_types` needs a mandatory or defaulted namespace filter — **planned v0.7.0 (#29)**
2. ~~`semantic_search` duplication~~ — **fixed v0.4.0 (#18)**
3. `find_references` ambiguity — **planned v0.7.0 (#29)**
