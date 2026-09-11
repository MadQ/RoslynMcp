# Changelog

All notable changes to RoslynMcp will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

> Several improvements in this cycle were informed by studying other Roslyn MCP servers — in particular
> [JoshuaRamirez/RoslynMcpServer](https://github.com/JoshuaRamirez/RoslynMcpServer) (load timeout, position-first resolution),
> [MarcelRoozekrans/roslyn-codelens-mcp](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp) (dropped-reference detection),
> and [darylmcd/Roslyn-Backed-MCP](https://github.com/darylmcd/Roslyn-Backed-MCP). Techniques only; no code was reused.

### Added
- **Position-based symbol resolution** — `roslyn_find_references`, `roslyn_find_callers`, and `roslyn_preview_rename` accept optional `filePath`+`line`(+`column`) to pinpoint one specific overload, local, or parameter instead of resolving by name.
- **Opt-in analyzer diagnostics** — `roslyn_get_diagnostics` gains `includeAnalyzers` to also run the project's analyzer references and include their findings; analyzer failures degrade to compiler-only output with an explanatory hint.
- **Configurable workspace load timeout** — MSBuild loads are bounded by `ROSLYNMCP_LOAD_TIMEOUT_SECONDS` (default 300; `0` disables) instead of hanging indefinitely when MSBuild wedges.
- **Post-load reference validation** — loads detect projects whose metadata references were silently dropped by a contended design-time build, retry once on a fresh workspace, and surface anything persistent via `load_warnings` on `roslyn_get_project_info`.
- **`roslyn_check_drift`** — new read-only tool that detects FileSystemWatcher misses (network drives, buffer overflow) by comparing on-disk timestamps against the workspace's last sync, reporting drifted or deleted files with a hint to resync via `roslyn_respawn`.
- **Load benchmarks** — `benchmarks/RoslynMcp.Benchmarks` BenchmarkDotNet suite measuring cold solution load, warm compilation cache hits, incremental text-change recompiles, and solution-wide reference search, with a recorded baseline in `benchmarks/BASELINE.md`.
- **Project-local server settings (`.madq_roslynmcp.json`)** — `setup-project` now also offers to write a commit-worthy server config at the repo root, so a team shares per-project preferences instead of every contributor hand-setting global config. The server discovers the file by walking up from each request's resolved project path (startup CWD is unreliable across MCP clients) and fills in `elicit` and `workspace` where no CLI arg or env var was given — precedence: CLI arg > env var > project file > built-in default. Machine-specific settings (log/MSBuild/backup paths) and `preload` are deliberately never read from the committed file. Reruns of `setup-project` preserve earlier choices and unknown keys; `FindRepoRoot` now also recognizes worktree/submodule checkouts where `.git` is a file. (#220)
- **Visual Studio version pin (`--vs-version` / `ROSLYNMCP_VS_VERSION` / `vsVersion`)** — legacy (non-SDK) .NET Framework projects still failed with the `Microsoft.Build.Shared.XMakeElements` type-initializer crash on machines whose newest Visual Studio is 2026 (18.x), even with the #258 BuildHost pin. The pin did exactly what it said — the BuildHost log read `Registered MSBuild 9999.0 instance at E:\Dev\Vs18` — but bare `vswhere -latest` and the BuildHost's own `OrderByDescending(Version)` agree on "newest", so in `vs` mode the pin re-selected the crashing instance, and in `auto`/`sdk` mode no pin was applied at all. The only escape was `--msbuild-path`, a machine-specific path that `ProjectConfig` rightly refuses from the committed file. `--vs-version 17` (or `17.14`, or a raw vswhere range like `[17.0,18.0)`) now passes `-version` to vswhere; in `vs` mode it selects the server's instance and fails with a message naming the pin when nothing matches, and in `auto`/`sdk` mode it steers only the .NET Framework BuildHost — the part that actually loads legacy projects — while the server keeps the .NET SDK. A version is portable, so unlike `msbuildPath` it is honored from `.madq_roslynmcp.json` (`"vsVersion": "17"`), and `setup-project` offers it when the workspace mode is `vs`. `--msbuild-path` still wins; `adhoc` ignores it; a malformed pin is logged at startup instead of silently dropped. Precedence: CLI arg > env var > project file > newest installed. (#265)

### Changed
- **Locked-mode restore genuinely enforced across all CI platforms** — the .NET 10 SDK update (10.0.302) broke CI by adding the machine RID and a newer `ILLink.Tasks` to the restore graph, which #226 unblocked with `--force-evaluate` (silently disabling lock enforcement). Fixed durably instead: `global.json` pins the SDK (10.0.302, `rollForward: disable`) so the graph is stable, and `RuntimeIdentifiers` in `RoslynMcp.csproj` makes `packages.lock.json` multi-RID and platform-independent (`win-x64;linux-x64;osx-arm64`) so one committed lock satisfies every runner. `--force-evaluate` removed; a new required `lock-check` CI job fails a PR whose lock is stale or not multi-RID. (Pins builds to net10.0 under the SDK constraint; net11 multitargeting resumes when net11 is GA.) (#222, #223, #227)
- **Deterministic CI builds** — `ContinuousIntegrationBuild` is now set when `GITHUB_ACTIONS` or `ContinuousIntegrationBuild` is already true, giving reproducible `/pathmap`'d source paths in CI without affecting local debugging. (#224)
- **Source Link + embedded symbols in the published tool** — the package now carries Source Link metadata (repository URL + commit) and embedded PDBs, so stack traces and step-into debugging resolve to the exact source. Source Link is built into the .NET SDK for GitHub repos (no extra package); deterministic source paths come from the CI `ContinuousIntegrationBuild`. `DebugType=embedded` now applies to the `Pack` configuration too, which previously defaulted to portable PDBs that pack omitted — so the tool shipped symbol-less before. (#225)
- **`ModelContextProtocol` updated 1.2.0 → 1.4.1** — current stable C# MCP SDK; the elicitation API surface we use is unchanged, so the bump is behavior-neutral for this stdio server. Not adopting 2.0.0-preview.x (experimental MRTR / task-augmentation surface, deferred). (#221)
- **NuGet lock files enabled** — `RestorePackagesWithLockFile` in `Directory.Build.props` pins the full transitive dependency graph in a committed `packages.lock.json` per project, making restores reproducible and future dependency drift a reviewable diff. (#221)
- **`--elicit` / `ROSLYNMCP_ELICIT` is now discoverable** — the interactive-elicitation opt-in is documented in `madq-roslynmcp --help`, INSTALLATION.md, and AGENTS.md, and offered as an opt-in prompt in `madq-roslynmcp setup` (writes `"args": ["--elicit"]` into the MCP server entry). The choice is preserved across `madq-roslynmcp update`, which also no longer clobbers other user-added server args. Default is unchanged: the agent-first structured candidate list. Not all MCP clients support elicitation; unsupported clients fall back to the candidate list. (#219)
- **Cancelling a rename or signature change is reported as success, not failure** — calling `roslyn_apply_rename` or `roslyn_apply_signature_change` with approval `n` now returns a successful, non-error result (`error: null`, log `success: true`) instead of an `error: "rejected"` failure. Declining a preview is a deliberate outcome, not a broken call, so it no longer shows red in the log viewer or surfaces an error to the agent; the token is still consumed. Genuine failures (invalid approval, expired token, backup/write errors) are unchanged. (#218)
- **Analyzer assemblies are shadow-copied** — `includeAnalyzers` diagnostics runs load analyzer DLLs from a per-content shadow copy under `%LOCALAPPDATA%\RoslynMcp\analyzer-shadow`, so the analyzed project's `bin\` output is never locked by the server; stale shadow copies are pruned by the server heartbeat.

### Fixed
- **Evicting a workspace no longer wedges every tool call** — `WorkspaceInstance.Dispose` quiesced its debounce and deferred-retry timers with `Timer.Dispose(WaitHandle)` followed by `ManualResetEventSlim.Wait()`. The timer signals the *kernel* event; the slim event's `Wait` watches only its own managed flag, which nothing sets — so the wait never returned once a timer existed, even with no callback running. `WorkspaceManager` ran that teardown inside `cacheLock` (LRU victims past their 30 s grace period via `SweepRetired`, and the loser of a load race), the lock every tool call takes to resolve its workspace, so the first eviction of a workspace whose watcher had ever fired stopped the server answering for good — a full TestHarness run reproduced 12+ minutes of silence after one project load. The quiesce now waits on a kernel `ManualResetEvent`, bounded by the existing 30 s timeout with a logged error instead of a hang; evicted instances are collected under the lock and disposed on the thread pool after it; each teardown logs `Dispose — Released '<path>' in N ms`. (#277)
- **Editing a non-compilation file no longer forces a full workspace reload** — `roslyn_insert_lines` on `CHANGELOG.md`, or `roslyn_replace_in_file`/`roslyn_write_file` on any `.md`/`.txt`/unrelated `.json`, wrote the file and then called `InvalidateFile`, whose untracked-path branch bumped `reloadVersion` unconditionally — so the next compilation-needing call paid a multi-second MSBuild reload (and, per #235, another chance for a contended design-time build to drop metadata references). The FileSystemWatcher was never the cause: its filter is `*.cs`. A single `RequiresReload` predicate now drives both `InvalidateFile` and the watcher flush: build output under `bin/`/`obj/` is dropped first (NuGet's generated `.props`/`.editorconfig` live there), a new `.cs` reloads, MSBuild evaluation inputs reload (`.csproj`, `.props`, `.targets`, `.sln*`, `.editorconfig`, `.globalconfig`, `.ruleset`, `.resx`, `global.json`, `nuget.config`, `packages.lock.json`), anything in a project's `AdditionalDocuments`/`AnalyzerConfigDocuments` reloads, and everything else is a workspace no-op (the pagination cache is still cleared). Every reload flag is now logged with path and reason (`Reload — Flagged (new document): …`) — previously the log showed that a reload happened but never which file caused it. Known limitation: MSBuild `EmbeddedResource` items are not exposed by Roslyn and cannot be recognized; they do not affect Roslyn-served results. New `ReloadFlaggingTests` harness group asserts `roslyn_check_drift.reload_pending` after each kind of write, in a temp fixture project. (#273)
- **`roslyn_replace_in_code` no longer leaves mixed line endings** — the replacement node was spliced in with the line endings of the replacement *text* (LF, as MCP clients send it), not the file's. On a CRLF file one semantic replace produced a file that was CRLF everywhere except the replaced member; `git ls-files --eol` reported `w/mixed`, `git diff` warned `LF will be replaced by CRLF`, and every touched file needed a manual re-normalize before committing. The replacement text is now normalized to the file's dominant style before parsing — the same `normalizeLineEndings` parameter (default `true`) `roslyn_replace_in_file` already has, so the behaviour is discoverable and can be switched off. The shared helper also became symmetric: a CRLF replacement into an LF file is converted to LF, where before only the LF→CRLF direction was handled. (#268)
- **`roslyn_replace_in_code` refuses a constructor whose name does not match its type** — the C# parser accepts any `Identifier(...) { }` inside a type as a constructor and leaves the name check to the compiler (CS1520), so the per-node parsing added in #267 still let a forced batch across differently named types write a `Widget` constructor into `Gadget` with a clean syntax verdict. The TestHarness case added for that scenario in #271 had been failing since merge (CI runs the build, not the harness). The check now lives in `ParseReplacement` and applies to destructors too. (#267 follow-up, found via #268)
- **`roslyn_replace_in_code` accepts constructors, and `dryRun` actually validates** — the replacement validator dispatched on a hard-coded list of node kinds; anything not on it (constructors, destructors, operators, events, indexers, enum members, delegates) fell through to `ParseExpression`, so a byte-for-byte valid constructor was rejected with `Unexpected token '{'; Invalid expression term 'bool'`. The replacement is now parsed in the grammatical position of the node it replaces: a type member inside a dummy type that borrows the enclosing type's name (and kind, for enums), so constructors parse as constructors; statements as statements; `using` directives as a compilation unit; everything else as an expression. The replacement must be exactly one node — smuggling a second member (or closing the type early) is refused. `dryRun` used to return before any parsing, so a passing dry run said nothing about whether the real write would pass; it now runs the same parse and tree validation and only skips the write. `ctor`/`constructor` are accepted as `nodeKind` aliases and `textPattern` matches constructor, destructor, event, delegate, and enum-member names properly instead of the whole node text. (#267)
- **Auto-detection judges a solution by the projects it references** — in `auto` mode a `.sln`/`.slnx` load peeked at whatever `.csproj` the file system enumerated first under the solution directory (`FindFirstCsproj`), which on a real repo was an SDK-style backup copy that sorted before the real project and was not in the solution at all. A legacy solution therefore auto-detected as `Sdk`, skipped the Visual Studio BuildHost pin, and failed to load. `DetectLoadStyle` now reads the project list from the solution file itself (`.sln` `Project(...)` lines or `.slnx` `<Project Path>` elements, text-parsed because MSBuild is not registered yet), skips projects missing on disk, and picks `Vs` if **any** referenced project is legacy — one is enough to route the load through the .NET Framework BuildHost. A solution listing nothing readable falls back to a bounded nearest-first directory scan that skips `bin/`, `obj/`, `packages/`, `node_modules/`, and dot-folders. The log line now says which file decided: `auto-detected Vs: DenseSense2022.csproj is legacy-style (DenseSense2020.sln, 5 projects)`. (#266)
- **`dotnet pack` can no longer silently produce a wrong-id package** — the dotnet-tool packaging metadata (`PackAsTool`, `PublishSingleFile=false`, the CI RID exclusion) was gated entirely on `Configuration == 'Pack'`, so packing with `-c Debug`/`-c Release` skipped it and emitted a plain `RoslynMcp.<ver>.nupkg` that `dotnet tool install MadQ.RoslynMcp` reported as "not found" — and README/INSTALLATION Option C actively instructed the broken `-c Debug`. The package identity (`PackageId`/`ToolCommandName`) is now unconditional so it can never diverge by config; a new `PackDebug` configuration packs a debug-flavored tool; a guard target fails any pack under a non-tool config with a message pointing at `-c Pack`/`-c PackDebug`; and the docs now use `-c PackDebug`. (#260)
- **Unchanged files no longer round-trip through the workspace** — a changed file's text was re-read and applied unconditionally. The only up-to-date check was a size comparison, and only for writes RoslynMcp made itself. Disk text is now compared against the document's current `SourceText` before applying, so an identical rewrite is skipped entirely — as is a same-length edit, which the size check could never catch. Applying identical text was not free: it still ran a `TryApplyChanges`, which writes to disk and flags a full reload when it fails.
- **Deleting several files flags one reload, not one per file** — the delete loop incremented `reloadVersion` unconditionally per file. Each increment moves the generation, and an in-flight reload discards its work when the generation no longer matches the one it captured, so a multi-file delete threw away a full workspace load per extra file.
- **A pending reload is now reported instead of being silently invisible** — `roslyn_check_drift` gains `reload_pending`, and `MarkSynced()` no longer runs when a flush could only *flag* a reload rather than apply the change. Previously the sync clock advanced at flag time, so the workspace could be behind disk while drift reported "in sync"; and because drift is an mtime comparison over documents the workspace already has, a newly added file was structurally invisible to it. This corrects the reasoning given in #238, which claimed drift already covered this case — it did not. (#235 follow-up)
- **A bad workspace reload no longer latches wrong symbol results** — an MSBuild design-time build that runs while another process holds a file it needs (commonly an agent writing source, or the `.csproj` itself) can silently produce projects with **zero metadata references**. Symbol queries over that workspace then return wrong-but-plausible results instead of failing. The initial load already retried once on a fresh workspace, but the reload path — the likelier victim, since it is often triggered by the very writes causing the contention — only warned, so a bad reload was promoted and stayed live until some later unrelated reload happened to succeed. `ReloadIfNeeded` now retries the same way, and **refuses to replace a healthy workspace with a reference-less one**: a stale-but-correct compilation beats a fresh-but-wrong one. The discarded reload stays pending behind a short cooldown so it retries once the contention clears, rather than costing a multi-second load on every tool call. Reference health is checked for MSBuild workspaces only — an `AdhocWorkspace` project legitimately has no metadata references. (#235)
- **Workspace load failures are now visible from the tools that show the symptom** — `load_warnings` was surfaced only by `roslyn_get_project_info`, while the symptom (a flood of phantom `CS0246`/`CS0234`) appears in `roslyn_get_diagnostics` and `roslyn_build_project`. `roslyn_get_diagnostics` now asks the workspace directly instead of inferring load trouble from error volume — so a small project producing only a handful of phantom errors is caught too — and returns `load_warnings` alongside `possible_workspace_load_issue`. `roslyn_check_drift` gains `workspace_healthy`, `projects_without_references` and `last_unhealthy_load`, making it a health probe on two independent axes: source can be perfectly in sync while the workspace holds no references. A `last_unhealthy_load` record is retained across the healing reload that clears `load_warnings`, so an episode stays diagnosable after recovery. (#235)
- **`roslyn_build_project` no longer repeats phantom errors when asked to verify them** — its Roslyn fast-path short-circuited on the same reference-less compilation, so the tool callers reach for to check whether the workspace is lying just repeated the lie, with `forceBuild: true` as the only escape. It now detects an unhealthy workspace, skips straight to a real `dotnet build`, and explains in `hint` why the fast path was bypassed. (#235)
- **`--help` and diagnostics no longer give advice that cannot work** — the `roslyn_get_diagnostics` hint said the workspace was "still resolving dependencies. Wait a few seconds and retry, or call roslyn_build_project to verify real compilation state." The state is latched until a reload, so waiting never helps, and `roslyn_build_project` reproduced the false result. The hint now names the actual remedy (`roslyn_respawn`) and, when the workspace confirms dropped references, states plainly that the errors are phantom. (#235)
- **`--msbuild-path` is honored instead of silently ignored** — the flag was parsed into `ServerArgs.MsBuildPath` and documented in `--help` and INSTALLATION.md, but nothing ever read it: `MSBuildBootstrap.EnsureReady` read `ROSLYNMCP_MSBUILD_PATH` directly, so only the env var worked and the higher-precedence CLI flag did nothing. Discovery now reads the merged `ServerArgs.MsBuildPath` (CLI arg > env var), and the override is checked **before** the mode branch, so `--workspace vs` honors it too — previously VS mode went straight to vswhere and ignored the override entirely. The path may now be a VS `MSBuild\Current\Bin` directory (which ships only `MSBuild.exe`, not `MSBuild.dll`) as well as a dotnet SDK directory. `adhoc` still ignores it — that mode skips MSBuild by design — and an invalid path still falls through to normal discovery. An empty `--msbuild-path ""` no longer masks a valid `ROSLYNMCP_MSBUILD_PATH`. (#231)
- **`--help` no longer misstates env-var precedence** — the environment-variable section was headed "override options above", contradicting the `CLI arg > env var > project file > built-in default` rule printed a few lines below it. The header now states that CLI flags win, and the list is complete: `ROSLYNMCP_MSBUILD_PATH`, `ROSLYNMCP_PRUNE_MIN_RUNS`, `ROSLYNMCP_MAX_CACHED_WORKSPACES`, `ROSLYNMCP_LOAD_TIMEOUT_SECONDS`, and `ROSLYNMCP_DISABLE_PATH_CACHE` were supported but undocumented there. `ROSLYNMCP_LOAD_TIMEOUT_SECONDS` was missing from INSTALLATION.md's table as well. (#231)
- **First `.csproj` load in adhoc mode no longer fails** — with adhoc requested (`--workspace adhoc`, `ROSLYNMCP_WORKSPACE=adhoc`, or `"workspace": "adhoc"` in `.madq_roslynmcp.json`), the first load of a path resolving to a `.csproj` crashed with a `Microsoft.Build.Framework` assembly-load error because routing consulted `MSBuildBootstrap.ResolvedMode`, which is only assigned *after* routing has committed to the MSBuild branch. Routing now also consults the requested effective mode (CLI > env > project file), so the first load goes straight to the adhoc branch; `ResolvedMode == Adhoc` remains as a fallback once the process has skipped MSBuild registration. (#229)
- **Adhoc workspaces are no longer re-loaded on every tool call** — a request whose path resolved to a `.csproj` while adhoc mode was in effect cached the workspace under its containing directory, but adhoc instances expose no `ProjectPaths`, so nothing ever mapped the `.csproj` back to that cache key. Every subsequent call missed the cache and rebuilt a full `AdhocWorkspace` (re-reading every `.cs` file under the root) only to discard it — and the same miss made `WriteAndInvalidate`/`ApplyChanges`/`InvalidateFile` fall through to unsuppressed direct writes. `GetOrLoadInstance` now aliases the requested path to whatever cache key it landed on, including when it loses the concurrent-load race. (#229)
- **VS Code Copilot `sym:` chat-reference prefixes no longer break tool arguments** — Copilot chat serializes backtick-quoted terms as `sym:`-prefixed symbol references (chat-variable form `#sym:`), which the model copies into tool arguments; one observed call reached `roslyn_semantic_search` truncated to a bare `sym:`. Symbol/type/method name parameters now strip the prefix silently (identifiers can never contain `:`; a bare prefix returns a structured error with a recovery hint). The three text-search tools stay **verbatim-first** — a `sym:`-prefixed pattern is searched as-given, and only a zero-match result triggers a stripped retry, labeled with a caution; literal searches for the text `sym:` keep working. (#228)
- **Ambiguous symbol names never mutate a silent first match** — when a name-based lookup in `roslyn_preview_rename` or `roslyn_change_signature` matches multiple symbols, the call fails with a structured candidate list (`candidates: [{ symbol, kind, containingType, filePath, line }]`) so the calling agent can retry with `containingType` or `filePath`+`line` on its own — long-running agent workflows keep moving without human intervention. Starting the server with `--elicit` (or `ROSLYNMCP_ELICIT=true`) opts into asking the user to pick interactively via MCP elicitation instead, when the client supports it.
- **`roslyn_change_signature` generates well-formed code** — the forwarding overload is now a compact expression-bodied stub (`old(...) => new(..., default);`) with correct comma/equals spacing; previously the synthesized nodes were rendered unformatted (`{returnFoo(...);}` with no space after `return`, `,bool x=false`) and the `[Obsolete]` attribute could detach from the stub. The XML doc comment stays on the updated method; the stub carries only the attribute.
- **Unified diffs no longer misalign around insertions** — the diff builder emitted context lines positionally without verifying they still matched, so lines inserted right after a change (e.g. the signature-change stub) could be swallowed as "context" and the rest of the file rendered as spurious delete/re-add churn. Hunks are now built strictly from the matched-line alignment, so context lines are matched lines by construction.
- **Metadata type lookup tolerates cross-assembly ambiguity** — `GetTypeByMetadataName` returns null when multiple referenced assemblies define the same name; lookups now fall back to `GetTypesByMetadataName`, preferring the source assembly.

### Security

---

## [0.8.1-beta] — 2026-07-18 — [Release](https://github.com/MadQ/RoslynMcp/releases/tag/v0.8.1-beta)

### Changed
- **Breaking (pre-1.0): the tool identity is now namespaced under `MadQ`.** To avoid a hard clash with the unrelated [`RoslynMcp`](https://www.nuget.org/packages/RoslynMcp) package already published on NuGet, three identifiers changed: the NuGet package id `RoslynMcp` → `MadQ.RoslynMcp`, the `dotnet tool` command `roslynmcp` → `madq-roslynmcp`, and the MCP server config key `roslyn` → `MadQ.RoslynMcp`. Update your MCP client config to the new server key and command. Your client's cached tool approvals are keyed to the old server name and will prompt again — see the [migration note in the Troubleshooting guide](docs/guides/TROUBLESHOOTING.md#tools-re-prompt-for-approval-after-renaming-the-mcp-server-key). (#215)

### Added
- **Now published on NuGet as [`MadQ.RoslynMcp`](https://www.nuget.org/packages/MadQ.RoslynMcp).** Install the CLI as a global .NET tool with `dotnet tool install -g MadQ.RoslynMcp --prerelease` (invoked as `madq-roslynmcp`).

### Fixed
- **Duplicate pre-tool-use hook entries when migrating from the legacy identity** — the `setup` command no longer appends a second hook entry when it finds an existing one written under an older `roslynmcp`/`dotnet-roslynmcp` command name; the existing entry is updated in place instead. (#215)
- **Third-party MCP entries could be silently overwritten** — `setup`, `verify`, and `update` no longer assume that an existing `RoslynMcp` / `roslyn` / `roslynmcp` config entry belongs to this tool. Because those keys are shared with the unrelated chrismo80/RoslynMcp package, an ambiguous match now requires explicit confirmation before it is overwritten. (#216)

### Security
- **Package is published via NuGet Trusted Publishing (OIDC).** Releases are pushed from GitHub Actions using short-lived OIDC tokens instead of a long-lived API key, giving the published package verifiable build provenance.

---

## [0.8.0-beta] — 2026-07-16 — [Release](https://github.com/MadQ/RoslynMcp/releases/tag/v0.8.0-beta)

### Changed
- **Minimum runtime is now .NET 10** — dropped the `net8.0` target framework; the MCP server targets `net10.0` (`net11.0` auto-added when a .NET 11 SDK is present). Release binaries and building from source now require a .NET 10 (or newer) runtime/SDK.

### Fixed
- **LogViewer initial load ordering** — merged entries are now timestamp-ordered on first load instead of appearing grouped by file
- **Expanded LogViewer entry visibility** — expanding an entry now reveals its bottom edge when the entry is taller than the viewport
- **LogViewer HOOK badge state** — HOOK entries now render with the correct status badge styling
- **`setup-hooks` and `hook` commands unreachable** — both subcommands were fully implemented but never wired into the `Program.cs` dispatch switch; also added both to the `--help` output (closes #185)
- **`TryGetCompilation` relative path rejection**— removed the early `!Path.IsPathRooted` guard that incorrectly rejected valid relative paths (e.g. `src/RoslynMcp/RoslynMcp.csproj`) before `WorkspaceManager` could resolve them; `WorkspaceManager.ResolveProjectPath` already calls `Path.GetFullPath` to handle relative paths correctly, and all path-not-found cases are already covered by `InvalidProjectPathException`; also updated `ProjectPathDescription` to document that relative paths are supported
- **`TryGetCompilation` path cache storing workspace root instead of `.csproj` path** — the path cache introduced alongside the relative-path fix stored `GetWorkspaceInfo().RootPath` (the solution root directory, e.g. `J:\Projects\RoslynMcp`) as the cached value; on the second call the root directory was passed to `GetCompilation`, which loaded an AdhocWorkspace with no BCL references, producing ~18,000 spurious CS0518/CS0246 errors; cache now stores `Path.GetFullPath(originalPath)` — the same resolution `ResolveProjectPath` performs
- **`roslyn_build_project`: project-level diagnostics now populate `target_frameworks`**— `ProjectLevelDiagnosticLine` regex now captures the MSBuild bracket suffix so NU\*/MSB\* errors from multi-target builds correctly report which target frameworks they apply to (closes #169)
- **TestHarness shutdown `TaskCanceledException`** — `WaitForExitAsync` now wrapped in `try/catch(OperationCanceledException)` so the harness exits cleanly when the server doesn't stop within the 5-second window; server process is still killed via the existing `proc.Kill()` fallback (closes #170)
- **BackupStore `meta.json` TOCTOU noise** — `ReadAllMetaEntries` now catches `FileNotFoundException` silently before the general `IOException` handler; file can be deleted by the pruner between the `File.Exists` check and the read without logging a spurious INFO warning
- **`--help` / `-h` flag** — when stdin is not redirected (human terminal) and no args are given, the server now prints help and exits instead of silently starting an MCP server that can't communicate; `--help`/`-h` flags always work regardless of TTY state (closes #181)
- **LogViewer multi-file watch** — LogViewer now tails all matching log files simultaneously instead of switching between them (closes #180)

### Added
- **`roslyn_find_overloads`** — new analysis tool that returns all ordinary method overloads declared on a containing type, with full signatures including generic/default/ref/out parameter details (#211, thanks @rubinashaik2022)
- **`roslyn_get_type_dependencies`** — new analysis tool that returns direct type dependencies from a type declaration and member signatures, including base type, direct interfaces, fields, properties, events, parameters, returns, generic constraints, operators, and conversions (#211, thanks @rubinashaik2022)
- **`roslyn_find_unused`** — new analysis tool that reports private, internal, and effectively internal source symbols with zero direct static references in the loaded solution, with conservative filtering plus `confidence` and `reason` metadata for refactoring guidance (#212, thanks @rubinashaik2022)
- **LogViewer usability upgrades** — added instance filtering, time-window filtering with persisted selection, configurable per-file tail caps with a **Load more** path, **Collapse all**, and Esc cascade behavior for faster log triage
- **Opt-in hook logging** — `dotnet roslynmcp hook --log` now emits HOOK entries so LogViewer can surface pre-tool-use hook activity during debugging
- **Claude Code support in `roslynmcp setup`** — `ClaudeCodeClient` added to agent detection; patches `~/.claude.json` under the `mcpServers` key (same schema as Claude Desktop) (closes #184)
- **`setup-hooks` → `setup-project` command rename** — per-project hook file moves from `.github/hooks/roslynmcp.json` → `.github/roslynmcp.json`; dispatch key updated in `Program.cs`; help text updated (closes #186)
- **`Copilot CLI` agent detection** — `CopilotCliClient` added to `AgentDetector.AllClients`; configures `~/.copilot/mcp-config.json` under the `mcpServers` key (global registration, same session for all projects) (closes #186)
- **Claude Code global pre-tool-use hook setup** — `setup` now prompts to add an advisor hook to `~/.claude.json` `PreToolUse` when Claude Code is selected; hook guides Claude Code to prefer `roslyn_*` tools for `.cs` files — advisory only, nothing is blocked (closes #186)
- **`setup` always shows `setup-project` tip** — a tip line at the end of `setup` output guides users to run `dotnet roslynmcp setup-project` per repo for VS Code Copilot and Copilot CLI (closes #186)
- **Hook server-awareness** — Copilot CLI pre-tool-use hook now suppresses `additionalContext` advice when the MCP server heartbeat is absent or stale (server not running, crashed, or idle >10 min); server writes `%LOCALAPPDATA%\RoslynMcp\heartbeat.{pid}` on startup and refreshes it on every tool call; stale files pruned via `FilePruner`; hook fails open (advice injected) on any check error (closes #187)
- **Prune failure surfacing in `roslyn_info`** — `FilePruner` now records all backup and log pruning failures in a `ConcurrentDictionary<string, ConcurrentBag<DateTimeOffset>>` keyed by error message; `roslyn_info` response includes a nullable `prune_errors` field (absent when none) with message-keyed timestamp arrays, enabling diagnosis of persistent prune failures that were previously silently swallowed (closes #176)
- **`roslyn_find_string_literal`** — new tool for searching string literal tokens specifically; matches against decoded `Token.ValueText` (quotes stripped, escapes resolved) by default; `useGlob: true` for `*`/`?` glob matching (no regex escaping required); `matchRaw: true` to match raw source text instead; returns both `text` (raw) and `value` (decoded) per result; covers all string forms including verbatim (`@""`), interpolated literal parts (`$""`), raw (`"""`), and UTF-8 variants; seenPaths dedup handles multi-targeted projects; paged with `page_token`
- **`roslyn_check_syntax`** — syntax-only mode (fast, no workspace) and semantic mode (full project compilation including project-defined types and global usings); `wrapInClass: true` default wraps snippet in a dummy class for member-level inputs; line numbers mapped back to original snippet (closes #183 prerequisite)
- **TestHarness: `target_frameworks` coverage test** — new `roslyn_build_project: forceBuild populates target_frameworks on CS diagnostics` test verifies that multi-TFM projects populate `target_frameworks` on at least one diagnostic item in the build output
- **LogViewer port auto-increment** — if port 5123 is already in use, the Log Viewer tries up to 10 consecutive ports (5123–5132) before giving up; enables running two instances simultaneously (e.g. Windows + WSL)
- **`Invoke-Git` wrapper in FSW test script** — optional pause-before-git mode lets you review changes in VS's Git Changes window before each commit

### Security
- **Filesystem access boundaries** — hardened path validation to enforce repository/file access limits for the MCP surface (VULN-001, VULN-004)
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
