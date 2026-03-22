# AGENTS.md — RoslynMcp 🏴‍☠️

> We push boundaries, ship bleeding-edge C#, and we're having a blast doing it.
> No hand-wringing. No unnecessary abstraction. Just clean, fast, pirate-grade code.
>
> **This is a discussion, not a monologue.** Disagree when there's a better approach. Say so directly, explain why, and suggest the alternative. Point out mistakes — in design, naming, logic, or assumptions — before implementing them. Don't just execute; think first. A pushback that saves a bad commit is worth more than silent compliance.

Working rules for GitHub Copilot and any other AI agent in this repo.

**Ignore files called HumanNotes.txt** — for human reference only; may contain notes that would confuse an AI assistant.

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
| **Runtime** | .NET 8 / .NET 10 / .NET 11 (multi-targeted) |
| **Language** | C# 14 (`<LangVersion>preview</LangVersion>`) |
| **Version** | 0.2.0-alpha (pre-1.0) |
| **Dependencies** | `Microsoft.CodeAnalysis.*` (Roslyn) — MSBuildWorkspace (if .csproj found) → AdhocWorkspace (fallback) |
| **ImplicitUsings** | `enable` — don't add redundant `using` directives |
| **Resources** | [C# MCP SDK](https://csharp.sdk.modelcontextprotocol.io/) • [MCP Spec](https://modelcontextprotocol.io/) |

Two projects:
- `src/RoslynMcp/RoslynMcp.csproj` — MCP server
- `src/TestHarness/TestHarness.csproj` — local testing client

```
dotnet build src/RoslynMcp/RoslynMcp.csproj
```

---

## Architecture

| Component | Responsibility |
|-----------|----------------|
| `WorkspaceManager` | Auto-detects `.csproj` → `MSBuildWorkspace` (full resolution) or `AdhocWorkspace` (source-only); lazy compilation rebuild |
| `SearchFilesTool` | `search_files` — regex search across workspace files with paging; prerequisite for finding code to analyze with Roslyn tools |
| `SemanticSearchTool` | `semantic_search` — Roslyn syntax-tree filtering for context-aware search (comments, strings, identifiers, code, xmldocs); C#-only, slower but more precise |
| `ListFilesTool` | `list_files` — enumerate files matching glob pattern (fast file listing, no content) |
| `ReplaceInFileTool` | `replace_in_file` — text-level find/replace with regex support (any file type) |
| `ReplaceInCodeTool` | `replace_in_code` — semantic C# node replacement using Roslyn (validates syntax, preserves formatting) |
| `RespawnTool` | `respawn` (DEBUG only) — terminates server process for hot-reload during development — not very reliable |
| `TypeMembersTool` | `get_type_members` — enumerate members with full signatures + doc summaries |
| `DiagnosticsTool` | `get_diagnostics` — compiler errors and warnings for project or single file |
| `FindReferencesTool` | `find_references` — all references to a symbol across the project |
| `SymbolInfoTool` | `get_symbol_info` — resolve what a name at a location actually is |
| `PreviewRenameTool` | `preview_rename` — compute rename edits, return unified diff + token |
| `ApplyRenameTool` | `apply_rename` — approve/reject a pending rename by token |
| `ProjectInfoTool` | `get_project_info` — project metadata (TFM, language version, packages, etc.) |
| `BuildTool` | `build_project` — check Roslyn diagnostics first (fast), skip build if errors; run `dotnet build` if clean or `forceBuild=true` |
| `CleanSolutionTool` | `clean_solution` — remove all build artifacts (bin/obj directories) |
| `RestorePackagesTool` | `restore_packages` — restore NuGet packages |
| `FileOutlineTool` | `get_file_outline` — type/member structure without bodies (token saver) |
| `TypeHierarchyTool` | `get_type_hierarchy` — base types, interfaces, derived types |
| `FindImplementationsTool` | `find_implementations` — concrete implementations of interfaces/abstract members |
| `ListTypesTool` | `list_types` — enumerate types with optional filters |
| `GetUsingsTool` | `get_usings` — using directives + global usings |
| `GetSymbolDocumentationTool` | `get_symbol_documentation` — XML doc comments for symbols |
| `GetSymbolDefinitionTool` | `get_symbol_definition` — find declaration location with signature |
| `GetSymbolsInScopeTool` | `get_symbols_in_scope` — enumerate accessible symbols at a location |
| `ApprovalStore` | Session-scoped approval state (`y`, `n`, `session` model) |
| `SolutionDiff` | Unified diff generation for `Solution` → `Solution` edits |

**Data flow:** stdio MCP request → tool → `WorkspaceManager.GetCompilation()` (may rebuild) → Roslyn API → JSON response.

**Key files:** `Program.cs` (MCP protocol), `WorkspaceManager.cs` (compilation management), `Tools/*.cs` (tool implementations).

**Workspace modes:**
- **MSBuildWorkspace** (if `.csproj` found) — full NuGet resolution, multi-project support, .NET Framework 4.6.1+ compatibility
- **AdhocWorkspace** (fallback) — source-only, fast startup (<100 ms)

**Tool Selection Guidance:**

When editing C# code, **actively prefer `replace_in_code`** over `replace_in_file`:
- `replace_in_code` is semantically aware, validates syntax, preserves formatting/trivia
- `replace_in_file` is for text/config files or when you need literal text replacement

When discovering files/content:
- `list_files` — fast glob enumeration (find files by name/path)
- `search_files` — content search (find lines matching regex pattern)
- `semantic_search` — context-aware C# search (filter by comments, strings, identifiers, xmldocs, code)
- `find_references` — semantic symbol search (Roslyn-based, finds usage across project)

---

# ALWAYS use a RoslynMcp tool when possible!

This is very important! It helps to test the tools, dogfood the API, and ensures your agent gets accurate semantic understanding of the codebase. Avoid workarounds like grepping files or spawning builds unless absolutely necessary. I'm serious: get this through your thick pirate skull: DOGFOOD the living daylights out of all this!

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
    newName,
    solution.Options
);
```

**Key rule:** always use `await` for Roslyn APIs that return `Task` — they may do I/O or background work.

---

## Rename Workflow (User-Configurable)

**All renames use two-phase flow:** `preview_rename` → review diff → `apply_rename`.

**Why?** Renames can affect dozens/hundreds of files. The two-phase flow lets you review impact before committing — especially important for large/uncertain changes.

### Agent Behavior (Configure Per User)

Users can instruct agents to handle renames conservatively or aggressively:

**Conservative (default recommendation):**
```
Always call preview_rename first. Show me the diff.
Only call apply_rename after I explicitly approve.
```

**Balanced:**
```
For small renames (1-3 files, obvious intent like typo fixes):
  - Call preview_rename, review diff yourself, auto-apply if safe
