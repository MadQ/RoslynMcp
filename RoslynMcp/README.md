# RoslynMcp

A [Model Context Protocol](https://modelcontextprotocol.io/) server that exposes Roslyn-powered code intelligence tools to AI coding agents. Gives agents resolved type information, live diagnostics, cross-file references, and symbol resolution — without spawning a build or leaving the process.

---

## Why

AI coding agents that work on C# via text-based tools (file reads, regex search, `edit_file`) have a structural problem: they pattern-match names rather than resolve them. This causes real bugs:

- Enum member names get truncated (`ShowWindowCommand.ShowNoActivate` → `ShowNoActive`)
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

---

## Design

**`AdhocWorkspace` not `MSBuildWorkspace`** — loads `.cs` files directly without requiring MSBuild assemblies on PATH. Starts in under 100 ms. The tradeoff is that NuGet types (from referenced packages) are not resolved; only types defined in the loaded source files are available. For most agent use-cases — verifying member names, finding in-project references, checking syntax errors — this is sufficient.

**Live compilation** — `FileSystemWatcher` monitors the target directory for `.cs` changes, additions, deletions, and renames. The `Compilation` is invalidated and rebuilt lazily on the next tool call. A `ReaderWriterLockSlim` keeps concurrent tool calls safe.

**stdio transport** — the MCP protocol runs over stdin/stdout. All logging is suppressed or redirected to stderr so it never corrupts the protocol stream.

---

## Requirements

- .NET 10 SDK

---

## Usage

### With `.mcp.json` (recommended for VS / GitHub Copilot)

Add to `.mcp.json` at your repo root:

```json
{
  "servers": {
    "roslyn": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "path/to/RoslynMcp/RoslynMcp.csproj", "--", "path/to/your/src"]
    }
  }
}
```

The last argument is the root directory containing `.cs` files to load. Defaults to the current working directory if omitted.

### Direct

```powershell
dotnet run --project RoslynMcp/RoslynMcp.csproj -- path/to/your/src
```

---

## Known limitations

- **No NuGet type resolution** — types from referenced packages are not available. Members on `string`, `List<T>`, etc. will not resolve. Only source-defined types are indexed.
- **No multi-project support** — loads a single directory tree. Cross-project references are not followed.
- **`AdhocWorkspace` parse options are fixed** — currently configured for C# preview with `DEBUG` defined. Projects with significantly different compile-time symbols may see false diagnostics.
- **`find_references` requires the symbol to be source-defined** — cannot find references to types that originate in packages.

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
