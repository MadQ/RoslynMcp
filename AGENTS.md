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
| **Runtime** | .NET 10 (net11.0 auto-added when .NET 11 SDK is detected) |
| **Language** | C# 14 (`<LangVersion>preview</LangVersion>`) |
| **Version** | 0.8.1-beta (pre-1.0) |
| **Tool Count** | 49 MCP tools (48 public + 1 debug-only: `roslyn_debug_attach`) |
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
| `WorkspaceManager` | LRU-cached workspace instances; auto-detects `.csproj` → `MSBuildWorkspace` or directory → `AdhocWorkspace`; loads outside its cache lock (#277), one load per workspace at a time — a call for a workspace whose load is in progress waits for it (`pendingLoads`, keyed by the cache key `PlanLoad` decides up front, #377); exposes `GetCompilation()`, `GetSolution()`, `GetProject()`, `GetWorkspaceInfo()`, `InvalidateFile()`; smart path resolution |
| `WorkspaceResolver` | Per-tool facade over `WorkspaceManager`; exposes `GetCompilation()`, `GetProject()`, `GetRootPath()`, `GetSolution()`, `GetWorkspaceInfo()`, `InvalidateFile()`; throws on path resolution failure (caught and structured by `RoslynMcpTool.TryGetCompilation`/`TryGetProject`) |
| `RoslynMcpTool` | Base class for all tools; provides `TryGetCompilation()` and `TryGetProject()` helpers with consistent error responses; defines `ProjectPathDescription` constant |
| `SearchFilesTool` | `roslyn_search_files` — regex search across workspace files with paging; prerequisite for finding code to analyze with Roslyn tools |
| `SemanticSearchTool` | `roslyn_semantic_search` — Roslyn syntax-tree filtering for context-aware search (comments, strings, identifiers, code, xmldocs); C#-only, slower but more precise |
| `FindStringLiteralTool` | `roslyn_find_string_literal` — string-literal-only search; matches against decoded `Token.ValueText` by default (escape sequences resolved, quotes stripped); `mode: "glob"` for `*`/`?` glob; `matchRaw: true` to match raw source text instead; returns both `text` (raw) and `value` (decoded) per result; covers all string forms including raw and UTF-8 variants |
| `GlobMatcher` | Filename-only glob matching (`*`, `**`, `?`, `{a,b}`) extracted from `SemanticSearchTool`; the `filePattern` of `roslyn_search_files`, `roslyn_semantic_search` and `roslyn_find_string_literal`. `TryCreateMatcher` ORs the expansions of a brace pattern |
| `GlobBraces` | Brace expansion for every glob the server matches (#294) — neither `FileSystemGlobbing.Matcher` nor `GlobMatcher` has it, so a pattern is expanded first: `*.{cs,csproj}` → `*.cs`, `*.csproj`. Groups nest and multiply; a brace with no match or no comma at its own level is literal (a file named `{x}.txt` can still be named); no escape character, so a pattern that held a group also keeps itself as one more alternative, matched literally (`Gen{A,B}.cs` finds `GenA.cs`, `GenB.cs` and a file named `Gen{A,B}.cs`). `GlobMatcher` joins the expansions into one regex; `roslyn_list_files` adds one include each. The pattern comes from an agent, so it is bounded before any work: longer than 4,096 characters, more than 256 distinct expansions, or more than 4M characters of expansion work, is an error, not a partial match. Expansion goes one group level at a time without recursion (a recursive expander overflowed the stack, and so killed the server, on a 100 KB pattern), and each search for the next group is one pass with a stack of open braces (rescanning from every unmatched `{` stalled a call for seconds) |
| `IgnoreRules` | The directories the server stays out of, per workspace root, in three tiers: never a compilation input (`node_modules`, `.git`, `.vs`, `packages`, plus the configured `--ignore` / `ROSLYNMCP_IGNORE` / `"ignore"` names) — safe for the watcher to skip; plus `bin`/`obj` for discovery and reload classification; plus the root `.gitignore` (plain directory names and root-anchored paths only — globs are skipped; a negation removes the entries it re-includes, or all of them when it holds a glob) for listing and file lookup. `.gitignore` never drives the watcher or document discovery: ignored directories often hold generated sources |
| `WorkspaceWalker` | The one recursive directory walk (`FileSystemEnumerable` with a per-directory prune predicate): never enters a pruned directory, never follows a directory link, skips unreadable directories, and stops at depth 32. Replaces `Directory.EnumerateFiles(…, AllDirectories)`, which cannot skip a directory (#309) |
| `WatchPlanner` / `WorkspaceWatchSet` | File watching for one workspace. The planner splits the root into watch roots around never-input directories (non-recursive on the way down to one, recursive everywhere else) so `node_modules` and `.git` are never watched — on Linux a recursive watcher costs one inotify watch per directory. Over 32 roots it falls back to project directories plus their ancestors, then to a single recursive root watcher. The watch set starts the watchers on a background thread, adopts directories created later, and logs `Watch — N/M watch root(s) live (<shape>)` |
| `ListFilesTool` | `roslyn_list_files` — enumerate files matching glob pattern (fast file listing, no content); skips ignored directories (see `IgnoreRules`) unless the pattern names one literally — in a brace pattern, any expansion naming it counts (`{bin,docs}/**`); one `Matcher.AddInclude` per expansion (`GlobBraces`) |
| `ReplaceInFileTool` | `roslyn_replace_in_file` — text-level find/replace with regex support (any file type) |
| `ReplaceInCodeTool` | `roslyn_replace_in_code` — semantic C# node replacement using Roslyn (validates syntax, preserves formatting). Nodes are selected by `nodeKind`, then narrowed by `textPattern` and by `line`/`column` of the node's start, which pick one of several similar nodes. The new node takes the old one's blank lines and first-line indentation; a comment in the replacement text replaces the comment attached to the node (the lines directly above it up to a blank line, or the one ending its last line), a replacement without one keeps them. Only the node and its comments are written: a comment that cannot be applied safely, a preprocessor directive in the replacement text, and anything below its node are named in `_caution` with the node's line, never dropped unseen. The syntax check parses the text that will be written, not the assembled tree (#416) |
| `InsertLinesTool` | `roslyn_insert_lines` — insert lines at a position or anchor pattern |
| `WriteFileTool` | `roslyn_write_file` — write or create files atomically with automatic pre- and post-write backup snapshots; returns a backup token usable with `roslyn_local_history` |
| `LocalHistoryTool` | `roslyn_local_history` — list, preview, and apply crash-safe file backup snapshots; token-based undo for write operations (actions: `list`, `preview`, `apply`) |
| `RespawnTool` | `roslyn_respawn` — ends the server process gracefully with exit code 0 (`IHostApplicationLifetime.StopApplication` after the response has flushed, `Process.Kill` after 10 s as the fallback for a shutdown that hangs — not `Environment.Exit`, which waits for that same shutdown) so the client starts a fresh one: the remedy the diagnostics, drift, build and reload hints name for a workspace that loaded badly and no reload repaired, and the way to load a new build of the server. Ships in every build since #406 — a hint naming a tool the build does not have is a dead end for an agent. The restart is the client's: Claude Code, Copilot CLI and VS Code restart a stdio server that exits; where the client does not, the user restarts it. Never a hook suggestion |
| `FileWriter` | Centralised file write entry point; `WriteWithRetryAsync` and `WriteWithRetry` wrap all disk writes with exponential-backoff retry on `IOException`, structured `INFO`/`ERROR` log entries per attempt, and are accessible from non-tool types (`BackupStore`, `SolutionDiff`). `ReplaceAtomicAsync` / `ReplaceAtomic` is the one way to replace a whole file (temp file + rename): it keeps the existing file's Unix permissions, writes through a symbolic link instead of replacing it (`AtomicReplace.FollowLink`), can create a new file owner-only (`AtomicReplace.PrivateWhenNew`), and removes its temp file on failure — never hand-roll temp-file-and-rename (#322) |
| `FileReader` | Centralised whole-file read entry point, the counterpart of `FileWriter` (#230): `ReadAllText`/`ReadAllBytes` (+`Async`), `ReadAllLines`, `ReadLines` and a streaming `OpenRead`, each with a byte limit — `FileReader.DocumentLimit` (64 MB: tool content, backups, project/solution files, agent configs) or `FileReader.ConfigLimit` (1 MB: git metadata and other small files) — plus `OpenUnbounded` for the reads that are unbounded on purpose (below) and `LoadXml` for project and solution files (`DocumentLimit`). The limit counts bytes actually read, not `FileInfo.Length` (a device reports 0), and a file past it throws `FileTooLargeException : IOException`; tools map it to `file_too_large` with `RoslynMcpTool.ReadFailure`. Decodes like `File.ReadAllText` (BOM-detected, else UTF-8). Follows links on purpose; a FIFO still blocks in `open()`, which is accepted. Every read opens with `FileReader.ReadSharing` — readers and deletes, not writers (#349): `Delete` lets the server's own atomic replace rename over a file a read holds open (on Windows the rename fails otherwise), and keeping writers out means no read sees a file half-saved in place, which the watcher flush, the edit tools and backups would write back. The one exception is the line count of `roslyn_get_line_count`, which opens with `FileReader.OpenForCount` and so `FileReader.ObservingSharing` (readers, writers and deletes), so an active log can be counted; no other read takes that sharing, least of all one whose content comes out — `roslyn_read_file`'s output is copied into edits, so it is written back as surely as a workspace document, a backup or an edit's own read. A read refused by a writer gets a hint saying so (`ReadFailure`). Use `FileReader` for every whole-file read of a file a tool is pointed at and of the server's own config, metadata and backups — never `File.ReadAll*`/`File.ReadLines`/`File.OpenRead`/`XDocument.Load(path)` in the server. Not bounded, on purpose: text documents Roslyn tracks (it holds them whole regardless, so a limit here would save nothing), a line count, which streams through a fixed buffer and keeps nothing (a link to a device that never ends keeps it counting, as it always has), and a hash, which streams (`FileReader.OpenUnbounded` + `SHA256.HashData`) and holds no copy — a limit there would turn a file past it into a skipped check |
| `SecurityBoundary` | Per-workspace file access check (`IsPathAllowed`): a path must be under a trusted root (workspace, solution directory, referenced projects). A link above the root is ignored — it is where the user put the workspace (#330). A link inside the root is followed when it resolves to somewhere inside a trusted root and refused when it leads out (#322). A caller that goes on to write through a link asks `ResolveAllowed` instead: it returns the fully resolved path that was approved, so the check and the write cannot come apart (#334). File walks still do not descend into linked directories (`WorkspaceWalker`, #314). `SecurityBoundary.PathComparer` is the one path comparison for the whole server, every path-keyed set included: on Windows and macOS a difference in case alone is put to the file system, once per directory (a Windows directory can be made case-sensitive, a macOS volume formatted so), and on Linux it is ordinal (#391). Never compare paths with a `StringComparison` of your own; a prefix check is `SecurityBoundary.StartsWithPath` |
| `PhysicalFileIndex` | Private nested type of `WorkspaceInstance` (`WorkspaceManager.Instance.PhysicalFileIndex.cs`). The tracked documents of a workspace that are another file's name — a symbolic link, or a path under a linked directory — keyed by the file they lead to. Built at load and on every reload; lets the write paths and the watcher flush refresh every document of a changed file, whichever name the change was made under (#341). Empty for a workspace without links. See `docs/development/WORKSPACE_SYNC.md` |
| `TypeMembersTool` |`roslyn_get_type_members` — enumerate members with full signatures + doc summaries |
| `FindOverloadsTool` | `roslyn_find_overloads` — returns all ordinary method overloads declared on a containing type, with full signatures including generic/default/ref/out parameter details |
| `TypeDependenciesTool` | `roslyn_get_type_dependencies` — returns direct type dependencies from a type declaration and direct member signatures; includes base type, direct interfaces, fields, properties, events, parameters, returns, generic constraints, operators, and conversions |
| `DiagnosticsTool` | `roslyn_get_diagnostics` — structured compiler errors and warnings (summary counts + paginated items) for one project, or every project of a solution path; `take: 0` for count-only fast path — returns `items: null` (not `[]`) to distinguish "not requested" from "no results". `includeAnalyzers` runs the project's analyzer references **and Roslyn's IDE analyzers** (`CodeActionHost.AnalyzersFor`, #389) — the IDE0xxx findings are mostly info or hidden, so `severity: "info"` / `"hidden"` show them (the default stays errors and warnings), and a note says so when they were left out |
| `CheckDriftTool` | `roslyn_check_drift` — workspace health probe on three axes: source drift (on-disk changes the FileSystemWatcher missed), load health (`workspace_healthy: false` when a project loaded with zero metadata references, or with a `WorkspaceFailed` failure naming its project file — which can leave its compilation options at defaults, #300; a project with no references and no `project.assets.json` is reported as never restored, `projects_not_restored`, whose remedy is `roslyn_restore_packages`, not a reload, #399), and `reload_pending: true` when a change is waiting on a reload that has not completed. Drift checks existing documents: missing files count as drift, and files newer than the sync clock plus tolerance are compared with workspace text, so owned edits and timestamp-only touches are clean. `reload_pending` is the only axis that can account for a **newly added** file. Use when symbol results look stale **or** when `roslyn_get_diagnostics` reports errors you don't believe |
| `FindReferencesTool` | `roslyn_find_references` — all references to a symbol across the project |
| `FindUnusedTool` | `roslyn_find_unused` — private, internal, and effectively internal source symbols with zero direct static references; conservative refactoring guidance with confidence and reason metadata |
| `FindCallersTool` | `roslyn_find_callers` — all methods that call a named symbol; filter by `isDirect` to exclude interface/delegate dispatch |
| `GetCallGraphTool` | `roslyn_get_call_graph` — all methods invoked within a method body; walks the Roslyn IOperation tree for precise semantic results |
| `AnalyzeRegionTool` | `roslyn_analyze_region` — `AnalyzeDataFlow` and `AnalyzeControlFlow` over a line range. The private nested `RegionResolver` turns the range into statements from one list (Roslyn throws on two) or a single expression, widening a partly covered statement and reporting the lines analyzed. `throws` is the tool's own walk, because `ExitPoints` omits `throw`; an expression region has no `control_flow`, because Roslyn has no expression overload |
| `SymbolInfoTool` | `roslyn_get_symbol_info` — resolve what a name at a location actually is, and what the compiler knows about the enclosing expression: flow-adjusted and converted type, nullable flow state, conversion, constant value, failed overload candidates; `includeOverloads` adds the method group. Type facts come from the enclosing expression: a method name climbs to its call, and the last member of a `?.` chain to the whole conditional access, while an earlier link and a delegate in call position stay themselves. Empty fields are omitted |
| `PreviewRenameTool` | `roslyn_preview_rename` — compute rename edits, return unified diff + token; builds a `PreviewFileState` snapshot and `WorkspaceBinding` so `roslyn_apply_rename` can commit via `PhysicalSolutionApplier` |
| `ApplyRenameTool` | `roslyn_apply_rename` — approve/reject a pending rename by token; applies via `PhysicalSolutionApplier` (crash-safe, TOCTOU-guarded); the type-matching file-rename step (including the case-only two-step move) runs as a separate post-apply phase after content writes are confirmed |
| `PreviewCodeFixTool` | `roslyn_preview_code_fix` — preview the code fixes `CodeActionHost` offers for a single diagnostic: Roslyn's own fixers, the project's analyzer-package fixers, the bundled ones and configured providers (#389); returns available action choices or unified diff + token for a selected fix; a fix may create or delete `.cs` files inside the workspace (`CodeActionChangeValidator`); a failing provider is recorded, the others' fixes still offered (ReadOnly — never writes) |
| `ApplyCodeFixTool` | `roslyn_apply_code_fix` — apply or reject a previewed code fix using the approval token from `roslyn_preview_code_fix`; applies via `PhysicalSolutionApplier` through `CodeActionApplyTool`; the result names `createdFiles` / `deletedFiles` |
| `ListRefactoringsTool` | `roslyn_list_refactorings` — the code refactorings Roslyn offers at a caret or selection (Move type to file, Extract method, Introduce local, Invert if, …), each with an index for the preview, its title, provider type name, export name and source; dialog-driven actions (`CodeActionWithOptions`) and actions tagged `RequiresNonDocumentChange` are listed under `unavailable` with the reason, as Roslyn's LSP host filters them; a provider that throws is a `providerFailures` entry, never a failed call; `providers` counts the sources behind the list and the providers each loaded (before de-duplication and the deny-list — not actions), and names only the sources with a problem (one that failed to load, or one the workspace mode cannot have), each with its first problem and a count of further ones — the whole per-source report is `roslyn_info`'s (the sources the server has loaded and kept, for all projects), since repeating it cost about 1,300 tokens a call (#414). An empty list is a normal answer (#389) |
| `PreviewRefactoringTool` | `roslyn_preview_refactoring` — computes one listed action (same location + `actionIndex`) into a unified diff + token; `CodeActionOperations` takes the one `ApplyChangesOperation` (navigation operations ignored), `CodeActionChangeValidator` allows text changes plus `.cs` creates and deletes inside the boundary and refuses renames (by path or document name), project changes and generated/linked/external files; echoes the chosen action so an index mismatch is visible; `createdFiles` / `deletedFiles` name what the apply will do (ReadOnly — never writes) |
| `ApplyRefactoringTool` | `roslyn_apply_refactoring` — apply or reject a previewed refactoring by token (`ApprovalWorkflow.Refactoring`), through `CodeActionApplyTool`; a created `.cs` flags a reload; a `_caution` when the project lists its Compile items explicitly (the file is not compiled until added) |
| `MefComposition` | The one MEF composition of the process (#389): `MefHostServices.DefaultAssemblies` (Workspaces, CSharp.Workspaces, Features, CSharp.Features — the VisualBasic pair is absent and skipped) composed once into a `CompositionHost`; both `MSBuildWorkspace.Create` call sites and `new AdhocWorkspace` take its `HostServices`, and `CodeActionHost` reads the provider exports from the same container, so providers and the workspace services they run against share their `[Shared]` singletons as in Visual Studio. Costs ~200 ms at the first workspace load, logged as `Composition — N assemblies composed in X ms` |
| `CodeActionHost` | Every `CodeRefactoringProvider` and `CodeFixProvider` the server may run, by source (`ProviderSourceKind`): built-in (the composition's exports, read through a System.Composition metadata view of the export attributes), bundled (`RoslynMcp.Analyzers`, always on), project (reflected from each `AnalyzerReference` through `ShadowCopyAnalyzerLoader.Instance` — the same load as the analyzers; cached per path + mtime, except a set whose load failed on the file system, which is loaded again at the next call, #412; every reference of the project is registered with the loader as a dependency location before the first is loaded, `AnalyzerLoading.RegisterDependencies`, because a reference may depend on a sibling listed after it, #421; types that do not load are a problem only when an exported provider is among them — `ExportedProviderTypes` reads the exported type names from metadata and each missing one is loaded by name; unavailable in an `AdhocWorkspace`, which the report says), and configured (`CodeActionSettings.Providers`: a NuGet package from the local cache via `NuGetPackageLocator`, or an assembly path; one `ShadowCopyAnalyzerLoader` per entry with the sibling assemblies as dependency locations, never Roslyn or the framework; a committed file's path loads only when trusted). `ProvidersFor(document)` filters by export metadata (language, document kind, extension), dedupes by type name (the bundled analyzers are also a project reference of this repo), and applies the deny-list: a built-in one (ChangeSignature, RenameTracking, ExtractInterface, ExtractClass, PullMemberUp, MoveStaticMembers, MoveToNamespace) plus `deny` entries, matched by export name, type name or type-name stem (`InvertIf` for `CSharpInvertIfCodeRefactoringProvider` — export names are whatever the author typed). `GetRefactoringsAsync` / `GetFixesAsync` run each provider in its own try/catch, flatten nested actions to leaves (`Parent › Child`), and set dialog and non-document actions aside. `AnalyzersFor(project)` is the project's analyzer references plus Roslyn's IDE analyzers (the `[DiagnosticAnalyzer]` types of the Features assemblies, found by reflection as a Roslyn host finds them) while built-in code actions are on — the IDE0xxx diagnostics the built-in fixers fix; `roslyn_get_diagnostics` and `roslyn_preview_code_fix` both run that set, the preview over the one document (`GetAnalyzerSyntaxDiagnosticsAsync` / `GetAnalyzerSemanticDiagnosticsAsync`). Some fixers answer to a companion of the diagnostic shown (the unnecessary-using fixer to `RemoveUnnecessaryImportsFixable`, not IDE0005), so the preview tries the companions at the position when the asked-for diagnostic has no fixer. `Reports` feeds `roslyn_info` |
| `ShadowCopyAnalyzerLoader` | Loads analyzer and provider assemblies from copies under `%LOCALAPPDATA%\RoslynMcp\analyzer-shadow\<hash of path + mtime>`, so a project's outputs are never locked by the server. The directory is shared by every server on the machine, so the copy is made for contention (#412): written through `FileWriter.ReplaceAtomic`, so the final name never leads to a file still being written; an existing file is reused only at the source's length; a copy of that length that is no assembly is copied again, once per process; the copy carries the time it was made, and gets the current time again when reused after a day, because the startup sweep deletes by age. `TransientFailures` counts the loads that failed for a reason another process causes and will stop causing (a sharing or lock violation, a file that went away, a copy another process holds — not a full disk or a name the context already has), at most five times per path — every cache of a load's result (`AnalyzerLoading`, `CodeActionHost`'s project and configured sets) compares it before and after and keeps only a result from a load during which it did not move. A dependency resolves by simple name among the registered dependency locations; a reference registers its own path only when it is created, so whoever loads a project's references registers all of them first (`AnalyzerLoading.RegisterDependencies`, #421) |
| `CodeActionSettings` / `CodeActionConfig` | The `codeActions` section of a config file (`enabled`, `builtIn`, `projectProviders`, `providers` as `{ "path" }` or `{ "package", "version"? }`, `deny`, and in the user file `trustProjectProviders`) and its resolution for a request: switches CLI arg > env var > project file > user file > default; the `providers` and `deny` lists of the two files are **merged** (a user's tools plus a team's — unlike `ignore`, where the winning source replaces). `--code-actions on\|off` / `ROSLYNMCP_CODE_ACTIONS` turns the refactoring tools and the non-bundled fixers off; `--trust-project-providers` / `ROSLYNMCP_TRUST_PROJECT_PROVIDERS=true` (or the user file) lets a committed file load assemblies by path |
| `UserConfig` | The user's own server config, `%LOCALAPPDATA%\RoslynMcp\config.json` (`ROSLYNMCP_USER_CONFIG` names another file; the harness points its servers at one that never exists), read once per process like `ProjectConfig`; only `codeActions` today. The user's own file, so its assembly paths need no trust gate |
| `NuGetPackageLocator` | A configured package's provider assemblies in the local NuGet cache (`NUGET_PACKAGES`, else `~/.nuget/packages`): `analyzers/dotnet/cs`, then `analyzers/dotnet`, `analyzers`, `lib/netstandard2.0`; the highest cached version when none is named. Never a feed — a package gets into the cache by a restore the user ran, which is what makes a package entry safe without a trust gate |
| `CodeActionOperations` | A code action's operations to the solution it proposes: exactly one `ApplyChangesOperation`, navigation operations (`OpenDocumentOperation`, `*NavigationOperation`) ignored, anything else `UnsupportedCodeActionOperationException` |
| `CodeActionChangeValidator` | What the physical apply path can carry, checked before a token is minted, shared by the code-fix and refactoring previews: text changes to tracked source, additional and analyzer-config documents; creation and deletion of `.cs` files inside the `SecurityBoundary` (the target must not exist, its directory must — the apply swaps a temp file into place and makes no directories); refused: added/removed additional or analyzer-config documents, project/reference/option changes, a document rename or move (by path **or by name** — Move type's "Rename file to match type" changes only the name), generated (`bin`/`obj`/`generated`, `.g.cs`, auto-generated header), linked, external or read-only files. Returns `ValidatedChanges` (created, deleted, changed) |
| `CodeActionApplyTool` | Abstract base of `roslyn_apply_code_fix` and `roslyn_apply_refactoring`: token/approval handling, workspace binding check, physical plan with the stale checks around the backups, `ApplyCodeActionResult` (per-file state, `createdFiles`, `deletedFiles`, link and explicit-Compile-items cautions). Returns an outcome the tool passes to its own scope terminal (RMCP004). Not a tool; the two tools are sealed |
| `ChangeSignatureTool` | `roslyn_change_signature` — preview adding parameters with a non-breaking forwarding overload; returns unified diff + token (ReadOnly — never writes); builds a `PreviewFileState` snapshot and `WorkspaceBinding` so `roslyn_apply_signature_change` can commit via `PhysicalSolutionApplier` |
| `ApplySignatureChangeTool` | `roslyn_apply_signature_change` — apply or reject a previewed signature change; applies via `PhysicalSolutionApplier` |
| `ProjectInfoTool` | `roslyn_get_project_info` — project metadata (TFM, language version, packages, etc.) |
| `BuildTool` | `roslyn_build_project` — check Roslyn diagnostics first (fast) — every project of a solution and every target framework of a project, or only `targetFramework` when given (#383) — skip build if errors; run `dotnet build` if clean or `forceBuild=true`; diagnostic items include optional `target_frameworks` when MSBuild emits TFM context |
| `CleanSolutionTool` | `roslyn_clean_solution` — remove all build artifacts (bin/obj directories) |
| `RestorePackagesTool` | `roslyn_restore_packages` — restore NuGet packages; on success flags a reload of the workspace it restored (`WorkspaceResolver.RequestReload`), since the assets file it writes is under `obj\`, which the watcher ignores (#399) |
| `DotnetRunner` | Shared helper for spawning `dotnet` CLI commands; strips MSBuild env vars (`MSBUILD_EXE_PATH` etc.) so child builds do clean SDK discovery; drains stdout+stderr concurrently to prevent pipe deadlock; runs one `dotnet` command at a time process-wide, through `MSBuildGate`, which workspace loads take too (concurrent builds of one solution race on shared `obj\` state, #300/#306/#379), recording `queued N ms behind <what held the gate>` when a call waits; used by `BuildTool`, `CleanSolutionTool`, `RestorePackagesTool` |
| `FileOutlineTool` | `roslyn_get_file_outline` — type/member structure without bodies (token saver) |
| `TypeHierarchyTool` | `roslyn_get_type_hierarchy` — base types, interfaces, derived types |
| `FindImplementationsTool` | `roslyn_find_implementations` — concrete implementations of interfaces/abstract members |
| `ListTypesTool` | `roslyn_list_types` — enumerate types with optional filters |
| `GetUsingsTool` | `roslyn_get_usings` — using directives + global usings |
| `GetSymbolDocumentationTool` | `roslyn_get_symbol_documentation` — XML doc comments for symbols |
| `GetSymbolDefinitionTool` | `roslyn_get_symbol_definition` — find declaration location with signature |
| `GetSymbolsInScopeTool` | `roslyn_get_symbols_in_scope` — enumerate accessible symbols at a location |
| `ReadFileTool` | `roslyn_read_file` — file contents as a one-line JSON header (`file`, `source`, `total_lines`, `start_line`, `end_line`) followed by the requested lines as raw, unescaped text (#328); tracked text documents come from the in-memory workspace |
| `GetLineCountTool` | `roslyn_get_line_count` — line count for one or more files |
| `GetMemberBodyTool` | `roslyn_get_member_body` — return full source of a single method/property/field/type by name, as a one-line JSON header followed by the raw, unescaped declaration (#328); a partial type returns one header line plus raw text per part |
| `CheckSyntaxTool` | `roslyn_check_syntax` — validate a C# snippet for syntax (and optionally semantic) errors without writing to disk; fast pre-flight check before writing; returns `valid`, counts, and `DiagnosticItem[]` with 1-indexed lines mapped back to the original snippet |
| `GetTriviaTool` | `roslyn_get_trivia` (**EXPERIMENTAL**) — extract whitespace, comments, and formatting trivia; filter by syntax kind, trivia kind, or line range; useful for understanding indentation context |
| `InfoTool` | `roslyn_info` — server version, PID, uptime, MSBuild discovery method, log markers |
| `DebugAttachTool` | `roslyn_debug_attach` (DEBUG only) — launches the JIT debugger dialog so Visual Studio can attach; blocks the server until dismissed or attached |
| `ApprovalStore` | Session-scoped approval state (`y`, `n`, `session` model); every workflow (`Rename`, `SignatureChange`, `CodeFix`, `Refactoring`) shares the same 3-state `TryBeginApply`/`CompleteApply`/`ReturnToPending` lifecycle to prevent double-apply races during a physical write |
| `BackupStore` | Crash-safe backup store for file write operations; stores pre- and post-write snapshots in `%LOCALAPPDATA%\RoslynMcp\backups\`; supports multi-level undo with token-based restore and conflict detection; async-safe via `SemaphoreSlim` |
| `PreviewFileStateBuilder` | Builds `PreviewFileState` snapshots (content hashes + intended bytes, encoding/preamble preserved) from a Solution diff; shared by every two-step preview tool (`roslyn_preview_rename`, `roslyn_change_signature`, `roslyn_preview_code_fix`, `roslyn_preview_refactoring`) so `PhysicalSolutionApplier` has what it needs to safely commit later |
| `PhysicalSolutionApplier` | Crash-safe, TOCTOU-guarded physical apply mechanism shared by `roslyn_apply_rename`, `roslyn_apply_signature_change`, `roslyn_apply_code_fix` and `roslyn_apply_refactoring`: builds a content-hash-verified `PhysicalSolutionApplyPlan` (Write/Create/Delete operations), re-validates disk state at three layers around backup preparation, and commits via atomic temp-file + `File.Replace`/`File.Move`. A document that is a symbolic link is written through when `SecurityBoundary` allows where it leads, and replaced where it sits (with a `_caution` in the result) when it leads out; plan entries that are one file on disk are written once (#334) |
| `PhysicalApplyResultMapper` | Maps a `PhysicalApplyReport` to the shared `ApplyFileResult[]` shape (per-file state, error, local-history recovery guidance) reused by all three apply tools' result records |
| `RoslynMcpJson` | Shared `JsonSerializerOptions` with a custom `JavaScriptEncoder` — passes through Unicode characters without `\uXXXX` escaping; used by all tools for consistent serialization |
| `IToolError` | Marker interface for error-bearing result types; paired with `ToolResult` in the `where T : ToolResult, IToolError` constraint on `ToolScope.Error<T>()` |
| `ToolResult` | Abstract base record for all tool results — success and error alike; nullable `Error`, `Hint`, and `Caution` properties are omitted from serialized JSON when null |
| `ErrorResult` | Standard error response record (`ToolResult, IToolError`); used by all tools for consistent JSON error shape; serializes as `{ "error": "...", "hint": "..." }` |
| `SolutionDiff` | Unified diff generation for `Solution` → `Solution` edits, shared by every two-step preview. The server's own differ: lines are matched by a longest common subsequence (common prefix and suffix paired directly, Myers' O(ND) search between them, bounded at 1,500 edits per file — past that the middle is reported as removed and added), then the gaps become hunks with three lines of context. A greedy matcher is not enough: one repeated line pairing with a far occurrence turns a ten-line change into a whole-file diff (#411) |
| `MSBuildBootstrap` | One-time MSBuild locator init; detects SDK vs VS workspace style; exposes `EnsureReady()`, `DetectLoadStyle()` (solution-aware: reads the `.sln`/`.slnx` project list, any legacy project ⇒ VS), `DetectProjectStyle()`, `ResolvedMode`, `DiscoveryMethod` |
| `MSBuildGate` | One MSBuild run at a time, process-wide (#379): every workspace load and reload (an MSBuild design-time build, in `LoadSolution` / `LoadMSBuildWorkspace`) and every `DotnetRunner` command take the same `SemaphoreSlim`, because they write the same generated files under `obj\`. A wait is logged naming what held the gate (`queued N ms behind a workspace load (Foo.slnx)`). A load waits at most the load timeout, which starts only once it holds the gate; past that it fails rather than run beside a long build, and the next call tries again. Disposal cancels a reload still queued on it, so teardown is not skipped behind a build Never taken under `cacheLock` (#277); a reload takes its `reloadGate` first |
| `PaginationCache` | Generic TTL-based cache for paginated tool results, shared across all tools via DI; stores each result set with the tool's static shape function so every page has the tool's own shape. `page_token` is a `PageCursor` (`{id}.{skip}.{take}`): the token alone yields the next page, and a token whose entry is gone re-runs the query from its position (#305) |
| `SymbolFormatter` | Static helpers to format Roslyn `ISymbol` instances into human-readable signatures (method, property, field, event, type) |
| `SymbolVisitors.cs` | Roslyn symbol tree visitors (`SimpleNameFinder`, `AllSymbolsFinder`, `AnySymbolFinder`) used by reference and rename tools |
| `Exceptions.cs` | Project-specific exception types for workspace path resolution (`ProjectNotFoundException`, `MultipleProjectsFoundException`, `InvalidProjectPathException`, `AmbiguousFileException`) |

**Data flow:** stdio MCP request → tool → `RoslynMcpTool.TryGetCompilation(projectPath, ...)` → `WorkspaceResolver.GetCompilation()` → `WorkspaceManager` (resolve path, load/cache workspace) → Roslyn API → JSON response.

**Key files:** `Program.cs` (MCP protocol), `WorkspaceManager.cs` + `.Resolution.cs` + `.Instance.cs` (workspace caching, path resolution, workspace lifecycle; the nested `WorkspaceInstance` is one partial class over `.Instance.cs` — state, construction, queries, disposal — and `.Instance.Loading.cs`, `.Instance.Reload.cs`, `.Instance.Adhoc.cs`, `.Instance.Writes.cs`, `.Instance.Links.cs`, `.Instance.EditImpact.cs`, `.Instance.Watcher.cs`; its own nested types `EditClassifier` (in `.Instance.EditImpact.cs`), `LinkResolver` and `PhysicalFileIndex` have a file each), `WorkspaceResolver.cs` (tool facade), `RoslynMcpTool.cs` + `RoslynMcpTool.ToolScope.cs` + `RoslynMcpTool.Discovery.cs` (base class), `FileLogger.cs` (file logging), `LogEntry.cs` (shared NDJSON log schema — linked into both `RoslynMcp` and `RoslynMcp.LogViewer`).

**Nested types.** A type that only one other type has any business using is a `private` nested type of it, not an `internal` type in the namespace: the compiler then keeps it out of everyone else's reach for good (`WorkspaceInstance` inside `WorkspaceManager`; `EditClassifier`, `LinkResolver`, `PhysicalFileIndex` inside `WorkspaceInstance`). Move it out only when that scope actually changes. Nesting separates scope, not state — a nested type can read every private member of an instance of its outer type that it is handed. So a nested type is **given what it needs** (constructor or method arguments) and holds no reference to its outer instance. Code that cannot work without reaching into the outer instance's locks and fields is not a separate concern: it stays a member, in a partial file of its own if the file gets long (the watcher flush, the write path and the reload in `WorkspaceInstance`). Name the file after the nesting (`WorkspaceManager.Instance.LinkResolver.cs`), and avoid nesting deeper than three levels.

**Tool subfolders** (all share the `RoslynMcp.Tools` namespace — subfolders are organisational only):
- `Tools/Analysis/` — 25 read-only Roslyn semantic queries (diagnostics, symbols, types, usings, outline, drift, …)
- `Tools/Search/` — 4 file/content search tools (list files, text search, semantic search, string literal search)
- `Tools/Editing/` — 5 file mutation tools (`roslyn_replace_in_file`, `roslyn_replace_in_code`, `roslyn_insert_lines`, `roslyn_write_file`, `roslyn_local_history`)
- `Tools/Rename/` — 2-step rename workflow (`roslyn_preview_rename` → `roslyn_apply_rename`)
- `Tools/CodeFix/` — 2 code-fix tools (`roslyn_preview_code_fix` → `roslyn_apply_code_fix`) over `CodeActionHost`: Roslyn's own fixers, the project's analyzer-package fixers, the bundled ones and configured providers; a fix may modify tracked text documents (`.cs` source, declared `AdditionalFiles` items, analyzer config) and create or delete `.cs` files inside the workspace, never add/remove other files, rename, or change the project (`CodeActionChangeValidator`, #389)
- `Tools/Refactoring/` — 2 signature-change tools (`roslyn_change_signature` → `roslyn_apply_signature_change`) and the 3 code-refactoring tools (`roslyn_list_refactorings` → `roslyn_preview_refactoring` → `roslyn_apply_refactoring`, over `RefactoringToolBase` / `CodeActionApplyTool`)
- `Tools/` root also holds `CodeActionApplyTool.cs`, the base of both apply tools; `src/RoslynMcp/CodeActions/` (namespace `RoslynMcp`) holds `CodeActionHost`, `CodeActionProviders`, `CodeActionConfig`, `CodeActionChangeValidator`, `CodeActionOperations`, `NuGetPackageLocator`
- `Tools/Build/` — 3 MSBuild/dotnet CLI tools (build, clean, restore)
- `Tools/` root — `RoslynMcpTool.cs`, `RoslynMcpTool.ToolScope.cs`, `RoslynMcpTool.Discovery.cs`, `ToolResults.cs`, `ErrorResult.cs`, `InfoTool.cs`, `RespawnTool.cs`, `DebugAttachTool.cs` (debug-only)

**File logging:** Every tool invocation, server start/stop, and workspace error is logged to a rolling file.
- Default path: `%LOCALAPPDATA%\RoslynMcp\logs\roslynmcp.{pid}.log` (PID always injected)
- Override base path: set `ROSLYNMCP_LOG_PATH` to any path — PID is still injected into the filename
- Disable: set `ROSLYNMCP_LOG_PATH` to an empty string
- Rotation: 10 MB cap, 3 rotated backups (`roslynmcp.{pid}.log`, `.log.1`, `.log.2`, `.log.3`)
- Pruning: old logs deleted after `ROSLYNMCP_LOG_MAX_AGE_DAYS` days (default: 30); backups after `ROSLYNMCP_BACKUP_MAX_AGE_DAYS` days (default: 90); pruning runs only after the server has started at least `ROSLYNMCP_PRUNE_MIN_RUNS` times (default: 3)
- Format: NDJSON — one `LogEntry` object per line
- Unhandled exceptions (#371): a tool that throws logs its TOOL entry as `unhandled <Type>: <message>`, and a call that fails around its tool gets a TOOL entry of its own with the raw arguments: `failed before the tool ran: …` (the SDK cannot bind an argument), `cancelled before the tool ran: …`, or `failed after the tool returned: …` (e.g. serializing the result). A wrapper exception is followed by its root cause (`AggregateException: … ← IOException: …`). A name that matches no tool gets no entry; the SDK answers it. Neither does a failure the tool already logged before rethrowing (build, clean and restore log `cancelled`). Both are written by `RoslynMcpTool.UnhandledExceptionFilter`, a `tools/call` request filter: the tool's `ToolScope` is disposed while the exception unwinds and cannot see it, so it defers its entry to the filter. The client still gets the SDK's `An error occurred invoking '<tool>'.`
- Key fields: `timestamp` (ISO 8601 UTC), `pid`, `level` (START/STOP/TOOL/ERROR/INFO), `instance` (per-process tool-call counter), `message` (non-TOOL entries), `tool_name`, `workspace_mode` (SDK/VS/ADH — MSB in pre-split logs and in an auto session before its first workspace load), `elapsed_ms`, `success`, `subject`, `detail`, `cache_tag`, `estimated_tokens`, `session_tokens`, `response_peek` (truncated JSON preview of response)

**Workspace modes:**
- **MSBuildWorkspace** (if `.csproj` found) — full NuGet resolution, multi-project support, .NET Framework 4.6.1+ compatibility
- **AdhocWorkspace** (fallback) — source-only, fast startup (<100 ms)
- **VS workspace** — uses Visual Studio's MSBuild instance when available
- See [docs/reference/WORKSPACE_MODES.md](docs/reference/WORKSPACE_MODES.md) for detailed comparison and FAQ
- **CLI flag:** `--workspace sdk|vs|adhoc|auto` (default: `auto`) — or set `ROSLYNMCP_WORKSPACE` env var to override; a committed `.madq_roslynmcp.json` at the repo root (written by `setup-project`) can pin a per-project mode when neither is given
- **Default workspace:** tools whose `projectPath` is optional fall back to a session default when it is omitted — `--root <path>` (or a bare positional path, so `"args": ["."]` works) > `ROSLYNMCP_ROOT` > server CWD, resolved to the one `.sln`/`.slnx` in that directory (`"solution"` in `.madq_roslynmcp.json` picks among several), else the nearest solution above it, else its single `.csproj`. Resolved lazily once and logged as `Workspace — Default workspace: …`. `.sln`/`.slnx` paths are also accepted as an explicit `projectPath`; build/clean/restore then target the whole solution. Many MCP clients do not start servers in the project directory — prefer an explicit `--root` in client configs. A committed `.github/mcp.json` with `"args": ["--root", "."]` pins the root per checkout so each git worktree self-roots (see *Git Rules § Worktrees*); `.mcp.json` (gitignored) is the per-checkout local override
- **Ignored directories:** `--ignore <name>` (repeatable), `ROSLYNMCP_IGNORE` (comma/semicolon-separated), or `"ignore": [...]` in a committed `.madq_roslynmcp.json` add bare directory names to the built-in list the server never walks or watches. Precedence as everywhere, and the winning source replaces the others rather than merging: CLI arg > env var > project file
- **Code actions (#389):** `--code-actions on|off` / `ROSLYNMCP_CODE_ACTIONS` turns Roslyn's refactorings and the non-bundled fixers off (the refactoring tools then answer `providers_disabled`; `roslyn_preview_code_fix` keeps the bundled fixers). `--trust-project-providers` / `ROSLYNMCP_TRUST_PROJECT_PROVIDERS=true` lets a committed `.madq_roslynmcp.json` load provider assemblies by path. `ROSLYNMCP_USER_CONFIG` names the user config file (default `%LOCALAPPDATA%\RoslynMcp\config.json`). The `codeActions` section of either file: `enabled`, `builtIn`, `projectProviders`, `providers` (`{ "path": … }` or `{ "package": …, "version"?: … }`), `deny` (export name, type name or stem), and in the user file `trustProjectProviders`. Switches: CLI arg > env var > project file > user file > default; the two files' `providers` and `deny` lists merge. `setup-project` prompts for `enabled`, `projectProviders` and package entries and writes only what differs from the defaults. Whatever one workspace mode cannot do is a structured answer, never a silent skip: project providers are unavailable in an `AdhocWorkspace` and the source report says so
- **MSBuild override:** `--msbuild-path <dir>` (or `ROSLYNMCP_MSBUILD_PATH`) points discovery at a dotnet SDK directory or a VS `MSBuild\Current\Bin` directory, bypassing auto-discovery. Honored in `auto`, `sdk`, and `vs`; ignored in `adhoc`. Invalid paths fall through to normal discovery rather than failing. Precedence, as everywhere: CLI arg > env var
- **VS version pin:** `--vs-version <ver>` (or `ROSLYNMCP_VS_VERSION`, or `"vsVersion"` in a committed `.madq_roslynmcp.json` — a version is portable, unlike a path) narrows vswhere to a Visual Studio version range: `17` → `[17.0,18.0)`, `17.14` → `[17.14,17.15)`, or a raw vswhere range. Needed when the newest installed VS (e.g. 2026 / 18.x) crashes Roslyn's .NET Framework BuildHost on legacy projects — bare `vswhere -latest` and the BuildHost agree on "newest", so the #258 pin alone cannot escape it. In `vs` mode it selects the server's instance (structured failure if nothing matches); in `auto`/`sdk` it only steers the BuildHost; `--msbuild-path` still wins; `adhoc` ignores it. Precedence: CLI arg > env var > project file

**Ambiguity handling:** ambiguous symbol names in `roslyn_preview_rename` / `roslyn_change_signature` return a structured candidate list by default, so the agent self-recovers via `containingType` (or `filePath`+`line`). Start the server with `--elicit` (or `ROSLYNMCP_ELICIT=true`) to instead prompt the user interactively — requires an MCP client that supports elicitation; unsupported clients fall back to the candidate list. A committed `.madq_roslynmcp.json` at the repo root (`"elicit": true`, written by `setup-project`) enables it per project when no CLI arg or env var was given — precedence: CLI arg > env var > project file > default.

**Tool Selection Guidance:**

> **⚠️ USE THE ROSLYN TOOLS. Every time. No exceptions unless the server is confirmed down.**
> - `roslyn_build_project` — **never** use `dotnet build` in a terminal when this tool exists
> - `roslyn_search_files` / `roslyn_semantic_search` — **never** use `Select-String`, `grep`, or `Get-ChildItem | Select-String` for C# source search
> - `roslyn_get_diagnostics` — **never** use terminal output parsing to check for errors
> - Terminal / PowerShell is a **last resort**, not a default. If the server is down, say so explicitly and explain why you're falling back.

When editing C# code, **actively prefer `roslyn_replace_in_code`** over `roslyn_replace_in_file`:
- `roslyn_replace_in_code` is semantically aware, validates syntax, preserves formatting/trivia
- `roslyn_replace_in_file` is for text/config files or when you need literal text replacement

**Before retyping code by hand, ask whether Roslyn has a refactoring for it.** Moving a type to its own file, extracting a method, introducing a local, inverting an `if`, converting to an interpolated string: `roslyn_list_refactorings` says what applies at a caret or selection, `roslyn_preview_refactoring` turns one action into a diff and a token, `roslyn_apply_refactoring` commits it. The refactoring keeps trivia, updates every reference and cannot mistype a line — a `roslyn_write_file` of the same change can. For a diagnostic (a CS or IDE id), `roslyn_preview_code_fix` offers Roslyn's own fixers and the project's analyzer-package fixers, and `roslyn_get_diagnostics` with `includeAnalyzers: true` and `severity: "hidden"` shows the IDE0xxx findings they fix.

For writing full file content (new files or wholesale rewrites), use `roslyn_write_file`:
- Takes crash-safe pre- and post-write backup snapshots automatically; returns a token usable with `roslyn_local_history` to undo
- Set `createNew: true` to create a new file or overwrite; default requires the file to already exist

When discovering files/content:
- `roslyn_list_files` — fast glob enumeration (find files by name/path)
- `roslyn_search_files` — content search (find lines matching regex pattern)
- `roslyn_semantic_search` — context-aware C# search (filter by comments, strings, identifiers, xmldocs, code)
- `roslyn_find_string_literal` — string-literal-only search with glob support and decoded-value matching
- `roslyn_find_references` — semantic symbol search (Roslyn-based, finds usage across project)

**Tool tips:**
- `roslyn_check_syntax` — call this before `roslyn_replace_in_code` or `roslyn_write_file` to catch syntax errors without a write round-trip; use `includeSemantics: true` to also validate against project types; use `wrapInClass: false` for complete class/namespace snippets or snippets containing `using` directives
- `roslyn_get_member_body` — use this to read a single method/property instead of `roslyn_read_file` on the whole file
- `roslyn_get_file_outline` — use to understand a file's type/member structure (signatures only, no bodies); more token-efficient than `roslyn_read_file` for exploration
- `roslyn_find_references` — without `containingType`, searches ALL symbols matching the name (union of results). Use `containingType` to narrow.
- `roslyn_list_types` — without `namespaceFilter`, returns only project-defined types (not framework). Use `namespaceFilter` for sub-namespace scoping.
- `roslyn_get_diagnostics` — use `severity: "errors"` or `take: 0` for a fast error-only check during editing; a `.sln`/`.slnx` `projectPath` checks every project. Every other tool that works on one project rejects a solution path with `solution_needs_project` and lists the `.csproj` files to pick from (#376)
- `roslyn_build_project` — checks Roslyn diagnostics first (fast, in-process). Only runs `dotnet build` if Roslyn is clean. To check that code compiles, use `roslyn_get_diagnostics` instead. When a build is needed, make **one** call with `projectPath` omitted (whole solution) — never one call per project, never several in parallel (they only queue), and no `forceBuild` unless diagnostics are untrustworthy. Skip it entirely before a TestHarness run, which builds everything itself.
- `roslyn_preview_rename` / `roslyn_apply_rename` — always call preview first; renames can affect dozens of files. The token from preview is required by apply.
- `roslyn_change_signature` / `roslyn_apply_signature_change` — same two-step pattern as rename: preview returns a diff + token, apply commits it.
- `roslyn_list_refactorings` → `roslyn_preview_refactoring` → `roslyn_apply_refactoring` — the same pattern with a discovery step: list first (a caret for most actions, a selection with `endLine`/`endColumn` for Extract method and the like), preview the action's index for the **same** location, apply the token. Dialog-driven actions are under `unavailable` with the reason; an empty list means nothing applies there, not an error.
- `roslyn_preview_code_fix` / `roslyn_apply_code_fix` — for a diagnostic at a location; pass `diagnosticId` when several sit on the line. Roslyn's fixers, the project's analyzer-package fixers and the bundled ones are all offered; a fix may create or delete `.cs` files.
- `roslyn_get_diagnostics` with `includeAnalyzers: true` — also runs the project's analyzers and Roslyn's IDE analyzers; their IDE0xxx findings are mostly info or hidden, so add `severity: "hidden"` to list them.
- Paginated tools return `page_token` + `has_more` — pass the token back **alone** (with the original arguments, no `skip`/`take`) to get the next page; it carries the position and page size. Each response has a new token, and none on the last page.

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
- Refactor:       roslyn_list_refactorings → roslyn_preview_refactoring → roslyn_apply_refactoring (not retyping code: move type to file, extract method, …)
- Fix diagnostic: roslyn_preview_code_fix → roslyn_apply_code_fix (not hand edits)
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

> **Note:** `scripts/Test-CodeStyle.ps1` is not in the repository (it never has been — check `git log`). Until it lands, apply the Code Style rules below by hand; `docs/development/CODE_STYLE_ENFORCEMENT.md` describes what the script is meant to check.

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
> This catches quirky rules (like indented blank lines) that you can't easily spot. See [docs/development/CODE_STYLE_ENFORCEMENT.md](docs/development/CODE_STYLE_ENFORCEMENT.md) for details. (The script is not in the repository yet — see the note under *Exception: Style Enforcement*; apply the rules by hand.)
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
> **Your style is yours, so `.editorconfig` is gitignored.** A committed one would impose one person's preferences on everyone, which is exactly what this section refuses to do. Keep a private `.editorconfig` at the repo root if you like: your IDE honours it, and so do Roslyn's refactorings and code fixes when `roslyn_apply_refactoring` / `roslyn_apply_code_fix` run them — Roslyn reads its formatting options (`indent_style`, `csharp_new_line_before_open_brace`, `csharp_space_after_keywords_in_control_flow_statements`, …) from that file, and without one it formats the changed region in its own defaults (four spaces, Allman braces, `foreach (`). Agents: never create or commit an `.editorconfig`; if the owner's private one is present, the refactoring output already matches it.
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

The server uses the C# MCP SDK (`ModelContextProtocol.Server` NuGet package). All protocol routing (`tools/list`, `tools/call`) is handled by the SDK automatically — there is no manual JSON-RPC switch.

**Host setup** (in `Program.cs`):
```csharp
builder.Services
    .AddSingleton<WorkspaceManager>()
    .AddSingleton<WorkspaceResolver>()
    // ... other singletons ...
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(serializerOptions: new JsonSerializerOptions(...))
;
```

**Tool discovery** — `WithToolsFromAssembly()` scans the assembly for all classes marked `[McpServerToolType]` and registers methods marked `[McpServerTool]` as callable tools. Tools are constructed via DI.

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

**Both are enforced at compile time:**
- **RMCP007** — every `[McpServerTool]` method must have a `[Description]` attribute (Error)
- **RMCP008** — every parameter on a `[McpServerTool]` method must have `[Description]`, except `CancellationToken` (Error)
- **RMCP009** — a required `string projectPath` must use `[Description(ProjectPathDescription)]`; an optional one must be declared `string? projectPath = null` (a literal `null` default) and use `[Description(OptionalProjectPathDescription)]` — inline strings drift; the constants are the single source of truth (Error)
- **RMCP010** — a string literal (descriptions, hints, messages) must not name a tool without its `roslyn_` prefix: an agent calls what it reads, and `apply_rename` is an unknown tool (Warning, #326). The names checked are the compilation's own `[McpServerTool(Name = …)]` values with an underscore after the prefix, so a new tool is covered at once, one-word names (`info`, `respawn`) are not matched, and the TestHarness, which declares no tools, is never checked. Comments are not checked

**Always use `ProjectPathDescription` or `OptionalProjectPathDescription`** for the `projectPath` parameter — constants with the canonical, full description. Inline text drifts and diverges. Never duplicate them.

#### Parameter Conventions

```csharp
public object MyToolMethod(
    [Description("...")] string requiredParam,           // required params first
    [Description(ProjectPathDescription)] string projectPath,  // required shape: last required param
    [Description("...")] string? optionalParam = null)   // optional params after projectPath — or see the optional shape below.
```

Actually: `projectPath` comes in two shapes, each enforced by RMCP009:

- **Optional** — `[Description(OptionalProjectPathDescription)] string? projectPath = null`, declared after every required parameter (and after `CancellationToken`). The first statement after `BeginTool` normalizes it: `if(!TryResolveProjectArg(projectPath, out projectPath, out var projectError)) return scope.Error(projectError);` for solution-level tools (an omitted value becomes the default workspace path, or `missing_project_path` when there is none), or the `TryResolveProjectArg(projectPath, filePath, …)` overload for file-anchored tools, which resolves an omitted value to the .csproj of the project in the default solution that contains `filePath` — and fails with `ambiguous_file` when a suffix-only `filePath` names different files in different projects. From there on `projectPath` is a plain non-null string and every helper works unchanged.
- **Required** — `[Description(ProjectPathDescription)] string projectPath`, declared last among required parameters. Still used by tools whose target project cannot yet be inferred without it: symbol-by-name and project-aggregate tools, plus the rename/signature apply tools (#296 tracks making those optional).

**Why omitting `projectPath` is safe (#295):**
An omitted `projectPath` resolves to the **session default workspace**, fixed by startup inputs only — `--root` / a bare positional argument, then `ROSLYNMCP_ROOT`, then the server CWD; from that directory the one `.sln`/`.slnx` in it (or the one pinned by `"solution"` in `.madq_roslynmcp.json`), else the nearest solution above it, else its single `.csproj`. It never depends on which workspaces happen to be cached, so it is not a guess — the concern that originally made `projectPath` mandatory. When nothing qualifies, the call fails with `missing_project_path` and a hint naming `--root`.

**`= null` is a deliberate exception to the no-default-parameters convention:** the MCP SDK marks a parameter optional in the tool schema only when it has a C# default, and MCP tool methods cannot use forwarding overloads.

#### The `BeginTool` / Scope Lifecycle

```csharp
public object MyToolMethod(..., string projectPath)
{
    using var scope = BeginTool("roslyn_my_tool", subject);
    // ... all tool logic
}
```

`using var scope = BeginTool(...)` **must be the first statement in every `[McpServerTool]` method.** This is enforced by RMCP003. Reasons:

- **`using`** — ensures `scope.Dispose()` is called on every exit path: normal return, early `return`, and unhandled exception. `Dispose()` writes the NDJSON log entry. Without it, the invocation is invisible in logs and timing is lost.
- **First statement** — any code before `BeginTool` is unlogged. Failures in parameter validation before `BeginTool` produce no log trace — impossible to diagnose remotely.
- **`name` arg** — must exactly match `[McpServerTool(Name = "...")]`. RMCP005 enforces this syntactically.
- **`subject` arg** — the primary identifier shown in the log (e.g., `symbolName`, `filePath`, `pattern`). Pass `null` for tools with no obvious primary key.

##### Scope Terminal Methods — Every `return` Must Use One

Every `return` statement that carries a value must go through a scope terminal. This is enforced by RMCP004.

| Method | When to use |
|--------|-------------|
| `scope.Outcome(detail, returnValue)` | Normal success. `detail` is a short log annotation ("12 results", "3 files changed"). Serializes the return value for log peek and token estimate. **Use this for successful returns.** |
| `scope.Outcome(detail)` | Success with no return value (rare — only for `void`-adjacent paths before final `return`). |
| `scope.Outcome(detail, header, source)` | Success for a tool that returns source code (#328). `header` is a `ToolResult` serialized as one JSON line; `source` follows as a raw text block, so the model reads the code unescaped and can copy it verbatim into an edit. The overload taking a list of `(Header, Source)` parts writes a one-line JSON header before each part. Every block ends with a newline, because a client may join blocks with nothing between them. Never put source text in a `ToolResult` property. |
| `scope.Error<T>(returnValue)` | Structured error. `T` must satisfy `where T : ToolResult, IToolError`. Marks the invocation failed, logs the error message. **Prefer over `Failed` when returning a known `ErrorResult` or custom `IToolError` type.** |
| `scope.Failed(reason, returnValue)` | Unstructured failure. Use when the error is a raw string (e.g., caught exception message) and no `IToolError` type is appropriate. |
| `scope.Failed(reason)` | Failure with no return value — marks the call failed and sets the log detail. Used before a `return` that returns void or before an exception. |

**RMCP006 (Warning)** — the `detail` argument to `scope.Outcome` and `scope.Failed` must not be a `"TODO"` placeholder string. Use a meaningful description of what happened.

The ternary form `return cond ? scope.Outcome(x, a) : scope.Error(b)` is valid — both branches are terminals.

##### Scope Non-Terminal Methods — Annotate Without Concluding

These may be called at any point before the terminal call:

| Method | When to use |
|--------|-------------|
| `scope.SetArgs(obj)` | Call early, right after `BeginTool`. Records key input arguments serialized to compact JSON. **Only included in the log entry on failure** — helps diagnose what inputs caused a problem. Truncate large values before passing. |
| `scope.Record(note)` | Append a mid-scope annotation. Useful for recording intermediate outcomes ("cache hit", "2 workspaces merged") that don't change the final outcome. Notes are kept apart from the terminal's detail and joined after it (`{detail}; {note}; …`) when the entry is written, so they survive `Outcome`/`Failed`/`Error`; a null or empty note is ignored. |
| `scope.SetCacheTag(bool hit)` | Record whether a pagination cache hit or miss occurred. Called by `TryServeCachedPage`. |
| `scope.SetWorkspaceMode(bool isMSBuild)` | Record whether MSBuildWorkspace or AdhocWorkspace was used. Called internally by every resolution guard (`TryGetCompilation`, `TryGetProject`, `TryResolveFileContext`, `TryResolveWorkspaceInfo`, `TryResolveRoot`, `TryResolveSolution`) — tools generally don't call this directly. The scope defaults to MSBuild, so a guard that skipped it would log an Adhoc call with the MSBuild label. |

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

`TryGetCompilation` and `TryGetProject` return a structured `ToolResult` on failure — pass it directly as the return value. Never unwrap or re-wrap.

#### File Mutation — Invalidate After Write

Any tool that writes to a file on disk **must** call `workspace.InvalidateFile(projectPath, fullPath)` afterward:

```csharp
await File.WriteAllTextAsync(fullPath, newContent);
workspace.InvalidateFile(projectPath, fullPath);
```

Without this, subsequent Roslyn tools see the stale in-memory source tree, not the updated file. `InvalidateFile` classifies the path via `GetDocumentIdsWithFilePath` + a `GetDocument`/`GetAdditionalDocument`/`GetAnalyzerConfigDocument` check: a tracked `.cs` document *or* a tracked `AdditionalFiles` item is applied incrementally (`Solution.WithDocumentText`/`WithAdditionalDocumentText`, #276 — `MSBuildWorkspace.CanApplyChange` supports both); a new `.cs`, a new `AdditionalFiles` item, an MSBuild evaluation input (`.csproj`, `.props`, `.targets`, `Directory.Build.*`, `global.json`, `nuget.config`, `packages.lock.json`, `.resx`), or a tracked analyzer-config document (`.editorconfig`, `.globalconfig` — `CanApplyChange(ChangeAnalyzerConfigDocument)` is `false` in Roslyn 5.3.0 and 5.9.0, so these always reload even when already tracked) flags a full reload on the next compilation-needing call, logged as `Reload — Flagged (<reason>): <path>`; anything else (`.md`, `.txt` not declared as an `AdditionalFiles` item, an unrelated `.json`) is a no-op for the workspace, so calling it is always cheap and always correct. See `docs/development/WORKSPACE_SYNC.md`.

#### Key Points (Summary)

- `projectPath` is either optional (`string? projectPath = null` + `OptionalProjectPathDescription`, normalized with `TryResolveProjectArg` right after `BeginTool` — the only way a tool opts in to the default workspace; `ResolveProjectPath("")` itself throws, so a required `projectPath` never silently resolves to the default) or required (`string projectPath` + `ProjectPathDescription`, last required parameter) — RMCP009 enforces the pairing
- `using var scope = BeginTool(...)` is always the first statement
- `Name` in `[McpServerTool]` must match `BeginTool`'s first argument exactly
- Every `return` with a value must go through `scope.Outcome`, `scope.Error`, or `scope.Failed`
- Use `[Description(ProjectPathDescription)]` / `[Description(OptionalProjectPathDescription)]` — never inline text for `projectPath`
- Use `scope.Error<T>` when `T : ToolResult, IToolError`; use `scope.Failed` otherwise
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

## Development Workflow

### Per-Issue Loop

**Starting the loop.** Any prompt that says to start work on an issue starts this loop — there is no fixed phrase. "#334 next", a plain "#334.", and "let's start on that issue" (the issue last mentioned, by either side) all count. Work out the issue from context; ask only when it is ambiguous which issue is meant. Starting the loop is the go-ahead for the commits and pushes on that issue's branch.

**The usual shape.** Steps in parentheses are the user's:

> plan → (approval) → branch off `dev` → implement → commit → push → (local review loop) → create the PR → (full review loop) → (merge)

**It is a default, not a contract.** Scale it to the change and use judgement, in the spirit of the Agile values: working software over ceremony, talking to the user over following the process, responding to what the work turns up over sticking to the plan. A change to the workspace boundary needs every step. A one-line fix in a `.md` file needs none of them — see the trivial-change exception below.

**Trivial changes go straight to `dev`.** `dev` is protected, and a pull request is the rule. The one exception: a trivial change — a typo, a one-line doc or comment fix, nothing that alters behaviour and nothing the user would want to review — may be committed on `dev` and pushed directly, using the admin bypass. This applies **only to the repo owner** and to an agent working in the owner's session; every other contributor, and their agents, opens a PR for everything. Name the commit in the next report, so it is never silent. The exception exists to keep a small fix from costing a whole PR; it is **not** a way to get something in without review. When in doubt whether a change is trivial, it is not: open a PR, or ask. When a shortcut would hide something the user would want to see, ask first; when a deviation was taken, say so in the report.

The steps in full:

1. **Create a plan** — read the issue and outline the approach before writing code. For anything non-trivial (more than a small, local change; anything touching security, the workspace boundary, concurrency or persistence), **stop and wait for approval** before implementing
2. `git checkout dev && git checkout -b feat/issue-NNN-short-description`
3. Implement
4. `roslyn_get_diagnostics(severity: "errors")` — must be zero before continuing
5. **First critic pass** — apply "Right Code" filter (see below); fix real bugs inline, discard noise; re-check diagnostics if anything was changed
6. **Second critic pass** — same filter; anything remaining goes to **todos** (not implemented inline), unless it's a real correctness bug — those always get fixed regardless of pass number
7. Commit with descriptive message + `Co-authored-by` trailer
8. Push the feature branch and report: what changed, the verify results, any deviation from this loop. This is where the user may start a local review loop (see *Review loops*)
9. **Submit a PR** targeting `dev` (`gh pr create`) — do not merge locally. The user may then start a full review loop on the PR, and the user merges
10. Reference the issue in the PR description (e.g. `Fixes #NNN`) so merging closes it automatically; open follow-up issues for deferred todos and link them back to the original

### Critic Integration

- **Always exactly 2 critic passes per issue** — no more, no fewer. This is what prevents the fix → critic → fix → critic rabbit hole.
- After pass 1: fix what's clearly worth it, discard style opinions and theoretical concerns.
- After pass 2: new findings in code the branch touches are fixed on the branch (see *Issue Scope Management*); only what needs a design of its own becomes a follow-up issue. Then move on — no third pass.
- **Exception:** real correctness bugs (race conditions, use-after-dispose, data corruption, API misuse) are always fixed inline — regardless of which pass or how many passes have run.

**"Right Code" filter (applied before every fix decision):**
- Is this *actually broken*, or just *imperfect*? Imperfect → todo.
- What's the real-world risk if left unfixed? Negligible risk → todo or discard.
- Does fixing this meaningfully complicate the implementation? If yes, is the benefit proportionate?

**Finding classification:**

| Category | Action |
|----------|--------|
| Race conditions, use-after-dispose, null refs, wrong results, API contract violations | Fix inline always |
| Design improvements, performance, low-risk edge cases | Fix on the branch when in code it touches; otherwise todo + follow-up issue |
| Style opinions, formatting, purely theoretical concerns | Discard |

### Review loops

Two review loops sit around the PR: a **local** one after the push and before the PR, and a **full** one on the PR (reviewer rounds, e.g. Copilot). Both are the user's to start — a `cr-loop` or `copilot-round` skill, or a plain request. Do not start one unprompted; for a change that is risky or large, recommend one in the report instead. Once started, a loop may commit and push its fixes to that branch without asking again. It never merges.

### Issue Scope Management

- **Close the original issue when its defined scope is done** — not when everything is theoretically perfect.
- **Fold work into the branch; spawn issues sparingly.** The moment a problem is found is the cheapest moment to fix it. A follow-up issue carries the context out of the session that had it, then waits; a branch that spawns one or two of them feels like two steps back for each step forward. So the default is to fix on the branch:
  - **Findings in code the branch changes** — from critic passes, local reviews or reviewer rounds — are fixed on the branch, **also once a review loop has started**. A review round exists to get that code right; deferring its findings only moves the work.
  - **Tangential fixes** found along the way, related to the issue or not, ride along when they can be verified (by a test or the existing harness) and do not raise the branch's risk: a test-only branch does not pick up a change to the workspace boundary, concurrency or persistence.
  - A folded-in fix outside the issue's own scope gets its own commit, its own changelog entry, and a line under "Also in this PR" in the description — PRs are squash-merged, and the title alone would hide it. Say what was bundled in the report.
- **A new issue is still right** for work that needs a design decision of its own, a new feature surface (a new tool family, say), a higher risk tier than the branch, or enough size to bury the branch's own change. When in doubt, ask. When each fix exposes the next case of the same thing, the answer is a better rule on this branch, not another issue.
- **Open fresh follow-up issues** for what is deferred — do not reopen the original. Each follow-up gets its own label, milestone, and scope.
- Rationale: fewer, more complete pull requests, and an issue list that stays worth reading. "Done" means the stated problem is fixed, not that the universe is in order.

---

## Git Rules

| Operation | Rule |
|-----------|------|
| Create / switch branch | ✅ Free |
| Stage files | ✅ Free |
| Commit | ✅ Free once the user starts work on an issue (see *Starting the loop*) — no per-commit approval needed |
| Push a feature branch | ✅ Free as part of the per-issue loop |
| Open a PR | ✅ Free as part of the per-issue loop — no local merge to `dev` |
| Commit outside issue work | ❌ Ask first — except a trivial change (next row) |
| Push to `dev` | ❌ Not for anything that deserves review. ✅ A trivial change only, and only for the repo owner (see *Trivial changes go straight to `dev`*), reported afterwards |
| Merge, force-push | ❌ Never — the user merges |

**Branch naming:** `feat/issue-NNN-short-description` for issue work; `fix/`, `chore/`, `docs/` prefixes for other change types.

**Branch for issue work:** Before starting any GitHub issue, always create a feature branch off `dev`:
```powershell
git checkout dev && git checkout -b feat/issue-NNN-short-description
```
Never commit implementation work directly to `dev` (only the trivial-change exception goes there). Push the feature branch and open a PR (`gh pr create`) targeting `dev` — do not merge locally with `git merge`.

**⚠️ Zero-byte check — required before every commit.** A known working-tree rollback issue can silently empty `.cs` files on disk while the Roslyn in-memory workspace still shows the correct content. Always run this before `git commit`:
```powershell
Get-ChildItem src\RoslynMcp -Recurse -Filter *.cs | Where-Object { $_.Length -lt 50 } | Select-Object FullName, Length
```
Any file under 50 bytes is suspicious. Before reaching for git, check the RoslynMcp backup store — `roslyn_write_file` snapshots files before every write, and a recent backup may be faster to restore from. **Do not use `roslyn_*` tools for this; use PowerShell directly** (the Roslyn workspace is equally stale when a file is 0 bytes on disk):

```powershell
$f = "MyFile.cs"
$dir = "C:\Users\madq4\AppData\Local\RoslynMcp\backups"
Get-ChildItem $dir -Recurse |
    Where-Object { $_.Name -like "${f}_*.bak" -and $_.Length -gt 0 } |
    Sort-Object LastWriteTime -Descending |
    Select-Object Name, LastWriteTime, Length
```

Backup naming: `{originalFileName}_{unixMs}_{nonce}.pre.bak` (pre-write) or `{originalFileName}_{unixMs}_{nonce}.post.bak` (post-write) — some backups may themselves be 0 bytes (taken after truncation); always filter `Length -gt 0` and sort descending. **Read and validate the backup content before copying** — confirm it contains the expected class/type names and reflects the correct version (not a stale draft). Cross-reference `LastWriteTime` against `git log` to pick the right snapshot. Only after validation: `Copy-Item $best.FullName "path\to\MyFile.cs"`. **After copying, read the restored file with `roslyn_get_file_outline` or `roslyn_get_member_body` and reason explicitly about whether it reflects the correct state for the work in progress** — the backup predates the write that caused the zeroing, so in-session edits may be missing and need to be re-applied before committing. If no usable backup exists, fall back to `git checkout <sha> -- path/to/file.cs`. See `docs/process/WORKING_TREE_ROLLBACK.md` for the full step-by-step procedure. Run the same check after merges — merges are a common trigger.

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

### Worktrees

RoslynMcp binds its workspace root **at session launch**, from the server's working directory (see *Architecture § Default workspace*). A running server does not re-root itself, so creating a worktree mid-session with a bare `git worktree add` leaves the current session's server pointed at the old checkout.

- **Config is committed and portable.** `.github/mcp.json` defines the `MadQ.RoslynMcp` server with `--root .`; being committed, every worktree checkout already carries it, and the relative root resolves to whichever worktree the session launched in — no global `~/.copilot/mcp-config.json` edits, ever. (`.mcp.json` is gitignored for per-checkout local overrides.)
- **Enter a worktree as a session, not just a directory.** Use `/fork worktree` (create + fork the session into it), `/new worktree`, `/worktree`, or a fresh `copilot` started inside the worktree — any of these launches a session rooted at that worktree. Do **not** rely on `git worktree add` alone from within an existing session; the already-running server will not follow.
- **Verify once per worktree:** call `roslyn_list_files` without `projectPath` to resolve the default workspace, then check the resulting `Workspace — Default workspace: …` log entry for the worktree's solution and absolute root.

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

### TestHarness

`src/TestHarness/TestHarness.csproj` is a comprehensive integration test suite. It dogfoods itself — starts the MCP server as a child process, fires real JSON-RPC calls, and verifies structured results against known expected values.

**Run all tests:**
```powershell
dotnet run --project src/TestHarness/TestHarness.csproj -f net10.0
```

**Run part of it (#367):**
```powershell
dotnet run --project src/TestHarness/TestHarness.csproj -f net10.0 -- --quiet --only DefaultWorkspace
dotnet run --project src/TestHarness/TestHarness.csproj -f net10.0 -- --quiet --only "symbol info,analyze region" --test nullable
dotnet run --project src/TestHarness/TestHarness.csproj -f net10.0 -- --list
```
- `--only <a,b>` builds and runs only the groups whose name contains one of the comma-separated parts (case-insensitive). A selection that happens to cover every group, with no `--test`, is a full run. Groups that are not selected are not even built, which matters: some start servers or create projects while being built. A part that matches no group stops the run with exit code 2 and the list of groups, so a typo cannot pass with zero tests. A `--test` that matches no test also exits 2, once the selected groups have been built.
- `--test <text>` keeps the tests whose name contains the text, within the selected groups. Combine it with `--only`, or every group is still built. Tests run in order and a few pass state to the next — *Pagination Cursor* and *String Literal Search* hand a `page_token` from one test to the following one — so dropping an earlier test can fail a later one that is not broken. Run such a group whole.
- `--only` and `--test` take one value each; give several groups in one `--only`, comma-separated. A repeated switch exits 2.
- `--list` prints the group names (`HarnessGroups.All`, in run order) and exits.
- `--only-build-diag` is `--only validation`.
- `--slow` also runs the slow tests (`TestCase.Slow`): tests that take tens of seconds and cannot be made quicker without weakening what they check — the *Dotnet Runner* check that a load waits out its timeout (~20 s, the server's 10 s timeout floor), and the *Load Health* check that an unhealthy reload is promoted after four discards (~1 min of backoff; on CI it runs on the Linux runner only, since the cap is platform-independent). Without it they are skips, and the summary line says how many: `PASS  440 passed, 9 skipped (1 slow: --slow runs it)`. A `--test` that matches a slow test runs it, and a CI runner (`Skip.OnCiRunner`, `GITHUB_ACTIONS=true`) always runs them, so no workflow passes the switch. Pass `--slow` locally when the change touches what a slow test guards: `MSBuildGate`, the load timeout, or the workspace retry (`IsRetryableWorkspaceFault`); the reload discard and its backoff (`ReloadAttempt`). Leaving slow tests out does not make a run filtered.
- A filtered run says so on its summary line — `PASS  25 passed, 2 skipped (filtered: DefaultWorkspace)` — so it can never be reported as a full run. *Workspace Hygiene* audits the whole run's log, so it runs only in a full run, or when `--only` names it — and then its two whole-run audits skip, since over a few groups they would pass or fail on too little evidence.
- The default (non-quiet) output prints each group's time and ends with the slowest groups.

**Filtered or full.** Filter while iterating: a full run takes about three minutes, most of it loading projects and running `dotnet`, and one group usually takes seconds. Before pushing, run the full suite when the change touches code that many tools share — `RoslynMcpTool`, `WorkspaceManager`, `SecurityBoundary`, `FileWriter`, `PhysicalSolutionApplier`, … — because such code is covered by groups other than the one holding the new test. #362 changed a helper in `RoslynMcpTool` used by twelve tools, whose tests are spread over many groups. A change confined to one tool and its group can rely on its group locally: every pull request runs the full suite in CI (see *Pull requests and the release gate*).

**Quiet output — AI agents: always use this.** `--quiet` prints nothing for a passing test, one line per failing test (with the server's stderr after them), and exactly one summary line at the end. The exit code is unchanged: 0 when everything passed, 1 otherwise.
```powershell
dotnet run --project src/TestHarness/TestHarness.csproj -f net10.0 -- --quiet
```
```
PASS  239 passed
```
```
FAIL  Discovery Tools (12 tests) › roslyn_list_types: enumerate types in RoslynMcp.Tools namespace: FAIL  (validation failed) [2453ms]
FAIL  238 passed, 1 failed
```
The summary line is always the last line, also when the run is cut short: if a test, a teardown or a group's setup throws, it reads `FAIL  70 passed, 0 failed, 169 not run — aborted in <group> › <test>: <exception>`. The server's stderr is printed after the server has shut down, so it includes what was logged during the last request.

**Skipped tests.** A test that cannot check anything on the machine is a skip, never a pass. It prints a `SKIP` line in quiet mode too and is counted in the summary: `PASS  240 passed, 1 skipped`. Use `Skip.NotApplicable(reason)` when the thing under test does not exist on this operating system (Unix file modes on Windows), or when a check audits a whole run and the run was filtered (#367) — always a skip; CI never filters. Use `Skip.SetupUnavailable(reason)` when the test applies but the machine will not let the fixture be built (no permission to create symbolic links) — a skip locally, a **failure on a CI runner**, so the release gate cannot pass with a case nobody checked. A slow test the run did not ask for is the third kind of skip (`Skip.SlowNotRequested`, written by the harness, not the test — see `--slow` above). Never return a plain pass from a test that did not run.

Do not filter the full output with a text match instead — passing tests with "error" or "fail" in their name match too.

**Linux run under WSL (#325).** The harness normally runs on Windows only, so anything that behaves differently on Linux is untested, and a test that skips itself on Windows checks nothing. `scripts/Test-HarnessWsl.ps1` runs the same harness on Linux, against the current working tree including uncommitted changes:
```powershell
.\scripts\Test-HarnessWsl.ps1 -Quiet
```
- It copies the tree into the WSL filesystem (`~/.roslynmcp-wsl/tree`, without `bin`/`obj`/`.git`) and runs there; running from `/mnt/<drive>` would share build output with Windows and is slow.
- It needs the SDK version `global.json` pins. If WSL lacks it the script says so; `-InstallSdk` installs it into `~/.roslynmcp-wsl/dotnet` (not on PATH, system SDK untouched).
- Further harness arguments go through `-HarnessArgs`, one array element per argument: `-HarnessArgs '--only','dotnet runner,linked files'`. A single string such as `'--only "a,b"'` arrives as one unknown argument, and the harness rejects it with exit code 2.
- Exit codes: 0 — all passed; 1 — the harness reported failures, or did not build on Linux; 2 — the harness could not be run (WSL, the distribution, `rsync` or the pinned SDK is missing, or a setup step failed).

**When to run it:** the Windows run (full or filtered, see above) is the verify step for every change. Add the WSL run when the change is platform-sensitive — file permissions or modes, symlinks, path separators or case-sensitive names, process spawning, shell or command-line parsing, anything behind an `OperatingSystem.Is…` check — or when a test in the change returns early on Windows. Report it separately ("Windows 239/239, Linux 239/239"); if WSL is not available on the machine (the script exits with code 2), say that the platform-only tests did not run rather than reporting a plain pass. macOS is not covered by either local run — see below.

**Pull requests and the release gate (#330, #367).** `.github/workflows/harness.yml` runs the full harness with `--quiet`. It runs:
- on every pull request to `dev` or `main`, on `windows-latest` and `ubuntu-latest` — docs-only ones included, because a path-filtered workflow reports no status and would block a docs-only pull request if the check were ever made required. A new push cancels the run still going for the previous one. macOS is left out because its runners queue for up to an hour;
- as part of `publish.yml`, on all three operating systems including `macos-latest`. The `publish` job needs it: nothing is published — not even a dry run reaches the packing step — unless all three pass;
- by hand: Actions → *Test Harness* → *Run workflow*, on any branch (`gh workflow run harness.yml --ref <branch>`), on all three.

Pushes to a branch without a pull request run only `build.yml`, which builds.

Run it by hand before merging a change that is platform-sensitive on macOS specifically (temp-directory paths, symlinks, file watching, case-insensitive-but-preserving names): macOS is the one platform no local run covers, and its first run found a boundary bug that Windows and Linux never hit.

**Fixture pattern (#274):** a test that needs real compilable code, or any file to edit, gets a throwaway project under `%TEMP%\RoslynMcp.TestHarness\<label>.<guid>` from `TestFixtures.NewMsBuildProject(label)` (or `NewAdhocDir` for an AdhocWorkspace) and passes `fx.Csproj` as `projectPath`. **Never write a fixture into `src/RoslynMcp`:** a directory target resolves to the repo's `.slnx`, so the FileSystemWatcher of every server with the repo open — the harness's own *and* the developer's live one — covers the whole tree; the SDK glob compiles the file into `RoslynMcp.dll`; and each create/delete forces a full solution reload. A checked-in fixture project elsewhere in the repo, or `<Compile Remove>`, does not help — the file is still an unknown document under the watched root. Example:
```csharp
var fx = TestFixtures.NewMsBuildProject("BuildDiag", targetFrameworks: "net10.0");
fx.Write("Probe.cs", "class Probe { }\n");   // before the first tool call — the initial load includes it
// ... call tools with projectPath = fx.Csproj, assert ...
return new TestGroup(header, tests, Teardown: () => { fx.Dispose(); return Task.CompletedTask; });
```

Write every fixture before the group's first tool call, so no test depends on the 300 ms watcher debounce, and dispose the fixture in the group's `Teardown`. Fixtures live outside the repo, so its `global.json` does not reach them: `TestFixtures.PinSdk` copies it to the fixture root at startup, or each fixture would resolve the newest SDK installed — and one newer than the server's MSBuild can load gives every unrestored fixture a load with no references. `TestFixtures.SweepStale` runs at startup and removes trees a killed run left behind (plus scratch files an old harness binary wrote into `src/RoslynMcp`). The last group, `WorkspaceHygieneTests`, reads the harness server's log and fails if any reload was flagged for a path inside the repo — the log line names the file, so the offending test is one grep away. Only read-only tests run against the dogfood project.

**Live testing:** configure in `.mcp.json` and test via GitHub Copilot or any MCP client.

Example `.mcp.json`:
```json
{
  "servers": {
    "MadQ.RoslynMcp": {
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
- **Never committed:** `docs/sessions/` is gitignored, so the handoff stays in the local checkout. Do not `git add -f` it

**When to write:**
- End of multi-hour development sessions
- Before switching major focus areas
- After significant refactoring or architecture changes
- When handing off to another session in this checkout (or future you)

**Include:**
- Executive summary of what was accomplished
- Current branch and commit state
- Open issues/PRs created or updated
- Technical discussions and decisions made
- Next steps (immediate and future)
- Any blocking issues or questions
- Context for resuming work

**Format:** Markdown, comprehensive but concise. Use the existing `HANDOFF.md` as the template when there is one.
