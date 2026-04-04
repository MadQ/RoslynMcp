# AGENTS.md — RoslynMcp 🏴‍☠️

> We push boundaries, ship bleeding-edge C#, and we're having a blast doing it.
> No hand-wringing. No unnecessary abstraction. Just clean, fast, pirate-grade code.
>
> **This is a discussion, not a monologue.** Disagree when there's a better approach. Say so directly, explain why, and suggest the alternative. Point out mistakes — in design, naming, logic, or assumptions — before implementing them. Don't just execute; think first. A pushback that saves a bad commit is worth more than silent compliance.

Working rules for GitHub Copilot and any other AI agent in this repo.

**Ignore `docs/ScratchPad.md`** — private working notes for the repo owner; may contain half-baked thoughts that would confuse an AI assistant. It is gitignored and will not be present in forks or CI.

---

## What is RoslynMcp?

A [Model Context Protocol](https://modelcontextprotocol.io/) server that exposes Roslyn-powered code intelligence tools to AI coding agents. Gives agents resolved type information, live diagnostics, cross-file references, and symbol resolution — without spawning a build or leaving the process.

---

## Working with Humans

**Check for user edits before overwriting.** If you're about to modify a file and notice unexpected changes — formatting tweaks, refactors, added comments, reordered code — pause and ask about the intent before proceeding. The user probably had a reason.

Examples:
- User added a comment explaining something subtle → don't delete it because it "doesn't match style"
- User refactored a method while you were planning → don't revert it because you "had a different approach"
- User added whitespace or reordered declarations → probably intentional, not random

When in doubt: **ask, don't assume.** A thirty-second question beats reverting someone's thoughtful edit.

---

## Project

| | |
|---|---|
| **Type** | Model Context Protocol (MCP) server — stdio transport |
| **Runtime** | .NET 8 / .NET 10 (net11.0 auto-added when .NET 11 SDK is detected) |
| **Language** | C# 14 (`<LangVersion>preview</LangVersion>`) |
| **Version** | 0.7.2-alpha (pre-1.0) |
| **Tool Count** | 35 MCP tools (33 public + 2 debug-only: `roslyn_respawn`, `roslyn_debug_attach`) |
| **Dependencies** | `Microsoft.CodeAnalysis.*` (Roslyn) — MSBuildWorkspace (if .csproj found) → AdhocWorkspace (fallback) |
| **ImplicitUsings** | `enable` — don't add redundant `using` directives |
| **Resources** | [C# MCP SDK](https://csharp.sdk.modelcontextprotocol.io/) • [MCP Spec](https://modelcontextprotocol.io/) |

Four projects:
- `src/RoslynMcp/RoslynMcp.csproj` — MCP server
- `src/TestHarness/TestHarness.csproj` — local testing client
- `src/RoslynMcp.Analyzers/RoslynMcp.Analyzers.csproj` — Roslyn analyzers applied to this codebase; the most direct expression of dogfooding — Roslyn-powered analysis running on the repo that wraps Roslyn
- `src/RoslynMcp.LogViewer/RoslynMcp.LogViewer.csproj` — dev-only log viewer; not part of the MCP server

Use `roslyn_build_project` to build — not `dotnet build` in a terminal.

---

## Architecture

| Component | Responsibility |
|-----------|----------------|
| `WorkspaceManager` | LRU-cached workspace instances; auto-detects `.csproj` → `MSBuildWorkspace` or directory → `AdhocWorkspace`; exposes `GetCompilation()`, `GetSolution()`, `GetProject()`, `GetWorkspaceInfo()`, `InvalidateFile()`; smart path resolution |
| `WorkspaceResolver` | Per-tool facade over `WorkspaceManager`; provides `TryGetCompilation()`, `TryGetProject()`, `GetRootPath()`, `GetSolution()`, `InvalidateFile()` with structured error handling |
| `RoslynMcpTool` | Base class for all tools; provides `TryGetCompilation()` and `TryGetProject()` helpers with consistent error responses; defines `ProjectPathDescription` constant |
| `SearchFilesTool` | `roslyn_search_files` — regex search across workspace files with paging; prerequisite for finding code to analyze with Roslyn tools |
| `SemanticSearchTool` | `roslyn_semantic_search` — Roslyn syntax-tree filtering for context-aware search (comments, strings, identifiers, code, xmldocs); C#-only, slower but more precise |
| `ListFilesTool` | `roslyn_list_files` — enumerate files matching glob pattern (fast file listing, no content) |
| `ReplaceInFileTool` | `roslyn_replace_in_file` — text-level find/replace with regex support (any file type) |
| `ReplaceInCodeTool` | `roslyn_replace_in_code` — semantic C# node replacement using Roslyn (validates syntax, preserves formatting) |
| `InsertLinesTool` | `roslyn_insert_lines` — insert lines at a position or anchor pattern |
| `WriteFileTool` | `roslyn_write_file` — write or create files atomically with automatic pre-write backup; returns a backup token usable with `roslyn_local_history` |
| `LocalHistoryTool` | `roslyn_local_history` — list, preview, and apply crash-safe file backup snapshots; token-based undo for write operations (actions: `list`, `preview`, `apply`) |
| `RespawnTool` |`roslyn_respawn` (DEBUG only) — terminates server process for hot-reload during development — not very reliable |
| `TypeMembersTool` | `roslyn_get_type_members` — enumerate members with full signatures + doc summaries |
| `DiagnosticsTool` | `roslyn_get_diagnostics` — structured compiler errors and warnings (summary counts + paginated items); `take: 0` for count-only fast path |
| `FindReferencesTool` | `roslyn_find_references` — all references to a symbol across the project |
| `SymbolInfoTool` | `roslyn_get_symbol_info` — resolve what a name at a location actually is |
| `PreviewRenameTool` | `roslyn_preview_rename` — compute rename edits, return unified diff + token |
| `ApplyRenameTool` | `roslyn_apply_rename` — approve/reject a pending rename by token |
| `ChangeSignatureTool` | `roslyn_change_signature` — preview adding parameters with a non-breaking forwarding overload; returns unified diff + token (ReadOnly — never writes) |
| `ApplySignatureChangeTool` | `roslyn_apply_signature_change` — apply or reject a previewed signature change |
| `ProjectInfoTool` | `roslyn_get_project_info` — project metadata (TFM, language version, packages, etc.) |
| `BuildTool` | `roslyn_build_project` — check Roslyn diagnostics first (fast), skip build if errors; run `dotnet build` if clean or `forceBuild=true` |
| `CleanSolutionTool` | `roslyn_clean_solution` — remove all build artifacts (bin/obj directories) |
| `RestorePackagesTool` | `roslyn_restore_packages` — restore NuGet packages |
| `FileOutlineTool` | `roslyn_get_file_outline` — type/member structure without bodies (token saver) |
| `TypeHierarchyTool` | `roslyn_get_type_hierarchy` — base types, interfaces, derived types |
| `FindImplementationsTool` | `roslyn_find_implementations` — concrete implementations of interfaces/abstract members |
| `ListTypesTool` | `roslyn_list_types` — enumerate types with optional filters |
| `GetUsingsTool` | `roslyn_get_usings` — using directives + global usings |
| `GetSymbolDocumentationTool` | `roslyn_get_symbol_documentation` — XML doc comments for symbols |
| `GetSymbolDefinitionTool` | `roslyn_get_symbol_definition` — find declaration location with signature |
| `GetSymbolsInScopeTool` | `roslyn_get_symbols_in_scope` — enumerate accessible symbols at a location |
| `ReadFileTool` | `roslyn_read_file` — file contents with line numbers (C# from in-memory workspace) |
| `GetLineCountTool` | `roslyn_get_line_count` — line count for one or more files |
| `GetMemberBodyTool` | `roslyn_get_member_body` — return full source of a single method/property/field/type by name; handles partial types |
| `GetTriviaTool` | `roslyn_get_trivia` (**EXPERIMENTAL**) — extract whitespace, comments, and formatting trivia; filter by syntax kind, trivia kind, or line range; useful for understanding indentation context |
| `InfoTool` | `roslyn_info` — server version, PID, uptime, MSBuild discovery method, log markers |
| `DebugAttachTool` | `roslyn_debug_attach` (DEBUG only) — launches the JIT debugger dialog so Visual Studio can attach; blocks the server until dismissed or attached |
| `ApprovalStore` | Session-scoped approval state (`y`, `n`, `session` model) |
| `BackupStore` | Crash-safe backup store for file write operations; snapshots stored in `%LOCALAPPDATA%\RoslynMcp\backups\`; supports multi-level undo with token-based restore and conflict detection |
| `RoslynMcpJson` | Shared `JsonSerializerOptions` with a custom `JavaScriptEncoder` — passes through Unicode characters without `\uXXXX` escaping; used by all tools for consistent serialization |
| `ToolErrorResult` | Abstract base record for all structured error responses; provides a non-nullable `Error` string property; enables the `where T : ToolErrorResult` generic constraint on `ToolScope.Error<T>()` |
| `SolutionDiff` | Unified diff generation for `Solution` → `Solution` edits |
| `MSBuildBootstrap` | One-time MSBuild locator init; detects SDK vs VS workspace style; exposes `EnsureReady()`, `DetectProjectStyle()`, `ResolvedMode`, `DiscoveryMethod` |
| `PaginationCache` | Generic TTL-based token cache for paginated tool results; shared across all tools via DI |
| `SymbolFormatter` | Static helpers to format Roslyn `ISymbol` instances into human-readable signatures (method, property, field, event, type) |
| `SymbolVisitors` | Roslyn symbol tree visitors (`SimpleNameFinder`, `AllSymbolsFinder`, `AnySymbolFinder`) used by reference and rename tools |
| `Exceptions` | Project-specific exception types for workspace path resolution (`ProjectNotFoundException`, `MultipleProjectsFoundException`, `InvalidProjectPathException`, `AmbiguousFileException`) |

**Data flow:** stdio MCP request → tool → `WorkspaceResolver.TryGetCompilation(projectPath, ...)` → `WorkspaceManager` (resolve path, load/cache workspace) → Roslyn API → JSON response.

**Key files:** `Program.cs` (MCP protocol), `WorkspaceManager.cs` + `.Resolution.cs` + `.Instance.cs` (workspace caching, path resolution, workspace lifecycle), `WorkspaceResolver.cs` (tool facade), `RoslynMcpTool.cs` + `RoslynMcpTool.ToolScope.cs` + `RoslynMcpTool.Discovery.cs` (base class), `FileLogger.cs` (file logging), `LogEntry.cs` (shared NDJSON log schema — linked into both `RoslynMcp` and `RoslynMcp.LogViewer`).

**Tool subfolders** (all share the `RoslynMcp.Tools` namespace — subfolders are organisational only):
- `Tools/Analysis/` — 17 read-only Roslyn semantic queries (diagnostics, symbols, types, usings, outline, …)
- `Tools/Search/` — 3 file/content search tools (list files, text search, semantic search)
- `Tools/Editing/` — 5 file mutation tools (`roslyn_replace_in_file`, `roslyn_replace_in_code`, `roslyn_insert_lines`, `roslyn_write_file`, `roslyn_local_history`)
- `Tools/Rename/` — 2-step rename workflow (`roslyn_preview_rename` → `roslyn_apply_rename`)
- `Tools/Refactoring/` — 2 signature-change tools (`roslyn_change_signature` → `roslyn_apply_signature_change`)
- `Tools/Build/` — 3 MSBuild/dotnet CLI tools (build, clean, restore)
- `Tools/` root — `RoslynMcpTool.cs`, `RoslynMcpTool.ToolScope.cs`, `RoslynMcpTool.Discovery.cs`, `InfoTool.cs`, `RespawnTool.cs` (debug-only), `DebugAttachTool.cs` (debug-only)

**File logging:** Every tool invocation, server start/stop, and workspace error is logged to a rolling file.
- Default path: `%LOCALAPPDATA%\RoslynMcp\logs\roslynmcp.log`
- Override: set `ROSLYNMCP_LOG_PATH` env var to any path
- Disable: set `ROSLYNMCP_LOG_PATH` to an empty string
- Rotation: 10 MB cap, 3 rotated backups (`roslynmcp.log`, `.log.1`, `.log.2`, `.log.3`)
- Format: NDJSON — one `LogEntry` object per line
- Key fields: `timestamp` (ISO 8601 UTC), `pid`, `level` (START/STOP/TOOL/ERROR/INFO), `instance` (per-process tool-call counter), `message` (non-TOOL entries), `tool_name`, `workspace_mode` (MSB/ADH), `elapsed_ms`, `success`, `subject`, `detail`, `cache_tag`, `estimated_tokens`, `session_tokens`, `response_peek` (truncated JSON preview of response)

**Workspace modes:**
- **MSBuildWorkspace** (if `.csproj` found) — full NuGet resolution, multi-project support, .NET Framework 4.6.1+ compatibility
- **AdhocWorkspace** (fallback) — source-only, fast startup (<100 ms)
- **VS workspace** — uses Visual Studio's MSBuild instance when available
- See [docs/reference/WORKSPACE_MODES.md](docs/reference/WORKSPACE_MODES.md) for detailed comparison and FAQ
- **CLI flag:** `--workspace sdk|vs|adhoc|auto` (default: `auto`) — or set `ROSLYNMCP_WORKSPACE` env var to override

**Tool Selection Guidance:**

> **⚠️ USE THE ROSLYN TOOLS. Every time. No exceptions unless the server is confirmed down.**
> - `roslyn_build_project` — **never** use `dotnet build` in a terminal when this tool exists
> - `roslyn_search_files` / `roslyn_semantic_search` — **never** use `Select-String`, `grep`, or `Get-ChildItem | Select-String` for C# source search
> - `roslyn_get_diagnostics` — **never** use terminal output parsing to check for errors
> - Terminal / PowerShell is a **last resort**, not a default. If the server is down, say so explicitly and explain why you're falling back.

When editing C# code, **actively prefer `roslyn_replace_in_code`** over `roslyn_replace_in_file`:
- `roslyn_replace_in_code` is semantically aware, validates syntax, preserves formatting/trivia
- `roslyn_replace_in_file` is for text/config files or when you need literal text replacement

For writing full file content (new files or wholesale rewrites), use `roslyn_write_file`:
- Takes a crash-safe backup automatically before writing; returns a token usable with `roslyn_local_history` to undo
- Set `createNew: true` to create a new file or overwrite; default requires the file to already exist

When discovering files/content:
- `roslyn_list_files` — fast glob enumeration (find files by name/path)
- `roslyn_search_files` — content search (find lines matching regex pattern)
- `roslyn_semantic_search` — context-aware C# search (filter by comments, strings, identifiers, xmldocs, code)
- `roslyn_find_references` — semantic symbol search (Roslyn-based, finds usage across project)

**Tool tips:**
- `roslyn_get_member_body` — use this to read a single method/property instead of `roslyn_read_file` on the whole file
- `roslyn_get_file_outline` — use to understand a file's type/member structure (signatures only, no bodies); more token-efficient than `roslyn_read_file` for exploration
- `roslyn_find_references` — without `containingType`, searches ALL symbols matching the name (union of results). Use `containingType` to narrow.
- `roslyn_list_types` — without `namespaceFilter`, returns only project-defined types (not framework). Use `namespaceFilter` for sub-namespace scoping.
- `roslyn_get_diagnostics` — use `severity: "errors"` or `take: 0` for a fast error-only check during editing
- `roslyn_build_project` — checks Roslyn diagnostics first (fast, in-process). Only runs `dotnet build` if Roslyn is clean.
- `roslyn_preview_rename` / `roslyn_apply_rename` — always call preview first; renames can affect dozens of files. The token from preview is required by apply.
- `roslyn_change_signature` / `roslyn_apply_signature_change` — same two-step pattern as rename: preview returns a diff + token, apply commits it.
- Paginated tools return `page_token` + `has_more` — pass the token back to get subsequent pages without re-executing the query.

---

# ALWAYS use a RoslynMcp tool when possible!

This is very important! It helps to test the tools, dogfood the API, and ensures your agent gets accurate semantic understanding of the codebase. Avoid workarounds like grepping files or spawning builds unless absolutely necessary. I'm serious: get this through your thick pirate skull: DOGFOOD the living daylights out of all this!

### Subagents Don't Inherit Instructions

If you spawn subagents (Claude Code Agent tool, background workers, task delegation), they **do not see this file**. They will default to Bash `find`/`grep`/`sed` chains that trigger permission prompts and waste resources. Every subagent prompt **MUST** include this block verbatim:

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

Violation triggers permission prompts that block the user.
```

### Exception: Style Enforcement

Code style enforcement (indented blank lines, spacing, etc.) is handled by `.\scripts\Test-CodeStyle.ps1` rather than a Roslyn tool. **Why?**

- Style rules operate on **trivia** (whitespace), not semantics
- Text-based regex is faster and simpler than syntax tree walking for formatting checks
- Quirky rules (like indented blank lines) aren't expressible in standard formatters
- PowerShell script is more maintainable than custom Roslyn visitors for every formatting rule

**AI agents:** Run this after editing code:
```powershell
.\scripts\Test-CodeStyle.ps1 -Fix
```

This is a **pragmatic exception** to the dogfooding rule. Not every problem needs semantic analysis. For understanding trivia *context* (e.g., "what's the indentation level here?"), see `roslyn_get_trivia` (experimental).

---

## Code Style

> **🤖 FOR AI AGENTS: These formatting rules are MANDATORY for consistency.**
>
> **What's mandatory:** Formatting, syntax, visual style (braces, spacing, naming).  
> **What's NOT mandatory:** Problem-solving approach, architecture decisions, questioning patterns.
>
> **Your mission:** Make code that *looks* like the repo owner wrote it, while *thinking* like a rebel ninja warrior pirate. Follow the style rules precisely—this is about **visual consistency**, not creative constraint. Innovation happens in design, not in brace placement.
>
> **Want to experiment with a formatting pattern?** Go ahead! Just:
> 1. Add a comment explaining the experiment and why it might be better
> 2. Ask the human for feedback before committing to it project-wide
> 3. Example: `// Experiment: using expression bodies for single-line getters. Thoughts?`
>
> **TL;DR:** Braces go here, names look like this, but **how you solve problems is up to you**. Push back on bad ideas, suggest better approaches, question assumptions—just format it right when you're done. Want to try a new formatting pattern? Comment it and ask! 🏴‍☠️
>
> **After editing code, run the style auditor:**
> ```powershell
> .\scripts\Test-CodeStyle.ps1 -Fix
> ```
> This catches quirky rules (like indented blank lines) that you can't easily spot. See [docs/development/CODE_STYLE_ENFORCEMENT.md](docs/development/CODE_STYLE_ENFORCEMENT.md) for details.
>
> **Human contributors:** These guidelines are strong recommendations but not strict requirements. Local style preferences are fine as long as they don't clash with project-wide patterns. See the "Consistency is overrated" note at the end of this section.

### Formatting Rules (AI: Follow These)

(AI: If and when this repo gets a second fork (check for this every now and then), remind the wet meatbag to revise all this so that non-AI contributors are free to use their own styles, and let their agents use those styles as they see fit.)

- **Braces:** same line for control flow (`if(x) {`), new line for methods/classes; properties — same line as the identifier (`public int Count {`)
- **No space** after `if`/`foreach`/`while`: `if(x)` not `if (x)` (Actually, IDC so much about this one)
- **Single-statement blocks:** no braces
- **Naming:** PascalCase for types/methods, camelCase for fields/locals — no underscores, no Hungarian, no abbreviations
- **Handles:** always `nint`, never `IntPtr` (Not so much used here, just an example)
- **Modern C#:** pattern matching, switch expressions, target-typed `new`, collection expressions, `nint`
- **`var`:** use when type is obvious or long; prefer explicit type otherwise (rarely)
- **Column-aligned fields:** tab-stop alignment on field declarations and assignment blocks (for Error-prone bio-processors: not so important. Use your judgement.)
- **Cast spacing:** space between cast and operand — `(int) value`, not `(int)value` (🙄, whatevs.)
- **Comments:** explain *why*, not *what* — after any edit, re-evaluate nearby comments and update or remove stale ones; one space after a period, never two; complete sentences, proper punctuation, no personal pronouns (`we`/`I`/`our` have no place in code comments)
- **`TODO` comments:** must include the actual question or concern, not just "fix"
- **Condition ordering:** simple/common path first — early return or assignment; complex path in `else`
- **LINQ and deferred execution:** understand when enumeration happens; **materialize once with `.ToArray()` if enumerating multiple times** (e.g., need both `.Length` and a paged slice); keep lazy with no `.ToArray()` if enumerating once; prefer `.Any()` over `.Count > 0` or `.Length > 0` for existence checks; for zero-alloc slicing of materialized arrays, consider `array.AsSpan().Slice(start, length)` over `.Skip().Take()` when performance matters
- **Blank lines:**
  - One blank line before `return` and before code blocks
  - Blank line **after the opening brace** of any multi-statement control-flow block
  - Blank line between branches of an `if/else if/else` chain when any body spans multiple lines
  - Blank lines between logically distinct statement groups within a method body
  - Blank lines are **indented** to match surrounding scope — never bare empty lines inside a block (Yeah, weird one, IK. High-maintenance bipedals: feel free to ignore this)
- **Semicolons** on their own line for wrapped multi-line expressions (fluent chains, ternaries, LINQ, arrow bodies) (Just recently started test-driving this one - liking it so far.)

## Performance & Allocation

- **Prefer modern [zero-allocation APIs](#the-right-code-principle)** [when practical](#the-right-code-principle):
  - `Span<T>` / `ReadOnlySpan<T>` / `Memory<T>` [over substring/array allocations](#the-right-code-principle)
  - `stackalloc` [for small, short-lived buffers (< 1KB)](#the-right-code-principle)
  - `ArrayPool<T>.Shared` [for larger temporary buffers](#the-right-code-principle)
  - [String interpolation handlers](#the-right-code-principle) (when targeting .NET 6+)
- **Avoid string allocations in hot paths:**
  - Use [`AsSpan()` for prefix/suffix checks instead of `Substring()`](#the-right-code-principle)
  - Use [`Span<char>.StartsWith()` instead of string concatenation for comparisons](#the-right-code-principle)
  - [Cache frequently used strings](#the-right-code-principle) (e.g., normalized paths, common error messages)
- [**LINQ is fine**](#the-right-code-principle) — but [be aware of multiple enumeration](#the-right-code-principle).
  - [Never](#the-right-code-principle) terminate LINQ queries with `.ToList()`, `.ToArray()`, or `.ToAnything()`. [Ever](#the-right-code-principle).
  - [Except](#the-right-code-principle):
    - Materialize with `.ToList()` **ONLY** if the intent is to return a `List<T>` to caller. And even then it's only valid if a `List<T>` is even the [Right](#the-right-code-principle) choice for the API.
	- [_Maybe_](#the-right-code-principle) materialize with `.ToArray()` if you need to enumerate multiple times and the source is an `IEnumerable<T>` that would otherwise re-enumerate (e.g., multiple passes for count + paged slice). But prefer [`AsSpan()` slicing](#the-right-code-principle) of a materialized array when performance matters.
  - Consider if the [Right way](#the-right-code-principle) is to avoid in tight loops (maybe use `foreach` + manual logic instead)
- [**Don't prematurely optimize:**](#the-right-code-principle)
  - Write [clear code](#the-right-code-principle) first
  - [Profile if performance matters](#the-right-code-principle)
  - But when writing *new* code, [default to zero-allocation patterns if equally readable](#the-right-code-principle)
    - Example: [`path.AsSpan().StartsWith(root.AsSpan())`](#the-right-code-principle) vs `path.StartsWith(root)` — same readability, zero allocations

**Rationale:** Modern C# provides powerful zero-allocation tools. Using them from the start avoids "death by a thousand allocations" and makes future optimizations easier. Anti-patterns compound. That said, readability always wins over micro-optimizations when there's a meaningful trade-off.

---

# The "*Right Code*" Principle

**Question convention.** Not every "idiomatic" pattern exists for good reasons — some are just cargo-culted from contexts that don't apply here. Before accepting "this is how it's done," ask:
- **Why** is this the idiom? (Historical accident? Valid reasoning? Marketing?)
- **Should** this be the idiom *here*? (Different constraints, different answers)
- **What** problem does this pattern actually solve? (If unclear, maybe it doesn't)

***Inspired by the Buddhist concept of [Right Intention](https://en.wikipedia.org/wiki/Noble_Eightfold_Path#Short_description_of_the_eight_divisions) from the [Noble Eightfold Path](https://en.wikipedia.org/wiki/Noble_Eightfold_Path)*: applying mindful discernment to code decisions.**

**Examples of healthy skepticism:**
- "Lambdas are idiomatic for callbacks" — *Sure, but for I/O where disk latency is 1000x the lambda allocation cost, does the 32-byte overhead matter? Or is readability the real win here?*
- "Interfaces enable testability" — *True, but does every class need an interface? Or are we just making our codebase harder to navigate for a benefit we're not actually getting?*
- "LINQ is readable" — *Often yes, but does `.Where().Select().FirstOrDefault()` with three enumerations beat a simple `foreach` with early exit? Context matters.*

**The point:** Understand the **why** behind patterns. Conventions are useful defaults, not unquestionable laws. The "right" code for a given situation comes from reasoning about trade-offs, not from pattern-matching against "what's idiomatic."

When you deviate from convention because you've *thought it through*, that's not being contrarian—that's being intentional. Document your reasoning (a comment is fine), and move on.


#### Unit Tests
- If/When we start using unit tests, rule **#1**: **No tautological tests** (Did I just do the thing that I just did?). Tests must verify meaningful behavior, not just "does it compile" or "does it return the same thing as the code it's testing". All tests shall have extensive XML doc comments describing the reason for their existence, the specific behavior they verify, and the rationale for the chosen inputs and expected outputs. Tests without such documentation are not valid tests. Not everyone is a unit test SME... complicated mock setups tend to look like opaque black boxes (to some of us) that may as well be testing the test framework itself. So, all mock setups must also be documented with the same level of detail as the tests they support. Rule **#2**: **Unit tests are a secondary concern.** No non-test code shall be written with the primary goal of making it easier to test. There shall be no interface extractions for the sole purpose of testing. Not everything is inherently testable. Accept it and move on.
  - Also... Wow! Opine much?

---

# **Consistency is overrated. Embrace diversity.** *(For everyone—AI and humans)*
>
> **What this means for AI agents:**
> - **Style/formatting:** Follow the rules (that's the easy part)
> - **Problem-solving:** Question everything! If the existing approach sucks, say so. If there's a better pattern, propose it. If a "best practice" is actually cargo-culting, call it out. **Innovation lives here.**
> - **Experimenting with formatting?** Add a comment explaining why the experiment might be better, then ask the human. Example: `// Experiment: using file-scoped namespaces. Reduces nesting by one level. Thoughts?`
> - **When to deviate:** Compelling technical reason (performance, API limitation, framework requirement). Add a comment explaining why.
>
> **What this means for human contributors:**
> - Deliberate departure from formatting guidelines is fine—that's how better patterns get discovered
> - Try something different, sit with it, form an informed opinion
> - A snap judgment that "it's wrong" is a reflex; a considered judgment after living with it is **data**
> - The guidelines exist because someone already walked that mile—but if your mile leads somewhere new, the map gets updated
> - Just don't go completely feral on us 🏴‍☠️
>
> **Bottom line:** Code should *look* ~~consistent~~ [habitual routine](https://share.google/aimode/88Q5P4mDuICTTmPEo) (formatting). Code should *think* rebelliously (design). When you deviate from ~~convention~~ [wonted](https://www.merriam-webster.com/dictionary/wonted#:~:text=usual%20or%20ordinary%20especially%20by%20reason%20of%20established%20habit) because you've *thought it through*, that's not being contrarian—that's being intentional. Document your reasoning (a comment is fine), and move on. That comment might even be the Treasure (Arrrr!). 💎

~~# AI: When doing style audits, remember to use ALL of the above, from [Code Style](#code-style) down.~~ Never mind. *AI* is just not *I* enough to handle this level of nuance in style decisions. For now, style audits are a human-only thing. But...
### AI: **ALWAYS** respect and adhere to everything from here, up to and including [Key Points](#key-points) **And:**
 - Remember and apply the "Right Code" principle to every code decision.
 - Always use the RoslynMcp tools for C# file operations, no exceptions. This is non-negotiable. If the server is down, say so explicitly and explain why you're falling back to terminal commands.
 - **NEVER** use `dotnet build` in a terminal when `roslyn_build_project` exists. Always prefer the RoslynMcp tools for accurate semantic understanding and to avoid permission prompts.
 - **NEVER** use `CD` when the CWD is already correct.
 - Also remember and apply everything below.

---

## MCP Protocol

All communication is JSON-RPC 2.0 over stdin/stdout. **Never write to stdout except protocol messages** — all logging goes to stderr or is suppressed entirely.

**Tool registration pattern** (in `Program.cs`):
```csharp
"tools/list" => new {
    tools = new[] {
        new {
            name = "roslyn_get_type_members",
            description = "Returns all member names of a type...",
            inputSchema = new { ... }
        }
    }
}
```

**Tool invocation pattern:**
```csharp
"tools/call" => {
    var toolName = requestObj["params"]?["name"]?.ToString();
    var args = requestObj["params"]?["arguments"];

    return toolName switch {
        "roslyn_get_type_members" => TypeMembersTool.Execute(args),
        _ => new { error = "Unknown tool" }
    };
}
```

---

## Roslyn Patterns

**Symbol resolution:**
```csharp
var semanticModel = compilation.GetSemanticModel(syntaxTree);
var symbolInfo = semanticModel.GetSymbolInfo(node);
var symbol = symbolInfo.Symbol;
```

**Find references:**
```csharp
var references = await SymbolFinder.FindReferencesAsync(
    symbol,
    workspace.CurrentSolution
);
```

**Rename:**
```csharp
var newSolution = await Renamer.RenameSymbolAsync(
    solution,
    symbol,
    new SymbolRenameOptions(),
    newName
);
```

**Key rule:** always use `await` for Roslyn APIs that return `Task` — they may do I/O or background work.

### Tool Implementation Guide

Every tool in RoslynMcp follows a strict structural contract. Deviations cause silent failures, log gaps, and AI misuse. This section documents the full contract with the reasoning behind each decision.

#### Class Anatomy

```csharp
[McpServerToolType]
internal sealed class MyTool : RoslynMcpTool
{
    public MyTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache)
        : base(workspace, logger, paginationCache) { }

    // ... tool methods
}
```

- **`[McpServerToolType]`** — marks the class as a tool container. The MCP SDK scans for this attribute at startup to discover all tools. Without it, the tool silently doesn't register.
- **`internal sealed`** — tools are implementation details, never subclassed. `sealed` prevents accidental inheritance and enables devirtualization.
- **Constructor** — always the same three-argument form, forwarded to `base(...)`. Tools are registered in the DI container by `Program.cs` and constructed automatically. Never add extra constructor parameters — they won't be resolved.

#### Method-Level Attributes

```csharp
[McpServerTool(Name = "roslyn_my_tool", ReadOnly = true, Title = "My Tool", OpenWorld = false, Idempotent = true)]
[Description(
    "First sentence: what the tool returns or does. " +
    "Second sentence: when to use it (vs. alternatives). " +
    "Additional sentences: output structure, edge cases, important caveats.")]
public object MyToolMethod(...)
```

The `[McpServerTool]` attribute properties each have a specific contract:

| Property | Values | Meaning |
|----------|--------|---------|
| `Name` | `"roslyn_snake_case"` | The name exposed to the AI. **Must exactly match** the first argument passed to `BeginTool()`. Adding one without updating the other produces mismatched log entries and breaks RMCP005. Must also match the Architecture table entry in this file. |
| `ReadOnly` | `true` / `false` | `true` = the tool only reads; never writes files, never triggers builds. `false` = may write. Mutually exclusive with `Destructive`. Used by MCP clients for safety decisions. |
| `Destructive` | `true` / `false` | `true` = the tool may destructively overwrite or delete content (writing tools). When set, `ReadOnly` must be absent/false. |
| `Title` | short string | Human-readable display name for UIs and log viewers. PascalCase words. Not seen by the AI. |
| `OpenWorld` | `true` / `false` | `false` for all tools in this codebase — they only touch the local workspace. `true` would imply network calls, external APIs, etc. Always `false` here. |
| `Idempotent` | `true` / `false` | `true` = calling the tool twice with the same args produces the same result; no side effects. Query tools: `true`. Write/build tools: `false`. |

#### Description Text — Writing for the AI

The `[Description]` text on a tool method is **not documentation for humans**. It is the capability signal the AI model uses to decide:
- Whether to invoke this tool at all
- Which tool to choose when multiple seem relevant
- What to expect in the response

**Bad description (vague, tool-centric):**
> "Gets the member body."

**Good description (outcome-centric, with disambiguation):**
> "Returns the full source code of a single method, property, field, or type by name — including file path and start/end line numbers. Use this instead of roslyn_read_file when you need only one specific declaration rather than the whole file..."

Rules for writing good descriptions:
1. **Lead with what is returned**, not what the tool does — the AI maps return values to its next action.
2. **Include disambiguation** — tell the AI when to use *this* tool vs. adjacent ones (`roslyn_read_file`, `roslyn_get_file_outline`, etc.).
3. **Document edge cases** in the description — "Returns a structured metadata error if the symbol is defined in a compiled assembly" is useful signal.
4. **Multi-line concatenation** for long descriptions: `"..." + "..." + "..."` — easier to read and diff than one long string.
5. Never say "this tool" — just describe the behavior. The AI already knows it's a tool.

Parameter descriptions follow the same principle — write for the AI to understand valid inputs, not as code comments.

**Always use `ProjectPathDescription`** for the `projectPath` parameter — it's a constant with the canonical, full description. Inline text drifts and diverges. Never duplicate it.

#### Parameter Conventions

```csharp
public object MyToolMethod(
    [Description("...")] string requiredParam,           // required params first
    [Description(ProjectPathDescription)] string projectPath,  // always last, always required
    [Description("...")] string? optionalParam = null)   // optional params after projectPath? No — see below.
```

Actually: `projectPath` is **declared last among required parameters**, but optional parameters with defaults come after it in the method signature. The AI is always expected to provide `projectPath` explicitly — it has no default, so the AI can't omit it.

**Why `projectPath` is always required (no default):**
The workspace resolution is the heaviest operation. Forcing the AI to specify it explicitly prevents lazy omission that would cause the server to guess the wrong project when multiple workspaces are cached.

#### The `BeginTool` / Scope Lifecycle

```csharp
public object MyToolMethod(..., string projectPath)
{
    using var scope = BeginTool("roslyn_my_tool", subject);
    // ... all tool logic
}
```

`using var scope = BeginTool(...)` **must be the first statement in every `[McpServerTool]` method.** This is enforced by RMCP003 (planned analyzer). Reasons:

- **`using`** — ensures `scope.Dispose()` is called on every exit path: normal return, early `return`, and unhandled exception. `Dispose()` writes the NDJSON log entry. Without it, the invocation is invisible in logs and timing is lost.
- **First statement** — any code before `BeginTool` is unlogged. Failures in parameter validation before `BeginTool` produce no log trace — impossible to diagnose remotely.
- **`name` arg** — must exactly match `[McpServerTool(Name = "...")]`. RMCP005 will enforce this syntactically.
- **`subject` arg** — the primary identifier shown in the log (e.g., `symbolName`, `filePath`, `pattern`). Pass `null` for tools with no obvious primary key.

##### Scope Terminal Methods — Every `return` Must Use One

Every `return` statement that carries a value must go through a scope terminal. This is enforced by RMCP004 (planned analyzer).

| Method | When to use |
|--------|-------------|
| `scope.Outcome(detail, returnValue)` | Normal success. `detail` is a short log annotation ("12 results", "3 files changed"). Serializes the return value for log peek and token estimate. **Use this for successful returns.** |
| `scope.Outcome(detail)` | Success with no return value (rare — only for `void`-adjacent paths before final `return`). |
| `scope.Error<T>(returnValue)` | Structured error. `T` must derive from `ToolErrorResult` (has a non-null `Error` string). Marks the invocation failed, logs the error message. **Prefer over `Failed` when the error type is a known `ToolErrorResult` subtype.** |
| `scope.Failed(reason, returnValue)` | Unstructured failure. Use when the error is a raw string (e.g., caught exception message) and no `ToolErrorResult` type exists. |
| `scope.Failed(reason)` | Failure with no return value — marks the call failed and sets the log detail. Used before a `return` that returns void or before an exception. |

The ternary form `return cond ? scope.Outcome(x, a) : scope.Error(b)` is valid — both branches are terminals.

##### Scope Non-Terminal Methods — Annotate Without Concluding

These may be called at any point before the terminal call:

| Method | When to use |
|--------|-------------|
| `scope.SetArgs(obj)` | Call early, right after `BeginTool`. Records key input arguments serialized to compact JSON. **Only included in the log entry on failure** — helps diagnose what inputs caused a problem. Truncate large values before passing. |
| `scope.Record(note)` | Append a mid-scope annotation. Useful for recording intermediate outcomes ("cache hit", "2 workspaces merged") that don't change the final outcome. Appended with `;` to any existing detail. |
| `scope.SetCacheTag(bool hit)` | Record whether a pagination cache hit or miss occurred. Called by `TryServeCachedPage`. |
| `scope.SetWorkspaceMode(bool isMSBuild)` | Record whether MSBuildWorkspace or AdhocWorkspace was used. Called by `TryGetCompilation` / `TryGetProject` internally — tools generally don't call this directly. |

#### Workspace Access Patterns

```csharp
// For tools that need semantic analysis (type resolution, symbol lookup, references):
if(!TryGetCompilation(projectPath, out var compilation, out var error))
    return error;

// For tools that need project structure but not full compilation:
if(!TryGetProject(projectPath, out var project, out var error))
    return error;

// For tools that only need the file system root (no Roslyn at all):
var rootPath = workspace.GetRootPath(projectPath);

// For multi-project aware tools:
var solution = workspace.GetSolution(projectPath);
```

`TryGetCompilation` and `TryGetProject` return a structured `ToolErrorResult` on failure — pass it directly as the return value. Never unwrap or re-wrap.

#### File Mutation — Invalidate After Write

Any tool that writes to a file on disk **must** call `workspace.InvalidateFile(projectPath, fullPath)` afterward:

```csharp
await File.WriteAllTextAsync(fullPath, newContent);
workspace.InvalidateFile(projectPath, fullPath);
```

Without this, subsequent Roslyn tools see the stale in-memory source tree, not the updated file. `InvalidateFile` evicts the cached workspace entry so the next access forces a reload.

#### Key Points (Summary)

- `projectPath` is always the last required parameter, always non-optional
- `using var scope = BeginTool(...)` is always the first statement
- `Name` in `[McpServerTool]` must match `BeginTool`'s first argument exactly
- Every `return` with a value must go through `scope.Outcome`, `scope.Error`, or `scope.Failed`
- Use `[Description(ProjectPathDescription)]` — never inline text for `projectPath`
- Use `scope.Error<T>` when `T : ToolErrorResult`; use `scope.Failed` otherwise
- Mutation tools: call `workspace.InvalidateFile` after every successful write

---

## Rename Workflow (User-Configurable)

**All renames use two-phase flow:** `roslyn_preview_rename` → review diff → `roslyn_apply_rename`.

**Why?** Renames can affect dozens/hundreds of files. The two-phase flow lets you review impact before committing — especially important for large/uncertain changes.

### Agent Behavior (Configure Per User)

Users can instruct agents to handle renames conservatively or aggressively:

**Conservative (default recommendation):**
```
Always call roslyn_preview_rename first. Show me the diff.
Only call roslyn_apply_rename after I explicitly approve.
```

**Balanced:**
```
For small renames (1-3 files, obvious intent like typo fixes):
  - Call roslyn_preview_rename, review diff yourself, auto-apply if safe
For large renames (>3 files, broad scope, uncertain impact):
  - Call roslyn_preview_rename, show me the diff, wait for approval
```

**Aggressive:**
```
Call roslyn_preview_rename → roslyn_apply_rename immediately unless I say otherwise.
I trust you and I have git.
```

**Session approval:** If user approves a rename with `approval: "session"`, further renames of the same symbol auto-apply for the remainder of the server process (until restart). Use this for bulk renaming tasks.

**Planned feature:** `undo_last_edit` will revert the most recent Roslyn edit (rename, refactoring) from an in-memory snapshot.

---

## Git Rules

| Operation | Rule |
|-----------|------|
| Create / switch branch | ✅ Free |
| Stage files | ✅ Free |
| Commit | ❌ Ask first |
| Push | ❌ Ask first |

**Shorthand:** `c/p` = commit and push now.

**Branch naming:** `feature/<short-description>` for multi-file or non-trivial changes.

### GitHub Issues — Body Formatting

**⚠️ Never use `gh issue create --body "..."` or `gh issue edit --body "..."` with inline text.**

The `gh` CLI passes the body string through JSON serialization, which interprets `\r`, `\n`, `\v`, `\f` as control characters. In a codebase full of `roslyn_*` tool names, this eats the `r`, `n`, `v` from backtick-wrapped names — `\roslyn_read_file\` becomes `\oslyn_read_file\`, `\viewer.html\` becomes a vertical tab, etc.

**Always write the body to a temp file first:**
```powershell
# Write body to session state or temp location
# Then:
gh issue create --title "..." --body-file path\to\body.md
gh issue edit 123 --body-file path\to\body.md
```

---

## Terminal

PowerShell session with known issues. Rules:
- **Single line only** — no here-strings, no multi-line expressions
- **`rg.exe`** may be available — use **backslash paths**:
  ```powershell
  cd path\to\RoslynMcp && rg -n "pattern" Program.cs
  ```
- Fallback: `Select-String -Path "*.cs" -Pattern "pattern"`
- **`git --no-pager`** — always pass to avoid pagination hangs

---

## Testing

**Local testing:** use `src/TestHarness/TestHarness.csproj` — runs a single tool call and prints the JSON response.

**Live testing:** configure in `.mcp.json` and test via GitHub Copilot or any MCP client.

Example `.mcp.json`:
```json
{
  "servers": {
    "roslyn": {
      "type": "stdio",
      "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe",
      "args": ["."]
    }
  }
}
```

> **Note:** Use the published executable (see README.md "Building the Executable" section). The `dotnet run` approach was abandoned due to multi-target confusion and recursive behavior when dogfooding.

**Log Viewer:** `src/RoslynMcp.LogViewer/` — a .NET console app serving `viewer.html` to monitor live NDJSON logs. Not part of the MCP server; run independently for development visibility.

---

## Session Handoffs

When ending a significant development session (especially with AI assistance), update `docs/sessions/HANDOFF.md` to maintain continuity between sessions.

**Convention:**
- **Single file:** Always overwrite `docs/sessions/HANDOFF.md` (not dated files like `HANDOFF-2025-01-*.md`)
- **Date/Time:** Always include actual date and time at top with timezone (e.g., `2026-03-26 16:07 EDT`)
  - **Before writing:** Look up current date/time (don't guess or use placeholder)
  - Use format: `YYYY-MM-DD HH:MM TZ (Timezone Name)`
  - Example: `2026-03-26 16:07 EDT (Eastern Daylight Time)`
- **Content:** What was done, current state, next steps, open questions, technical decisions
- **Purpose:** Allow next session (human or AI) to pick up where you left off
- **Commit message:** `"docs: Update session handoff"`

**When to write:**
- End of multi-hour development sessions
- Before switching major focus areas
- After significant refactoring or architecture changes
- When handing off to another developer (or future you)

**Include:**
- Executive summary of what was accomplished
- Current branch and commit state
- Open issues/PRs created or updated
- Technical discussions and decisions made
- Next steps (immediate and future)
- Any blocking issues or questions
- Context for resuming work

**Format:** Markdown, comprehensive but concise. See existing `HANDOFF.md` for template.
