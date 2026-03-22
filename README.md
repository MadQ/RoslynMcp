# RoslynMcp

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-8%20%7C%2010%20%7C%2011-512BD4)](https://dotnet.microsoft.com/)
[![MCP](https://img.shields.io/badge/MCP-1.1.0-blue)](https://modelcontextprotocol.io/)

A [Model Context Protocol](https://modelcontextprotocol.io/) server that exposes Roslyn-powered code intelligence tools to AI coding agents. Gives agents resolved type information, live diagnostics, cross-file references, and symbol resolution — without spawning a build or leaving the process.

**Works with any MCP-compatible client:** GitHub Copilot, Claude Desktop, Cline, Roo Code, Continue, and more.

---

## Quick Start

```bash
# Clone and publish
git clone https://github.com/MadQ/RoslynMcp.git
cd RoslynMcp
dotnet publish RoslynMcp/RoslynMcp.csproj -c Release -f net10.0 -o ./publish/net10.0
```

**Add to your MCP client config** (e.g., `.mcp.json`):
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

See [Configuration](#configuration) below for argument details and [INSTALLATION.md](INSTALLATION.md) for client-specific setup.

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
      "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe",
      "args": ["/path/to/your/project"]
    }
  }
}
```

### Building the Executable

Publish a Release build for your platform:

```bash
cd /path/to/RoslynMcp
dotnet publish RoslynMcp/RoslynMcp.csproj -c Release -f net10.0 -o ./publish/net10.0
```

**Choose your target framework:**
- `net8.0` — .NET 8 (LTS)
- `net10.0` — .NET 10 (STS, recommended for latest features)
- `net11.0` — .NET 11 (preview, if available)

> **Why published executable?** Early experiments with `dotnet run` in `.mcp.json` produced interesting recursive behavior when dogfooding RoslynMcp on itself. Abandoned in favor of the simpler, more reliable executable approach.

### Command Line Arguments

| Argument | Required | Description |
|----------|----------|-------------|
| `<target-path>` | Yes | Path to the project/directory to analyze (`.` for current directory, or absolute path) |

**That's it!** Just point RoslynMcp.exe at your C# project directory.

### Configuration Examples

**Analyze current workspace:**
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

**Analyze specific project:**
```json
{
  "servers": {
    "roslyn": {
      "type": "stdio",
      "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe",
      "args": ["C:/my-workspace/MyApp.Web"]
    }
  }
}
```

**Using published NuGet tool (future):**
```json
{
  "servers": {
    "roslyn": {
      "type": "stdio",
      "command": "roslyn-mcp",
      "args": ["."]
    }
  }
}
```
*Requires: `dotnet tool install --global RoslynMcp` (coming soon)*

### Troubleshooting

**Server doesn't load target project:**
- Check that the target path contains a `.csproj` file (or `.cs` files for AdhocWorkspace fallback)
- Check server stderr logs for "Target: ..." to see what path was detected

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

---

## Tools

| Tool | Description |
|------|-------------|
| `search_files` | Searches workspace files for lines matching a regex pattern. Returns file paths, line numbers, and matching text with paging support. Use this to discover code locations before applying Roslyn tools. |
| `get_type_members` | Returns detailed information about all members of a type with full signatures (parameter types, return types, modifiers) and XML doc summaries. Use this to understand a type's API surface. |
| `get_diagnostics` | Returns compiler errors and warnings for the whole project or a single file. No build process. |
| `find_references` | Finds every reference to a named symbol (type, method, field, property) across the project. |
| `get_symbol_info` | Resolves what a name at a given file/line/column actually is: kind, containing type, return type. |
| `preview_rename` | Computes a rename across all files, returns unified diff + confirmation token. |
| `apply_rename` | Applies or rejects a pending rename by token. |
| `get_project_info` | Returns project metadata: target framework, language version, output kind, nullable setting, NuGet packages, additional files. |
| `build_project` | Builds the project and returns structured diagnostics. **Smart behavior:** checks Roslyn diagnostics first and skips the build if errors exist (fast path). If Roslyn reports no errors, runs `dotnet build` to validate MSBuild configuration. Set `forceBuild=true` to bypass Roslyn — use sparingly. |
| `get_file_outline` | Returns a structured outline of a file: types and their members (signatures only, no bodies). Saves tokens by avoiding full file reads. |
| `get_type_hierarchy` | Returns the inheritance hierarchy for a type: base types, interfaces, and derived types found in the project. |
| `find_implementations` | Finds all types that implement an interface/abstract class, or all methods that override a virtual/abstract member. |
| `list_types` | Lists all types in the project with optional namespace or kind filters (class, interface, enum, struct). |
| `get_usings` | Returns all `using` directives in a file plus implicit global usings from the project. |
| `get_symbol_documentation` | Returns XML documentation comments for a symbol: summary, parameter descriptions, return value description, remarks. Use to understand API contracts without reading source. |
| `get_symbol_definition` | Returns the definition location and signature of a symbol. Shows where the symbol is declared (file/line/column), its full signature, and doc summary. |
| `get_symbols_in_scope` | Returns all symbols accessible at a specific file location: locals, parameters, fields, properties, methods, types. Use when generating code to understand what's available in scope. |
| `respawn` | **DEBUG ONLY:** Terminates the server process, forcing the MCP client to respawn it. Use this to reload code changes after rebuilding without restarting your IDE. |

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
      "command": "dotnet",
      "args": ["run", "--no-build", "--project", "path/to/RoslynMcp/RoslynMcp.csproj", "--", "."]
    }
  }
}
```

> Build once first: `dotnet build RoslynMcp/RoslynMcp.csproj`

---

## Workspace modes

### MSBuildWorkspace (full resolution)

**When:** Target directory contains a `.csproj` file.

**Capabilities:**
- ✅ NuGet package type resolution (`List<T>`, `HttpClient`, etc.)
- ✅ Multi-project support (follows `<ProjectReference>`)
- ✅ .NET Framework projects (4.6.1+)
- ✅ Correct preprocessor symbols from project file

**Requirements:**
- MSBuild must be on PATH (installed with .NET SDK or Visual Studio)

### AdhocWorkspace (fast, source-only)

**When:** No `.csproj` file found in target directory.

**Capabilities:**
- ✅ Fast startup (<100 ms)
- ✅ Source-defined type resolution
- ✅ Syntax and semantic analysis

**Limitations:**
- ❌ No NuGet type resolution
- ❌ Fixed parse options (C# preview, `DEBUG` defined)
- ❌ Single directory tree only

---

## Write operations

Roslyn's `Renamer` API operates on the `Solution` object and produces a new `Solution` with edits applied — the same data structure already held by `WorkspaceManager`. Symbol rename is **always two-phase**: preview computes the diff and issues a token; apply commits it.

### Why Two-Phase?

**Safety at scale.** Renames can affect dozens or hundreds of files. The two-phase flow lets agents (and users) review impact before committing:

1. Agent calls `preview_rename` → sees diff affects 47 files across 3 projects
2. Agent decides: "This is bigger than expected, let me check with the user"
3. User reviews diff, approves or rejects

**You control the workflow.** Configure your agent's behavior in `.github/copilot-instructions.md` or your MCP client settings:
- **Conservative:** "Always show preview, never `apply_rename` without my explicit approval"
- **Balanced:** "Preview large renames (>5 files), auto-apply small ones"
- **Aggressive:** "Apply renames immediately unless I say otherwise"

The two-phase design supports all three modes — the agent decides when to ask.

### Tools

| Tool | Roslyn API | What it does |
|------|-----------|--------------|
| `preview_rename` | `Renamer.RenameSymbolAsync` | Computes a rename across all files, returns unified diff + confirmation token |
| `apply_rename` | — | Applies or rejects a pending rename by token |

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

## Future: refactoring tools

`get_refactorings` / `apply_refactoring` are the natural next step. The `CodeRefactoringContext` constructor is public, the `ApprovalStore` + `SolutionDiff` infrastructure is already in place, and the two-phase token model extends directly. The blocker: all concrete `CodeRefactoringProvider` implementations in `Microsoft.CodeAnalysis.CSharp.Features` are internal — the XML docs list them as public but the compiler disagrees. Options when revisiting:

- **OmniSharp HTTP API** — exposes refactorings over JSON; heavier but correct
- **Roslyn source build** — compile Features with `InternalsVisibleTo`; fragile across updates
- **Implement target refactorings directly** — reasonable for a short list (extract method, introduce variable, inline); correct but significant work

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

