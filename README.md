# RoslynMcp

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![MCP](https://img.shields.io/badge/MCP-1.1.0-blue)](https://modelcontextprotocol.io/)

A [Model Context Protocol](https://modelcontextprotocol.io/) server that exposes Roslyn-powered code intelligence tools to AI coding agents. Gives agents resolved type information, live diagnostics, cross-file references, and symbol resolution — without spawning a build or leaving the process.

**Works with any MCP-compatible client:** GitHub Copilot, Claude Desktop, Cline, Roo Code, Continue, and more.

> **⚠️ Security Note:** RoslynMcp runs with your user permissions and currently has unrestricted filesystem access. Only use with trusted agents and on projects you control. Filesystem security boundaries are planned for a future release. See [Issue #9](https://github.com/MadQ/RoslynMcp/issues/9).

---

## Quick Start

```bash
# Clone and publish
git clone https://github.com/MadQ/RoslynMcp.git
cd RoslynMcp
dotnet publish src/RoslynMcp/RoslynMcp.csproj -c Release -f net10.0 -o ./publish/net10.0
```

**Add to your MCP client config** (e.g., `.mcp.json`):
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

See [INSTALLATION.md](INSTALLATION.md) for client-specific setup.

---

## Key Features

- **26 Roslyn-powered tools** — semantic code understanding, navigation, refactoring, validation, and trivia analysis (25 stable + 1 experimental)
- **Solution-level loading** — automatically discovers `.sln`/`.slnx` files; cross-project references, rename, and find-implementations work across the entire solution
- **Live compilation** — in-memory Roslyn workspace with FileSystemWatcher for real-time change detection (MSBuild and AdhocWorkspace)
- **Token-based pagination** — paginated tools return a `page_token`; pass it back to get the next page without re-executing the query
- **`roslyn_get_member_body`** — returns just the source of a single method/property/field. 20 lines instead of reading a 600-line file.
- **No external processes** — all analysis happens in-process using Roslyn APIs (except `roslyn_build_project` which calls `dotnet build`)
- **Smart build** — `roslyn_build_project` checks Roslyn diagnostics first and skips MSBuild if errors exist (17ms vs seconds)

---

## Configuration

### MCP Client Configuration

RoslynMcp runs as a standalone executable. Configuration goes in your MCP client's config file (e.g., `.mcp.json` for GitHub Copilot).

**Basic pattern:**
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

No `args` needed — the agent specifies `projectPath` in each tool invocation.

**See [INSTALLATION.md](INSTALLATION.md) for complete setup instructions** including client-specific examples and troubleshooting.

### Quick Build

```bash
cd /path/to/RoslynMcp
dotnet publish src/RoslynMcp/RoslynMcp.csproj -c Release -f net10.0 -o ./publish/net10.0
```

Supports `net8.0`, `net10.0`, or `net11.0` (auto-detected if SDK is installed).

### Troubleshooting

**Server doesn't load target project:**
- Ensure the agent is specifying `projectPath` in every tool invocation
- `projectPath` supports smart resolution: directory, `.csproj` file, or source file
- Check server stderr logs for errors

**VS Publish UI errors (`WebToolsException`):**
- Visual Studio's "Publish" UI may incorrectly treat the console app as a web app
- Use `dotnet publish` command line instead

See **[INSTALLATION.md](INSTALLATION.md)** for client-specific examples and full troubleshooting guide.

---

## Why

AI coding agents that work on C# via text-based tools (file reads, regex search, `edit_file`) have a structural problem: they pattern-match names rather than resolve them. This causes real bugs:

- Enum member names get truncated or misspelled
- `get_errors` requires a full `dotnet build` — slow and process-spawning
- Finding every call site of a method requires grep, which misses renames and overloads
- There's no way to ask "what type does this expression actually resolve to?"

RoslynMcp fixes all four by keeping a live Roslyn `Compilation` in process, warm and incrementally updated via `FileSystemWatcher`.

**Yes, there are a lot of tools** (25 in total). That's not bloat — it's Roslyn's power surface. Each tool exposes a specific Roslyn capability that agents can't get any other way. Think of it as a curated API for semantic code understanding, not a grab bag of features.

---

## Tools

RoslynMcp provides 25 tools for code analysis and manipulation (24 stable + 1 experimental). **All tools work on the in-memory Roslyn compilation** — no external processes or file system dependencies beyond the initial load.

### Guiding Your AI Agent

**Add these instructions to your agent's context** (e.g., in your project's `.github/copilot-instructions.md`, `AGENTS.md`, or custom instructions) to help it choose the right tools:

```markdown
When building or checking for errors:
- **ALWAYS use `roslyn_build_project`** — NEVER run `dotnet build` in a terminal.
  It checks Roslyn diagnostics first (instant, ~17ms) and only spawns MSBuild if
  the code is clean. This is faster, quieter, and returns structured JSON — not
  terminal output you have to parse.

When working with C# code:
- **Prefer `roslyn_replace_in_code`** for editing C# files — it validates syntax, preserves formatting, and understands code structure
- Use `roslyn_replace_in_file` only for non-C# files (JSON, markdown, etc.) or when literal text replacement is needed
- Use `roslyn_get_member_body` to read a single method/property/field — don't read the entire file

When discovering code:
- `roslyn_search_files` — finds content (regex patterns across file contents)
- `roslyn_semantic_search` — context-aware C# search (filter by comments, strings, identifiers, xmldocs)
- `roslyn_list_files` — enumerates by name (glob patterns, fast)
- `roslyn_find_references` — finds usage (semantic, Roslyn-based)
```

**Why this matters:** RoslynMcp provides both text-level (`roslyn_replace_in_file`) and semantic (`roslyn_replace_in_code`) editing tools. Without explicit guidance, agents may default to the simpler text-based tool even when the semantic tool is more appropriate. The guidance above ensures your agent uses the most robust tool for C# code changes.

### Available Tools

| Tool | Description |
|------|-------------|
| `roslyn_search_files` | Searches workspace files for lines matching a regex pattern. Returns file paths, line numbers, and matching text with paging support. Use this to discover code locations before applying Roslyn tools. |
| `roslyn_semantic_search` | Context-aware C# search using Roslyn syntax-tree filtering. Allows searching within specific syntax contexts (comments, strings, identifiers, code, xmldocs). More precise than `roslyn_search_files` but C#-only. |
| `roslyn_list_files` | Lists files matching a glob pattern (e.g., `*.cs`, `**/*.json`). Returns relative paths without content. Fast enumeration for file discovery. |
| `roslyn_replace_in_file` | Text-level find-and-replace with regex support. Works on any file type. Returns changed line numbers and match count. Supports dry-run preview. |
| `roslyn_replace_in_code` | **Semantic C# editing** — replaces syntax nodes by kind (MethodDeclaration, FieldDeclaration, IdentifierName, etc.). Validates syntax, preserves formatting. C# files only. |
| `roslyn_get_type_members` | Returns detailed information about all members of a type with full signatures (parameter types, return types, modifiers) and XML doc summaries. Use this to understand a type's API surface. |
| `roslyn_get_diagnostics` | Returns compiler errors and warnings for the whole project or a single file. No build process. |
| `roslyn_find_references` | Finds every reference to a named symbol (type, method, field, property) across the project. |
| `roslyn_get_symbol_info` | Resolves what a name at a given file/line/column actually is: kind, containing type, return type. |
| `roslyn_preview_rename` | Computes a rename across all files, returns unified diff + confirmation token. |
| `roslyn_apply_rename` | Applies or rejects a pending rename by token. |
| `roslyn_get_project_info` | Returns project metadata: target framework, language version, output kind, nullable setting, NuGet packages, additional files. |
| `roslyn_build_project` | Builds the project and returns structured diagnostics. **Smart behavior:** checks Roslyn diagnostics first and skips the build if errors exist (fast path). If Roslyn reports no errors, runs `dotnet build` to validate MSBuild configuration. Set `forceBuild=true` to bypass Roslyn — use sparingly. |
| `roslyn_clean_solution` | Cleans the solution by removing all build artifacts (bin/ and obj/ directories). Use when the build is in a bad state or before a fresh rebuild. |
| `roslyn_restore_packages` | Restores NuGet packages for the solution. Use after adding package references or when packages are missing. |
| `roslyn_get_file_outline` | Returns a structured outline of a file: types and their members (signatures only, no bodies). Saves tokens by avoiding full file reads. |
| `roslyn_get_type_hierarchy` | Returns the inheritance hierarchy for a type: base types, interfaces, and derived types found in the project. |
| `roslyn_find_implementations` | Finds all types that implement an interface/abstract class, or all methods that override a virtual/abstract member. |
| `roslyn_list_types` | Lists all types in the project with optional namespace or kind filters (class, interface, enum, struct). |
| `roslyn_get_usings` | Returns all `using` directives in a file plus implicit global usings from the project. |
| `roslyn_get_symbol_documentation` | Returns XML documentation comments for a symbol: summary, parameter descriptions, return value description, remarks. Use to understand API contracts without reading source. |
| `roslyn_get_symbol_definition` | Returns the definition location and signature of a symbol. Shows where the symbol is declared (file/line/column), its full signature, and doc summary. |
| `roslyn_get_symbols_in_scope` | Returns all symbols accessible at a specific file location: locals, parameters, fields, properties, methods, types. Use when generating code to understand what's available in scope. |
| `roslyn_get_trivia` | **EXPERIMENTAL:** Returns whitespace, comments, and formatting trivia from C# files. Filter by syntax kind, trivia kind, or line range. Useful for understanding indentation context. May be removed or changed in future releases. |
| `roslyn_respawn` | **DEBUG ONLY:** Terminates the server process, forcing the MCP client to respawn it. Use this to reload code changes after rebuilding without restarting your IDE. |

---

## Design

**Automatic workspace selection** — RoslynMcp detects `.csproj` files in the target directory and automatically chooses the best workspace mode:

- **MSBuildWorkspace** (if `.csproj` found) — full project resolution including NuGet packages, multi-project support, and .NET Framework compatibility. Requires MSBuild on PATH. Startup: 1-2 seconds.
- **AdhocWorkspace** (fallback) — loads `.cs` files directly without MSBuild. Fast startup (<100 ms), but only resolves types defined in loaded source files.

**Live compilation** — MSBuildWorkspace monitors files via Roslyn's internal mechanisms. AdhocWorkspace uses `FileSystemWatcher` to detect `.cs` changes and invalidates the compilation lazily on the next tool call. Thread-safe via `ReaderWriterLockSlim`.

**stdio transport** — the MCP protocol runs over stdin/stdout. All logging is suppressed or redirected to stderr so it never corrupts the protocol stream.

---

## Requirements

- .NET 8, .NET 10, or .NET 11 SDK (multi-targeted — use whichever you have installed)

---

## Installation

See **[INSTALLATION.md](INSTALLATION.md)** for complete setup instructions for:

- **GitHub Copilot** (Visual Studio / VS Code)
- **Claude Desktop** (Windows / macOS / Linux)
- **Cursor** (workspace or global config)
- **Windsurf** (workspace or global config)
- **Cline** (VS Code extension)
- **Continue** (VS Code / JetBrains)
- **Roo Code** (VS Code extension)
- **Zed** (text editor)
- **Direct CLI** (any platform)

**Quick start** (GitHub Copilot / Visual Studio):

Add to `.mcp.json` at your workspace root:

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

---

## Workspace Modes

RoslynMcp automatically selects the best workspace mode for your project:

- **MSBuildWorkspace** (when `.csproj` found) — Full NuGet resolution, multi-project support, .NET Framework 4.6.1+ compatibility. Startup: 1-2 seconds.
- **AdhocWorkspace** (fallback) — Fast startup (<100 ms), source-only type resolution. Perfect for scripts or demos without project files.

**See [Workspace Modes Reference](docs/reference/WORKSPACE_MODES.md) for detailed comparison, troubleshooting, and FAQ.**

---

## Write operations

Roslyn's `Renamer` API operates on the `Solution` object and produces a new `Solution` with edits applied — the same data structure already held by `WorkspaceManager`. Symbol rename is **always two-phase**: preview computes the diff and issues a token; apply commits it.

### Why Two-Phase?

**Safety at scale.** Renames can affect dozens or hundreds of files. The two-phase flow lets agents (and users) review impact before committing:

1. Agent calls `roslyn_preview_rename` → sees diff affects 47 files across 3 projects
2. Agent decides: "This is bigger than expected, let me check with the user"
3. User reviews diff, approves or rejects

**You control the workflow.** Configure your agent's behavior in `.github/copilot-instructions.md` or your MCP client settings:
- **Conservative:** "Always show preview, never `roslyn_apply_rename` without my explicit approval"
- **Balanced:** "Preview large renames (>5 files), auto-apply small ones"
- **Aggressive:** "Apply renames immediately unless I say otherwise"

The two-phase design supports all three modes — the agent decides when to ask.

### Tools

| Tool | Roslyn API | What it does |
|------|-----------|--------------|
| `roslyn_preview_rename` | `Renamer.RenameSymbolAsync` | Computes a rename across all files, returns unified diff + confirmation token |
| `roslyn_apply_rename` | — | Applies or rejects a pending rename by token |

### Approval model

- **`y`** — apply this change now
- **`n`** — reject, no files changed
- **`session`** — apply and auto-approve further renames of the same symbol for the lifetime of the server process

**Note:** This approval is *semantic* (change-level), not *protocol-level*. MCP clients like GitHub Copilot already ask "Allow this tool to run?" at invocation time. RoslynMcp's approval is about **reviewing the diff** before committing, especially for large-scope changes.

`always` is intentionally omitted — `session` already covers the use-case, and persistence would require a config file and permissions infrastructure. You have git. We done tole you once. 🏴‍☠️

### File mutation strategy

Files are written directly to disk. `WorkspaceManager`'s `FileSystemWatcher` detects the writes and invalidates the compilation automatically.

**Planned:** `undo_last_edit` — reverts the most recent Roslyn-generated edit (rename, refactoring, etc.) by restoring from an in-memory snapshot. Useful for "wait, let me rethink that" moments mid-task.

---

## Roadmap

Active development — see [ROADMAP.md](ROADMAP.md) for the full plan.

**Coming soon (v0.7.0):**
- Harden `roslyn_list_types` and `roslyn_find_references` defaults (context-window safety)
- Add filtering parameters to existing tools (`includeInherited`, `directOnly`, `severity`)
- `roslyn_change_signature` tool for semantic signature refactoring

**Planned (v0.8.0–v0.9.0):**
- **Call graph tools** — `roslyn_find_callers`, `roslyn_get_call_graph` (IOperation-based, impossible with text search)
- **Dead code detection** — `roslyn_find_unused` for private/internal symbols with zero references
- **Style-aware editing** — `roslyn_get_style_profile` infers formatting rules from trivia; `preserveStyle` flag on `replace_in_code` respects the author's style during edits

---

## License

MIT

---

## Contributing

Contributions are welcome! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

**Quick contribution checklist:**
- Fork the repository
- Create a feature branch
- Make your changes with tests
- Run the test suite: `dotnet run --project TestHarness/TestHarness.csproj`
- Submit a pull request

---

## License

MIT License - see [LICENSE](LICENSE) for details.