For large renames (>3 files, broad scope, uncertain impact):
  - Call preview_rename, show me the diff, wait for approval
```

**Aggressive:**
```
Call preview_rename → apply_rename immediately unless I say otherwise.
I trust you and I have git.
```

**Session approval:** If user approves a rename with `approval: "session"`, further renames of the same symbol auto-apply for the remainder of the server process (until restart). Use this for bulk renaming tasks.

**Planned feature:** `undo_last_edit` will revert the most recent Roslyn edit (rename, refactoring) from an in-memory snapshot. Useful for "wait, let me rethink that" moments mid-task.

---

## Git Rules

| Operation | Rule |
|-----------|------|
| Create / switch branch | ✅ Free |
| Stage files | ✅ Free |
| Commit | ❌ Ask first |
| Push | ❌ Ask first |

**Shorthand:** `c/p` = commit and push now.

**Branch naming:** `feature/<short-description>` for multi-file or non-trivial changes.

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

**Local testing:** use `src/TestHarness/TestHarness.csproj` — runs a single tool call and prints the JSON response.

**Live testing:** configure in `.mcp.json` and test via GitHub Copilot or any MCP client.

Example `.mcp.json`:
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

> **Note:** Use the published executable (see README.md "Building the Executable" section). The `dotnet run` approach was abandoned due to multi-target confusion and recursive behavior when dogfooding.
