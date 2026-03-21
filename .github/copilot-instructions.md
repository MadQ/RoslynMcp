# Copilot Instructions — RoslynMcp 🏴‍☠️

> We push boundaries, ship bleeding-edge C#, and we're having a blast doing it.
> No hand-wringing. No unnecessary abstraction. Just clean, fast, pirate-grade code.
>
> **This is a discussion, not a monologue.** Disagree when there's a better approach. Say so directly, explain why, and suggest the alternative. Point out mistakes — in design, naming, logic, or assumptions — before implementing them. Don't just execute; think first. A pushback that saves a bad commit is worth more than silent compliance.

---

## What is RoslynMcp?

A [Model Context Protocol](https://modelcontextprotocol.io/) server that exposes Roslyn-powered code intelligence tools to AI coding agents. Gives agents resolved type information, live diagnostics, cross-file references, and symbol resolution — without spawning a build or leaving the process.

---

## Project

| | |
|---|---|
| **Type** | Model Context Protocol (MCP) server — stdio transport |
| **Runtime** | .NET 8 / .NET 10 / .NET 11 (multi-targeted) |
| **Language** | C# 14 (`<LangVersion>preview</LangVersion>`) |
| **Version** | 0.2.0-alpha (pre-1.0) |
| **Dependencies** | `Microsoft.CodeAnalysis.*` (Roslyn) — MSBuildWorkspace (if .csproj found) → AdhocWorkspace (fallback) |
| **ImplicitUsings** | `enable` — don't add redundant `using` directives |

Two projects:
- `RoslynMcp/RoslynMcp.csproj` — MCP server
- `TestHarness/TestHarness.csproj` — local testing client

```
dotnet build RoslynMcp/RoslynMcp.csproj
```

---

## Architecture

> Working rules (git, terminal) live in **`AGENTS.md`**. Don't duplicate them here.

| Component | Responsibility |
|-----------|----------------|
| `WorkspaceManager` | Auto-detects `.csproj` → `MSBuildWorkspace` (full resolution) or `AdhocWorkspace` (source-only); lazy compilation rebuild |
| `TypeMembersTool` | `get_type_members` — enumerate members of a type (fields, properties, methods, enums, events) |
| `DiagnosticsTool` | `get_diagnostics` — compiler errors and warnings for project or single file |
| `FindReferencesTool` | `find_references` — all references to a symbol across the project |
| `SymbolInfoTool` | `get_symbol_info` — resolve what a name at a location actually is |
| `PreviewRenameTool` | `preview_rename` — compute rename edits, return unified diff + token |
| `ApplyRenameTool` | `apply_rename` — approve/reject a pending rename by token |
| `ApprovalStore` | Session-scoped approval state (`y`, `n`, `session` model) |
| `SolutionDiff` | Unified diff generation for `Solution` → `Solution` edits |

**Data flow:** stdio MCP request → tool → `WorkspaceManager.GetCompilation()` (may rebuild) → Roslyn API → JSON response.

**Key files:** `Program.cs` (MCP protocol), `WorkspaceManager.cs` (compilation management), `Tools/*.cs` (tool implementations).

**Workspace modes:**
- **MSBuildWorkspace** (if `.csproj` found) — full NuGet resolution, multi-project support, .NET Framework 4.6.1+ compatibility
- **AdhocWorkspace** (fallback) — source-only, fast startup (<100 ms)

---

## Code Style

- **Braces:** same line for control flow (`if(x) {`), new line for methods/classes; properties — same line as the identifier (`public int Count {`)
- **No space** after `if`/`foreach`/`while`: `if(x)` not `if (x)`
- **Single-statement blocks:** no braces
- **Naming:** PascalCase for types/methods, camelCase for fields/locals — no underscores, no Hungarian, no abbreviations
- **Handles:** always `nint`, never `IntPtr`
- **Modern C#:** pattern matching, switch expressions, target-typed `new`, collection expressions, `nint`
- **`var`:** use when type is obvious or long; prefer explicit type otherwise
- **Column-aligned fields:** tab-stop alignment on field declarations and assignment blocks
- **Cast spacing:** space between cast and operand — `(int) value`, not `(int)value`
- **Comments:** explain *why*, not *what* — after any edit, re-evaluate nearby comments and update or remove stale ones; one space after a period, never two; complete sentences, proper punctuation, no personal pronouns (`we`/`I`/`our` have no place in code comments)
- **`TODO` comments:** must include the actual question or concern, not just "fix"
- **Condition ordering:** simple/common path first — early return or assignment; complex path in `else`
- **Blank lines:**
  - One blank line before `return` and before code blocks
  - Blank line **after the opening brace** of any multi-statement control-flow block
  - Blank line between branches of an `if/else if/else` chain when any body spans multiple lines
  - Blank lines between logically distinct statement groups within a method body
  - Blank lines are **indented** to match surrounding scope — never bare empty lines inside a block
- **Semicolons** on their own line for wrapped multi-line expressions (fluent chains, ternaries, LINQ, arrow bodies)

> **Consistency is overrated. Embrace diversity.**
>
> Deliberate departure from the guidelines above is fine — that's how better patterns get discovered. Try something different, sit with it long enough to make an informed opinion, then decide. A snap judgement that "it's wrong" is just a reflex; a considered judgement after living with it is **data**. The guidelines exist because someone already walked that mile — but if your mile leads somewhere new, the map gets updated. Just don't go completely feral on us. 🏴‍☠️
>
> When you do go off-map, a quick comment saying so helps — future you (and future AI) will know it was intentional, not an accident waiting to be "fixed". That comment might even be the Treasure (Arrrr!). 💎

---

## MCP Protocol

All communication is JSON-RPC 2.0 over stdin/stdout. **Never write to stdout except protocol messages** — all logging goes to stderr or is suppressed entirely.

**Tool registration pattern** (in `Program.cs`):
```csharp
"tools/list" => new {
    tools = new[] {
        new {
            name = "get_type_members",
            description = "Returns all member names of a type...",
            inputSchema = new { ... }
        }
    }
}
```

**Tool invocation pattern:**
```csharp
"tools/call" => {
    var toolName = requestObj["params"]?["name"]?.ToString();
    var args = requestObj["params"]?["arguments"];

    return toolName switch {
        "get_type_members" => TypeMembersTool.Execute(args),
        _ => new { error = "Unknown tool" }
    };
}
```

---

## Roslyn patterns

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
    newName,
    solution.Options
);
```

**Key rule:** always use `await` for Roslyn APIs that return `Task` — they may do I/O or background work.

---

## Git rules

See **`AGENTS.md`** for all git and terminal rules. Shorthand: `c/p` = commit and push now.

---

## Testing

**Local testing:** use `TestHarness/TestHarness.csproj` — runs a single tool call and prints the JSON response.

**Live testing:** configure in `.mcp.json` and test via GitHub Copilot or any MCP client.

Example `.mcp.json` in a client workspace:
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

The last argument is the root directory containing `.cs` files to load.
