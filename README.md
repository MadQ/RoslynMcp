# RoslynMcp

A [Model Context Protocol](https://modelcontextprotocol.io/) server that exposes Roslyn-powered code intelligence tools to AI coding agents. Gives agents resolved type information, live diagnostics, cross-file references, and symbol resolution — without spawning a build or leaving the process.

**Works with any MCP-compatible client:** GitHub Copilot, Claude Desktop, Cline, Roo Code, Continue, and more.

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
| `get_type_members` | Returns all member names of a type — class, struct, enum, or interface. Filter by kind: `field`, `property`, `method`, `enum`, `event`. |
| `get_diagnostics` | Returns compiler errors and warnings for the whole project or a single file. No build process. |
| `find_references` | Finds every reference to a named symbol (type, method, field, property) across the project. |
| `get_symbol_info` | Resolves what a name at a given file/line/column actually is: kind, containing type, return type. |
| `preview_rename` | Computes a rename across all files, returns unified diff + confirmation token. |
| `apply_rename` | Applies or rejects a pending rename by token. |

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

## Usage

### GitHub Copilot / Visual Studio

Add to `.mcp.json` at your workspace root:

```json
{
  "servers": {
    "roslyn": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "path/to/RoslynMcp/RoslynMcp.csproj", "--", "."]
    }
  }
}
```

The last argument (`.`) is the root directory containing `.cs` files to load. Defaults to the current working directory if omitted.

### Claude Desktop

Add to your Claude Desktop MCP settings file:

**Windows:** `%APPDATA%\Claude\claude_desktop_config.json`  
**macOS:** `~/Library/Application Support/Claude/claude_desktop_config.json`  
**Linux:** `~/.config/Claude/claude_desktop_config.json`

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/RoslynMcp/RoslynMcp.csproj", "--", "/absolute/path/to/your/project/src"]
    }
  }
}
```

Replace both paths with absolute paths to the RoslynMcp project and your C# project's source directory.

### Cline (VS Code)

Add to Cline's MCP settings (Settings → Extensions → Cline → MCP Servers):

```json
{
  "roslyn": {
    "command": "dotnet",
    "args": ["run", "--project", "/absolute/path/to/RoslynMcp/RoslynMcp.csproj", "--", "${workspaceFolder}"]
  }
}
```

Cline supports `${workspaceFolder}` for the current workspace directory.

### Direct (any platform)

```bash
dotnet run --project RoslynMcp/RoslynMcp.csproj -- path/to/your/src
```

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

Roslyn's `Renamer` API operates on the `Solution` object and produces a new `Solution` with edits applied — the same data structure already held by `WorkspaceManager`. Symbol rename is two-phase: preview computes the diff and issues a token; apply commits it.

### Tools

| Tool | Roslyn API | What it does |
|------|-----------|--------------|
| `preview_rename` | `Renamer.RenameSymbolAsync` | Computes a rename across all files, returns unified diff + confirmation token |
| `apply_rename` | — | Applies or rejects a pending rename by token |

### Approval model

- **`y`** — apply this change now
- **`n`** — reject, no files changed
- **`session`** — apply and auto-approve further renames of the same symbol for the lifetime of the server process

`always` is intentionally omitted — `session` already covers the use-case, and persistence would require a config file and permissions infrastructure. You have git. We done tole you once. 🏴‍☠️

### File mutation strategy

Files are written directly to disk. `WorkspaceManager`'s `FileSystemWatcher` detects the writes and invalidates the compilation automatically.

---

## Future: refactoring tools

`get_refactorings` / `apply_refactoring` are the natural next step. The `CodeRefactoringContext` constructor is public, the `ApprovalStore` + `SolutionDiff` infrastructure is already in place, and the two-phase token model extends directly. The blocker: all concrete `CodeRefactoringProvider` implementations in `Microsoft.CodeAnalysis.CSharp.Features` are internal — the XML docs list them as public but the compiler disagrees. Options when revisiting:

- **OmniSharp HTTP API** — exposes refactorings over JSON; heavier but correct
- **Roslyn source build** — compile Features with `InternalsVisibleTo`; fragile across updates
- **Implement target refactorings directly** — reasonable for a short list (extract method, introduce variable, inline); correct but significant work

---

## License

MIT
