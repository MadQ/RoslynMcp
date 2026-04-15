# Changelog

All notable changes to RoslynMcp will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

---

## [0.8.0-beta] — 2026-04-15

### Fixed
- **`TryGetCompilation` relative path rejection** — removed the early `!Path.IsPathRooted` guard that incorrectly rejected valid relative paths (e.g. `src/RoslynMcp/RoslynMcp.csproj`) before `WorkspaceManager` could resolve them; `WorkspaceManager.ResolveProjectPath` already calls `Path.GetFullPath` to handle relative paths correctly, and all path-not-found cases are already covered by `InvalidProjectPathException`; also updated `ProjectPathDescription` to document that relative paths are supported
- **`roslyn_build_project`: project-level diagnostics now populate `target_frameworks`**— `ProjectLevelDiagnosticLine` regex now captures the MSBuild bracket suffix so NU\*/MSB\* errors from multi-target builds correctly report which target frameworks they apply to (closes #169)
- **TestHarness shutdown `TaskCanceledException`** — `WaitForExitAsync` now wrapped in `try/catch(OperationCanceledException)` so the harness exits cleanly when the server doesn't stop within the 5-second window; server process is still killed via the existing `proc.Kill()` fallback (closes #170)
- **BackupStore `meta.json` TOCTOU noise** — `ReadAllMetaEntries` now catches `FileNotFoundException` silently before the general `IOException` handler; file can be deleted by the pruner between the `File.Exists` check and the read without logging a spurious INFO warning
- **`--help` / `-h` flag** — when stdin is not redirected (human terminal) and no args are given, the server now prints help and exits instead of silently starting an MCP server that can't communicate; `--help`/`-h` flags always work regardless of TTY state (closes #181)
- **LogViewer multi-file watch** — LogViewer now tails all matching log files simultaneously instead of switching between them (closes #180)

### Added
- **`roslyn_check_syntax`** — new tool for validating C# snippets before writing: syntax-only mode (fast, no workspace) and semantic mode (full project compilation including project-defined types and global usings); `wrapInClass: true` default wraps snippet in a dummy class for member-level inputs; line numbers mapped back to original snippet (closes #183 prerequisite)
- **TestHarness: `target_frameworks` coverage test** — new `roslyn_build_project: forceBuild populates target_frameworks on CS diagnostics` test verifies that multi-TFM projects populate `target_frameworks` on at least one diagnostic item in the build output
- **LogViewer port auto-increment** — if port 5123 is already in use, the Log Viewer tries up to 10 consecutive ports (5123–5132) before giving up; enables running two instances simultaneously (e.g. Windows + WSL)
- **`Invoke-Git` wrapper in FSW test script** — optional pause-before-git mode lets you review changes in VS's Git Changes window before each commit

### Security
- **LogViewer SSE + CSRF hardening** — `/logs/stream` now blocks requests where `Origin` is present but non-loopback (VULN-002); `/shutdown` now rejects requests where `Origin` is absent or non-loopback, closing the CSRF bypass when a tool omits the header entirely (VULN-003); shared `IsLoopbackOrigin` helper extracted (closes #182)

---

## [0.7.8-alpha] — 2026-04-09 — [Release](https://github.com/MadQ/RoslynMcp/releases/tag/v0.7.8-alpha)

### Fixed
- **Critical: use-after-dispose on LRU eviction** — `WorkspaceManager` now defers disposal of evicted `WorkspaceInstance`s with a 30-second grace period so concurrent callers that already hold a reference finish safely (#145 item 1, PR #146)
- **`workspace` field read without lock** — `GetSolution`, `GetProject`, and `RebuildCompilation` now capture `workspace.CurrentSolution` under the read lock, preventing use-after-dispose when `ReloadIfNeeded` swaps the workspace on another thread (#145 item 2, PR #146)
- **FSW suppression race** — `ApplyChangesWithFswSuppressed` now uses `Interlocked` ref-counted suppression instead of a bool toggle, so concurrent calls don't race on re-enabling `EnableRaisingEvents` (#145 item 3, PR #146)
- **Path traversal on absolute paths** — `TryResolveTargetPath` absolute-path branch now has a separator guard preventing prefix collisions (e.g. root `D:\Foo` matching `D:\FooBar\secret.cs`) (#145 item 4, PR #147)
- **`GetCallGraphTool` missing constructors** — now captures `IObjectCreationOperation` in addition to `IInvocationOperation`, matching the documented claim "Includes constructors" (#145 item 5, PR #148)
- **`InsertLinesTool` silently converting line endings** — now detects the file's existing line-ending style (LF vs CRLF) and preserves it instead of forcing `Environment.NewLine` (#145 item 6, PR #149)
- **`ResolveFilePath` ambiguity** — suffix match fallback now collects all candidates and returns null on ambiguity instead of silently returning the first filesystem hit (#145 item 7, PR #147)
- **Backup token millisecond collision** — tokens now include a random 4-char nonce (`{hash}_{ms}_{nonce}`) to prevent collisions during pruning; `.bak` filenames updated accordingly; legacy tokens remain parseable (#145 item 9, PR #149)
- **Diagnostic deduplication** — `DiagnosticsTool` deduplicates by `(code, file, line, column, message)` to prevent double-counted entries from multi-TFM workspaces (#145 item 13, PR #148)
- **`roslyn_build_project` returning 0 diagnostics on multi-target projects** — `MSBuildLocator.RegisterDefaults()` injected `MSBUILD_EXE_PATH` into the host process; child `dotnet build` inherited it and failed pre-compilation, producing no diagnostics. `DotnetRunner` now strips `MSBUILD_EXE_PATH`, `MSBuildExtensionsPath`, `MSBuildSDKsPath`, and `MSBUILDUSESERVER=0` from the child process environment before launch (closes #165)
- **`roslyn_apply_rename` stale file after type rename** — when a type rename moves the type to a differently-named file, the original file was left on disk alongside the new one; the old file is now deleted after the workspace applies the rename (closes #151)
- **`WorkspaceManager` thread-safety** — generation counter prevents stale-cache reads under concurrent invalidation; all `projectMap` reads now acquire the read lock; workspace loaded outside the write lock to reduce contention; FSW callbacks quiesced during reload to prevent reentrant invalidation (closes #153)
- **`DotnetRunner` deadlock + process leak** — stdout and stderr now drained concurrently via `Task.WhenAll`, eliminating the deadlock when either pipe buffer fills; `Process` wrapped in `using`; args passed via `ArgumentList` to prevent injection via untrusted path segments (closes #154)
- **`MSBuildBootstrap` hardening** — `completed` flag marked `volatile` to prevent DCL tear; `vswhere` stderr drained concurrently to prevent deadlock; Build Tools install path added as fallback when VS instance list is empty; `EnsureReady` now catches unexpected exceptions before `completed` fires (closes #155)
- **`BackupStore` async + deadlock prevention** — `lock` replaced with `SemaphoreSlim(1,1)` for async-safe mutual exclusion; `Save`/`TryCheck` promoted to async; `PruneOldBackups` wraps `File.Delete` in `try/catch` to tolerate concurrent deleters; `LocalHistoryTool.ApplyAsync` uses a guid-suffix temp file with `try/finally` cleanup on failure (closes #156)
- **`SemanticSearchTool` `FindToken` position bug** — token lookup used the line-start position instead of the actual match span; fix stores `span.Start` at match time so `FindToken` is called with the precise character offset, eliminating misclassified matches (closes #157)
- **`SemanticSearchTool` P1/P2 correctness** — identifier matches no longer fire inside string literals; comment matches bounded to the comment span; XML-doc matches scoped to the correct element (closes #157)
- **`WorkspaceManager` write-path snapshot race** — workspace and solution captured atomically under lock in all write paths, preventing a concurrent reload from producing a torn snapshot where workspace and solution belong to different generations (closes #161)
- **`MSBuildBootstrap` failure surface** — workspace-load failures now propagated to `WorkspaceInfo` instead of being silently swallowed; `DotnetRunner` concatenates stdout and stderr in a single ordered stream so the full output is visible in `error_details`

### Added
- **`target_frameworks` field on diagnostic items** — `roslyn_build_project` now populates `target_frameworks: string[]` on each diagnostic when MSBuild emits TFM context in the bracket suffix (e.g. `net8.0`, `net10.0`). Items emitted for each target framework are aggregated into a single entry with a sorted `target_frameworks` array instead of duplicates. Field is omitted (`null`) on the Roslyn fast path and for project-level diagnostics (NU*/MSB*) where no TFM context is present (closes #166)
- **Self-healing truncation recovery** — `WorkspaceManager` detects when a tracked file is truncated to zero bytes and automatically re-reads from disk; guards against the known working-tree-rollback issue where `.cs` files are silently zeroed (closes #162)
- **Pre/post backup snapshots** — write operations now create a post-write snapshot in addition to the pre-write backup; `roslyn_local_history` can return both the "before" and "after" states of any write, enabling comparison and selective rollback (closes #163)

### Improved
- **Backup before editing** — `ReplaceInCodeTool`, `ReplaceInFileTool`, and `InsertLinesTool` now call `BackupStore.Save` before destructive writes, making them recoverable via `roslyn_local_history` (#145 item 12, PR #149)
- **`AsyncLocal` scope** — replaced `[ThreadStatic] activeScope` with `AsyncLocal<ToolScope>` so the scope flows correctly across `await` continuations (#145 item 11, PR #149)
- **`CancellationToken` propagation** — `GetTriviaTool` is now fully async; `GetUsingsTool` awaits `GetRootAsync`; `GetMemberBodyTool` propagates `CancellationToken` through the body-lookup loop (closes #158)
- **`PageToken` nullability** — all paginated result records now use `string? PageToken = null`; `null` means no more pages (replaces the empty-string convention), enabling a null-check idiom in consumers (closes #159)

### Deprecated
- **`BackupStore.TryRestore`** — marked `[Obsolete]`; bypasses `WorkspaceManager` and leaves workspace out of sync. Use the `TryCheck`/`WriteAndInvalidate`/`CompleteRestore` split instead (#145 item 8, PR #149)

Closes [#145](https://github.com/MadQ/RoslynMcp/issues/145), [#151](https://github.com/MadQ/RoslynMcp/issues/151), [#153](https://github.com/MadQ/RoslynMcp/issues/153), [#154](https://github.com/MadQ/RoslynMcp/issues/154), [#155](https://github.com/MadQ/RoslynMcp/issues/155), [#156](https://github.com/MadQ/RoslynMcp/issues/156), [#157](https://github.com/MadQ/RoslynMcp/issues/157), [#158](https://github.com/MadQ/RoslynMcp/issues/158), [#159](https://github.com/MadQ/RoslynMcp/issues/159), [#161](https://github.com/MadQ/RoslynMcp/issues/161), [#162](https://github.com/MadQ/RoslynMcp/issues/162), [#163](https://github.com/MadQ/RoslynMcp/issues/163), [#165](https://github.com/MadQ/RoslynMcp/issues/165), [#166](https://github.com/MadQ/RoslynMcp/issues/166)

---

## [0.7.6-alpha] — 2026-04-06 — [Release](https://github.com/MadQ/RoslynMcp/releases/tag/v0.7.6-alpha)

### Added
- **Call graph tools** — two new analysis tools for navigating the call graph in both directions: `roslyn_find_callers` returns all methods that call a named symbol (with `isDirect` filter for direct vs. interface/delegate dispatch); `roslyn_get_call_graph` returns all methods directly invoked within a method body by walking the Roslyn IOperation tree (closes #32)
- **Write-retry telemetry** — `WriteWithRetryAsync` and `WriteWithRetry` now emit structured `INFO` log entries when file-lock retries occur: one entry per caught `IOException` (attempt number, delay applied, hint message, file name) and a recovery entry when a non-final attempt succeeds; if all retries are exhausted, an `ERROR` entry is logged before re-throwing (closes #136)
- **RMCP007** — new Error diagnostic: every `[McpServerTool]` method must have a `[Description]` attribute; without it the tool is invisible to agent decision-making
- **RMCP008** — new Error diagnostic: every parameter on a `[McpServerTool]` method must have a `[Description]` attribute; `CancellationToken` parameters are exempt (infrastructure, not surfaced in agent schema)
- **RMCP009** — new Error diagnostic: `string projectPath` parameter must use `[Description(ProjectPathDescription)]` specifically, not an inline string; inline strings drift across tools
- **`ToolDescriptionAnalyzer`** — new analyzer class enforcing RMCP007, RMCP008, and RMCP009; no fixer (descriptions require human judgment)

### Fixed
- **`roslyn_local_history` double-write on apply** — `HandleApply` previously called `backups.TryRestore` (which wrote the file inside the lock) and then `workspace.InvalidateFile` (which caused a second read + workspace update); the FSW handler could also fire a third reload. Refactored via new `BackupStore.TryCheck` (validates token + conflict check inside `syncRoot`, returns `CheckedRestore`) and `BackupStore.CompleteRestore` (removes meta + deletes .bak inside `syncRoot`); `HandleApply` now calls `TryCheck` → `workspace.WriteAndInvalidate` (FSW-suppressed write) → `CompleteRestore`, eliminating the double-write (closes #141)
- **`roslyn_find_callers` returning duplicate results** — `AllSymbolsFinder` can return multiple matching symbols (e.g. interface + concrete implementation) for the same name when `containingType` is not specified; `FindCallersAsync` ran on each, producing duplicate `CallerEntry` records for the same call site. Fixed by deduplicating via `.DistinctBy(c => (c.Caller, c.File, c.Line))` before sorting (closes #143)
- **`roslyn_build_project` reporting failure on successful builds** — `dotnet build` can exit with code 1 even when compilation succeeds; MSBuild analyzer diagnostics (MSBL*, NU*) set the exit code without emitting a parseable CS error line. Now checks whether the output contains "Build succeeded." as positive evidence; if no structured errors were found and that text is present, `succeeded` is set `true` regardless of exit code. The `error_details` fallback still fires for genuine failures (non-zero exit, no structured errors, and no "Build succeeded." text).
- **RMCP003 code fix** — `InsertBeginToolAsync` now detects the end-of-line style from the method body's opening brace and applies it as trailing trivia on the inserted `using var scope = BeginTool(...);` statement; previously the next statement ran on the same line immediately after the semicolon
- **BOM written by rename/signature-change tools** — `SolutionDiff.ApplyToDiskAsync` was using `sourceText.Encoding ?? Encoding.UTF8` to write changed files; `Encoding.UTF8` is `new UTF8Encoding(true)` (BOM-emitting), so renames and signature changes always wrote a UTF-8 BOM. Fixed to use `new UTF8Encoding(false)` directly — RM's policy is always UTF-8 without BOM. Root cause: `StreamReader.CurrentEncoding` always returns a BOM-emitting instance in .NET's `detectEncodingFromByteOrderMarks` mode regardless of file content, so `sourceText.Encoding` was never reliable for this purpose.
- **`SourceText.From` encoding** — `WorkspaceManager.Instance.cs` and `ReplaceInCodeTool.cs` were passing `Encoding.UTF8` (BOM-emitting) to `SourceText.From(stream, encoding)`, causing Roslyn's in-memory documents to record a BOM-emitting encoding. Changed to `new UTF8Encoding(false)` to align with RM's BOM-free policy.

### Improved
- **`FileWriter` — centralised file write entry point** — new `internal static class FileWriter` with `WriteWithRetryAsync` and `WriteWithRetry` replaces the `protected static` methods on `RoslynMcpTool`; all file writes across the server now go through a single path that retries on `IOException`, logs each attempt and final failure, and is accessible from non-tool types (`BackupStore`, `SolutionDiff`) (closes #139)
- **Complete `WriteWithRetry` coverage** — every naked file write has been wrapped: `BackupStore` (5 writes), `SolutionDiff.ApplyToDiskAsync` (1 write), `WriteFileTool` temp-file write; `BackupStore` also gains `FileLogger` constructor injection and atomic meta-file writes (write-to-tmp + `File.Move` with overwrite) (closes #139)
- **`WriteFileTool` TOCTOU fix** — `isNewFile` detection replaced `File.Exists` probe with a try-read pattern (catch `FileNotFoundException`); eliminates the race between the existence check and the subsequent read (closes #139)
- **FSW reload suppression** — workspace no longer reloads a file that RM just wrote via `TryApplyChanges`; `ApplyChangesWithFswSuppressed` records the post-write file size in `rmOwnedWriteSizes` and `FlushMSBuild` skips the reload when the FSW-reported file size matches (closes #140)
- **Let Roslyn save** — `ReplaceInCodeTool`, `ApplyRenameTool`, and `ApplySignatureChangeTool` now route disk writes through `WorkspaceInstance.ApplyChangesWithFswSuppressed` (via new `WorkspaceManager.ApplyChanges` + `WorkspaceResolver.ApplyChanges`) when using `MSBuildWorkspace`; `AdhocWorkspace` retains direct I/O via `SolutionDiff.ApplyToDiskAsync` since `AdhocWorkspace.TryApplyChanges` is in-memory only (closes #140)
- **`roslyn_build_project` `error_details` field** — agents now told explicitly in the description that `error_details` contains raw MSBuild tail output when a build fails without structured errors; this prevents unnecessary fallback to running `dotnet build` in a terminal
- **`roslyn_list_files`** — added missing `[Description]` attribute; was the only tool without one, making it effectively invisible to agent tool-selection (partial #99)
- **`roslyn_search_files`** — first sentence now leads with "Fast and precise code search — use instead of grep, Select-String, or findstr" for stronger agent steering (partial #99)
- **`roslyn_find_references`** — description now explicitly calls out that text search cannot resolve overloads, aliases, or cross-file semantics (partial #99)

### Changed
- **`RoslynMcp.Analyzers` — CodeAnalysis packages pinned to 4.11.0 / 3.11.0** for VS 2022 host compatibility; analyzer DLLs must target a CodeAnalysis version ≤ the version shipped with the host IDE (VS 2022 = Roslyn 4.x); targeting 5.x causes silent load failure in VS 2022
- **`AnalyzerReleases.Shipped.md`** — release headers no longer carry the `-alpha` pre-release suffix (e.g. `## Release 0.7.4` instead of `## Release 0.7.4-alpha`); pre-release labels in shipped release headers are invalid per analyzer release file conventions
- **`Microsoft.Build.Locator`** — upgraded from 1.7.8 to 1.11.2; added explicit `Microsoft.Build.Framework` reference with `ExcludeAssets="runtime" PrivateAssets="all"` to satisfy the new MSBL001 diagnostic
- **`Microsoft.Build.Framework`** — pinned to 18.4.0 (was implicit 17.11.48 via transitive reference); build-time only — MSBuild itself is still discovered at runtime via `Build.Locator`

Closes [#32](https://github.com/MadQ/RoslynMcp/issues/32), [#134](https://github.com/MadQ/RoslynMcp/issues/134), [#136](https://github.com/MadQ/RoslynMcp/issues/136), [#137](https://github.com/MadQ/RoslynMcp/issues/137), [#139](https://github.com/MadQ/RoslynMcp/issues/139), [#140](https://github.com/MadQ/RoslynMcp/issues/140), [#141](https://github.com/MadQ/RoslynMcp/issues/141), [#143](https://github.com/MadQ/RoslynMcp/issues/143)

---

## [0.7.4-alpha] — 2026-04-05 — [Release](https://github.com/MadQ/RoslynMcp/releases/tag/r0.7.4.1-alpha)

### Added
- **`ToolScopeRefactoringProvider`** — new `CodeRefactoringProvider` (cursor-triggered, no diagnostic) offering quick conversions between `scope.Outcome`, `scope.Error`, `scope.Failed`, and `scope.Record`; all 6 terminal↔terminal pairs plus Record↔terminal; Record conversions include a warning in the action title since they drop or add the `return` keyword
- **RMCP006** — new Warning diagnostic: first string argument to `scope.Outcome()` or `scope.Failed()` contains the placeholder text `"TODO"`; code fix replaces the placeholder with the tool name inferred from `[McpServerTool(Name)]` (e.g., `roslyn_info` → `"info"`)
- **`ToolScopeHelpers`** — new internal static class shared by `ToolScopeAnalyzer`, `ToolScopeCodeFixProvider`, and `ToolScopeRefactoringProvider`; provides `HasMcpServerToolAttribute`, `GetMcpToolName`, and `InferDetailName`

### Changed
- **RMCP005** — upgraded from Warning to Error; a `BeginTool` name that mismatches `[McpServerTool(Name)]` makes log correlation impossible — it is factually incorrect, not cosmetic
- **RMCP004 code fix** — `scope.Outcome` and `scope.Failed` fixes now infer the `detail`/`reason` label from `[McpServerTool(Name)]` (e.g., `roslyn_info` → `"info"`) instead of always using `"TODO: describe outcome"`; `scope.Failed` always uses `"failed"` as the reason; `scope.Error` is unchanged (no string arg)
- **`ToolScopeAnalyzer` RMCP003** — expression-bodied `[McpServerTool]` methods now fail RMCP003 immediately rather than being skipped; the pattern is one class / one tool method and expression bodies cannot satisfy the `using var scope = BeginTool(...)` requirement
- **`ModelContextProtocol`** — updated from 1.1.0 to 1.2.0; breaking changes (SSE disabled by default, `RequestContext` constructor obsoleted) are non-issues for this stdio server
- **`Microsoft.Extensions.Hosting` / `Logging.Console`** — updated from `10.0.0-preview.3` to `10.0.5` (preview → stable)

### Fixed
- **RMCP004 code fix** — wrapping a `null` or `null!` return expression no longer produces uncompilable code (CS0411 type-inference failure on `scope.Failed<T>` / `scope.Outcome<T>` / `scope.Error<T>`); the fix now substitutes `new ErrorResult(<arg>)` where `<arg>` is the first `scope.Record(...)` string argument in the method, or `"TODO"` if none is found
- **`ToolScopeRefactoringProvider`** — Record→terminal conversions ("Convert to return scope.Failed(...)") now also substitute `new ErrorResult(<Record arg>)` instead of the uncompilable `null!`
- **`TryServeCachedPage`** — added `[NotNullWhen(true)]` to the `out` parameter; eliminates 10× CS8603 nullable-return warnings project-wide
- **`roslyn_write_file`** — new `.cs` files no longer get a UTF-8 BOM; was incorrectly using `encoderShouldEmitUTF8Identifier: true` as a "VS default" for C# files
- **`roslyn_replace_in_code`** — no longer uses the Roslyn workspace's cached `SourceText.Encoding` when writing; now peeks at the actual on-disk bytes to detect the BOM, preventing BOM-pollution when the workspace loaded a file before the encoding setting was corrected
- **Log viewer** — instance column now shows a unique per-PID sequential label (`#1`, `#2`, …) instead of the global call counter, making multi-process log sessions easier to follow
- **TestHarness** — 8 pre-existing test failures fixed; validators were using camelCase field names (`matchCount`, `changedLines`, `insertedAt`, `lineCount`) but tool responses use snake_case; now passes 41/41

### Refactored
- **`FileEncoding`** — new shared static helper (`FileEncoding.Detect(ReadOnlySpan<byte>)` + `FileEncoding.Peek(string)`) centralises BOM detection; replaces duplicated logic in `WriteFileTool` and `ReplaceInCodeTool`

---

## [0.7.3-alpha] — 2026-04-04

### Added
- **`RoslynMcp.Analyzers` — ToolScopeAnalyzer (RMCP003/RMCP004/RMCP005)** — three analyzer rules that enforce the `BeginTool`/`ToolScope` pattern on all `[McpServerTool]` methods (#127)
  - **RMCP003** (Error): `[McpServerTool]` method must begin with `using var scope = BeginTool(...)`
  - **RMCP004** (Error): all return paths in a `[McpServerTool]` method must go through `scope.Outcome`, `scope.Error`, or `scope.Failed`
  - **RMCP005** (Warning): the `name` argument passed to `BeginTool` must match `[McpServerTool(Name = ...)]`
- **`AnalyzerReleases.Shipped.md`** — Release 0.3.0 block added documenting RMCP003/RMCP004/RMCP005 (#127)
- **`ToolScopeCodeFixProvider`** — IDE lightbulb code fixes for RMCP003/004/005 (#128)
  - RMCP003: inserts `using var scope = BeginTool("toolName");` as the first statement; tool name read from `[McpServerTool(Name = "...")]`
  - RMCP004: offers three alternatives — `scope.Outcome(...)`, `scope.Error(...)`, `scope.Failed(...)`; the wrong choice produces a compile-time generic constraint error, guiding the developer to the right one
  - RMCP005: replaces the mismatched name literal with the value from `[McpServerTool(Name = "...")]`
- **`roslyn_get_project_info`** — exposes MSBuild-derived project properties (#117)
  - New fields: `version`, `root_namespace`, `target_frameworks`, `allow_unsafe_blocks`, `warnings_as_errors`
  - Parsed from `.csproj` XML; `_caution` is populated whenever any MSBuild property is returned, noting that `Condition` attributes are not evaluated
- **`roslyn_build_project`** — populates `error_details` with the last 30 lines of raw MSBuild output when the build fails with exit code ≠ 0 but zero C# diagnostics (#131)
  - Prevents `succeeded: false` with empty `errors` from leaving agents without actionable context
- **`BackupStore`** — now records `gitBranch` and `gitCommit` at snapshot time via `git rev-parse` (#122)
- **`RoslynMcpJson`** — new static class with shared `JsonSerializerOptions` used by all serialization (#123)
  - Custom `JavaScriptEncoder` using `TextEncoderSettings` — allows all Unicode through instead of escaping as `\uXXXX`; still escapes what is actually necessary
  - Literal `\n` in JSON string values now preserved (not double-escaped)
- **`ToolErrorResult`** — abstract base record in `ErrorResult.cs`; all structured error types now derive from it, replacing the `ExtractDetail` switch (#125)
  - `PathErrorResult` and `UnexpectedErrorResult` in `ToolResults.cs` now inherit `: ToolErrorResult`, completing the structured error hierarchy
  - `TryGetCompilation` and `TryGetProject` `out` parameters typed as `out ToolErrorResult?`, enabling callers to handle structured errors without casting

### Changed
- **`roslyn_local_history`** — `list` action now includes a `_caution` field when any backup was taken on a different branch than the current one (#122)
- **`roslyn_local_history` and `roslyn_write_file`** — tool descriptions updated with branch-agnostic caution notes (#122)
- **`ToolResults.cs`** — all ~51 result records converted to positional syntax with `[property: JsonPropertyName("snake_case")]` attributes; C# names normalized to PascalCase (#124)
  - Snake_hybrid C# names (e.g. `Symbol_name`) → PascalCase (e.g. `SymbolName`)
  - `_caution` → `Caution` in C# (JSON key `_caution` preserved via attribute)
  - `MetadataSymbolResult.Message` → `Error`, JSON key `message` → `error`
  - `SymbolDocumentationEmptyResult.Message` → `Error`, JSON key `message` → `error`
- **`ToolScope.Error<T>()`** — gains `where T : ToolErrorResult` constraint; body simplified to `returnValue.Error` (#125)
- **Operation-type results** — Build/Clean/Restore/ReplaceInFile/ReplaceInCode now use `scope.Failed(reason, result)` instead of `scope.Error(result)` (#125)
- **`ExtractDetail<T>()` switch** — deleted entirely; replaced by `ToolErrorResult` abstract base record (#125)
- **All tool files** — all RMCP003/004/005 violations resolved; every `[McpServerTool]` method now opens with `using var scope = BeginTool(...)` and all return paths go through `scope` (#127)
- **`TryServeCachedPage<T>`** — moved from `RoslynMcpTool` to `ToolScope`; signature changed to `bool + out object?` (no longer calls `scope.Outcome` internally); callers receive the cached result directly and call `scope.Outcome` themselves (#130)

### Fixed
- **`BuildLiteralRegex` newline normalization** — CRLF-tolerance was a no-op: `Regex.Escape` converts literal `\n` to the two-char sequence `\n`, so the subsequent `.Replace("\n", ...)` searching for the actual newline char never matched; fix: `.Replace(@"\n", @"\r?\n")` (#126)
- **`ToolErrorResult.Error`** — made non-nullable (`string?` → `string`); aligns with all derived types; dead `?? "error"` fallback removed from `ToolScope.Error<T>()` (#126)
- **`IsUnderRoot` filter** — tightened to also check `LocationKind` and use path-separator-aware comparison, eliminating false positives for external files with overlapping path prefixes (#114)
- **`roslyn_write_file`** — retries with exponential backoff (up to 3 attempts) on `IOException` due to file contention, preventing transient lock failures from surfacing as errors (#129)

---

## [0.7.2-alpha] — 2026-04-03

### Added
- **NDJSON log viewer** — `src/RoslynMcp.LogViewer/viewer.html`: self-contained browser-based log viewer (#118)
  - Tree-view expand/collapse with chevron-left layout
  - Win95-style `[+]`/`[-]` expand boxes (Parchment theme, pure CSS `:has()`)
  - Parchment dotted tree lines aligned to box center; local time display
  - Double-click clears accidental text selection; text remains normally selectable
  - JSON syntax highlighting: recursive `renderJSON()` walker with colored keys/strings/numbers/booleans/null
  - C# syntax highlighting: two-pass tokenizer (atomic strings/comments first, then keywords/numbers/brackets)
  - Rainbow bracket coloring: `(`, `)`, `{`, `}`, `[`, `]` cycle 3 colors by nesting depth
  - Embedded C# code blocks (multi-line string values) detected and rendered as `<pre>` blocks
  - Graceful fallback for truncated/invalid JSON in `highlightJSON()`
- **`response_peek` pipeline** — tool responses now captured and surfaced in the log viewer (#118)
  - `LogEntry.ResponsePeek` property in shared NDJSON schema
  - `FileLogger.LogTool` gains `responsePeek` parameter
  - `RoslynMcpTool.SerializeResponse<T>` captures peek (600 char cap, truncated with `…`)
  - `Outcome`/`Error`/`Failed` all pass peek through to the logger
- **`roslyn_write_file`** — atomically write or create any file within the project root (#116)
  - Atomic write via temp-file + `File.Move(overwrite: true)` — no partial writes
  - Automatic pre-write backup; returns a `backupToken` usable with `roslyn_local_history` for undo
  - BOM-preserving: sniffs existing encoding on overwrite; new `.cs` files default to UTF-8 BOM (VS default)
  - `createNew: true` mode for file creation; path traversal guard via `TryResolveTargetPath`
  - SDK-style projects: new `.cs` files in the project directory are auto-included (no `.csproj` edit needed)
- **`roslyn_local_history`** — crash-safe token-based undo for file write operations (#116)
  - `action: list` — list backup snapshots for a file (or all files)
  - `action: preview` — inspect a backup by token; includes `conflictRisk` flag if file was modified since backup
  - `action: apply` — restore a file from a backup token; invalidates workspace cache on success; returns conflict details if the current file diverges
- **`BackupStore`** — crash-recoverable backup infrastructure (#116)
  - Snapshots stored in `%LOCALAPPDATA%\RoslynMcp\backups\{path-hash}/`; override with `ROSLYNMCP_BACKUP_PATH` env var
  - Token format: `{8-char-path-hash}_{unix-ms}` — unique, multi-level, crash-recoverable without server state
  - File hash in `meta.json` for dedup (skip backup if identical content), conflict detection, and integrity checks
  - Retention: max 10 snapshots per file; oldest pruned automatically on write
  - `TryResolveTargetPath` added to `RoslynMcpTool` base class: validates path stays under root without requiring file existence

### Changed
- **Tool metadata improvements — all 33 tools** (#112)
  - Added `Title` (Title Case display names), `OpenWorld = false`, `Idempotent = true` (read-only tools), `Destructive = false` (additive/non-destructive tools) to all tool attributes
  - Rewrote all `[Description]` strings with agent-centric framing: concise first sentence, key parameters, return shape, performance notes, caveats
  - Correctness fixes: `ChangeSignatureTool` `ReadOnly = true`, `InsertLinesTool` `Destructive = false`, `BuildTool` remove incorrect `ReadOnly = true`, `ReplaceInFileTool` stale XML doc removed
  - AGENTS.md: added missing `DebugAttachTool` entry, clarified `ChangeSignatureTool` as preview-only, added tool tips
- **`roslyn_get_diagnostics` — structured response, pagination, shared types** (#111)
  - **Breaking:** response is now a structured JSON object instead of a flat `string[]`
  - Always-present `summary`, `errors`, `warnings`, `total`, `returned`, `has_more`, `page_token`, `items`
  - `items` is a paginated array of `{ code, severity, file, line, column, message }` objects
  - `take: 0` fast path — returns summary counts with empty `items`, no compilation overhead
  - Stateless `page_token` (base64-encoded `{ skip, severity }`) — no server-side cache needed
  - `severity` filter: `"errors"`, `"warnings"`, or `"all"` (default: errors + warnings)
  - File paths now project-relative (consistent with `roslyn_build_project`)
- **`roslyn_build_project`** — improved tool description; `Errors`/`Warnings` arrays now typed as `DiagnosticItem[]`
- **`RoslynMcpTool` base class** — added `GetSeverityFilter` and `TryMakeRelative` as `protected static` helpers

### Fixed
- **External diagnostics noise** (#113) — `roslyn_get_diagnostics` and `roslyn_build_project` no longer surface diagnostics from files outside the project root (e.g. files open in VS from unrelated directories); `IsUnderRoot` helper added to `RoslynMcpTool` base class
- **`SolutionDiff.BuildHunkList` infinite loop** (#115) — loop hung when new file is shorter than old; LCS-matched old lines with exhausted `ni` now correctly treated as deletions

---

## [0.7.1-alpha] — 2026-03-31

### Added
- **`roslyn_info` tool** — server version, PID, uptime, MSBuild discovery method, log markers. `roslyn_info clear` clears the LogViewer.
- **`roslyn_insert_lines` tool** — insert lines by line number or anchor pattern (insertAfter/insertBefore). (#72)
- **Workspace mode selection** — `--workspace sdk|vs|adhoc|auto` CLI arg and `ROSLYNMCP_WORKSPACE` env var. Adhoc mode reduces Orleans (63 projects) load from 7 minutes to 22 seconds. (#101)
- **Auto-detection of SDK vs Framework projects** — peeks at .csproj to choose .NET SDK or VS MSBuild automatically.
- **Large solution warning** — logs a warning with actionable advice when >30 projects detected before MSBuild load.
- **Token estimation in logs** — per-tool `~Ntok` and session cumulative `(Ntot)` in every log line. (#84)
- **PID in log lines** — all log entries include `[pid]` for multi-instance disambiguation. (#96)
- **`scope.Error<T>()` method** — error returns now estimate tokens and log properly. (#94)
- **PreToolUse enforcement hook** — `scripts/enforce-roslyn-tools.sh` blocks Read/Grep/Edit on .cs files with actionable error. (#98, #100)
- **LogViewer themes** — dark, light, parchment (original), system-following. Dropdown + `T` key cycling. Persisted in localStorage. (#91)
- **LogViewer SSE clear** — INFO entries containing "clear" auto-clear the viewer.
- **No-cache headers** on LogViewer HTML endpoint.
- **Battle-test results** — first comparative benchmark against Spectre.Console (26 projects) and Orleans (63 projects). 38-69% token savings on refactoring workflows. (`docs/battle-test-results.md`)
- **Multi-instance architecture design doc** — named-pipe workspace service, read-only/read-write modes. (`docs/plans/multi-instance-architecture.md`, #86)
- **Complete agent instructions reference** — `docs/AGENT-INSTRUCTIONS.md` with full, compact, and subagent versions. (#77)
- **CODE_OF_CONDUCT.md** and **SECURITY.md** for community standards.

### Fixed
- **`.slnx` parser** — `.Elements("Project")` → `.Descendants("Project")`. Projects nested inside `<Folder>` elements (like Orleans) are now found. (#102)
- **CRLF-agnostic pattern matching** — `BuildLiteralRegex` makes literal newlines match both `\n` and `\r\n`. `normalizeLineEndings` parameter on `replace_in_file`. (#80)
- **Workspace reload for new files** — FSW detects new .cs files, `ReloadIfNeeded()` rebuilds the workspace under write lock. No more server restarts. (#71)
- **Test harness pre-build** — `dotnet run` build output no longer breaks MCP stdio protocol. (#74)
- **MSBuild discovery logging** — logs on first workspace load (not at startup when it's "not attempted").
- **Stale MSBuild log removed** from startup.
- **`scope.Outcome` on all success paths** — 7 tools were missing token estimation on success returns. (#102)
- **UTF-8 BOM stripped** from community standard files. (#76)

### Changed
- **Result records** — ALL anonymous `new { }` types replaced with typed records across all 33 tools. `ErrorResult`, `ToolResults.cs` with 30+ record types. JSON field names preserved — no breaking change. (#74, #79, #81, #82)
- **`Failed<T>` token estimation** — error paths now estimate tokens.
- **SearchFiles/SemanticSearch** — switched from void `Outcome` to `Outcome<T>` for token estimation.
- **README rewritten** — hero section, quick start, tool catalog, agent instructions, help wanted. (#74, #77, #78, #88)
- **MSBuild discovery messages** — `"resolved via PATH (.NET SDK found on PATH)"` etc. for clarity.
- **FileLogger** — `FileLogger` injected into `WorkspaceManager` → `WorkspaceInstance` for load timing and reload logging.

---

## [0.7.0-alpha] — 2026-03-28

### Added
- **`roslyn_get_member_body`** — returns the full source of a single method/property/field/type by name; handles partial types. The flagship token-reduction tool (#28)
- **`roslyn_change_signature`** — adds parameters in non-breaking mode with forwarding overload + `[Obsolete]`. Strategy pattern architecture ready for Phase 2 (#7)
- **`roslyn_apply_signature_change`** — applies or rejects signature changes via token (same workflow as rename)
- **Token-based pagination cache** — `PaginationCache` with `ReadOnlyMemory<T>` mutation protection, sliding-window 60s TTL. Agents pass `page_token` to get subsequent pages without re-executing the query (#40)
- **`AllSymbolsFinder`** — finds all symbols matching a name (not just the first), used by `find_references` for complete results
- **`SymbolFormatter`** — shared static utility for method/property/field/event/type signature formatting
- **LogViewer overhaul** — keyboard shortcuts (1-6 filters, S, Ctrl+F, Ctrl+L, Esc, Q, ?), search bar with live highlighting, FAIL filter (failed TOOL entries), legend toggle, equal-width buttons

### Changed
- **`roslyn_list_types`** — without `namespaceFilter`, returns only source-defined types (no more 829K character context-window bomb). Added skip/take/page_token (#29)
- **`roslyn_find_references`** — without `containingType`, searches ALL matching symbols and unions results. Response includes `symbols_searched` field (#29)
- **`roslyn_get_diagnostics`** — added `severity` filter ('errors', 'warnings', 'all') (#30)
- **`roslyn_get_symbol_info`** — returns structured JSON instead of pipe-delimited string (#30)
- **`roslyn_get_project_info`** — added `directOnly` parameter (default true) for direct NuGet references only (#30)
- **`roslyn_get_type_members`** — added `includeInherited` parameter for base type members (#30)
- **`roslyn_semantic_search`** — added `containingKind` filter for syntax-scoped search (#30)
- **Complete pagination coverage** — `list_files`, `get_trivia`, `list_types` now have skip/take/page_token. All 10 paginated tools wired through `PaginateAndStore`/`TryServeCachedPage`
- **DRY `Math.Clamp`** — moved into `TryServeCachedPage` with explicit `maxTake` parameter (no defaults)
- **Log format redesign** — local time (no date), MSB/ADH workspace indicators (no more nullable `---`), tool name without `roslyn_` prefix, separate subject column, `[HIT]`/`[MISS]` cache tags, column-aligned output

### DRY Extractions
- `FindSymbol` → `RoslynMcpTool` base class (was duplicated 6x)
- `FormatSymbolName` → `RoslynMcpTool` base class (was duplicated 3x)
- `FormatModifiers` → `SymbolFormatter` (was duplicated 4x)
- `FormatMethod`/`FormatProperty`/`FormatField`/`FormatEvent`/`FormatType` → `SymbolFormatter` (was duplicated 3x each)
- `FindSyntaxTree` → `RoslynMcpTool` base class (normalizes + suffix matches, used by 8+ tools)

### Docs
- AGENTS.md updated: constructor pattern, rename API, tool tips section, tool count 33
- README: highlights solution loading, pagination, get_member_body, smart build 17ms, active roadmap
- `docs/plans/pagination-cache.md` — design doc for token-based pagination
- `docs/plans/oop-dry-opportunities.md` — 7 refactoring opportunities in 3 phases

---

## [0.6.0-alpha] — 2026-03-28

### Fixed
- **cacheLock contention** — workspace loading (multi-second MSBuild) no longer holds the cache lock; other tool calls can still hit cached workspaces during loading. Double-checked pattern handles concurrent loads.
- **`msbuildRegistered` volatile** — double-checked locking read outside lock needs `volatile` for correctness on ARM (.NET ECMA-335 memory model)
- **FSW feedback loop** — `MSBuildWorkspace.TryApplyChanges` writes text back to disk, re-triggering the FSW. New `ApplyChangesWithFswSuppressed` method disables FSW during writes (#49)
- **SolutionDiff O(n*m) LCS memory** — replaced full DP table (~400 MB for 10K-line files) with O(n+m) greedy line matching using dictionary + binary search (#26)
- **GetTriviaTool bounds checking** — clamp `startLine`/`endLine` to valid range instead of crashing on out-of-range input
- **DiagnosticsTool single-file performance** — use `GetSemanticModel(tree).GetDiagnostics()` instead of full-project compilation for single-file queries
- **ApprovalStore eviction** — cap pending operations at 10, evict oldest. Each holds two `Solution` snapshots; previously accumulated without bound (#27)

---

## [0.5.0-alpha] — 2026-03-27

### Fixed
- **FormatModifiers** — `protected internal` and `private protected` were silently dropped in FileOutlineTool, GetSymbolDefinitionTool, TypeMembersTool. Extracted to shared `FormatModifiers` in `RoslynMcpTool` base class.
- **DiagnosticsTool** — errors now sorted before warnings (`OrderByDescending`); error path returns structured object instead of `.ToString()` on anonymous type
- **TypeHierarchyTool** — `FindDerivedClassesAsync` replaced with `FindImplementationsAsync` for interface types (was returning nothing for interfaces)
- **Rename workflow** — store base solution in `PendingOperation` so apply uses the same snapshot as preview; `SymbolKey` includes parameter types to distinguish overloaded methods (#22)
- **ReplaceInCodeTool** — added `ParseStatement` fallback for statement/directive syntax kinds; added `RecordDeclaration` to member arm
- **SimpleNameFinder** — verify namespace qualification for dotted names instead of silently discarding the prefix
- **SemanticSearchTool** — replaced per-token regex with line-level matching so multi-token patterns work in `code` context; comments/strings excluded via `DetermineContext`
- **PreviewRenameTool** — error path uses `JsonSerializer.Serialize` instead of `.ToString()` on anonymous object (#46)
- **MSBuild FileSystemWatcher** — added FSW for MSBuild workspaces; external file changes (IDE edits, `rm`, non-Roslyn agent tools) now detected. Handles modify (push SourceText) and delete (clear text). New files require server restart (#47)

---

## [0.4.0-alpha] — 2026-03-27

Folds in previously unreleased v0.3.0-alpha work (multi-project infrastructure) plus v0.4.0 bug fixes, solution-level loading, and comprehensive IO hardening.

### Added (v0.3.0 — multi-project infrastructure)
- **Multi-project support** — all 25 tools accept `projectPath` parameter; switch projects mid-session without restarting
- **WorkspaceResolver** facade layer for consistent per-tool project resolution and structured error handling
- **RoslynMcpTool** base class — `TryGetCompilation()` / `TryGetProject()` with `[NotNullWhen]` attributes; eliminates boilerplate from every tool
- **AdhocWorkspace restored** — directories without `.csproj` now fully supported with FileSystemWatcher for incremental updates
- **Exceptions.cs** — `InvalidProjectPathException`, `ProjectNotFoundException`, `MultipleProjectsFoundException` with structured messages
- **`roslyn_semantic_search`** tool — Roslyn syntax-tree filtering by context (comments, strings, identifiers, xmldocs, code)
- **Version centralization** — `Directory.Build.props` with `VersionPrefix`/`VersionSuffix`; Git commit SHA automatically appended to `InformationalVersion` for traceability

### Added (v0.4.0)
- **Solution-level workspace loading** — WorkspaceManager searches upward for .sln/.slnx and loads the full solution; cross-project references, rename, and find-implementations work across all projects
- **`.slnx` support** — parses the new XML solution format and loads each project into the same MSBuildWorkspace
- **`roslyn_debug_attach`** tool (DEBUG builds only) — launches JIT debugger dialog for mid-session VS attach
- **`Paginate<T>` helper** in `RoslynMcpTool` base class — DRY pagination with bounds checking
- **`ResolveFilePath` helper** — suffix-match fallback for file path resolution; preserves backward compat when rootPath is the solution directory
- **`GetFilesSafe` helper** — `Directory.GetFiles` with exception guarding
- **Comprehensive planning docs** — ROADMAP.md, code audit (23 bugs documented), tool assessment, style preservation plans

### Changed
- **BREAKING: `projectPath` now REQUIRED** — all 25 tools require explicit project path; no CWD fallback (prevents catastrophic drive root enumeration, Issue #9 prerequisite)
- All tools migrated to `RoslynMcpTool` base class pattern
- All tools renamed with `roslyn_` prefix (e.g. `get_type_members` → `roslyn_get_type_members`) for unambiguous identification in agent tool lists
- All tools annotated with `ReadOnly`, `Destructive`, or `Idempotent` hints via `McpServerToolAttribute`
- **File logging** — every tool invocation, server start/stop, and workspace error logged to a rotating file; controlled via `ROSLYNMCP_LOG_PATH` env var
- **`ToolScope`** — `BeginTool(name, subject?)` returns a disposable scope with `Failed<T>()`, `Outcome<T>()`, `Record()` for fluent per-tool logging
- **Tools reorganized** into semantic subfolders: `Analysis/` (16), `Search/` (3), `Editing/` (2), `Rename/` (2), `Build/` (3)
- **WorkspaceManager split** into partial classes: `WorkspaceManager.cs` (cache + API), `WorkspaceManager.Resolution.cs` (path resolution), `WorkspaceManager.Instance.cs` (workspace lifecycle)
- **Per-project compilation cache** replaces single `Compilation` field (supports multi-project solutions)
- **`GetRootPath`** returns solution directory when loaded from a solution; project directory otherwise
- **LINQ optimization** — tools use materialize-once pattern when enumerating multiple times

### Fixed (v0.3.0)
- **AdhocWorkspace safety** — protected directory enumeration with try/catch for `UnauthorizedAccessException`; skips system/hidden directories and common large folders
- **Root directory protection** — fail-fast check prevents accidental scanning of drive roots
- **stdout contamination** — server startup messages use `Console.Error.WriteLine()` to avoid corrupting MCP protocol stream
- **Server crash on startup** — `MSBuildLocator.RegisterDefaults()` moved into deferred `EnsureMSBuildRegistered()` with double-checked locking
- **`LoadMSBuildWorkspace` unhandled exception** — wrapped in try/catch with context
- **`ListTypesTool` / `DiagnosticsTool` return type** — changed `string[]` → `object` for correct MCP SDK serialization
- **`AppDomain.UnhandledException` handler** — fatal crashes now write `[FATAL]` to log file

### Fixed (v0.4.0)
- **5 unregistered tools** — FileOutlineTool, GetSymbolsInScopeTool, GetUsingsTool, GetLineCountTool, SearchFilesTool were missing `[McpServerTool]` attributes; 20% of tools were silently invisible (#15)
- **Pagination crash** — `AsSpan(skip, ...)` threw `ArgumentOutOfRangeException` when `skip >= results.Length` in 5 tools (#16)
- **AdhocWorkspace root path** — `GetRootPath` returned parent directory instead of the directory itself (#17)
- **Semantic search duplicate results** — multi-TFM projects produced duplicate matches; deduplicated by `FilePath` (#18)
- **SolutionDiff `\r\n` splitting** — diff output had spurious `\r` on Windows (#19)
- **MSBuildWorkspace comment** — corrected misleading comment claiming auto file watching (#19)
- **LRU eviction gap** — `GetSolution`/`GetProject`/`GetWorkspaceInfo` were missing eviction on cache miss
- **IO exception audit** — guarded all unprotected filesystem calls with exception filters; added `UnauthorizedAccessException` to `ReplaceInFileTool` catch blocks; guarded `SolutionDiff.ApplyToDiskAsync`, `RestorePackagesTool.FindProjectFile`, `CleanSolutionTool.FindProjectFile`

### Security
- **Removed CWD fallback** — prevents server started from drive roots from enumerating entire drives
- **ArgumentException on missing projectPath** — clear error message when agent fails to specify project

⚠️ **Known Issue:** RoslynMcp currently has unrestricted filesystem access. Only use with trusted agents and on projects you control. Filesystem security boundaries are planned for a future release. See [Issue #9](https://github.com/MadQ/RoslynMcp/issues/9).

---

## [0.2.0-alpha] - 2026-01-XX (releases removed — broken builds)

### Added
- **23 MCP tools** covering all core agent workflows:
  - Discovery: `roslyn_search_files`, `roslyn_list_types`, `roslyn_get_file_outline`, `roslyn_get_project_info`, `roslyn_get_usings`
  - Type Understanding: `roslyn_get_type_members` (enhanced), `roslyn_get_type_hierarchy`, `roslyn_find_implementations`, `roslyn_get_symbol_documentation`
  - Navigation: `roslyn_get_symbol_info`, `roslyn_find_references`, `roslyn_get_symbol_definition`
  - Code Generation: `roslyn_get_symbols_in_scope`
  - Validation: `roslyn_get_diagnostics`, `roslyn_build_project` (smart Roslyn-first)
  - Editing: `roslyn_replace_in_file`, `roslyn_replace_in_code`, `roslyn_list_files`
  - Refactoring: `roslyn_preview_rename`, `roslyn_apply_rename`
  - Maintenance: `roslyn_clean_solution`, `roslyn_restore_packages`
  - Debug: `roslyn_respawn` (DEBUG only)

### Enhanced
- **`roslyn_get_type_members`**: Now returns full signatures with parameter types, return types, modifiers, and XML doc summaries (was just names)
- **`roslyn_build_project`**: Smart Roslyn-first behavior — checks diagnostics before running MSBuild, skips build if errors exist
- **Diagnostic filtering**: NETSDK1209 and other non-actionable SDK warnings automatically filtered from output

### Added (Infrastructure)
- Comprehensive test suite: 23 tests covering all tools (100% pass rate)
- TestHarness project for dogfooding (RoslynMcp tests itself)
- GitHub-ready documentation: README, CONTRIBUTING, INSTALLATION
- MIT License
- .gitattributes for consistent line endings

### Fixed
- Multi-target framework builds now require explicit `-f` flag
- Metadata symbols (external types) handled gracefully in `roslyn_find_implementations`
- Preview rename validation handles PascalCase/camelCase property names

---

## [0.1.0-alpha] - 2026-01-XX (Initial Development — release removed)

### Added
- Core MCP server infrastructure
- WorkspaceManager with MSBuildWorkspace and AdhocWorkspace support
- Initial tool set: `roslyn_get_type_members`, `roslyn_get_diagnostics`, `roslyn_find_references`, `roslyn_get_symbol_info`
- Rename tools: `roslyn_preview_rename`, `roslyn_apply_rename` with approval flow
- ApprovalStore for session-scoped rename approvals
- SolutionDiff for unified diff generation
- FileSystemWatcher integration for AdhocWorkspace
- ImplicitUsings enabled (.NET 8/10/11 multi-targeting)

### Technical Details
- Multi-targeted: net8.0, net10.0 (net11.0 auto-added when .NET 11 SDK detected)
- C# 14 preview language version
- Roslyn 5.3.0
- ModelContextProtocol 1.1.0
- Microsoft.Extensions.Hosting for DI and lifetime management

---

## Future Considerations

### Performance
- Lazy symbol loading for large workspaces
- Incremental compilation cache
- Parallel compilation for multi-project solutions

### Features
- Support for F# projects
- Support for VB.NET projects
- Cross-solution symbol search
- Project-wide rename with dependency tracking
- Code fix suggestions (Roslyn analyzers integration)

### Developer Experience
- VS Code extension for easier configuration
- GitHub Copilot Chat integration examples
- Sample MCP client for testing

---

[Unreleased]: https://github.com/MadQ/RoslynMcp/compare/r0.7.4.1-alpha...HEAD
[0.7.4-alpha]: https://github.com/MadQ/RoslynMcp/releases/tag/r0.7.4.1-alpha
[0.7.3-alpha]: https://github.com/MadQ/RoslynMcp/compare/v0.7.2-alpha...v0.7.3-alpha
[0.7.2-alpha]: https://github.com/MadQ/RoslynMcp/compare/v0.7.1-alpha...v0.7.2-alpha
[0.7.1-alpha]: https://github.com/MadQ/RoslynMcp/compare/v0.7.0-alpha...v0.7.1-alpha
[0.7.0-alpha]: https://github.com/MadQ/RoslynMcp/compare/v0.6.0-alpha...v0.7.0-alpha
[0.6.0-alpha]: https://github.com/MadQ/RoslynMcp/compare/v0.5.0-alpha...v0.6.0-alpha
[0.5.0-alpha]: https://github.com/MadQ/RoslynMcp/compare/v0.4.0-alpha...v0.5.0-alpha
[0.4.0-alpha]: https://github.com/MadQ/RoslynMcp/releases/tag/v0.4.0-alpha
