# RoslynMcp

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![MCP](https://img.shields.io/badge/MCP-1.4.1-blue)](https://modelcontextprotocol.io/)
[![Beta](https://img.shields.io/badge/status-beta-blue)]()

**Give your AI agent a C# compiler instead of grep.**
([first battle-test results: 38-69% token savings, bugs found, lessons learned](docs/battle-test-results.md) · [shared workspace architecture](docs/plans/multi-instance-architecture.md) · [help wanted](#help-wanted))

RoslynMcp is a [Model Context Protocol](https://modelcontextprotocol.io/) server that gives AI coding agents real Roslyn compiler semantics: type resolution, cross-file references, semantic rename, diagnostics, and 43 MCP tools (41 public + 2 debug-only). Not string matching. Not regex. Actual compiler-level understanding of your C# code.

```
Agent: "Rename OrderStatus.Pending to OrderStatus.AwaitingApproval"

Without RoslynMcp:  grep + find-and-replace across 50 files. Hope nothing else is named "Pending."
With RoslynMcp:     roslyn_preview_rename -> reviews diff across 3 projects -> roslyn_apply_rename. Done.
```

Works with any MCP-compatible client: Claude Code, GitHub Copilot, Claude Desktop, Cline, Cursor, Windsurf, Roo Code, Continue, and more.

> **Security Note:** RoslynMcp runs with your user permissions. File access is bounded to the resolved workspace tree — project/solution root plus referenced projects ([#9](https://github.com/MadQ/RoslynMcp/issues/9)) — but `projectPath` can still target any project you can read, so only use with trusted agents and on projects you control.

> [!IMPORTANT]
> **Breaking change (pre-1.0): the tool identity was namespaced under `MadQ`.** To avoid a hard clash with the unrelated [`RoslynMcp`](https://www.nuget.org/packages/RoslynMcp) package already on NuGet, the package id, its `dotnet tool` command, and the MCP server key all changed:
>
> | | Old | New |
> |---|-----|-----|
> | NuGet package | `RoslynMcp` | `MadQ.RoslynMcp` |
> | `dotnet tool` command | `roslynmcp` | `madq-roslynmcp` |
> | MCP server key | `roslyn` | `MadQ.RoslynMcp` |
>
> If you configured an earlier build, update your MCP config to the new server key and command. Your client's cached tool approvals are keyed to the old name and will re-prompt — see [Troubleshooting](docs/guides/TROUBLESHOOTING.md#tools-re-prompt-for-approval-after-renaming-the-mcp-server-key).

---

## Quick Start

**1. Get RoslynMcp**

**Option A — Install from NuGet as a .NET tool** (requires .NET 10 or 11 SDK; easiest to keep updated):

```bash
dotnet tool install --global MadQ.RoslynMcp --prerelease
```

This puts the `madq-roslynmcp` command on your PATH. In the MCP config below, use
`"command": "madq-roslynmcp"` instead of a full `.exe` path. Update later with
`dotnet tool update --global MadQ.RoslynMcp --prerelease`.

> `--prerelease` is required while RoslynMcp is in beta — a prerelease-only package won't resolve without it.

Then run:

```bash
madq-roslynmcp setup
```

`setup` detects your installed MCP-compatible clients and writes the server config for you —
no manual JSON editing needed. It's interactive, so it'll also ask about optional flags like
`--elicit`. If you'd rather configure a client by hand (or `setup` doesn't detect it), use the
manual JSON in step 2 below.

> [!IMPORTANT]
> **Tell your agent to use RoslynMcp.** Agents default to grep and file reads unless you explicitly instruct them. Add a few lines to your project's `CLAUDE.md` or `AGENTS.md` — see [Agent Instructions](#agent-instructions) for a quick example, or [docs/AGENT-INSTRUCTIONS.md](docs/AGENT-INSTRUCTIONS.md) for complete copy-paste instructions covering every tool. Having trouble getting your agent to comply? See [#99](https://github.com/MadQ/RoslynMcp/issues/99).
> Claude Code users: try our experimental [PreToolUse hook](scripts/enforce-roslyn-tools.sh) to enforce this automatically.

**Option B — Download and extract** (simplest, no SDK required):

Download the latest release from the [Releases page](https://github.com/MadQ/RoslynMcp/releases/latest) — grab the `net10.0` asset. Extract it anywhere and note the full path to `RoslynMcp.exe`.

**Option C — Clone and build** (requires .NET 10 or 11 SDK; for contributing or testing local changes):

```bash
git clone https://github.com/MadQ/RoslynMcp.git
cd RoslynMcp
dotnet pack src/RoslynMcp/RoslynMcp.csproj -o nupkg -c PackDebug
dotnet tool install --global MadQ.RoslynMcp --add-source ./nupkg --version 0.8.1-beta
```

This installs your local build as the `madq-roslynmcp` command, exactly like Option A — run `madq-roslynmcp setup` from there. (Made more code changes? Re-pack, then `dotnet tool uninstall --global MadQ.RoslynMcp` before reinstalling — a stale global install otherwise keeps serving the old build.)

**2. Add to your MCP client config.**

**Claude Code** — create `.mcp.json` in your project root:

```json
{
  "mcpServers": {
    "MadQ.RoslynMcp": {
      "type": "stdio",
      "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
    }
  }
}
```

> Global alternative: add the same `"mcpServers"` block to `~/.claude.json` (`%USERPROFILE%\.claude.json` on Windows).

**GitHub Copilot (VS Code / CLI / Visual Studio)** — create `.mcp.json` in your project root:

```json
{
  "servers": {
    "MadQ.RoslynMcp": {
      "type": "stdio",
      "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
    }
  }
}
```

> Global alternative: add the same `"servers"` block to `~/.copilot/mcp-config.json` (`%USERPROFILE%\.copilot\mcp-config.json` on Windows).

See [INSTALLATION.md](INSTALLATION.md) for Claude Desktop, Cursor, Windsurf, Cline, Continue, Roo Code, Zed, and direct CLI usage.

**3. Start using it.** Every tool accepts a `projectPath` parameter pointing at your `.csproj`, `.sln`, or project directory. Your agent handles this automatically.

```
"What does the ProcessOrder method do?"
-> Agent calls roslyn_get_member_body("ProcessOrder", projectPath: "src/MyApp")
-> Returns just that method's source. 20 lines, not a 600-line file dump.
```

---

## Why RoslynMcp?

AI agents working on C# through file reads and regex have a structural problem: they pattern-match text instead of understanding code. RoslynMcp fixes that by keeping a live Roslyn compilation in-process, incrementally updated as files change.

**Find references, not text matches.** `roslyn_find_references` uses Roslyn's semantic model to find every actual usage of a symbol across your entire solution. Grep finds string matches. Roslyn finds the call site in `OrderService`, the override in `PriorityOrderProcessor`, and the test mock in `OrderServiceTests` -- even when they use different names through inheritance.

**Read one method, not the whole file.** `roslyn_get_member_body` returns just the source of a single method, property, or field. When your agent needs to understand `ProcessPayment`, it gets 15 lines instead of reading a 600-line file. Massive token savings, every call.

**Rename with confidence.** `roslyn_preview_rename` + `roslyn_apply_rename` performs semantic rename across your entire solution. It knows that `order.Status` and `IOrder.Status` are the same symbol. Grep doesn't.

**Build without leaving the process.** `roslyn_build_project` checks Roslyn diagnostics first — fast, in-process, no MSBuild spawn. If there are errors, it returns them instantly. Clean code triggers a real `dotnet build` for full validation.

---

## Tool Catalog

43 total MCP tools: 41 public tools organized by what you need to do below, plus 2 debug-only tools: `roslyn_respawn` and `roslyn_debug_attach`. All tools work in-process using Roslyn APIs unless noted.

### Discovery

| Tool | What it does |
|------|--------------|
| `roslyn_search_files` | Regex search across workspace files with paging |
| `roslyn_semantic_search` | Context-aware C# search -- filter by comments, strings, identifiers, xmldocs |
| `roslyn_find_string_literal` | Search C# string literals with glob or regex; returns raw and decoded values |
| `roslyn_list_files` | Glob-based file enumeration (fast, no content) |
| `roslyn_list_types` | All types in the project with namespace/kind filters |

### Navigation

| Tool | What it does |
|------|--------------|
| `roslyn_find_references` | Every reference to a symbol across the solution |
| `roslyn_find_unused` | Private/internal source symbols with zero direct static references |
| `roslyn_find_callers` | All methods that call a named symbol (direct or via interface dispatch) |
| `roslyn_get_call_graph` | All methods invoked within a method body (IOperation tree walk) |
| `roslyn_find_implementations` | All types implementing an interface or overriding a member |
| `roslyn_get_symbol_info` | What a name at a location actually resolves to |
| `roslyn_get_symbol_definition` | Jump to where a symbol is declared |
| `roslyn_get_symbols_in_scope` | All symbols accessible at a file location |
| `roslyn_get_type_hierarchy` | Base types, interfaces, and derived types |

### Reading Code

| Tool | What it does |
|------|--------------|
| `roslyn_get_member_body` | Source of a single method/property/field -- the token saver |
| `roslyn_get_type_members` | All members of a type with full signatures and doc summaries |
| `roslyn_find_overloads` | All overloads for a method on a containing type, with full signatures |
| `roslyn_get_type_dependencies` | Direct type dependencies from a type declaration and member signatures |
| `roslyn_get_file_outline` | File structure (types + member signatures, no bodies) |
| `roslyn_get_symbol_documentation` | XML doc comments for any symbol |
| `roslyn_get_usings` | Using directives + implicit global usings |
| `roslyn_get_project_info` | Project metadata: TFM, packages, language version |
| `roslyn_read_file` | File contents with line numbers (C# from in-memory workspace) |
| `roslyn_get_line_count` | Line count for one or more files |
| `roslyn_get_diagnostics` | Compiler errors and warnings without building |
| `roslyn_get_trivia` | Whitespace, comments, formatting trivia (experimental) |

### Editing

| Tool | What it does |
|------|--------------|
| `roslyn_check_syntax` | Validate a C# snippet for syntax (and optionally semantic) errors without writing to disk |
| `roslyn_replace_in_code` | Semantic C# editing -- replaces syntax nodes, validates syntax |
| `roslyn_replace_in_file` | Text-level find-and-replace with regex (any file type) |
| `roslyn_insert_lines` | Insert lines at a position or anchor pattern |
| `roslyn_write_file` | Atomically write or create a file; returns a backup token for undo |
| `roslyn_local_history` | Browse, preview, and restore crash-safe file backups (list/preview/apply) |

### Refactoring

| Tool | What it does |
|------|--------------|
| `roslyn_preview_rename` | Compute rename diff + confirmation token |
| `roslyn_apply_rename` | Apply or reject a previewed rename |
| `roslyn_preview_code_fix` | Preview a bundled Roslyn code fix and return a reviewed approval token |
| `roslyn_apply_code_fix` | Apply or reject an approved code-fix preview using verified physical writes |
| `roslyn_change_signature` | Add parameters with non-breaking forwarding overload |
| `roslyn_apply_signature_change` | Apply or reject a previewed signature change |

### Build

| Tool | What it does |
|------|--------------|
| `roslyn_build_project` | Smart build: Roslyn diagnostics first, MSBuild only if clean |
| `roslyn_clean_solution` | Remove all build artifacts |
| `roslyn_restore_packages` | Restore NuGet packages |

### Diagnostics & Info

| Tool | What it does |
|------|--------------|
| `roslyn_check_drift` | Workspace health probe on three axes: source drift (files changed without the workspace noticing), reference health (projects that loaded with no metadata references), and pending reload (a change not yet applied — the only signal that can account for a brand-new file) |
| `roslyn_info` | Server version, PID, uptime, MSBuild discovery, log markers |

---

## Log Viewer

RoslynMcp writes structured NDJSON logs to `%LOCALAPPDATA%\RoslynMcp\logs\roslynmcp.{pid}.log`. `src/RoslynMcp.LogViewer/` is a .NET console app that serves a browser-based log viewer on `http://localhost:5123` — run it, then open that URL in any browser to watch tool calls stream in live.

**Features:** expandable rows with response peek data, JSON and C# syntax highlighting (with rainbow bracket coloring), four themes (Dark, Light, Parchment, Auto), local time display, and a Win95-inspired tree-view UI in Parchment mode.

Useful for understanding what your agent is actually doing, spotting slow tool calls, and debugging unexpected responses.

---

## Agent Instructions

### For Claude Code (CLAUDE.md)

Add to your project's `CLAUDE.md`:

```markdown
## Tool Preferences

ALWAYS prefer roslyn_* MCP tools over built-in tools when working with C#:
- Use `roslyn_get_member_body` to read methods -- not Read on the whole file
- Use `roslyn_find_references` to find usages -- not Grep
- Use `roslyn_build_project` to build -- never run `dotnet build` directly
- Use `roslyn_replace_in_code` for C# edits -- it validates syntax
- Use `roslyn_search_files` or `roslyn_semantic_search` for code search -- not Grep
- Use `roslyn_preview_rename` + `roslyn_apply_rename` for renames -- semantic, cross-project
- Use `roslyn_get_file_outline` to understand file structure -- not Read on the whole file
```

### For GitHub Copilot (AGENTS.md or copilot-instructions.md)

Add to your project's `AGENTS.md` or `.github/copilot-instructions.md`:

```markdown
When working with C# code, prefer roslyn_* MCP tools:
- `roslyn_get_member_body` to read a single method/property (don't read entire files)
- `roslyn_find_references` for semantic symbol search (not grep)
- `roslyn_build_project` for builds (never `dotnet build` in terminal)
- `roslyn_replace_in_code` for C# edits (validates syntax, preserves formatting)
- `roslyn_search_files` / `roslyn_semantic_search` for code discovery
- `roslyn_preview_rename` + `roslyn_apply_rename` for semantic renames
- `roslyn_get_file_outline` for file structure (don't read the whole file)
- `roslyn_get_diagnostics` with `take: 0` for a fast error count check (no items), or `severity: "errors"` to page through individual errors
```

For complete instructions covering every tool, see [docs/AGENT-INSTRUCTIONS.md](docs/AGENT-INSTRUCTIONS.md) — includes a full version, a compact version, and guidance for subagent delegation (subagents don't inherit your instructions).

---

## How It Works

RoslynMcp loads your project through MSBuild (full NuGet resolution, multi-project support, source generators) or falls back to AdhocWorkspace for quick source-only analysis. A `FileSystemWatcher` keeps the in-memory compilation current as files change on disk.

- **MSBuildWorkspace** -- when `.csproj`/`.sln` is found. Full project semantics.
- **AdhocWorkspace** -- fallback. Loads `.cs` files directly. Fast startup, limited resolution.
- **Smart MSBuild discovery** -- finds your MSBuild installation automatically on most machines.
- **Token-based pagination** -- large results return a `page_token`. Pass it back for the next page.
- **stdio transport** -- runs over stdin/stdout. All logging goes to stderr.

See [Workspace Modes Reference](docs/reference/WORKSPACE_MODES.md) for details.

---

## Help Wanted

RoslynMcp works. We use it daily for C# development with AI agents. But it is alpha software and there are areas where outside perspectives would make a real difference.

**First-call latency.** Loading an MSBuild workspace takes ~10 seconds on first tool call as the solution is parsed and compiled. Subsequent calls are fast (the workspace is cached and incrementally updated). Adding a new `.cs` file also triggers a full workspace reload (~8-10 seconds on next tool call) because Roslyn's `MSBuildWorkspace` doesn't support in-place document addition for SDK-style projects ([dotnet/roslyn#36781](https://github.com/dotnet/roslyn/issues/36781)). The cold start pays back quickly: once loaded, `roslyn_get_diagnostics` runs in-process in milliseconds — [18× faster than `dotnet build`](docs/battle-test-results.md) in our battle-testing. On very large solutions (Orleans with 235 compiled projects hit 7+ minutes), `--workspace adhoc` cuts cold start to ~22 seconds when full NuGet resolution isn't needed. If you have ideas for improving cold-start or reload time -- lazy compilation, workspace preloading, partial loading strategies -- we would like to hear them.

**Platform testing.** RoslynMcp is developed and tested on Windows. It should work on Linux and macOS (Roslyn and MSBuild are cross-platform), but it has not been validated. If you run it on a non-Windows platform, your experience report is valuable whether it works perfectly or fails completely.

**Large solution testing.** The tool catalog has been tested against small and medium projects. If you have a large real-world codebase (50+ projects, 500K+ lines), we want to know how it performs: load times, memory usage, pagination behavior, anything that breaks.

**Making agents choose the right tools.** Agents default to built-in file tools even when roslyn_* tools are available and better. We're working on enforcement hooks, better tool descriptions, and per-client instruction templates — for Claude Code, GitHub Copilot, Cursor, Windsurf, and others. See [#99](https://github.com/MadQ/RoslynMcp/issues/99) for the full wishlist and how to help.

**Multi-agent resource usage.** Each subagent spawns its own MCP server process with its own Roslyn workspace (~100MB+ RAM, ~10s load time). Parallel subagents multiply this cost. We're designing a shared Workspace Service via named pipes — one workspace per solution, shared across all agents. See [#86](https://github.com/MadQ/RoslynMcp/issues/86) and the [design doc](docs/plans/multi-instance-architecture.md).

**Benchmarks in progress.** We're actively benchmarking token usage and correctness across real-world repos — RoslynMcp vs built-in tools, across different model tiers. See [initial battle-test results](docs/battle-test-results.md).

If any of this sounds interesting, [open an issue](https://github.com/MadQ/RoslynMcp/issues) or submit a PR.

---

## Contributing

Contributions are welcome at every level. See [CONTRIBUTING.md](CONTRIBUTING.md) for the full guide.

**Small PRs are great.** Tool description improvements, documentation fixes, typo corrections -- these directly affect how well agents use the tools. First open-source PR? This is a good place to start.

**Larger contributions.** New tools, performance improvements, platform fixes. Fork from `dev` and submit a PR.

```bash
# Build
dotnet publish src/RoslynMcp/RoslynMcp.csproj -c Release -f net10.0 -o ./publish/net10.0

# Run the test suite (tests RoslynMcp against itself)
dotnet run --project src/TestHarness/TestHarness.csproj -f net10.0
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for the full guide and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) for community standards.

---

## Requirements

- .NET 10 SDK to build from source (`net11.0` is auto-added when a .NET 11 SDK is detected)

---

## License

MIT License -- see [LICENSE](LICENSE) for details.
