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
| **Tool Count** | 51 MCP tools (50 public + 1 debug-only: `roslyn_debug_attach`) |
| **Dependencies** | `Microsoft.CodeAnalysis.*` (Roslyn) — MSBuildWorkspace (if .csproj found) → AdhocWorkspace (fallback) |
| **ImplicitUsings** | `enable` — don't add redundant `using` directives |
| **Resources** | [C# MCP SDK](https://csharp.sdk.modelcontextprotocol.io/) • [MCP Spec](https://modelcontextprotocol.io/) |

Five projects:
- `src/RoslynMcp/RoslynMcp.csproj` — MCP server
- `src/TestHarness/TestHarness.csproj` — local testing client
- `src/RoslynMcp.Analyzers/RoslynMcp.Analyzers.csproj` — Roslyn analyzers applied to this codebase; the most direct expression of dogfooding — Roslyn-powered analysis running on the repo that wraps Roslyn
- `src/RoslynMcp.LogViewer/RoslynMcp.LogViewer.csproj` — dev-only log viewer; not part of the MCP server
- `src/RoslynMcp.DevHost/RoslynMcp.DevHost.csproj` — dev-only host an MCP client launches in place of the server, so a new server build can be swapped in under running sessions; never packed, not part of the tool package (see *Testing § Development host*)

Use `roslyn_build_project` to build — not `dotnet build` in a terminal.

---

## Architecture

| Component | Responsibility |
|-----------|----------------|
| `WorkspaceManager` | LRU-cached workspace instances; auto-detects `.csproj` → `MSBuildWorkspace` or directory → `AdhocWorkspace`; loads outside its cache lock (#277), one load per workspace at a time — a call for a workspace whose load is in progress waits for it (`pendingLoads`, keyed by the cache key `PlanLoad` decides up front, #377); exposes `GetCompilation()`, `GetSolution()`, `GetProject()`, `GetWorkspaceInfo()`, `InvalidateFile()`; smart path resolution |
| `WorkspaceResolver` | Per-tool facade over `WorkspaceManager`; exposes `GetCompilation()`, `GetProject()`, `GetRootPath()`, `GetSolution()`, `GetWorkspaceInfo()`, `InvalidateFile()`; throws on path resolution failure (caught and structured by `RoslynMcpTool.TryGetCompilation`/`TryGetProject`) |
| `RoslynMcpTool` | Base class for all tools; provides `TryGetCompilation()` and `TryGetProject()` helpers with consistent error responses; defines the `OptionalProjectPathDescription` constant; `ResolveSymbolProjectArgAsync` (`RoslynMcpTool.SymbolProject.cs`) finds the project that declares a symbol named by a call whose `projectPath` is omitted or a solution (#296): `SymbolFinder.FindSourceDeclarationsAsync` over the solution, one entry per project file (a multi-targeted project is one), none is `symbol not found` unless a referenced assembly has the name, several are a candidate list with each candidate's `project`, never a preference; `SymbolNotFoundError` / `TypeNotFoundError` are the "not found" answers of the symbol and type tools, and add `LoadProblemHint` when the workspace is unhealthy for the project asked about (narrowed with `WorkspaceHealth.Covering`; a solution counts every project) — a project that lost its references has none of their types, and "not found" alone reads like a misspelled name (#447) |
| `SearchFilesTool` | `roslyn_search_files` — regex search across workspace files with paging; prerequisite for finding code to analyze with Roslyn tools. The hits are grouped by file and, within a file, by the declaration each is in (`EnclosingDeclaration`, from the syntax tree alone, #460): `files[].in[]` names the declaration (`method Enqueue`), its `lines` from the comment attached to it (the rule of `roslyn_replace_in_code`, #416) to its end, and `at`, the line and column its node starts on; a match on the declaration's own name has `declares: true`. Agents stay with the text search, so the text search carries what they did next by hand: telling a declaration from a use, and finding the member's boundaries. What a use refers to is not answered there. A `filePattern` that is not limited to `.cs` names (`*.csproj`, `*.md`, `*`) also searches the other files under the root (#327): the files `roslyn_list_files` lists, with the same ignored directories, read from disk line by line through `TextLineReader`, each checked against `SecurityBoundary` first; a tracked additional or analyzer-config document is searched as the workspace holds it. A disk file's group has `source: "disk"` and no declaration fields. Files not searched are counted in `skipped` (binary: a NUL in the first characters; unreadable; a line past the line limit; a link leading out of the workspace; a line the pattern ran on for more than two seconds, since a search that reads disk files gives each match that limit, and stops with a caution after five such files), never left out unseen. Such a search builds its pattern for the engine that matches in linear time (`RegexOptions.NonBacktracking`, #472), on which no pattern runs away, and falls back to the backtracking engine, with that limit, only for a pattern the linear one refuses (a lookaround, a backreference); its log entry says `engine: linear` or `engine: backtracking`. No regex built from a call's pattern is `Compiled`: it is thrown away with the call, and generating the matcher cost more than it saved. The default `*.cs` walks nothing. A hit's text is cut to 400 characters around its match, and a search that reads disk files stops at 100,000 hits with a caution |
| `SemanticSearchTool` | `roslyn_semantic_search` — Roslyn syntax-tree filtering for context-aware search (comments, strings, identifiers, code, xmldocs); C#-only, slower but more precise. The matches are grouped as a text search's hits (`files[].in[].matches[]`, `SourcePlace`, #487), one per line, a match on a declaration's own name with `declares: true`; the answer's `context` names the context searched, and a listed line names its own only in a search over `all`: every context its matches are in, in their order (`identifier, comment`), each being what the match itself starts in. A match of no length, which a pattern of assertions alone makes (`^(?!.*TODO)`, `^(?!I)`), is a place like any other and is labelled by what follows its position. Which of a line's matches says where its place is, and under which declaration, is `EnclosingDeclaration.Among`'s rule, shared with `roslyn_search_files` (#515): the leftmost, with any match on the name of its declaration marking it, or else the first match that is the name of a declaration of its own, for a line that holds two (`int A; int Foo;`). The second half is asked for at most eight matches, and not at all on a line inside a member's body |
| `FindStringLiteralTool` | `roslyn_find_string_literal` — string-literal-only search; matches against decoded `Token.ValueText` by default (escape sequences resolved, quotes stripped); `mode: "glob"` for `*`/`?` glob; `matchRaw: true` to match raw source text instead; covers all string forms including raw and UTF-8 variants. The literals are grouped as a text search's hits (`files[].in[].matches[]`, `SourcePlace`, #487), each with `line`, `column`, `text` (the source line the literal starts on, cut around it) and `value` (decoded); two literals on one line are two matches, since each has a value of its own |
| `GlobMatcher` | Filename-only glob matching (`*`, `**`, `?`, `{a,b}`) extracted from `SemanticSearchTool`; the `filePattern` of `roslyn_search_files`, `roslyn_semantic_search` and `roslyn_find_string_literal`. `TryCreateMatcher` ORs the expansions of a brace pattern |
| `GlobBraces` | Brace expansion for every glob the server matches (#294) — neither `FileSystemGlobbing.Matcher` nor `GlobMatcher` has it, so a pattern is expanded first: `*.{cs,csproj}` → `*.cs`, `*.csproj`. Groups nest and multiply; a brace with no match or no comma at its own level is literal (a file named `{x}.txt` can still be named); no escape character, so a pattern that held a group also keeps itself as one more alternative, matched literally (`Gen{A,B}.cs` finds `GenA.cs`, `GenB.cs` and a file named `Gen{A,B}.cs`). `GlobMatcher` joins the expansions into one regex; `roslyn_list_files` adds one include each. The pattern comes from an agent, so it is bounded before any work: longer than 4,096 characters, more than 256 distinct expansions, or more than 4M characters of expansion work, is an error, not a partial match. Expansion goes one group level at a time without recursion (a recursive expander overflowed the stack, and so killed the server, on a 100 KB pattern), and each search for the next group is one pass with a stack of open braces (rescanning from every unmatched `{` stalled a call for seconds) |
| `IgnoreRules` | The directories the server stays out of, per workspace root, in three tiers: never a compilation input (`node_modules`, `.git`, `.vs`, `packages`, plus the configured `--ignore` / `ROSLYNMCP_IGNORE` / `"ignore"` names) — safe for the watcher to skip; plus `bin`/`obj` for discovery and reload classification; plus the root `.gitignore` (plain directory names and root-anchored paths only — globs are skipped; a negation removes the entries it re-includes, or all of them when it holds a glob) for listing and file lookup. `.gitignore` never drives the watcher or document discovery: ignored directories often hold generated sources. Beside the tiers, the linked git worktrees below the root (`GitWorktrees`, #511) are left out of listing, file lookup and the disk search, and of an AdhocWorkspace's own search for sources. They are still watched, and what a project compiles is MSBuild's to say: a change inside one flags a reload only when the loaded solution has a project file or a document there (`EditClassifier.RequiresReload`), since without that every save of another session in its worktree was an unknown source file here and a full reload |
| `GitWorktrees` | The linked git worktrees below a workspace root (#511), from git's own records: `.git/worktrees/<name>/gitdir` holds the path of the `.git` file at that worktree's root. A worktree inside the checkout it belongs to (`.claude/worktrees/<name>`) is every file of the tree a second time: a file named by the end of its path matched twice and was "not found", and listings and searches answered for both trees. The records cost nothing per directory walked. The repository is found by the `.git` entry at or above the root (a directory, or a file whose `gitdir:` and `commondir` lead to it); a record counts only while the directory still holds the `.git` file it names, since git keeps a record until it is pruned; both sides are compared by their real paths (`FileIdentity.FinalPath`); a `gitdir:` that names a UNC share other than the root's own is not followed. A submodule and a nested clone are not in the records and are walked. Worktrees are made while a session runs, so nothing holds the answer for the life of a workspace: `GitWorktrees.For(root, fresh)` hands out one `Below` per root and checks it against the records (a stat per worktree, and whether each record's `.git` file is still there), every time for a call that lists or looks a file up (`IgnoreRules.ForRoot`) and wherever a late answer would stay wrong (a file an AdhocWorkspace adds is not taken out again, and git writes a worktree's record and its files in the same instant), at most once a second where it would only be late (the reload rule). The same instance is handed out while nothing changed, which is how `IgnoreRules` knows its cached rules still stand |
| `WorkspaceWalker` | The one recursive directory walk (`FileSystemEnumerable` with a per-directory prune predicate): never enters a pruned directory, never follows a directory link, skips unreadable directories, and stops at depth 32. Replaces `Directory.EnumerateFiles(…, AllDirectories)`, which cannot skip a directory (#309) |
| `WatchPlanner` / `WorkspaceWatchSet` | File watching for one workspace. The planner splits the root into watch roots around never-input directories (non-recursive on the way down to one, recursive everywhere else) so `node_modules` and `.git` are never watched — on Linux a recursive watcher costs one inotify watch per directory. Over 32 roots it falls back to project directories plus their ancestors, then to a single recursive root watcher. The watch set starts the watchers on a background thread, adopts directories created later, and logs `Watch — N/M watch root(s) live (<shape>)`. An adopted directory is logged with the number of files reported from it, and listed again half a second and three seconds later (`LookBackAt`, #339): it is often adopted before its first file exists, and a file written before the new watcher delivers is otherwise seen by neither the listing nor the watcher. A look back reports only what nobody reported yet: the watchers note what they report (`Note`) for the directories still to be listed again, since a project or props file said twice flags a second reload; it belongs to the watcher that was started for the directory, and a directory past the limit of adopted files is not listed again. A root whose watcher cannot start is kept with the reason (#495: on Linux every watcher is an inotify instance, of which a user has 128 by default, and a full harness run used them up): `Status` (`WatchStatus`) has the live count and the unwatched roots, which reach `WorkspaceHealth.Watching`, `roslyn_check_drift` (`unwatched_roots`) and `roslyn_info` (`workspaces[]`). The failure happens on a background thread after the loading call has answered, so it is posted to `WatchNews` and the next tool answer without a caution of its own carries it, once per state. A plan with any root unwatched forgets its key like one with none live, so the next reload tries again. A watcher's `Error` event after it started (`Failed`, #499) is one of four things: its directory is gone (retired, and the key forgotten so a directory that comes back is planned for again); a buffer overflow (no state: `CatchUpSoon` runs the reconcile scan from `syncedUtc`, the last moment the disk was known, once for a burst of overflows; the scan covers documents written, files created or deleted and project files written, not a props or lock file edited in place); on Windows any other error, which has switched the watcher off (the root moves to the unwatched ones); elsewhere a watcher that goes on, which on Linux is how the limit on watches shows, one error per directory that got no watch (`degraded`, in `WatchStatus.Degraded`, `degraded_roots` of `roslyn_check_drift` and `roslyn_info`; the next plan replaces such a watcher, since only a start adds watches). An error counts only from the set's watcher for the root or from one `Add` is starting (`adding`), and nothing is announced while a start runs (`starting`): a degraded root is cleared and reports again at every start. Under .NET 10 on Linux a watcher disposed after its directory was deleted keeps its inotify instance and its thread for the life of the process: the runtime's leak, not avoidable here, and the reason a full harness run ends near the default limit of 128 |
| `ListFilesTool` | `roslyn_list_files` — enumerate files matching glob pattern (fast file listing, no content); skips ignored directories (see `IgnoreRules`) unless the pattern names one literally — in a brace pattern, any expansion naming it counts (`{bin,docs}/**`); one `Matcher.AddInclude` per expansion (`GlobBraces`) |
| `ReplaceInFileTool` | `roslyn_replace_in_file` — text-level find/replace with regex support (any file type). It is the tool agents edit C# with (788 of 808 edits in eight days of logs), so it carries what the semantic tool was preferred for (#476). A literal pattern that matches several times replaces nothing and lists the matches, grouped as a search groups its hits (`EnclosingDeclaration`), unless `replaceAll` is set; a regex or glob replaces every match. On a `.cs` file, `inMember` (`Foo`, `Type.Foo`, a signature as copied; overloads of the name all count, `EnclosingDeclaration.Named`) limits the matches to that declaration, and the result is parsed before it is written, with the project's parse options where the file is a document: an edit that adds a syntax error is refused, quoting the parser with the line, unless `allowSyntaxErrors` is set. Errors are compared by id and message, not counted, so a file that is already broken stays editable. A dry run gets the write's verdict. The XML files a project is evaluated from (`.csproj`, `.props`, `.targets`, `.slnx`, `.ruleset`, `.resx`, `nuget.config`, `packages.config`) are checked the same way (#245, `EditChecks`): an edit that leaves a well-formed file as malformed XML is refused, quoting the XML parser, because such a file fails the next workspace load and not the edit; a file that was malformed before stays editable. `roslyn_multi_edit` runs the same check on each file as the batch leaves it. Well-formedness only: nothing is resolved, and MSBuild's own rules are not checked. A write answers `backup_token` (#513), as `roslyn_insert_lines` and `roslyn_multi_edit` do: the tool whose edits are undone most answered no handle for it, and an undo took a `roslyn_local_history` listing first |
| `ReplaceInCodeTool` | `roslyn_replace_in_code` — semantic C# node replacement using Roslyn (validates syntax, preserves formatting). Nodes are selected by `nodeKind`, then narrowed by `textPattern` and by `line`/`column` of the node's start, which pick one of several similar nodes. The new node takes the old one's blank lines and first-line indentation; a comment in the replacement text replaces the comment attached to the node (the lines directly above it up to a blank line, or the one ending its last line), a replacement without one keeps them. Only the node and its comments are written: a comment that cannot be applied safely, a preprocessor directive in the replacement text, and anything below its node are named in `_caution` with the node's line, never dropped unseen. The syntax check parses the text that will be written, not the assembled tree (#416). One member of a class, struct, interface or record may be replaced by several (#463): the first takes the old member's place and comments, the rest follow as written, and the end of the old member's last line comes behind the last; a type or delegate in a namespace or at file level takes a list of types the same way (#466: such text is parsed as a compilation unit, which is also what lets a namespace and a top-level statement be replaced at all, one for one); an enum member, a namespace, a statement, a using directive and an expression are still replaced by exactly one. Its failures say what to do next (#476): no match lists the nodes of the kind with name and position (`candidates`; after a position that missed, the nodes the kind and the name do match), a replacement that does not parse quotes the parser with the line in the replacement text, and a `textPattern` that found nothing is read again as the name it holds (`void Foo`, `Foo(int a)`, `Type.Foo`; `EnclosingDeclaration.NameSegments`). A write answers `backup_token` (#513) |
| `InsertLinesTool` | `roslyn_insert_lines` — insert lines at a position or anchor pattern |
| `MultiEditTool` | `roslyn_multi_edit` — several edits in one call (#475): each entry of `edits` names an `op`, the name of the tool it stands for (`roslyn_replace_in_file`, `roslyn_insert_lines`, `roslyn_replace_in_code`), a file and that tool's parameters. The name without the prefix is taken on input and named nowhere: a short name an agent has read is one it will call, which is what RMCP010 is for (#326). Nine of ten edits in the logs were part of a run of edits, more than half of the runs crossed files and a quarter mixed tools, so it is one tool and not an array on each. Every position refers to the file **as it was before the batch**: each file is read once, every edit is resolved against that text by the core of its tool (`EditPlan`), and the changes of a file are applied in one pass from the bottom up, so no edit moves the place another names. One edit cannot build on another of the same batch, and two that change the same text are an error (`EditPlan.Overlaps` over each edit's `Claims`; an insertion at the edge of a span is not one). A text edit claims what it changes; a node edit claims its nodes whole, though the change it amounts to may be one character of them, since another edit inside a replaced node has been overwritten wherever it is. Nothing is written when any edit cannot be made, and the answer names each edit's outcome with its tool's own answer. A batch that can be made answers `edit_count` and the files, and names only the edits that changed another number of places than one (`EditPlan.Places`; a regex, `replaceAll`, several nodes): for the rest it would repeat the call, at thirty tokens an edit (#490). A C# file is checked as the whole batch leaves it, so two edits that are each half of a change pass together. Backups of every file are saved before the first write; the writes are one per file, and one that fails after others succeeded is reported file by file with the tokens that undo the ones made. There is no batch token: a backup token restores one file |
| `EditPlan` | What an edit tool's core found out about one edit before anything is written (#475): why it cannot be made (`Stop`, with the answer and which scope terminal it goes through), or its changes as spans of the original text. `ReplaceInFileTool.Plan`, `InsertLinesTool.Plan` and `ReplaceInCodeTool.Plan` are those cores, static and without I/O; each tool is its core plus a wrapper that reads, writes and backs up, and `roslyn_multi_edit` runs the same cores. A node edit's result is a whole new text, which `EditPlan.Between` turns into the one change it amounts to. `roslyn_insert_lines` itself still writes the file from its lines (one line-ending style, a line break at the end), as it always did; only the batch inserts the plan's change into the text as it is |
| `WriteFileTool` | `roslyn_write_file` — write or create files atomically with automatic pre- and post-write backup snapshots; returns a backup token usable with `roslyn_local_history` |
| `LocalHistoryTool` | `roslyn_local_history` — list, preview, and apply crash-safe file backup snapshots; token-based undo for write operations (actions: `list`, `preview`, `apply`) |
| `HookCommand` | `madq-roslynmcp hook` — the target of the clients' tool-use hooks. Reads one hook event from stdin and, for a built-in read, search, listing or edit of a `.cs` file, answers with one line naming the `roslyn_*` tools for that operation (#310); everything else, and every failure, is `{}`. It never blocks and never sends a permission decision: advice must not grant permission. Claude Code takes the hint before the tool, as `hookSpecificOutput.additionalContext` of a `PreToolUse` hook (`setup` installs it in `~/.claude/settings.json`). Copilot CLI documents no such output for `preToolUse`, so there the hook is a `postToolUse` hook, whose top-level `additionalContext` Copilot appends to the tool's result; a Copilot event without a `toolResult` is a `preToolUse` event from a hook file written before #310 and is answered `{}`. `setup-project` writes the Copilot hook file to `.github/hooks/roslynmcp.json`, the directory Copilot CLI loads hook files from, and retires the one earlier versions wrote to `.github/roslynmcp.json`. Silent unless `ServerHeartbeat.IsAliveFor` finds a server for the event's `cwd` |
| `ServerHeartbeat` | One file per server process, `%LOCALAPPDATA%\RoslynMcp\heartbeat.<pid>` (`ROSLYNMCP_HEARTBEAT_PATH` names another directory; the harness uses one of its own), rewritten on every tool call: the PID and, on a second line, the directory the server's default workspace is looked for in (`--root` / `ROSLYNMCP_ROOT`, else its working directory). The hook advises only when a heartbeat of the last ten minutes names the session's directory, one above it or one inside it (#310): one server alive anywhere on the machine used to make it advise in every session, also in one without RoslynMcp. A heartbeat without a directory (an older server) and an event without `cwd` count as a match, and every failure of the check fails open. A server that starts deletes the heartbeats older than an hour: a killed server never removes its own, and they used to stay for ninety days |
| `RespawnTool` | `roslyn_respawn` — ends the server process gracefully with exit code 0 (`IHostApplicationLifetime.StopApplication` after the response has flushed, `Process.Kill` after 10 s as the fallback for a shutdown that hangs — not `Environment.Exit`, which waits for that same shutdown) so the client starts a fresh one: the remedy the diagnostics, drift, build and reload hints name for a workspace that loaded badly and no reload repaired, and the way to load a new build of the server. Ships in every build since #406 — a hint naming a tool the build does not have is a dead end for an agent. The restart is the client's: Claude Code, Copilot CLI and VS Code restart a stdio server that exits; where the client does not, the user restarts it. Never a hook suggestion |
| `FileWriter` | Centralised file write entry point; `WriteWithRetryAsync` and `WriteWithRetry` wrap all disk writes with exponential-backoff retry on `IOException`, structured `INFO`/`ERROR` log entries per attempt, and are accessible from non-tool types (`BackupStore`, `SolutionDiff`). `ReplaceAtomicAsync` / `ReplaceAtomic` is the one way to replace a whole file (temp file + rename): it keeps the existing file's Unix permissions, writes through a symbolic link instead of replacing it (`AtomicReplace.FollowLink`), can create a new file owner-only (`AtomicReplace.PrivateWhenNew`), and removes its temp file on failure — never hand-roll temp-file-and-rename (#322) |
| `FileReader` | Centralised whole-file read entry point, the counterpart of `FileWriter` (#230): `ReadAllText`/`ReadAllBytes` (+`Async`), `ReadAllLines`, `ReadLines` and a streaming `OpenRead`, each with a byte limit — `FileReader.DocumentLimit` (64 MB: tool content, backups, project/solution files, agent configs) or `FileReader.ConfigLimit` (1 MB: git metadata and other small files) — plus `OpenUnbounded` for the reads that are unbounded on purpose (below) and `LoadXml` for project and solution files (`DocumentLimit`). The limit counts bytes actually read, not `FileInfo.Length` (a device reports 0), and a file past it throws `FileTooLargeException : IOException`; tools map it to `file_too_large` with `RoslynMcpTool.ReadFailure`. Decodes like `File.ReadAllText` (BOM-detected, else UTF-8). Follows links on purpose; a FIFO still blocks in `open()`, which is accepted. Every read opens with `FileReader.ReadSharing` — readers and deletes, not writers (#349): `Delete` lets the server's own atomic replace rename over a file a read holds open (on Windows the rename fails otherwise), and keeping writers out means no read sees a file half-saved in place, which the watcher flush, the edit tools and backups would write back. The one exception is the line count of `roslyn_get_line_count`, which opens with `FileReader.OpenForCount` and so `FileReader.ObservingSharing` (readers, writers and deletes), so an active log can be counted; no other read takes that sharing, least of all one whose content comes out — `roslyn_read_file`'s output is copied into edits, so it is written back as surely as a workspace document, a backup or an edit's own read. A read refused by a writer gets a hint saying so (`ReadFailure`). Use `FileReader` for every whole-file read of a file a tool is pointed at and of the server's own config, metadata and backups — never `File.ReadAll*`/`File.ReadLines`/`File.OpenRead`/`XDocument.Load(path)` in the server. Not bounded, on purpose: text documents Roslyn tracks (it holds them whole regardless, so a limit here would save nothing), a line count, which streams through a fixed buffer and keeps nothing (a link to a device that never ends keeps it counting, as it always has), and a hash, which streams (`FileReader.OpenUnbounded` + `SHA256.HashData`) and holds no copy — a limit there would turn a file past it into a skipped check. `OpenLines` returns a `TextLineReader` (#327, #401): the lines of a disk file one at a time, numbered as `SourceText` numbers them (`\r\n`, `\r`, `\n`, U+0085, U+2028 and U+2029 end a line, and what follows the last break is a line) and decoded as the whole-file reads decode, so a line number from a search or a streamed read is the one every other tool uses. Two bounds: `TextLineReader.LineLimit` (1,000,000 characters, `LineTooLongException`, mapped to `line_too_long`) ends a file without line breaks, and `FileReader.StreamLimit` (2 GB of bytes read) a file without an end |
| `SecurityBoundary` | Per-workspace file access check (`IsPathAllowed`): a path must be under a trusted root (workspace, solution directory, referenced projects). A link above the root is ignored — it is where the user put the workspace (#330). A link inside the root is followed when it resolves to somewhere inside a trusted root and refused when it leads out (#322). A caller that goes on to write through a link asks `ResolveAllowed` instead: it returns the fully resolved path that was approved, so the check and the write cannot come apart (#334). File walks still do not descend into linked directories (`WorkspaceWalker`, #314). `SecurityBoundary.PathComparer` is the one path comparison for the whole server, every path-keyed set included: on Windows and macOS a difference in case alone is put to the file system, once per directory (a Windows directory can be made case-sensitive, a macOS volume formatted so), and on Linux it is ordinal (#391). Never compare paths with a `StringComparison` of your own; a prefix check is `SecurityBoundary.StartsWithPath` |
| `PhysicalFileIndex` | Private nested type of `WorkspaceInstance` (`WorkspaceManager.Instance.PhysicalFileIndex.cs`). The tracked documents of a workspace that are another file's name — a symbolic link, or a path under a linked directory — keyed by the file they lead to. Built at load and on every reload; lets the write paths and the watcher flush refresh every document of a changed file, whichever name the change was made under (#341). Empty for a workspace without links. See `docs/development/WORKSPACE_SYNC.md` |
| `TypeMembersTool` |`roslyn_get_type_members` — enumerate members with full signatures + doc summaries, listed by kind (`members.method[]`, `members.property[]`, …; each entry `signature` and, when there is one, `docSummary`): the kind and the name beside every signature were a fifth of the answer (#490) |
| `FindOverloadsTool` | `roslyn_find_overloads` — returns all ordinary method overloads declared on a containing type, with full signatures including generic/default/ref/out parameter details |
| `TypeDependenciesTool` | `roslyn_get_type_dependencies` — returns direct type dependencies from a type declaration and direct member signatures; includes base type, direct interfaces, fields, properties, events, parameters, returns, generic constraints, operators, and conversions |
| `DiagnosticsTool` | `roslyn_get_diagnostics` — structured compiler errors and warnings (summary counts + the findings, paged) for one project, or every project of a solution path. The findings are grouped (#487, `DiagnosticPlace`): `files[]` by file, `in[]` by the declaration they are in (`declaration`, `lines`, `at`, as a text search names them), `findings[]` once per code and message with `places[]` of line, column and the line's text; findings with no place in a file are under `general`. The file with the most severe finding comes first, `take` counts places, and a group cut by a page is named again on the next. An analyzer reports one message dozens of times and each used to repeat the file's path; and the read that followed a finding is in the answer. `take: 0` for count-only fast path — `files` is absent (not `[]`) to distinguish "not requested" from "no results". Pages through `PaginationCache` like every other paginated tool (#435): one analyzer run per query, later pages cut from the cached result, the token a `PageCursor` that carries the page size. `includeAnalyzers` runs the project's analyzer references **and Roslyn's IDE analyzers** (`CodeActionHost.AnalyzersFor`, #389) — the IDE0xxx findings are mostly info or hidden, so `severity: "info"` / `"hidden"` show them (the default stays errors and warnings), and a note says so when they were left out; the note also names the project's analyzer references whose file was not found, since their analyzers did not run and Roslyn says nothing (#424) |
| `CheckDriftTool` | `roslyn_check_drift` — workspace health probe on three axes: source drift (on-disk changes the FileSystemWatcher missed), load health (`workspace_healthy: false` when a project loaded with zero metadata references, or with a `WorkspaceFailed` failure naming its project file — which can leave its compilation options at defaults, #300; a project with no references and no `project.assets.json` is reported as never restored, `projects_not_restored`, whose remedy is `roslyn_restore_packages`, not a reload, #399), and `reload_pending: true` when a change is waiting on a reload that has not completed. Drift checks existing documents: missing files count as drift, and files newer than the sync clock plus tolerance are compared with workspace text, so owned edits and timestamp-only touches are clean. `reload_pending` is the only axis that can account for a **newly added** file. Beside the three, `unwatched_roots` lists the directories whose file watcher could not start, with the reason, and the hint names the remedy (#495): changes made there outside the server raise no event, so a new file is found by none of the axes until a reload. Use when symbol results look stale **or** when `roslyn_get_diagnostics` reports errors you don't believe |
| `SourcePlace` | One place in source as a semantic tool answers it (#487): file, line, column, the text of the line and the declaration the place is in (`EnclosingDeclaration`, from the syntax tree alone), as plain values the pagination cache can keep. `Group` lists places by file and declaration in the shape of a text search's hits (`SearchFileGroup`), so what an agent does with a search hit (read the member with `lines`, edit it with `at`) holds for a reference too; `GroupingNote` is the log's `N reference(s) in F file(s), G group(s), D in a declaration; flat ~C chars`. A place can carry what its tool found there (`Context`, `Value`) and say its column (`ColumnMatters`). Used by `roslyn_find_references`, `roslyn_find_callers`, `roslyn_semantic_search` and `roslyn_find_string_literal`; `roslyn_get_call_graph` takes its method's place from it, and the tools whose answer is a declaration take its `lines` and `at` (`AsDeclared`): `roslyn_find_implementations`, `roslyn_find_unused`, `roslyn_get_symbol_definition`. Both are left out where they would be another declaration's: for a local or a parameter (the enclosing member's), for one of several fields of one statement (`DeclaresSeveral`: the statement's), and by those tools for a member the compiler declares, whose location is its type's name |
| `FindReferencesTool` | `roslyn_find_references` — all references to a symbol across the project, each under the declaration it is in with the line's text (`references[].in[].matches[]`, #487; two references on one line are one place), and where it is declared (`declarations`: Roslyn's reference search leaves the declaration out, and "where is it and who uses it" is one question, which a text search answered in one call). Several names in one `symbolName` (`A|B|C`, or commas outside `<…>`; `SplitSymbolNames`) are one lookup each, with its own project, answered as `symbols[]`: per name its declarations, total and first `take` references, or that name's own error; no page token there, a name asked alone pages (#459) |
| `FindUnusedTool` | `roslyn_find_unused` — private, internal, and effectively internal source symbols with zero direct static references; conservative refactoring guidance with confidence and reason metadata. Each entry says where the symbol is declared, with the `lines` it takes and the `at` its node starts on (#487): an unused symbol is read next, or removed |
| `FindCallersTool` | `roslyn_find_callers` — every line that calls a named symbol, under the declaration it is in with the line's text (`callers[].in[].matches[]`, #487): that declaration is the caller, with the `lines` to read it and the `at` to edit it, so no caller name is repeated per line; two calls on one line are one calling line; filter by `isDirect` to exclude interface/delegate dispatch |
| `GetCallGraphTool` | `roslyn_get_call_graph` — all methods invoked within a method body; walks the Roslyn IOperation tree for precise semantic results. The answer names the method's `file`, `lines` and `at` once, and each call is `callee`, `line` and the line's `text` (#487); a call names a `file` only when it is in another part of the method than the one the answer names |
| `AnalyzeRegionTool` | `roslyn_analyze_region` — `AnalyzeDataFlow` and `AnalyzeControlFlow` over a line range. The private nested `RegionResolver` turns the range into statements from one list (Roslyn throws on two) or a single expression, widening a partly covered statement and reporting the lines analyzed. `throws` is the tool's own walk, because `ExitPoints` omits `throw`; an expression region has no `control_flow`, because Roslyn has no expression overload |
| `SymbolInfoTool` | `roslyn_get_symbol_info` — resolve what a name at a location actually is, and what the compiler knows about the enclosing expression: flow-adjusted and converted type, nullable flow state, conversion, constant value, failed overload candidates; `includeOverloads` adds the method group. Type facts come from the enclosing expression: a method name climbs to its call, and the last member of a `?.` chain to the whole conditional access, while an earlier link and a delegate in call position stay themselves. Empty fields are omitted |
| `AnnotateSourceTool` | `roslyn_annotate_source` — a member's source, or a line range's, and apart from it what the compiler knows that the text does not say (#355, the last of the epic #351): the type of each implicitly typed variable (`var`, `foreach`, `out var`, a deconstruction, a lambda parameter, a target-typed `new`), each dereferenced value that flow analysis has as maybe-null, and the overload a call resolved to when its name is overloaded or its type arguments were inferred, for a method declared in source of the solution only (`IsOwn`, #502: calls into the framework or a package, `Where` and `ToDictionary`, were twelve of the fourteen annotations of the tool's first real use, and say what a reader of C# knows; the types such a call infers are still said when its result goes into an implicitly typed variable, on that variable, and nowhere when it is returned or passed straight on). The answer is the source terminal's: a JSON header, the source as the file has it, then `{"annotations": N}` and the annotations one per line as `line: fact`. Never written into the source: agents edit by quoting text, and an annotated line quoted into an edit matches nothing. Only what cannot be read off the text is annotated (not a literal, a written type, a call with one overload), since an annotation on every node costs more than the reads it saves; code that does not bind gets none; 200 annotations and 400 lines at most. The rules it shares with `roslyn_get_symbol_info` are in `ExpressionFacts` (the type format with the nullable marker, where a null state applies, an error type being no fact, and `LocalType`: an implicitly typed local is given what its value yields, since its declared type is nullable for every reference type whatever it is given), so the two never answer one question differently |
| `PreviewRenameTool` | `roslyn_preview_rename` — compute rename edits, return unified diff + token; builds a `PreviewFileState` snapshot and `WorkspaceBinding` so `roslyn_apply_rename` can commit via `PhysicalSolutionApplier` |
| `ApplyRenameTool` | `roslyn_apply_rename` — approve/reject a pending rename by token; applies via `PhysicalSolutionApplier` (crash-safe, TOCTOU-guarded); the type-matching file-rename step (including the case-only two-step move) runs as a separate post-apply phase after content writes are confirmed |
| `PreviewCodeFixTool` | `roslyn_preview_code_fix` — preview the code fixes `CodeActionHost` offers for a single diagnostic: Roslyn's own fixers, the project's analyzer-package fixers, the bundled ones and configured providers (#389); returns available action choices or unified diff + token for a selected fix; a fix may create or delete `.cs` files inside the workspace (`CodeActionChangeValidator`); a failing provider is recorded, the others' fixes still offered; an answer without a fix ("no fixes", or a provider failure with nothing left) says what may explain the missing fixer: the message names the switches that are off for the project (`codeActions.enabled` / `builtIn` / `projectProviders`), and `providerProblems` names the provider sources that had a problem (the shape of the refactoring list's `providers.problems`), since a fixer may be missing because its source failed to load or the workspace mode cannot have it — an answer with a fix carries nothing about sources and leaves their count in the log; a provider failure for a companion diagnostic never changes the answer for the diagnostic asked for, but a "no fixes" message names it with the companion's id (#423) (ReadOnly — never writes) |
| `ApplyCodeFixTool` | `roslyn_apply_code_fix` — apply or reject a previewed code fix using the approval token from `roslyn_preview_code_fix`; applies via `PhysicalSolutionApplier` through `CodeActionApplyTool`; the result names `createdFiles` / `deletedFiles` |
| `ListRefactoringsTool` | `roslyn_list_refactorings` — the code refactorings Roslyn offers at a caret or selection (Move type to file, Extract method, Introduce local, Invert if, …), each with an index for the preview, its title, provider type name, export name and source; dialog-driven actions (`CodeActionWithOptions`) and actions tagged `RequiresNonDocumentChange` are listed under `unavailable` with the reason, as Roslyn's LSP host filters them; a provider that throws is a `providerFailures` entry, never a failed call; `providers` counts the sources behind the list and the providers each loaded (before de-duplication and the deny-list — not actions), and names only the sources with a problem (one that failed to load, or one the workspace mode cannot have), each with its first problem and a count of further ones — the whole per-source report is `roslyn_info`'s (the sources the server has loaded and kept, for all projects), since repeating it cost about 1,300 tokens a call (#414). An empty list is a normal answer (#389) |
| `PreviewRefactoringTool` | `roslyn_preview_refactoring` — computes one listed action (same location + `actionIndex`) into a unified diff + token; `CodeActionOperations` takes the one `ApplyChangesOperation` (navigation operations ignored), `CodeActionChangeValidator` allows text changes plus `.cs` creates and deletes inside the boundary and refuses renames (by path or document name), project changes and generated/linked/external files; echoes the chosen action so an index mismatch is visible; `createdFiles` / `deletedFiles` name what the apply will do (ReadOnly — never writes) |
| `ApplyRefactoringTool` | `roslyn_apply_refactoring` — apply or reject a previewed refactoring by token (`ApprovalWorkflow.Refactoring`), through `CodeActionApplyTool`; a created `.cs` flags a reload; a `_caution` when the project lists its Compile items explicitly (the file is not compiled until added) |
| `MefComposition` | The one MEF composition of the process (#389): `MefHostServices.DefaultAssemblies` (Workspaces, CSharp.Workspaces, Features, CSharp.Features — the VisualBasic pair is absent and skipped) composed once into a `CompositionHost`; both `MSBuildWorkspace.Create` call sites and `new AdhocWorkspace` take its `HostServices`, and `CodeActionHost` reads the provider exports from the same container, so providers and the workspace services they run against share their `[Shared]` singletons as in Visual Studio. Costs ~200 ms at the first workspace load, logged as `Composition — N assemblies composed in X ms` |
| `CodeActionHost` | Every `CodeRefactoringProvider` and `CodeFixProvider` the server may run, by source (`ProviderSourceKind`): built-in (the composition's exports, read through a System.Composition metadata view of the export attributes), bundled (`RoslynMcp.Analyzers`, always on), project (reflected from each `AnalyzerReference` through `ShadowCopyAnalyzerLoader.Instance` — the same load as the analyzers; cached per path + mtime, except a set whose load failed on the file system, which is loaded again at the next call, #412; every reference of the project is registered with the loader as a dependency location before the first is loaded, `AnalyzerLoading.RegisterDependencies`, because a reference may depend on a sibling listed after it, #421; types that do not load are a problem only when an exported provider or a diagnostic analyzer is among them — `ExportedProviderTypes` reads those type names from metadata and each missing one is loaded by name; a set during whose load a dependency was found at no registered location (`UnresolvedDependencies` on the loader) is loaded again once the loader knows more dependency locations than it did then, since a later project may name the sibling an earlier one lacked, and `AnalyzerLoading` does the same for its analyzer sets, because Roslyn does not say when a set came up short; a reference whose file is not there is a source with a "not found" problem and the remedy that fits where the file should be — `roslyn_restore_packages` under the NuGet package cache the server knows, `roslyn_build_project` otherwise, since a project referenced as an analyzer has no output before its first build (`AnalyzerLoading.MissingReferenceRemedy`); it is looked for again at every call, and `roslyn_info` lists what each project was missing when its providers were last asked for, once per path and only while the file is still missing (`missingByProject`, by project file and target, a target's list replaced whole, so a reference it dropped is forgotten and the targets of a multi-target project do not overwrite each other; #424) — a reference without a path, an analyzer handed over in memory, is no provider source; unavailable in an `AdhocWorkspace`, which the report says), and configured (`CodeActionSettings.Providers`: a NuGet package from the local cache via `NuGetPackageLocator`, or an assembly path; one `ShadowCopyAnalyzerLoader` per entry with the sibling assemblies as dependency locations, never Roslyn or the framework; a committed file's path loads only when trusted). `ProvidersFor(document)` filters by export metadata (language, document kind, extension), dedupes by type name (the bundled analyzers are also a project reference of this repo), and applies the deny-list: a built-in one (ChangeSignature, RenameTracking, ExtractInterface, ExtractClass, PullMemberUp, MoveStaticMembers, MoveToNamespace) plus `deny` entries, matched by export name, type name or type-name stem (`InvertIf` for `CSharpInvertIfCodeRefactoringProvider` — export names are whatever the author typed). `GetRefactoringsAsync` / `GetFixesAsync` run each provider in its own try/catch, flatten nested actions to leaves (`Parent › Child`), and set dialog and non-document actions aside. `AnalyzersFor(project)` is the project's analyzer references plus Roslyn's IDE analyzers (the `[DiagnosticAnalyzer]` types of the Features assemblies, found by reflection as a Roslyn host finds them) while built-in code actions are on — the IDE0xxx diagnostics the built-in fixers fix; `roslyn_get_diagnostics` and `roslyn_preview_code_fix` both run that set, the preview over the one document (`GetAnalyzerSyntaxDiagnosticsAsync` / `GetAnalyzerSemanticDiagnosticsAsync`). Some fixers answer to a companion of the diagnostic shown (the unnecessary-using fixer to `RemoveUnnecessaryImportsFixable`, not IDE0005), so the preview tries the companions at the position when the asked-for diagnostic has no fixer. `Reports` feeds `roslyn_info` |
| `ShadowCopyAnalyzerLoader` | Loads analyzer and provider assemblies from copies under `%LOCALAPPDATA%\RoslynMcp\analyzer-shadow\<hash of path + mtime>`, so a project's outputs are never locked by the server. The directory is shared by every server on the machine, so the copy is made for contention (#412): written through `FileWriter.ReplaceAtomic`, so the final name never leads to a file still being written; an existing file is reused only at the source's length; a copy of that length that is no assembly is copied again, once per process; the copy carries the time it was made, and gets the current time again when reused after a day, because the startup sweep deletes by age. `TransientFailures` counts the loads that failed for a reason another process causes and will stop causing (a sharing or lock violation, a file that went away, a copy another process holds — not a full disk or a name the context already has), at most five times per path — every cache of a load's result (`AnalyzerLoading`, `CodeActionHost`'s project and configured sets) compares it before and after and keeps only a result from a load during which it did not move. One load context per reference directory, as Roslyn's own hosts have (`DirectoryContext`): a context holds one assembly of a simple name, so with a single context two versions of an analyzer package could not both load (#419). A context remembers the modification time each file had when it loaded it; a load that finds another gives the directory a new context, so an analyzer rebuilt in place is the new build at the next call (#418) — the old context stays in memory, the contexts are not collectible. The caches above keep one set per file and drop an earlier build's (#425). An analyzer whose own file is unchanged while a helper beside it was rebuilt keeps the helper it has loaded; one that first needs the helper after the rebuild gets the new build. A dependency resolves by simple name among the registered dependency locations: the one in the asking assembly's own directory first, else the highest assembly version anywhere, never by registration order; a reference registers its own path only when it is created, so whoever loads a project's references registers all of them first (`AnalyzerLoading.RegisterDependencies`, #421) |
| `CodeActionSettings` / `CodeActionConfig` | The `codeActions` section of a config file (`enabled`, `builtIn`, `projectProviders`, `providers` as `{ "path" }` or `{ "package", "version"? }`, `deny`, and in the user file `trustProjectProviders`) and its resolution for a request: switches CLI arg > env var > project file > user file > default; the `providers` and `deny` lists of the two files are **merged** (a user's tools plus a team's — unlike `ignore`, where the winning source replaces). `--code-actions on\|off` / `ROSLYNMCP_CODE_ACTIONS` turns the refactoring tools and the non-bundled fixers off; `--trust-project-providers` / `ROSLYNMCP_TRUST_PROJECT_PROVIDERS=true` (or the user file) lets a committed file load assemblies by path |
| `UserConfig` | The user's own server config, `%LOCALAPPDATA%\RoslynMcp\config.json` (`ROSLYNMCP_USER_CONFIG` names another file; the harness points its servers at one that never exists), read once per process like `ProjectConfig`; only `codeActions` today. The user's own file, so its assembly paths need no trust gate |
| `NuGetPackageLocator` | A configured package's provider assemblies in the local NuGet cache (`NUGET_PACKAGES`, else `~/.nuget/packages`): `analyzers/dotnet/cs`, then `analyzers/dotnet`, `analyzers`, `lib/netstandard2.0`; the highest cached version when none is named. Never a feed — a package gets into the cache by a restore the user ran, which is what makes a package entry safe without a trust gate |
| `CodeActionOperations` | A code action's operations to the solution it proposes: exactly one `ApplyChangesOperation`; Roslyn's editor-only operations are ignored — navigation (`OpenDocumentOperation`, `*NavigationOperation`) and the `RecordCodeActionOperation` every wrapping action adds, which only moves its title up a most-recently-used list (#438; matched by name and namespace, the type is private to Roslyn); anything else `UnsupportedCodeActionOperationException` |
| `CodeActionChangeValidator` | What the physical apply path can carry, checked before a token is minted, shared by the code-fix and refactoring previews: text changes to tracked source, additional and analyzer-config documents; creation and deletion of `.cs` files inside the `SecurityBoundary` (the target must not exist, its directory must — the apply swaps a temp file into place and makes no directories); refused: added/removed additional or analyzer-config documents, project/reference/option changes, a document rename or move (by path **or by name** — Move type's "Rename file to match type" changes only the name), generated (`bin`/`obj`/`generated`, `.g.cs`, auto-generated header), linked, external or read-only files. Returns `ValidatedChanges` (created, deleted, changed) |
| `CodeActionApplyTool` | Abstract base of `roslyn_apply_code_fix` and `roslyn_apply_refactoring`: token/approval handling, workspace binding check, physical plan with the stale checks around the backups, `ApplyCodeActionResult` (per-file state, `createdFiles`, `deletedFiles`, link and explicit-Compile-items cautions). Returns an outcome the tool passes to its own scope terminal (RMCP004). Not a tool; the two tools are sealed |
| `ChangeSignatureTool` | `roslyn_change_signature` — preview adding parameters with a non-breaking forwarding overload; returns unified diff + token (ReadOnly — never writes); builds a `PreviewFileState` snapshot and `WorkspaceBinding` so `roslyn_apply_signature_change` can commit via `PhysicalSolutionApplier` |
| `ApplySignatureChangeTool` | `roslyn_apply_signature_change` — apply or reject a previewed signature change; applies via `PhysicalSolutionApplier` |
| `ProjectInfoTool` | `roslyn_get_project_info` — project metadata (TFM, language version, packages, etc.) |
| `BuildTool` | `roslyn_build_project` — check Roslyn diagnostics first (fast) — every project of a solution and every target framework of a project, or only `targetFramework` when given (#383) — skip build if errors; run `dotnet build` if clean or `forceBuild=true` — for a solution with `targetFramework`, one build per project that targets it, since `dotnet build <solution> -f` fails for the others (#298); the errors and warnings are counted (`error_count`, `warning_count`) and listed as `roslyn_get_diagnostics` lists its findings (#487, `DiagnosticPlace`): `files[].in[].findings[].places[]`, with `general` for the ones with no place in a source file (a restore error, an MSBuild error of the project, each with the file MSBuild named as its `project`). The Roslyn check's places come from the compilation. A build's come from what MSBuild printed, and their line's text and declaration from the file on disk (`LocateAsync`): the disk is what the build compiled, a generated file under `obj` is no stranger to it, and only a C# file the workspace's boundary allows is read, parsed with its project's parse options where the workspace has it as a document, so that a member inside `#if` is a member. A finding names its `target_frameworks` when MSBuild emits TFM context |
| `CleanSolutionTool` | `roslyn_clean_solution` — remove all build artifacts (bin/obj directories) |
| `RestorePackagesTool` | `roslyn_restore_packages` — restore NuGet packages; on success flags a reload of the workspace it restored (`WorkspaceResolver.RequestReload`), since the assets file it writes is under `obj\`, which the watcher ignores (#399). A restore that succeeded answers with its warning lines only, without the ones that are the same but for the project named in brackets at their end (a warning from a shared targets file comes once per project; one that names its project at its start stays once per project); one that failed keeps the whole output (#490) |
| `DotnetRunner` | Shared helper for spawning `dotnet` CLI commands; strips MSBuild env vars (`MSBUILD_EXE_PATH` etc.) so child builds do clean SDK discovery; drains stdout+stderr concurrently to prevent pipe deadlock; runs one `dotnet` command at a time process-wide, through `MSBuildGate`, which workspace loads take too (concurrent builds of one solution race on shared `obj\` state, #300/#306/#379), recording `queued N ms behind <what held the gate>` when a call waits; used by `BuildTool`, `CleanSolutionTool`, `RestorePackagesTool` |
| `FileOutlineTool` | `roslyn_get_file_outline` — type/member structure without bodies (token saver); a type's `members` are its signatures as strings by kind (`members.method[]`, `members.field[]`, …), in the order of declaration within a kind: an object per member with its kind beside the signature was a quarter of an outline (#490) |
| `TypeHierarchyTool` | `roslyn_get_type_hierarchy` — base types, interfaces, derived types |
| `FindImplementationsTool` | `roslyn_find_implementations` — concrete implementations of interfaces/abstract members. Each entry is its `name` and where it is declared (`file`, `line`, `lines`, `at`, #487): an implementation is a declaration, and reading it was the next call. One declared in a referenced assembly has its name only, a type declared in parts names its first, and nothing found is an empty list |
| `ListTypesTool` | `roslyn_list_types` — enumerate types with optional filters |
| `GetUsingsTool` | `roslyn_get_usings` — using directives + global usings |
| `GetSymbolDocumentationTool` | `roslyn_get_symbol_documentation` — XML doc comments for symbols |
| `GetSymbolDefinitionTool` | `roslyn_get_symbol_definition` — find declaration location with signature, the `lines` the declaration takes and the `at` its node starts on (#487; `line` and `column` are where its name is); several names in one `symbolName` are answered as `definitions[]`, one entry per name with its definition or its own error (#459) |
| `GetSymbolsInScopeTool` | `roslyn_get_symbols_in_scope` — enumerate accessible symbols at a location |
| `ReadFileTool` | `roslyn_read_file` — file contents as a one-line JSON header (`file`, `source`, `total_lines`, `start_line`, `end_line`) followed by the requested lines as raw, unescaped text (#328); tracked text documents come from the in-memory workspace. A disk file past `FileReader.DocumentLimit` is not refused (#401): it is streamed, the lines of the range are kept and the rest counted for `total_lines`; a line past the line limit is `line_too_long`, and a range that itself holds more than 64 MB is refused with a hint to narrow it. The edit tools still refuse such a file |
| `GetLineCountTool` | `roslyn_get_line_count` — line count for one or more files |
| `GetMemberBodyTool` | `roslyn_get_member_body` — return full source of a single method/property/field/type by name, as a one-line JSON header followed by the raw, unescaped declaration (#328); a partial type returns one header line plus raw text per part |
| `CheckSyntaxTool` | `roslyn_check_syntax` — validate a C# snippet for syntax (and optionally semantic) errors without writing to disk; fast pre-flight check before writing; returns `valid`, counts, and `DiagnosticItem[]` with 1-indexed lines mapped back to the original snippet |
| `GetTriviaTool` | `roslyn_get_trivia` (**EXPERIMENTAL**) — extract whitespace, comments, and formatting trivia; filter by syntax kind, trivia kind, or line range; useful for understanding indentation context |
| `InfoTool` | `roslyn_info` — server version, PID, uptime, MSBuild discovery method, log markers; `default_workspace`: what an omitted `projectPath` stands for or why there is none, and which setting chose its root (`--root`, `positional argument`, `ROSLYNMCP_ROOT`, `server working directory`), read from the cached answer without loading anything (#298); `code_actions.sources` lists the fixed sources (built-in, bundled), the configured entries, and a project's analyzer reference only when it holds a refactoring or a fix or had a problem — the rest (helper assemblies of analyzer packages, source generators, analyzer-only assemblies) are counted in `sources_without_providers`, and `allSources: true` lists them (#448); `workspaces[]`: every workspace the server holds, with its live `watch_roots` and any `unwatched_roots` (#495) — the sum of the watch roots is the watcher instances the server takes from the system |
| `DebugAttachTool` | `roslyn_debug_attach` (DEBUG only) — launches the JIT debugger dialog so Visual Studio can attach; blocks the server until dismissed or attached |
| `ApprovalStore` | Session-scoped approval state (`y`, `n`, `session` model); every workflow (`Rename`, `SignatureChange`, `CodeFix`, `Refactoring`) shares the same 3-state `TryBeginApply`/`CompleteApply`/`ReturnToPending` lifecycle to prevent double-apply races during a physical write |
| `BackupStore` | Crash-safe backup store for file write operations; stores pre- and post-write snapshots in `%LOCALAPPDATA%\RoslynMcp\backups\`; supports multi-level undo with token-based restore and conflict detection; async-safe via `SemaphoreSlim`; the pass that deletes expired snapshots runs on a thread of its own in a process that is a pruning run, one directory at a time under that lock, behind a named mutex of the store's own (by its path), which it waits for — never on a caller's time: the constructor ran it inside the first call of a tool that writes and held two such calls for 76 and 16 seconds (#445). A pruning run builds the store when the server starts, so a server that never writes prunes too. The pass opens a directory's meta file only when a snapshot's file name, which carries the time it was made, says one may be due (`MayHoldExpired`), and a snapshot with nothing left to be restored into is an orphan that goes after a week, not after the retention (`IsOrphan`: its file is gone, the folder it was in is gone, every project a snapshot of it was written for is gone, and no directory above where the folder was holds a `.git`, a solution or a project, `InLivingTree` — each of the first three alone, and all three together after a project's folder is renamed or on a branch without it, is an ordinary state of something still alive, so what is left is a tree removed whole: a deleted checkout, a temporary directory. And never where the place may only be away: a drive that is not there on Windows, a folder near a mount directory elsewhere, `LooksUnmounted`). Which paths are orphans is asked before the store's lock is taken, since the answer comes from the user's file system, and per path, since two paths can share a directory (#474). Logged as `Backups — pruned in N ms: D directories, S skipped unread, R expired snapshot(s) removed, O of them orphaned` |
| `PreviewFileStateBuilder` | Builds `PreviewFileState` snapshots (content hashes + intended bytes, encoding/preamble preserved) from a Solution diff; shared by every two-step preview tool (`roslyn_preview_rename`, `roslyn_change_signature`, `roslyn_preview_code_fix`, `roslyn_preview_refactoring`) so `PhysicalSolutionApplier` has what it needs to safely commit later |
| `PhysicalSolutionApplier` | Crash-safe, TOCTOU-guarded physical apply mechanism shared by `roslyn_apply_rename`, `roslyn_apply_signature_change`, `roslyn_apply_code_fix` and `roslyn_apply_refactoring`: builds a content-hash-verified `PhysicalSolutionApplyPlan` (Write/Create/Delete operations), re-validates disk state at three layers around backup preparation, and commits via atomic temp-file + `File.Replace`/`File.Move`. A document that is a symbolic link is written through when `SecurityBoundary` allows where it leads, and replaced where it sits (with a `_caution` in the result) when it leads out; plan entries that are one file on disk are written once (#334) |
| `PhysicalApplyResultMapper` | Maps a `PhysicalApplyReport` to the shared `ApplyFileResult[]` shape (per-file state, error, local-history recovery guidance) reused by all three apply tools' result records. An existing file that was written, in an apply that succeeded, has no `recovery`: how to undo it is said once, in the answer's `hint` (`UndoHint`), where the same sentence used to be more than half of every answer and repeated per file (#490). A created or deleted file, and every file of an apply that did not succeed, keeps its own |
| `RoslynMcpJson` | Shared `JsonSerializerOptions` with a custom `JavaScriptEncoder` — passes through Unicode characters without `\uXXXX` escaping; used by all tools for consistent serialization. A property without a value is left out of every tool answer (#490): the serializer the SDK writes answers with (`Program.cs`) and `RoslynMcpJson.Header` both ignore nulls, so a result record needs no `JsonIgnore` of its own for that, and a test or a reader must take an absent property for null |
| `IToolError` | Marker interface for error-bearing result types; paired with `ToolResult` in the `where T : ToolResult, IToolError` constraint on `ToolScope.Error<T>()` |
| `ToolResult` | Abstract base record for all tool results — success and error alike; nullable `Error`, `Hint`, and `Caution` properties are omitted from serialized JSON when null |
| `ErrorResult` | Standard error response record (`ToolResult, IToolError`); used by all tools for consistent JSON error shape; serializes as `{ "error": "...", "hint": "..." }` |
| `SolutionDiff` | Unified diff generation for `Solution` → `Solution` edits, shared by every two-step preview. The server's own differ: lines are matched by a longest common subsequence (common prefix and suffix paired directly, Myers' O(ND) search between them, bounded at 1,500 edits per file — past that the middle is reported as removed and added), then the gaps become hunks with three lines of context. A greedy matcher is not enough: one repeated line pairing with a far occurrence turns a ten-line change into a whole-file diff (#411). Two things since #491. `KeepBlankLinesAsync` runs in every preview on the solution an action proposes, before the diff and the file states are built from it: where a line was blank before and is blank after (the lines are matched with every blank line counted as the same), the original line is put back. Roslyn's formatter empties an indented blank line over the whole region it formats, which made a five-line action a hunk of hundreds and an apply that restyled them; a line with text that was re-indented is still a change, and a blank line the action adds stays as it came. Blank lines an action moved with their block (inverting an `if` swaps its branches) are paired in order when as many are left unmatched on one side as on the other; a blank line inside a token (a verbatim or raw string) is part of a value and is never touched; an action that changes nothing but blank lines is left as it is, since those lines are then what it is about. And a preview that got a token answers through the source terminal (`scope.Outcome(detail, header, source)`): one line of JSON, then the diff as raw text with paths relative to the workspace root. The header's `diff` is absent then; an answer without a token is plain JSON |
| `MSBuildBootstrap` | One-time MSBuild locator init; detects SDK vs VS workspace style; exposes `EnsureReady()`, `DetectLoadStyle()` (solution-aware: reads the `.sln`/`.slnx` project list, any legacy project ⇒ VS), `DetectProjectStyle()`, `ResolvedMode`, `DiscoveryMethod` |
| `MSBuildGate` | One MSBuild run at a time, process-wide (#379): every workspace load and reload (an MSBuild design-time build, in `LoadSolution` / `LoadMSBuildWorkspace`) and every `DotnetRunner` command take the same `SemaphoreSlim`, because they write the same generated files under `obj\`. A wait is logged naming what held the gate (`queued N ms behind a workspace load (Foo.slnx)`). A load waits at most the load timeout, which starts only once it holds the gate; past that it fails rather than run beside a long build, and the next call tries again. Disposal cancels a reload still queued on it, so teardown is not skipped behind a build Never taken under `cacheLock` (#277); a reload takes its `reloadGate` first |
| `PaginationCache` | Generic TTL-based cache for paginated tool results, shared across all tools via DI; stores each result set with the tool's static shape function so every page has the tool's own shape. `page_token` is a `PageCursor` (`{id}.{skip}.{take}`): the token alone yields the next page, and a token whose entry is gone re-runs the query from its position (#305). An entry is tied to the workspace it was computed from: every resolution guard notes that workspace's `WorkspaceChangeCounter` on the scope, the entry is stored with a check against it, and a later page is cut from it only while the workspace has not changed since — the server's own writes clear the cache, but a change made outside reaches the workspace through the watcher, which clears nothing (#435) |
| `SymbolFormatter` | Static helpers to format Roslyn `ISymbol` instances into human-readable signatures (method, property, field, event, type) |
| `SymbolVisitors.cs` | Roslyn symbol tree visitors (`SimpleNameFinder`, `AllSymbolsFinder`, `AnySymbolFinder`) used by reference and rename tools |
| `Exceptions.cs` | Project-specific exception types for workspace path resolution (`ProjectNotFoundException`, `MultipleProjectsFoundException`, `InvalidProjectPathException`, `AmbiguousFileException`) |

**Data flow:** stdio MCP request → tool → `RoslynMcpTool.TryGetCompilation(projectPath, ...)` → `WorkspaceResolver.GetCompilation()` → `WorkspaceManager` (resolve path, load/cache workspace) → Roslyn API → JSON response.

**Key files:** `Program.cs` (MCP protocol), `WorkspaceManager.cs` + `.Resolution.cs` + `.Instance.cs` (workspace caching, path resolution, workspace lifecycle; the nested `WorkspaceInstance` is one partial class over `.Instance.cs` — state, construction, queries, disposal — and `.Instance.Loading.cs`, `.Instance.Reload.cs`, `.Instance.Adhoc.cs`, `.Instance.Writes.cs`, `.Instance.Links.cs`, `.Instance.EditImpact.cs`, `.Instance.Watcher.cs`; its own nested types `EditClassifier` (in `.Instance.EditImpact.cs`), `LinkResolver` and `PhysicalFileIndex` have a file each), `WorkspaceResolver.cs` (tool facade), `RoslynMcpTool.cs` + `RoslynMcpTool.ToolScope.cs` + `RoslynMcpTool.Discovery.cs` (base class), `FileLogger.cs` (file logging), `LogEntry.cs` (shared NDJSON log schema — linked into both `RoslynMcp` and `RoslynMcp.LogViewer`).

**Nested types.** A type that only one other type has any business using is a `private` nested type of it, not an `internal` type in the namespace: the compiler then keeps it out of everyone else's reach for good (`WorkspaceInstance` inside `WorkspaceManager`; `EditClassifier`, `LinkResolver`, `PhysicalFileIndex` inside `WorkspaceInstance`). Move it out only when that scope actually changes. Nesting separates scope, not state — a nested type can read every private member of an instance of its outer type that it is handed. So a nested type is **given what it needs** (constructor or method arguments) and holds no reference to its outer instance. Code that cannot work without reaching into the outer instance's locks and fields is not a separate concern: it stays a member, in a partial file of its own if the file gets long (the watcher flush, the write path and the reload in `WorkspaceInstance`). Name the file after the nesting (`WorkspaceManager.Instance.LinkResolver.cs`), and avoid nesting deeper than three levels.

**Tool subfolders** (all share the `RoslynMcp.Tools` namespace — subfolders are organisational only):
- `Tools/Analysis/` — 26 read-only Roslyn semantic queries (diagnostics, symbols, types, usings, outline, drift, …)
- `Tools/Search/` — 4 file/content search tools (list files, text search, semantic search, string literal search)
- `Tools/Editing/` — 6 file mutation tools (`roslyn_replace_in_file`, `roslyn_replace_in_code`, `roslyn_insert_lines`, `roslyn_multi_edit`, `roslyn_write_file`, `roslyn_local_history`)
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
- Pruning: old logs deleted after `ROSLYNMCP_LOG_MAX_AGE_DAYS` days (default: 30); backups after `ROSLYNMCP_BACKUP_MAX_AGE_DAYS` days (default: 90), or after `ROSLYNMCP_BACKUP_ORPHAN_MAX_AGE_DAYS` days (default: 7) when the snapshot's file was in a tree that was removed whole (#474; see `BackupStore`); pruning runs only after the server has started at least `ROSLYNMCP_PRUNE_MIN_RUNS` times (default: 3). A start that reaches that count is a pruning run and writes the counter back to 0 in the same step (`FilePruner.IncrementAndGetRunCount`); the pruners run later, whenever their owner is first built (the backup store's as the server starts, in a pruning run), so a reset at a fixed point of startup came before all of them and never happened (#445)
- Format: NDJSON — one `LogEntry` object per line
- Unhandled exceptions (#371): a tool that throws logs its TOOL entry as `unhandled <Type>: <message>`, and a call that fails around its tool gets a TOOL entry of its own with the raw arguments: `failed before the tool ran: …` (the SDK cannot bind an argument), `cancelled before the tool ran: …`, or `failed after the tool returned: …` (e.g. serializing the result). A wrapper exception is followed by its root cause (`AggregateException: … ← IOException: …`). A name that matches no tool gets no entry; the SDK answers it. Neither does a failure the tool already logged before rethrowing (build, clean and restore log `cancelled`). Both are written by `RoslynMcpTool.UnhandledExceptionFilter`, a `tools/call` request filter: the tool's `ToolScope` is disposed while the exception unwinds and cannot see it, so it defers its entry to the filter. The client still gets the SDK's `An error occurred invoking '<tool>'.`
- What a TOOL entry's `detail` also says (#449): `waited N ms before the tool ran (pool: …)` when a quarter of a second or more passed between the `tools/call` filter and `BeginTool` — a tool's own clock starts in `BeginTool`, so a request that waits for a thread looked like a fast call (#445); `project <path>` on a failed call, the path the last resolution guard worked on, which since #296 is often one the server picked; `literal` / `regex` on `roslyn_replace_in_file`, with the size of what it writes (`replacement N chars, L line(s)`, #476: the pattern says how an edit finds its place, only the replacement how large it is) and `inMember` / `replaceAll` when the call used them; `denied with actions: ExtractInterface, …` on `roslyn_list_refactorings`, the deny-listed built-in providers that would have had an action there — in the log only, never in the answer (`CodeActionHost.DeniedWithActionsAsync`, which only the list tool calls, so a preview does not pay for it; a provider the user's configuration denies is not run for it)
- Places under declarations (#487): a `roslyn_find_references` entry says `N reference(s) in F file(s), G group(s), D in a declaration; flat ~C chars`, and a `roslyn_get_diagnostics` entry `N place(s) in F file(s), G group(s), E finding(s); flat ~C chars`: how the page grouped, and what the same places took in the flat form the answers had before. `roslyn_find_callers` (`calling line(s)`), `roslyn_semantic_search` (`match(es)`) and `roslyn_find_string_literal` (`literal(s)`) note the same as the reference search, `roslyn_get_call_graph` says `N call(s) under 1 declaration; flat ~C chars`, and `roslyn_build_project` notes its findings as `roslyn_get_diagnostics` does
- Search grouping (#460): a `roslyn_search_files` entry says `N hit(s) in F file(s), G group(s), D in a declaration, K declaring; flat ~C chars` — how the page grouped, and the characters the same hits would have taken listed flat, to compare with the entry's own size; a `roslyn_read_file` entry says `lines A-B`, which shows whether a read took up the lines a search reported for a declaration; the search entry also lists the ranges its page reported (`ranges: Foo.cs 23-323, 562-689; Bar.cs 10-20`, forty at most), since its preview of the answer is cut after the first group
- Changes taken in (#522): a watcher flush that takes the text of files changed outside the server into the workspace writes `Sync — Took in N file(s) changed on disk: A.cs, B.cs` (ten names at most). Such a change drops the compilations and every kept page; a file saved without a change is taken in by no flush and gets no line
- Reference counts (#456): every load and reload writes `Load — metadata references per project: Foo 167, Bar 212 (+1 project), …`, fewest first, sixty projects at most. A project that loaded with only part of its references is healthy to the health check, and this line is what shows it afterwards: the same project with fewer references than in its other loads
- `ThreadPoolProbe` (#449): a thread of its own queues one trivial work item a second and logs `ThreadPool — a queued work item waited N ms for a thread (pool: T thread(s), P item(s) waiting)` when that takes a second or more, and `… has been waiting N ms …` every five seconds while the item still has no thread — a pool that never recovers, or a server killed first, would otherwise leave no line. A thread and not a timer, whose callback is itself a pool work item. It is how a starved pool shows in the log at all: what waits for a thread has not run, and so has written nothing
- Key fields: `timestamp` (ISO 8601 UTC), `pid`, `level` (START/STOP/TOOL/ERROR/INFO), `instance` (per-process tool-call counter), `message` (non-TOOL entries), `tool_name`, `workspace_mode` (SDK/VS/ADH — MSB in pre-split logs and in an auto session before its first workspace load), `elapsed_ms`, `success`, `subject`, `detail`, `cache_tag` (`STORED`: a page computed by its query, whose result was kept for the next; `HIT`: a page cut from a kept result; `MISS`: a page token whose result was no longer kept, so the query ran again, which wins when both apply; absent when no paging was in play, an answer of one page included, #498), `estimated_tokens`, `session_tokens`, `response_peek` (truncated JSON preview of response)

**Workspace modes:**
- **MSBuildWorkspace** (if `.csproj` found) — full NuGet resolution, multi-project support, .NET Framework 4.6.1+ compatibility
- **AdhocWorkspace** (fallback) — source-only, fast startup (<100 ms); takes every `.editorconfig` under its root and in the directories above it as analyzer-config documents, kept current like the sources (`AddOrUpdateAnalyzerConfig`, #413), so code actions format as they do in a project
- **VS workspace** — uses Visual Studio's MSBuild instance when available
- See [docs/reference/WORKSPACE_MODES.md](docs/reference/WORKSPACE_MODES.md) for detailed comparison and FAQ
- **CLI flag:** `--workspace sdk|vs|adhoc|auto` (default: `auto`) — or set `ROSLYNMCP_WORKSPACE` env var to override; a committed `.madq_roslynmcp.json` at the repo root (written by `setup-project`) can pin a per-project mode when neither is given
- **Default workspace:** tools whose `projectPath` is optional fall back to a session default when it is omitted — `--root <path>` (or a bare positional path, so `"args": ["."]` works) > `ROSLYNMCP_ROOT` > server CWD, resolved to the one `.sln`/`.slnx` in that directory (`"solution"` in `.madq_roslynmcp.json` picks among several), else the nearest solution above it, else its single `.csproj`. Resolved lazily once and logged as `Workspace — Default workspace: …`. A relative `projectPath` is taken against the default workspace's directory when it names something there, else against the server CWD (`WorkspaceManager.RootedPath`, #298): tools report root-relative paths, and agents pass those back. `.sln`/`.slnx` paths are also accepted as an explicit `projectPath`; build/clean/restore then target the whole solution. Many MCP clients do not start servers in the project directory — prefer an explicit `--root` in client configs. A committed `.github/mcp.json` with `"args": ["--root", "."]` pins the root per checkout so each git worktree self-roots (see *Git Rules § Worktrees*); `.mcp.json` (gitignored) is the per-checkout local override
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

When editing C# code, pick the edit tool by the size of the change (#476); both check the syntax of what they write:
- `roslyn_replace_in_file` for a change of a few lines: quote the lines, give the new ones. On a `.cs` file an edit that adds a syntax error is refused, a literal snippet that matches several times replaces nothing and lists the places under their declarations, and `inMember: "Type.Member"` says which declaration is meant
- `roslyn_replace_in_code` for a whole node, named by kind and name with nothing of the old text to quote: rewriting a member, replacing one by several (a method and a new overload beside it; a type outside a type by several types), deleting one. It keeps the node's comments and trivia
- `roslyn_replace_in_file` is also the tool for text and config files
- `roslyn_multi_edit` whenever a change is more than one edit (#475): the edits of the three tools above, in one file or several, in one call. Positions are those of the files as last read, in any order, and all edits are written or none

**Before retyping code by hand, ask whether Roslyn has a refactoring for it.** Moving a type to its own file, extracting a method, introducing a local, inverting an `if`, converting to an interpolated string: `roslyn_list_refactorings` says what applies at a caret or selection, `roslyn_preview_refactoring` turns one action into a diff and a token, `roslyn_apply_refactoring` commits it. The refactoring keeps trivia, updates every reference and cannot mistype a line — a `roslyn_write_file` of the same change can. For a diagnostic (a CS or IDE id), `roslyn_preview_code_fix` offers Roslyn's own fixers and the project's analyzer-package fixers, and `roslyn_get_diagnostics` with `includeAnalyzers: true` and `severity: "hidden"` shows the IDE0xxx findings they fix.

For writing full file content (new files or wholesale rewrites), use `roslyn_write_file`:
- Takes crash-safe pre- and post-write backup snapshots automatically; returns a token usable with `roslyn_local_history` to undo
- Set `createNew: true` to create a new file or overwrite; default requires the file to already exist

When discovering files/content:
- `roslyn_list_files` — fast glob enumeration (find files by name/path)
- `roslyn_search_files` — content search (find lines matching regex pattern); C# source by default, every text file under the root when `filePattern` names other files
- `roslyn_semantic_search` — context-aware C# search (filter by comments, strings, identifiers, xmldocs, code)
- `roslyn_find_string_literal` — string-literal-only search with glob support and decoded-value matching
- `roslyn_find_references` — semantic symbol search (Roslyn-based, finds usage across project)

**Tool tips:**
- `roslyn_check_syntax` — call this before `roslyn_replace_in_code` or `roslyn_write_file` to catch syntax errors without a write round-trip; use `includeSemantics: true` to also validate against project types; use `wrapInClass: false` for complete class/namespace snippets or snippets containing `using` directives
- `roslyn_get_member_body` — use this to read a single method/property instead of `roslyn_read_file` on the whole file
- `roslyn_get_file_outline` — use to understand a file's type/member structure (signatures only, no bodies); more token-efficient than `roslyn_read_file` for exploration
- `roslyn_search_files` — each hit comes under the declaration it is in: read the whole member with the group's `lines` (`roslyn_read_file`), edit it with its `at` (`roslyn_replace_in_code`), and find a declaration by its plain name (`declares: true`) without a keyword in the pattern. For a project file, a props file, config or Markdown, name it in `filePattern` (`*.csproj`, `Directory.Build.*`, `*.md`, or `*` for everything): those are searched from disk, and `skipped` counts the files that were not
- `roslyn_find_references` — without `containingType`, searches ALL symbols matching the name (union of results). Use `containingType` to narrow. Ask several symbols in one call (`symbolName: "InvalidateFile|RequestReload"`): each gets its declarations and references, where a regex search for the names gives lines without knowing which symbol they are. `roslyn_get_symbol_definition` takes a name list the same way.
- `roslyn_list_types` — without `namespaceFilter`, returns only project-defined types (not framework). Use `namespaceFilter` for sub-namespace scoping.
- `roslyn_get_diagnostics` — use `severity: "errors"` or `take: 0` for a fast error-only check during editing; a solution (a `.sln`/`.slnx` `projectPath`, or none with a solution as default workspace) checks every project, and each item names its `project`. `roslyn_list_types`, `roslyn_find_unused` and `roslyn_get_project_info` cover every project of a solution the same way (#296). A tool that names a symbol finds the project that declares it; a tool that names a file, the project that contains it. Only a call that names neither — `roslyn_check_syntax` with `includeSemantics` and no `filePath` — still answers a solution with `solution_needs_project` and the `.csproj` files to pick from (#376)
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
- Several edits:  roslyn_multi_edit (one call, positions from the files as last read)
- Insert lines:   roslyn_insert_lines (not Edit)
- Refactor:       roslyn_list_refactorings → roslyn_preview_refactoring → roslyn_apply_refactoring (not retyping code: move type to file, extract method, …)
- Fix diagnostic: roslyn_preview_code_fix → roslyn_apply_code_fix (not hand edits)
- Build:          roslyn_build_project (NEVER dotnet build in terminal)
- Diagnostics:    roslyn_get_diagnostics

Violation triggers permission prompts that block the user.
```

---

## Code Style

**Respect each other's style.** Nobody's formatting is the rule here, the owner's included. Three things are asked of everyone, people and agents alike:

- **In a small change, keep the style of the code around it.** A block that braces its single statements gets a braced statement. A file whose blank lines are indented keeps them indented. The change should read as if whoever wrote the surrounding code had made it.
- **In new code, use your own.** A new method, property, type or file is yours to format. Nobody has to ask first, and nobody has to imitate anyone.
- **Never restyle code that is not otherwise being changed.** No formatting passes, no drive-by fixes to braces or whitespace. A diff shows the change and nothing else. A tool that reformats more than it changed (a refactoring's formatter, an editor's format on save) counts too: put back what it touched around the change.

The reason is at the end of this part of the file: *Consistency is overrated. Embrace diversity.*

**Not style, and expected of everyone:**

- **Comments** explain *why*, not *what*. After an edit, re-read the comments near it and update or remove the stale ones. Complete sentences, no personal pronouns (`we`/`I`/`our`).
- **`TODO` comments** include the actual question or concern, not just "fix".
- **LINQ and deferred execution:** know when enumeration happens; materialize once when enumerating several times, stay lazy when enumerating once (see *Performance & Allocation*).

### The owner's habits (for reference, never a requirement)

What most of the existing code looks like, and so what a small change in it usually looks like. These are one person's preferences from an old experiment with style rules. They are listed so that the surrounding code can be read, not so that new code copies them.

- **Braces:** same line for control flow (`if(x) {`), new line for methods/classes; properties — same line as the identifier (`public int Count {`)
- **No space** after `if`/`foreach`/`while`: `if(x)` not `if (x)`
- **Single-statement blocks:** no braces
- **Naming:** PascalCase for types/methods, camelCase for fields/locals — no underscores, no Hungarian, no abbreviations
- **Handles:** `nint`, not `IntPtr`
- **Modern C#:** pattern matching, switch expressions, target-typed `new`, collection expressions
- **`var`:** when the type is obvious or long; explicit type otherwise (rarely)
- **Column-aligned fields:** tab-stop alignment on field declarations and assignment blocks
- **Cast spacing:** `(int) value`, not `(int)value`
- **Condition ordering:** simple/common path first — early return or assignment; complex path in `else`
- **Blank lines:** before `return` and before code blocks; after the opening brace of a multi-statement control-flow block; between the branches of an `if/else` chain when a body spans several lines; between distinct statement groups; and **indented** to match the surrounding scope
- **Semicolons** on their own line for wrapped multi-line expressions (fluent chains, ternaries, LINQ, arrow bodies)

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
> - **Style/formatting:** Respect the style that is there, use your own in new code, restyle nothing (see [Code Style](#code-style))
> - **Problem-solving:** Question everything! If the existing approach sucks, say so. If there's a better pattern, propose it. If a "best practice" is actually cargo-culting, call it out. **Innovation lives here.**
> - **When to deviate:** Compelling technical reason (performance, API limitation, framework requirement). Add a comment explaining why.
>
> **What this means for human contributors:**
> - Your own formatting in your own code is fine—that's how better patterns get discovered
> - Try something different, sit with it, form an informed opinion
> - A snap judgment that "it's wrong" is a reflex; a considered judgment after living with it is **data**
> - The owner's habits exist because someone already walked that mile—but if your mile leads somewhere new, the map gets updated
> - Just don't go completely feral on us 🏴‍☠️
>
> **Your style is yours, so `.editorconfig` is gitignored.** A committed one would impose one person's preferences on everyone, which is exactly what this section refuses to do. Keep a private `.editorconfig` at the repo root if you like: your IDE honours it, and so do Roslyn's refactorings and code fixes when `roslyn_apply_refactoring` / `roslyn_apply_code_fix` run them — Roslyn reads its formatting options (`indent_style`, `csharp_new_line_before_open_brace`, `csharp_space_after_keywords_in_control_flow_statements`, …) from that file, and without one it formats the changed region in its own defaults (four spaces, Allman braces, `foreach (`). The harness pins this for both workspace kinds, with a gitignored file (*Code Refactorings*, #413). Agents: never create or commit an `.editorconfig`; if the owner's private one is present, the refactoring output already matches it.
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
- **RMCP009** — a `projectPath` parameter must be declared `string? projectPath = null` (a literal `null` default) and use `[Description(OptionalProjectPathDescription)]` — a required `projectPath` is a tool that does not find its project on its own (#296), and inline strings drift; the constant is the single source of truth (Error)
- **RMCP010** — a string literal (descriptions, hints, messages) must not name a tool without its `roslyn_` prefix: an agent calls what it reads, and `apply_rename` is an unknown tool (Warning, #326). The names checked are the compilation's own `[McpServerTool(Name = …)]` values with an underscore after the prefix, so a new tool is covered at once, one-word names (`info`, `respawn`) are not matched, and the TestHarness, which declares no tools, is never checked. Comments are not checked

**Always use `OptionalProjectPathDescription`** for the `projectPath` parameter — the constant with the canonical, full description. Inline text drifts and diverges. Never duplicate it.

#### Parameter Conventions

```csharp
public object MyToolMethod(
    [Description("...")] string requiredParam,           // required params first
    CancellationToken cancellationToken,                 // then the SDK-injected ones
    [Description(OptionalProjectPathDescription)] string? projectPath = null,  // then projectPath, optional
    [Description("...")] string? optionalParam = null)   // and the other optional params
```

`projectPath` has one shape, enforced by RMCP009: `[Description(OptionalProjectPathDescription)] string? projectPath = null`, declared after every required parameter (and after `CancellationToken`). A statement near the top of the method normalizes it — after `BeginTool`, after any `TryStripChatSymbolRef` on the names it uses, and after `TryServeCachedPage` — and from there on `projectPath` is a plain non-null string and every helper works unchanged. Which statement depends on what the tool can find its project by:

- **Solution-level and aggregate tools** — `if(!TryResolveProjectArg(projectPath, out projectPath, out var projectError)) return scope.Error(projectError);` An omitted value becomes the default workspace path, or `missing_project_path` when there is none. A tool that covers a project then takes every project the path stands for from `GetTargetsAsync` and names the project on each item when there are several.
- **File-anchored tools** — the `TryResolveProjectArg(projectPath, filePath, …)` overload, which resolves an omitted value, or a solution, to the .csproj of the project that contains `filePath` — and fails with `ambiguous_file` when a suffix-only `filePath` names different files in different projects.
- **Symbol-by-name tools** — `var (resolvedProject, projectError) = await ResolveSymbolProjectArgAsync(projectPath, symbolName, containingType, SymbolSought.…, filePath, cancellationToken);` then `if(projectError is not null) return scope.Error(projectError); projectPath = resolvedProject;`. `SymbolSought` says what the tool is after (`Type`, `Method`, `Member`, `Any` type-first, `All`); `filePath` is `null` for a tool that takes no position; the two that may elicit (`roslyn_preview_rename`, `roslyn_change_signature`) pass their `McpServer` as well. Always awaited, with the call's token: the lookup reads the solution's declaration index and may compile project after project (#444). An omitted value, or a solution, becomes the .csproj that declares the symbol; a name declared in several projects fails with the candidates.
- **Apply tools** — nothing: the token's `WorkspaceBinding` is the workspace, and a `projectPath` that is given is only checked against it (`CheckApplyWorkspace`).

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
| `scope.Record(note)` | Append a mid-scope annotation. Useful for recording intermediate outcomes ("cache hit", "2 workspaces merged") that don't change the final outcome. Notes are kept apart from the terminal's detail and joined after it (`{detail}; {note}; …`) when the entry is written, so they survive `Outcome`/`Failed`/`Error`; a null or empty note is ignored, and so is a note the call has already recorded (#490: a batch resolves a project per edit, and noted it per edit). |
| `scope.NoteStored()` | Record that the answer is a page computed by its query, with the rest of the result kept for the next (`cache_tag: STORED`). Called by `PaginateAndStore` only when there is more; `TryServeCachedPage` sets `HIT` for a page cut from a kept result and `MISS` for a token whose entry was gone. An answer of one page has no tag (#498). |
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

Without this, subsequent Roslyn tools see the stale in-memory source tree, not the updated file. `InvalidateFile` classifies the path via `GetDocumentIdsWithFilePath` + a `GetDocument`/`GetAdditionalDocument`/`GetAnalyzerConfigDocument` check: a tracked `.cs` document *or* a tracked `AdditionalFiles` item is applied incrementally (`Solution.WithDocumentText`/`WithAdditionalDocumentText`, #276), in memory only: the file already holds the text, and the workspace must not write it there again (`ApplyTextsInMemory` / `WorkspaceTextSetter`, #403 — an edit that is not on disk yet goes through `ApplyChangesToDisk` instead); a new `.cs`, a new `AdditionalFiles` item, an MSBuild evaluation input (`.csproj`, `.props`, `.targets`, `Directory.Build.*`, `global.json`, `nuget.config`, `packages.lock.json`, `.resx`), or a tracked analyzer-config document (`.editorconfig`, `.globalconfig` — `CanApplyChange(ChangeAnalyzerConfigDocument)` is `false` in Roslyn 5.3.0 and 5.9.0, so these always reload even when already tracked) flags a full reload on the next compilation-needing call, logged as `Reload — Flagged (<reason>): <path>`; anything else (`.md`, `.txt` not declared as an `AdditionalFiles` item, an unrelated `.json`) is a no-op for the workspace, so calling it is always cheap and always correct. See `docs/development/WORKSPACE_SYNC.md`.

#### Key Points (Summary)

- `projectPath` is always `string? projectPath = null` + `OptionalProjectPathDescription` (RMCP009), normalized near the top of the method with `TryResolveProjectArg` (solution-level, aggregate, file-anchored) or `ResolveSymbolProjectArgAsync` (symbol by name) — the only way a tool reaches the default workspace; `ResolveProjectPath("")` itself throws
- `using var scope = BeginTool(...)` is always the first statement
- `Name` in `[McpServerTool]` must match `BeginTool`'s first argument exactly
- Every `return` with a value must go through `scope.Outcome`, `scope.Error`, or `scope.Failed`
- Use `[Description(OptionalProjectPathDescription)]` — never inline text for `projectPath`
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

- **Config is committed and portable.** `.github/mcp.json` defines the `MadQ.RoslynMcp` server with `--root .`, started through `scripts/Start-RoslynMcp.ps1` (the development host when it is installed, the installed tool otherwise — see *Testing § Development host*); being committed, every worktree checkout already carries it, and the relative root resolves to whichever worktree the session launched in — no global `~/.copilot/mcp-config.json` edits, ever. (`.mcp.json` is gitignored for per-checkout local overrides.)
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

**Filtered or full.** Filter while iterating: a full run takes about three minutes, most of it loading projects and running `dotnet`, and one group usually takes seconds. Before pushing, run the full suite when the change touches code that many tools share — `RoslynMcpTool`, `WorkspaceManager`, `SecurityBoundary`, `FileWriter`, `PhysicalSolutionApplier`, … — because such code is covered by groups other than the one holding the new test. #362 changed a helper in `RoslynMcpTool` used by twelve tools, whose tests are spread over many groups. A change confined to one tool and its group can rely on its group locally: every pull request runs the full suite in CI on Linux, and every night on all three systems (see *Pull requests, the night and the release gate*).

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

**Late answers (#455).** `TestContext` matches the server's answers to requests by id. A read that times out abandons the requests it was owed answers to, and an answer to an abandoned request is dropped when it comes, with a `[late answer] …` line; a test never reads the previous call's answer as its own, which is how one stalled call used to fail every later test of its group. Notifications, the server's own requests, and the answers to several requests sent before a read pass through as they arrive. Group *Harness Protocol* checks this over in-memory streams.

**Skipped tests.** A test that cannot check anything on the machine is a skip, never a pass. It prints a `SKIP` line in quiet mode too and is counted in the summary: `PASS  240 passed, 1 skipped`. Use `Skip.NotApplicable(reason)` when the thing under test does not exist on this operating system (Unix file modes on Windows), or when a check audits a whole run and the run was filtered (#367) — always a skip; CI never filters. Use `Skip.SetupUnavailable(reason)` when the test applies but the machine will not let the fixture be built (no permission to create symbolic links) — a skip locally, a **failure on a CI runner**, so the release gate cannot pass with a case nobody checked. A slow test the run did not ask for is the third kind of skip (`Skip.SlowNotRequested`, written by the harness, not the test — see `--slow` above). Never return a plain pass from a test that did not run.

Do not filter the full output with a text match instead — passing tests with "error" or "fail" in their name match too.

**Linux run under WSL (#325).** The harness normally runs on Windows only, so anything that behaves differently on Linux is untested, and a test that skips itself on Windows checks nothing. `scripts/Test-HarnessWsl.ps1` runs the same harness on Linux, against the current working tree including uncommitted changes:
```powershell
.\scripts\Test-HarnessWsl.ps1 -Quiet
```
- It copies the tree into the WSL filesystem (`~/.roslynmcp-wsl/tree`, without `bin`/`obj`/`.git`) and runs there; running from `/mnt/<drive>` would share build output with Windows and is slow.
- Linked git worktrees inside the working tree (`.claude/worktrees/<name>`) are not copied (#511): each is a second checkout, and without `.git` the server could not tell one from a directory of the tree.
- It needs the SDK version `global.json` pins, exactly: a local build also takes a newer 10.0.x SDK (#506), but this run is there to show what CI will see, so the copied tree gets `rollForward: disable` as a CI job's checkout does. If WSL lacks it the script says so; `-InstallSdk` installs it into `~/.roslynmcp-wsl/dotnet` (not on PATH, system SDK untouched).
- Further harness arguments go through `-HarnessArgs`, one array element per argument: `-HarnessArgs '--only','dotnet runner,linked files'`. A single string such as `'--only "a,b"'` arrives as one unknown argument, and the harness rejects it with exit code 2.
- Exit codes: 0 — all passed; 1 — the harness reported failures, or did not build on Linux; 2 — the harness could not be run (WSL, the distribution, `rsync` or the pinned SDK is missing, or a setup step failed).
- A full run needs more inotify instances than Linux gives a user by default (128): every fixture that is deleted while the harness server still watches it costs one for the rest of the run (#499, a leak in .NET 10's watcher). The script warns before the run when `fs.inotify.max_user_instances` is below 512 and names the line that raises it; with the default, tests that wait for a file event fail late in the run (*Ignored Directories*, *Incremental Additional/AnalyzerConfig Doc*). CI raises the limit to 1024.

**When to run it:** the Windows run (full or filtered, see above) is the verify step for every change. Add the WSL run when the change is platform-sensitive — file permissions or modes, symlinks, path separators or case-sensitive names, process spawning, shell or command-line parsing, anything behind an `OperatingSystem.Is…` check — or when a test in the change returns early on Windows. Report it separately ("Windows 239/239, Linux 239/239"); if WSL is not available on the machine (the script exits with code 2), say that the platform-only tests did not run rather than reporting a plain pass. macOS is not covered by either local run — see below.

**Pull requests, the night and the release gate (#330, #367, #469).** `.github/workflows/harness.yml` runs the full harness with `--quiet`. It runs:
- on every pull request to `dev` or `main`, on `ubuntu-latest` only — the platform no local run covers by default, and the faster job (about eight minutes; Windows took twelve). Docs-only pull requests included, because a path-filtered workflow reports no status and would block a docs-only pull request if the check were ever made required. A new push cancels the run still going for the previous one. **The check is not required, and a green run is not something to wait for:** merge when the change is ready; a run that goes red a few minutes later names its pull request;
- every night at 07:00 UTC on `dev`, on all three operating systems, through `.github/workflows/nightly.yml`. This is where CI runs Windows and macOS between releases. A failed night opens the issue *The nightly harness run failed*, or comments on it while it is still open; it came in with one of the pull requests merged since the last green night. *Run workflow* on *Nightly Harness* runs it by hand;
- as part of `publish.yml`, on all three operating systems including `macos-latest`. The `publish` job needs it: nothing is published — not even a dry run reaches the packing step — unless all three pass;
- by hand: Actions → *Test Harness* → *Run workflow*, on any branch (`gh workflow run harness.yml --ref <branch>`), on all three. A run by hand also takes a system, the harness's arguments and a number of repetitions (#339), for a test that fails now and then on one system: `gh workflow run harness.yml --ref <branch> -f os=macos-latest -f "harness_args=--quiet --only ignored" -f repeat=40` runs one group forty times on macOS in about six minutes, makes every run also after one failed, and ends with `N of 40 run(s) failed`. The arguments are split at spaces with no quoting, so a group is named by one word of its name.

Windows is no longer run by CI for a pull request, so it rests on the local run before the push: the full suite when the change touches shared code, the change's own groups otherwise (see *Filtered or full*), with the night as the backstop.

Pushes to a branch without a pull request run only `build.yml`, which builds.

Run it by hand before merging a change that is platform-sensitive on macOS specifically (temp-directory paths, symlinks, file watching, case-insensitive-but-preserving names): macOS is the one platform no local run covers, and its first run found a boundary bug that Windows and Linux never hit.

**Fixture pattern (#274):** a test that needs real compilable code, or any file to edit, gets a throwaway project under `%TEMP%\RoslynMcp.TestHarness\<label>.<guid>` from `TestFixtures.NewMsBuildProject(label)` (or `NewAdhocDir` for an AdhocWorkspace) and passes `fx.Csproj` as `projectPath`. **Never write a fixture into `src/RoslynMcp`:** a directory target resolves to the repo's `.slnx`, so the FileSystemWatcher of every server with the repo open — the harness's own *and* the developer's live one — covers the whole tree; the SDK glob compiles the file into `RoslynMcp.dll`; and each create/delete forces a full solution reload. A checked-in fixture project elsewhere in the repo, or `<Compile Remove>`, does not help — the file is still an unknown document under the watched root. Example:
```csharp
var fx = TestFixtures.NewMsBuildProject("BuildDiag", targetFrameworks: "net10.0");
fx.Write("Probe.cs", "class Probe { }\n");   // before the first tool call — the initial load includes it
// ... call tools with projectPath = fx.Csproj, assert ...
return new TestGroup(header, tests, Teardown: () => { fx.Dispose(); return Task.CompletedTask; });
```

The harness points every server it starts at a backup directory of its own under the fixture root (`ROSLYNMCP_BACKUP_PATH`, set on the harness process), deleted when the run ends and swept with the fixture trees when a run was killed (#474): the snapshots of a run are not the developer's (#445). Write every fixture before the group's first tool call, so no test depends on the 300 ms watcher debounce, and dispose the fixture in the group's `Teardown`. Fixtures live outside the repo, so its `global.json` does not reach them: `TestFixtures.PinSdk` copies it to the fixture root at startup, or each fixture would resolve the newest SDK installed — and one newer than the server's MSBuild can load gives every unrestored fixture a load with no references. `TestFixtures.SweepStale` runs at startup and removes trees a killed run left behind (plus scratch files an old harness binary wrote into `src/RoslynMcp`). The last group, `WorkspaceHygieneTests`, reads the harness server's log and fails if any reload was flagged for a path inside the repo — the log line names the file, so the offending test is one grep away. Only read-only tests run against the dogfood project.

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

**Development host (#426):** `src/RoslynMcp.DevHost/` — the process an MCP client launches in place of the server during development. It starts the server build that the pointer file `%LOCALAPPDATA%\RoslynMcp\dev\current` names and forwards stdio unchanged, reading only each message's `id` and `method`. `scripts/Publish-DevServer.ps1` publishes the working tree into a new directory under `dev\builds` and writes the pointer; every running host then swaps to that build at its next idle moment (no request waiting in either direction), replaying the client's `initialize` to the new server before the old one is stopped and sending the client `tools/list_changed`. A build that does not start or answer is refused and the session keeps its server. The server's state (preview tokens, session approvals, the loaded workspace) is gone after a swap. The server's own exit ends the host with the same exit code. Development only: the tool package installs the plain server and the host is never packed. Install it once with `.\scripts\Publish-DevServer.ps1 -InstallHost`: the committed `.github/mcp.json` runs `scripts/Start-RoslynMcp.ps1`, which starts the host when it is installed with a published build and the installed tool otherwise (`ROSLYNMCP_USE_DEVHOST=0` forces the tool, #464); a client configured outside the repository is pointed at the host by hand. The server knows nothing of the host — keep it that way, so what is dogfooded is what ships. Details: `docs/development/DEV_HOST.md`; harness group *Dev Host*.

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
