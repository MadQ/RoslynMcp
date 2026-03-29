# RoslynMcp

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![MCP](https://img.shields.io/badge/MCP-1.1.0-blue)](https://modelcontextprotocol.io/)
[![Alpha](https://img.shields.io/badge/status-alpha-orange)]()

**Give your AI agent a C# compiler instead of grep.**
([benchmarks coming](docs/plans/token-benchmark.md) · [shared workspace architecture](docs/plans/multi-instance-architecture.md) · [help wanted](#help-wanted))

RoslynMcp is a [Model Context Protocol](https://modelcontextprotocol.io/) server that gives AI coding agents real Roslyn compiler semantics: type resolution, cross-file references, semantic rename, diagnostics, and 30+ tools. Not string matching. Not regex. Actual compiler-level understanding of your C# code.

```
Agent: "Rename OrderStatus.Pending to OrderStatus.AwaitingApproval"

Without RoslynMcp:  grep + find-and-replace across 50 files. Hope nothing else is named "Pending."
With RoslynMcp:     roslyn_preview_rename -> reviews diff across 3 projects -> roslyn_apply_rename. Done.
```

Works with any MCP-compatible client: Claude Code, GitHub Copilot, Claude Desktop, Cline, Cursor, Windsurf, Roo Code, Continue, and more.

> **Security Note:** RoslynMcp runs with your user permissions and has unrestricted filesystem access. Only use with trusted agents and on projects you control. See [Issue #9](https://github.com/MadQ/RoslynMcp/issues/9).

---

## Quick Start

**1. Clone and build** (requires .NET 8 or 10 SDK):

```bash
git clone https://github.com/MadQ/RoslynMcp.git
cd RoslynMcp
dotnet publish src/RoslynMcp/RoslynMcp.csproj -c Release -f net10.0 -o ./publish/net10.0
```

**2. Add to your MCP client config.**

For Claude Code, create `.mcp.json` in your project root:

```json
{
  "servers": {
    "roslyn": {
      "type": "stdio",
      "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
    }
  }
}
```

**3. Start using it.** Every tool accepts a `projectPath` parameter pointing at your `.csproj`, `.sln`, or project directory. Your agent handles this automatically.

```
"What does the ProcessOrder method do?"
-> Agent calls roslyn_get_member_body("ProcessOrder", projectPath: "src/MyApp")
-> Returns just that method's source. 20 lines, not a 600-line file dump.
```

> [!IMPORTANT]
> **Tell your agent to use RoslynMcp.** Agents default to grep and file reads unless you explicitly instruct them. Add a few lines to your project's `CLAUDE.md` or `AGENTS.md` — see [Agent Instructions](#agent-instructions) for a quick example, or [docs/AGENT-INSTRUCTIONS.md](docs/AGENT-INSTRUCTIONS.md) for complete copy-paste instructions covering every tool. Having trouble getting your agent to comply? See [#99](https://github.com/MadQ/RoslynMcp/issues/99).
> Claude Code users: try our experimental [PreToolUse hook](scripts/enforce-roslyn-tools.sh) to enforce this automatically.

See [INSTALLATION.md](INSTALLATION.md) for setup guides for GitHub Copilot, Claude Desktop, Cursor, Windsurf, Cline, Continue, Roo Code, Zed, and direct CLI usage.

---

## Why RoslynMcp?

AI agents working on C# through file reads and regex have a structural problem: they pattern-match text instead of understanding code. RoslynMcp fixes that by keeping a live Roslyn compilation in-process, incrementally updated as files change.

**Find references, not text matches.** `roslyn_find_references` uses Roslyn's semantic model to find every actual usage of a symbol across your entire solution. Grep finds string matches. Roslyn finds the call site in `OrderService`, the override in `PriorityOrderProcessor`, and the test mock in `OrderServiceTests` -- even when they use different names through inheritance.

**Read one method, not the whole file.** `roslyn_get_member_body` returns just the source of a single method, property, or field. When your agent needs to understand `ProcessPayment`, it gets 15 lines instead of reading a 600-line file. Massive token savings, every call.

**Rename with confidence.** `roslyn_preview_rename` + `roslyn_apply_rename` performs semantic rename across your entire solution. It knows that `order.Status` and `IOrder.Status` are the same symbol. Grep doesn't.

**Build without leaving the process.** `roslyn_build_project` checks Roslyn diagnostics first (~17ms). If there are errors, it returns them instantly without spawning MSBuild. Clean code triggers a real `dotnet build` for full validation.

---

## Tool Catalog

33 tools organized by what you need to do. All tools work in-process using Roslyn APIs unless noted.

### Discovery

| Tool | What it does |
|------|--------------|
| `roslyn_search_files` | Regex search across workspace files with paging |
| `roslyn_semantic_search` | Context-aware C# search -- filter by comments, strings, identifiers, xmldocs |
| `roslyn_list_files` | Glob-based file enumeration (fast, no content) |
| `roslyn_list_types` | All types in the project with namespace/kind filters |

### Navigation

| Tool | What it does |
|------|--------------|
| `roslyn_find_references` | Every reference to a symbol across the solution |
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
| `roslyn_replace_in_code` | Semantic C# editing -- replaces syntax nodes, validates syntax |
| `roslyn_replace_in_file` | Text-level find-and-replace with regex (any file type) |
| `roslyn_insert_lines` | Insert lines at a position or anchor pattern |

### Refactoring

| Tool | What it does |
|------|--------------|
| `roslyn_preview_rename` | Compute rename diff + confirmation token |
| `roslyn_apply_rename` | Apply or reject a previewed rename |
| `roslyn_change_signature` | Add parameters with non-breaking forwarding overload |
| `roslyn_apply_signature_change` | Apply or reject a previewed signature change |

### Build

| Tool | What it does |
|------|--------------|
| `roslyn_build_project` | Smart build: Roslyn diagnostics first, MSBuild only if clean |
| `roslyn_clean_solution` | Remove all build artifacts |
| `roslyn_restore_packages` | Restore NuGet packages |

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
- `roslyn_get_diagnostics` with `severity: "errors"` for fast error checks
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

**First-call latency.** Loading an MSBuild workspace takes ~10 seconds on first tool call as the solution is parsed and compiled. Subsequent calls are fast (the workspace is cached and incrementally updated). Adding a new `.cs` file also triggers a full workspace reload (~8-10 seconds on next tool call) because Roslyn's `MSBuildWorkspace` doesn't support in-place document addition for SDK-style projects ([dotnet/roslyn#36781](https://github.com/dotnet/roslyn/issues/36781)). If you have ideas for improving cold-start or reload time -- lazy compilation, workspace preloading, partial loading strategies -- we would like to hear them.

**Platform testing.** RoslynMcp is developed and tested on Windows. It should work on Linux and macOS (Roslyn and MSBuild are cross-platform), but it has not been validated. If you run it on a non-Windows platform, your experience report is valuable whether it works perfectly or fails completely.

**Large solution testing.** The tool catalog has been tested against small and medium projects. If you have a large real-world codebase (50+ projects, 500K+ lines), we want to know how it performs: load times, memory usage, pagination behavior, anything that breaks.

**Making agents choose the right tools.** Agents default to built-in file tools even when roslyn_* tools are available and better. We're working on enforcement hooks, better tool descriptions, and per-client instruction templates — for Claude Code, GitHub Copilot, Cursor, Windsurf, and others. See [#99](https://github.com/MadQ/RoslynMcp/issues/99) for the full wishlist and how to help.

**Multi-agent resource usage.** Each subagent spawns its own MCP server process with its own Roslyn workspace (~100MB+ RAM, ~10s load time). Parallel subagents multiply this cost. We're designing a shared Workspace Service via named pipes — one workspace per solution, shared across all agents. See [#86](https://github.com/MadQ/RoslynMcp/issues/86) and the [design doc](docs/plans/multi-instance-architecture.md).

**Benchmarks in progress.** We're actively benchmarking token usage and correctness across real-world repos — RoslynMcp vs built-in tools, across different model tiers. Results will be published before the first binary release.

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
dotnet run --project src/TestHarness/TestHarness.csproj
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for the full guide and [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) for community standards.

---

## Requirements

- .NET 8 or .NET 10 SDK (multi-targeted -- use whichever you have installed)

---

## License

MIT License -- see [LICENSE](LICENSE) for details.
