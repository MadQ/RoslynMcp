# RoslynMcp Roadmap

## Vision

Make RoslynMcp the tool that serious C# developers actually want their AI agents to use — precise, fast, style-aware, and trustworthy enough that you don't second-guess its output.

---

## Shipped Releases

All milestones through v0.7.8 are complete. Highlights per release:

| Release | Key additions |
|---------|---------------|
| v0.4.0 | Fixed 5 unregistered tools, pagination crash, GetRootPath, SolutionDiff `\r\n`, SemanticSearch duplicates |
| v0.5.0 | Fixed wrong answers across analysis tools, TypeHierarchy for interfaces, rename workflow consistency, ReplaceInCode fallback, SemanticSearch correctness |
| v0.6.0 | Fixed workspace infrastructure (LRU, lock, FSW loop), SolutionDiff O(n²) memory, GetTrivia bounds, DiagnosticsTool single-file scoping |
| v0.7.0 | `roslyn_get_member_body`, hardened defaults for list_types and find_references, filtering parameters across tools, AGENTS.md update |
| v0.7.1 | `roslyn_info`, `roslyn_insert_lines`, workspace mode CLI arg (`--workspace sdk\|vs\|adhoc\|auto`) |
| v0.7.2 | `roslyn_write_file`, `roslyn_local_history`, `BackupStore` (crash-safe backup infrastructure), NDJSON log format with `response_peek` pipeline, structured `roslyn_get_diagnostics` response (#111), tool metadata improvements (#112) |
| v0.7.3 | `BackupStore` gains git branch/commit metadata in snapshots (#122), `RoslynMcpJson` shared serializer options — no `\uXXXX` spam (#123), `ToolResults.cs` normalized to PascalCase C# + snake_case JSON (#124), `ToolErrorResult` abstract base record — replaces `ExtractDetail` switch; `PathErrorResult`/`UnexpectedErrorResult` inherit it (#125), `BuildLiteralRegex` CRLF fix (#126), `ToolScopeAnalyzer` RMCP003/004/005 + code fix provider (#127, #128), `roslyn_get_project_info` MSBuild-derived fields (#117), `roslyn_build_project` MSBuild tail on exit-code failures (#131), retry with exponential backoff on file write contention (#129), `TryServeCachedPage` moved to `ToolScope` (#130), `roslyn_get_diagnostics` success/locked-file fix (#114) |
| v0.7.4 | `FileEncoding` shared BOM-detection helper; BOM fixes in `roslyn_write_file` and `roslyn_replace_in_code`; `ToolScopeAnalyzer` RMCP003 now fires on expression-bodied tool methods; `TryServeCachedPage` gains `[NotNullWhen(true)]`, eliminating 10× CS8603 warnings |
| v0.7.6 | `roslyn_find_callers`, `roslyn_get_call_graph`; write-retry telemetry (#136); FSW reload suppression + let Roslyn save (#140); `roslyn_local_history` double-write fix (#141); `roslyn_find_callers` dedup fix (#143); `roslyn_build_project` false-failure fix; `FileWriter` centralised write entry point (#139); RMCP007/008/009 diagnostics; BOM fixes; tool description improvements (#99) |
| v0.7.8 | Thread-safety hardening, DotnetRunner deadlock fix, BackupStore async, SemanticSearch correctness, self-healing truncation, pre/post backups, TFM context in build diagnostics, rename stale-file fix, MSBuildBootstrap hardening (#145, #151, #153–#159, #161–#163, #165–#166) |
| v0.7.9 | TestHarness split (#167), shutdown fix (#170), TFM context fix in project-level diagnostics (#169); folded into v0.8.0-beta — no separate tag |

---

## Milestones

### v0.4.0-alpha — Fix Crashes and Invisible Tools

The tools that exist must be visible and not crash on valid input. This is the minimum bar.

| # | Type | Title | Scope | Audit refs |
|---|------|-------|-------|------------|
| 1 | bug | Fix 5 unregistered tools | FileOutlineTool, GetSymbolsInScopeTool, GetUsingsTool, GetLineCountTool, SearchFilesTool all missing `[McpServerTool]` attribute — 20% of tools are invisible | #3 |
| 2 | bug | Fix pagination crash when skip >= results | `AsSpan(skip, ...)` throws `ArgumentOutOfRangeException` in FindImplementationsTool, FindReferencesTool, TypeHierarchyTool, TypeMembersTool, FileOutlineTool | #4 |
| 3 | bug | Fix GetRootPath for AdhocWorkspaces | `Path.GetDirectoryName` on a directory returns the parent — all relative paths wrong for adhoc workspaces | #1 |
| 4 | bug | Fix semantic_search duplicate results | Multi-TFM projects produce one `Project` per target framework with identical source files; documents processed twice | — |
| 5 | bug | Fix SolutionDiff \r\n + MSBuildWorkspace comment | Diff output has spurious `\r` on Windows; comment incorrectly claims MSBuildWorkspace auto-watches files | #20, #2 |

**Theme:** Trust. A project where 20% of tools are invisible and pagination crashes on valid input doesn't attract contributors.

---

### v0.5.0-alpha — Fix Wrong Answers

Tools that run without crashing but return incorrect results. Each of these silently misleads agents.

| # | Type | Title | Scope | Audit refs |
|---|------|-------|-------|------------|
| 6 | bug | Fix analysis tool output correctness | FormatModifiers drops `protected internal` / `private protected` (3 tools); DiagnosticsTool sorts warnings before errors; DiagnosticsTool error.ToString() on anonymous object | #5, #21 |
| 7 | bug | Fix TypeHierarchyTool for interfaces | `FindDerivedClassesAsync` returns nothing for interfaces; should use `FindImplementationsAsync` | #9 |
| 8 | bug | Fix Rename workflow solution consistency | Symbol from compilation may not exist in separately-fetched solution; stale solution overwrites post-preview edits; SymbolKey doesn't distinguish overloads | #10, #11, #13 |
| 9 | bug | Fix ReplaceInCodeTool + SimpleNameFinder | `ParseExpression` fallback wrong for statements/directives/using; SimpleNameFinder discards namespace from dotted names | #7, #8 |
| 10 | bug | Fix SemanticSearchTool correctness | Dead comment-skip code (trivia kinds on tokens never match); `SearchInCode` can't match multi-token patterns | #6, #22 |

**Theme:** Correctness. Every tool that claims to return results must return the *right* results.

---

### v0.6.0-alpha — Robustness and Performance

Tools work correctly but have performance pitfalls, missing validation, or resource leaks.

| # | Type | Title | Scope | Audit refs |
|---|------|-------|-------|------------|
| 11 | bug | Fix workspace infrastructure | LRU eviction gap in GetSolution/GetProject/GetWorkspaceInfo; cacheLock held during multi-second workspace loading blocks all tools; `TryApplyChanges` return value ignored; `msbuildRegistered` lacks `volatile` | #14, #17, #19 |
| 12 | bug | Fix SolutionDiff O(n*m) LCS memory | 400 MB allocation for two 10K-line files; consider Myers diff or line-hashing | #15 |
| 13 | bug | Fix tool robustness | GetTriviaTool no bounds checking on startLine/endLine; DiagnosticsTool uses full-project diagnostics for single-file queries; ApprovalStore has no eviction for unconsumed pending operations | #18, #16, #23 |

**Theme:** Production-readiness. No tool should OOM, deadlock, or leak memory under normal use.

---

### v0.7.0-alpha — Make It Genuinely Useful

The highest-value new tool, hardened defaults for existing tools, and updated contributor docs.

| # | Type | Title | Scope | Audit refs |
|---|------|-------|-------|------------|
| 14 | feature | Implement `roslyn_get_member_body` | Returns source of a single method/property/field — the biggest token reduction win | — |
| 15 | enhancement | Harden `roslyn_list_types` and `roslyn_find_references` defaults | list_types: default namespace filter to project types (audit #12); find_references: search all matching symbols when no containingType given | #12 |
| 16 | enhancement | Add filtering/output parameters to existing tools | get_type_members: includeInherited; get_project_info: directOnly; get_diagnostics: severity filter; semantic_search: containingKind; get_symbol_info: structured JSON output | — |
| 17 | docs | Update AGENTS.md | Fix constructor pattern (missing FileLogger); fix Rename pattern (SymbolRenameOptions); incorporate tool gotchas from assessment. Version tracked via `Directory.build.props`. | — |

**Theme:** Quality of life. This is where someone trying RoslynMcp says "oh, this is actually good."

---

### v0.8.0-beta — Public Launch

Distribution infrastructure and pre-launch hardening. First beta release — the "IPO".

| # | Type | Title | Scope | Refs |
|---|------|-------|-------|------|
| — | feature | dotnet tool packaging | `<PackAsTool>true</PackAsTool>`, NuGet CI/CD pipeline, INSTALLATION.md Option A update | #173 |
| — | feature | MCP marketplace listings | smithery.yaml, listings on Smithery / mcp.so / glama.ai, README badges | #174 |
| — | feature | Global BackupStore pruning | Max-age + max-size eviction with run-count guard; configurable via env vars | #172 |
| — | investigation | Audit Roslyn workspace events | Evaluate `DocumentChanged` / `WorkspaceChanged` events for simplification opportunities | #142 |

**Theme:** Ship it. Anyone can install in 30 seconds; AI tool directories surface RoslynMcp to new users.

---

### v0.9.0 — Semantic Analysis

Capabilities that text search fundamentally cannot provide — the tools that justify RoslynMcp's existence.

| # | Type | Title | Scope | Refs |
|---|------|-------|-------|------|
| — | feature | `roslyn_find_unused` | Find unused types, members, and variables via semantic analysis | #33 |
| — | feature | `roslyn_get_type_dependencies` | Return type dependency graph (imports, references, coupling) | #36 |
| — | feature | `roslyn_find_overloads` | List all overloads of a method | #37 |
| — | feature | `roslyn_check_syntax` | Validate arbitrary C# snippet syntax without a full compilation | — |
| — | feature | `roslyn_apply_code_fix` | Apply a Roslyn code fix by diagnostic ID | #86 |
| — | investigation | LogViewer rework | `RoslynMcp.LogViewer` currently a dev-only skeleton; evaluate scope for a proper rework | #118 |

**Theme:** The "wow" release. Capabilities that grep can't match and agents can't fake.

---

### v1.0.0-beta — Style-Aware Editing

Agents that use RoslynMcp don't just understand code — they respect the author's formatting choices.

| # | Type | Title | Scope | Audit refs |
|---|------|-------|-------|------------|
| — | feature | Implement `roslyn_get_style_profile` | StyleSampler helper; trivia-based style inference; returns named style properties | #34 |
| — | feature | Add `preserveStyle` flag to `replace_in_code` | StyleNormalizer helper; contextual trivia normalization during targeted edits | #35 |
| — | feature | Implement `roslyn_preview_style` / `roslyn_apply_style` | Two-phase style normalization; column alignment scoped to per-type bodies | — |
| — | feature | Full file-wide column alignment rebalancing | Cross-type trivia rewriting; the hardest case | — |

**Theme:** Respect. The author's column alignment, blank line patterns, and comment placement survive AI-assisted editing.

---

### v1.0.0 — Stable Release

Everything from alpha and beta, battle-tested.

**Entry criteria:**
- All planned v1.0.0 issues closed
- TestHarness passes on net8.0, net10.0, and net11.0
- README updated with the full tool list and accurate descriptions
- No open bugs marked as affecting correctness

---

## Dependency Graph

```
v0.4.0 (crashes + invisible tools)
  └─► v0.5.0 (wrong answers)
        └─► v0.6.0 (robustness + performance)
              └─► v0.7.0 (get_member_body + enhancements + AGENTS.md)
                    ├─► v0.8.0-beta (dotnet tool + marketplace — public launch)
                    └─► v0.9.0 (semantic analysis tools)
                          └─► v1.0.0-beta (style-aware editing)
                                └─► v1.0.0 (stable)
```

v0.8.0-beta and v0.9.0 are independent — they can be developed in parallel once v0.7.x ships.

---

## Audit Coverage

All 23 items from `docs/plans/code-audit.md` are mapped to issues above. Cross-reference:

| Audit # | Title | Issue # | Milestone |
|---------|-------|---------|-----------|
| 1 | GetRootPath wrong for AdhocWorkspace | 3 | v0.4.0 |
| 2 | MSBuildWorkspace doesn't watch files | 5 | v0.4.0 |
| 3 | 5 unregistered tools | 1 | v0.4.0 |
| 4 | AsSpan pagination crash | 2 | v0.4.0 |
| 5 | FormatModifiers accessibility gaps | 6 | v0.5.0 |
| 6 | SemanticSearch dead comment-skip | 10 | v0.5.0 |
| 7 | ReplaceInCode ParseExpression fallback | 9 | v0.5.0 |
| 8 | SimpleNameFinder namespace qualification | 9 | v0.5.0 |
| 9 | TypeHierarchy FindDerivedClasses for interfaces | 7 | v0.5.0 |
| 10 | PreviewRename symbol/solution mismatch | 8 | v0.5.0 |
| 11 | ApplyRename stale solution overwrites | 8 | v0.5.0 |
| 12 | ListTypes walks all assemblies | 15 | v0.7.0 |
| 13 | SymbolKey doesn't distinguish overloads | 8 | v0.5.0 |
| 14 | cacheLock held during workspace loading | 11 | v0.6.0 |
| 15 | LCS O(n*m) memory | 12 | v0.6.0 |
| 16 | DiagnosticsTool full-project for single-file | 13 | v0.6.0 |
| 17 | TryApplyChanges return ignored | 11 | v0.6.0 |
| 18 | GetTriviaTool no bounds checking | 13 | v0.6.0 |
| 19 | msbuildRegistered lacks volatile | 11 | v0.6.0 |
| 20 | SolutionDiff \r\n splitting | 5 | v0.4.0 |
| 21 | DiagnosticsTool error.ToString() | 6 | v0.5.0 |
| 22 | SemanticSearch token-level matching | 10 | v0.5.0 |
| 23 | ApprovalStore no eviction | 13 | v0.6.0 |
| 24 | FSW feedback loop (TryApplyChanges writes) | 49 | v0.6.0 |

The semantic_search duplicate results (multi-TFM) was discovered during tool testing and is not in the audit — it's issue #18.
The DiagnosticsTool ordering (warnings before errors) was in the original analysis but not the audit — it's folded into issue #20.
The PreviewRenameTool error.ToString() was found during v0.5.0 work — issue #46.
The MSBuild FSW (external changes invisible) was found during v0.5.0 testing — issue #47.
The FSW feedback loop (TryApplyChanges re-triggers FSW) was found during v0.6.0 work — audit item #24, issue #49.

---

## Superseded Documents

| Document | Status |
|----------|--------|
| `roslyn-code-analysis.md` | **Deleted** — superseded by `docs/plans/code-audit.md` |
| `roslyn-mcp-v040-contributions.md` | **Deleted** — superseded by this roadmap |
| `roslyn-mcp-v040-plan.md` | **Moved** to `docs/plans/v040-implementation.md` — implementation details for early items still useful |

## Active Reference Documents

| Document | Contents |
|----------|----------|
| [docs/plans/code-audit.md](docs/plans/code-audit.md) | Source of truth: 23 confirmed bugs with file locations and descriptions |
| [docs/reference/tools-assessment.md](docs/reference/tools-assessment.md) | Honest evaluation of all existing tools — what works, what's broken, comparison to standard tools |
| [docs/plans/tool-suggestions.md](docs/plans/tool-suggestions.md) | Proposed new tools and enhancements, priority-ranked |
| [docs/plans/style-preservation.md](docs/plans/style-preservation.md) | Design discussion: how to preserve author style during edits |
| [docs/plans/style-analysis-plan.md](docs/plans/style-analysis-plan.md) | Implementation plan for style inference and preserveStyle flag |
| [docs/plans/apply-style-plan.md](docs/plans/apply-style-plan.md) | Implementation plan for preview_style / apply_style |

---

## For Contributors

RoslynMcp is designed to be straightforward to extend. Every tool follows the same pattern: inherit from `RoslynMcpTool`, call `TryGetCompilation`, use Roslyn APIs, return structured results. See `AGENTS.md` for project layout, code style, and tool-choice guidance.

**Most accessible to new contributors:**
- **Issue #16** (filtering parameters) — small, well-scoped enhancements to existing tools
- **Issue #19** (supporting analysis tools) — each sub-tool is independent and follows established patterns
- **Issue #6** (output correctness) — small fixes across a few files

**Needs deep Roslyn experience:**
- **Issue #18** (call graph tools) — IOperation tree walking
- **Issues #20–21** (style infrastructure) — trivia manipulation at scale
- **Issues #22–23** (style application) — the hardest correctness guarantees in the project
