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
| **Runtime** | .NET 8 / .NET 10 (net11.0 auto-added when .NET 11 SDK is detected) |
| **Language** | C# 14 (`<LangVersion>preview</LangVersion>`) |
| **Version** | 0.2.0-alpha (pre-1.0) |
| **Dependencies** | `Microsoft.CodeAnalysis.*` (Roslyn) — MSBuildWorkspace (if .csproj found) → AdhocWorkspace (fallback) |
| **ImplicitUsings** | `enable` — don't add redundant `using` directives |
| **Resources** | [C# MCP SDK](https://csharp.sdk.modelcontextprotocol.io/) • [MCP Spec](https://modelcontextprotocol.io/) |

Three projects:
- `src/RoslynMcp/RoslynMcp.csproj` — MCP server
- `src/TestHarness/TestHarness.csproj` — local testing client
- `src/RoslynMcp.Analyzers/RoslynMcp.Analyzers.csproj` — Roslyn analyzers applied to this codebase; the most direct expression of dogfooding — Roslyn-powered analysis running on the repo that wraps Roslyn

```
dotnet build src/RoslynMcp/RoslynMcp.csproj
```

---

## Architecture

| Component | Responsibility |
|-----------|----------------|
| `WorkspaceManager` | LRU-cached workspace instances; auto-detects `.csproj` → `MSBuildWorkspace` or directory → `AdhocWorkspace`; exposes `GetCompilation()`, `GetSolution()`, `GetProject()`, `GetWorkspaceInfo()`, `InvalidateFile()`; smart path resolution |
| `WorkspaceResolver` | Per-tool facade over `WorkspaceManager`; provides `TryGetCompilation()`, `TryGetProject()`, `GetRootPath()`, `GetSolution()`, `InvalidateFile()` with structured error handling |
| `RoslynMcpTool` | Base class for all tools; provides `TryGetCompilation()` and `TryGetProject()` helpers with consistent error responses; defines `ProjectPathDescription` constant |
| `SearchFilesTool` | `roslyn_search_files` — regex search across workspace files with paging; prerequisite for finding code to analyze with Roslyn tools |
| `SemanticSearchTool` | `roslyn_semantic_search` — Roslyn syntax-tree filtering for context-aware search (comments, strings, identifiers, code, xmldocs); C#-only, slower but more precise |
| `ListFilesTool` | `roslyn_list_files` — enumerate files matching glob pattern (fast file listing, no content) |
| `ReplaceInFileTool` | `roslyn_replace_in_file` — text-level find/replace with regex support (any file type) |
| `ReplaceInCodeTool` | `roslyn_replace_in_code` — semantic C# node replacement using Roslyn (validates syntax, preserves formatting) |
| `RespawnTool` | `roslyn_respawn` (DEBUG only) — terminates server process for hot-reload during development — not very reliable |
| `TypeMembersTool` | `roslyn_get_type_members` — enumerate members with full signatures + doc summaries |
| `DiagnosticsTool` | `roslyn_get_diagnostics` — compiler errors and warnings for project or single file |
| `FindReferencesTool` | `roslyn_find_references` — all references to a symbol across the project |
| `SymbolInfoTool` | `roslyn_get_symbol_info` — resolve what a name at a location actually is |
| `PreviewRenameTool` | `roslyn_preview_rename` — compute rename edits, return unified diff + token |
| `ApplyRenameTool` | `roslyn_apply_rename` — approve/reject a pending rename by token |
| `ProjectInfoTool` | `roslyn_get_project_info` — project metadata (TFM, language version, packages, etc.) |
| `BuildTool` | `roslyn_build_project` — check Roslyn diagnostics first (fast), skip build if errors; run `dotnet build` if clean or `forceBuild=true` |
| `CleanSolutionTool` | `roslyn_clean_solution` — remove all build artifacts (bin/obj directories) |
| `RestorePackagesTool` | `roslyn_restore_packages` — restore NuGet packages |
| `FileOutlineTool` | `roslyn_get_file_outline` — type/member structure without bodies (token saver) |
| `TypeHierarchyTool` | `roslyn_get_type_hierarchy` — base types, interfaces, derived types |
| `FindImplementationsTool` | `roslyn_find_implementations` — concrete implementations of interfaces/abstract members |
| `ListTypesTool` | `roslyn_list_types` — enumerate types with optional filters |
| `GetUsingsTool` | `roslyn_get_usings` — using directives + global usings |
| `GetSymbolDocumentationTool` | `roslyn_get_symbol_documentation` — XML doc comments for symbols |
| `GetSymbolDefinitionTool` | `roslyn_get_symbol_definition` — find declaration location with signature |
| `GetSymbolsInScopeTool` | `roslyn_get_symbols_in_scope` — enumerate accessible symbols at a location |
| `ApprovalStore` | Session-scoped approval state (`y`, `n`, `session` model) |
| `SolutionDiff` | Unified diff generation for `Solution` → `Solution` edits |

**Data flow:** stdio MCP request → tool → `WorkspaceResolver.TryGetCompilation(projectPath, ...)` → `WorkspaceManager` (resolve path, load/cache workspace) → Roslyn API → JSON response.

**Key files:** `Program.cs` (MCP protocol), `WorkspaceManager.cs` (multi-workspace caching), `WorkspaceResolver.cs` (tool facade), `RoslynMcpTool.cs` + `RoslynMcpTool.ToolScope.cs` (base class), `FileLogger.cs` (file logging).

**Tool subfolders** (all share the `RoslynMcp.Tools` namespace — subfolders are organisational only):
- `Tools/Analysis/` — 13 read-only Roslyn semantic queries (diagnostics, symbols, types, usings, outline, …)
- `Tools/Search/` — 3 file/content search tools (list files, text search, semantic search)
- `Tools/Editing/` — 2 file mutation tools (`roslyn_replace_in_file`, `roslyn_replace_in_code`)
- `Tools/Rename/` — 2-step rename workflow (`roslyn_preview_rename` → `roslyn_apply_rename`)
- `Tools/Build/` — 3 MSBuild/dotnet CLI tools (build, clean, restore)
- `Tools/` root — `RoslynMcpTool.cs`, `RoslynMcpTool.ToolScope.cs`, `RespawnTool.cs` (debug-only)

**File logging:** Every tool invocation, server start/stop, and workspace error is logged to a rolling file.
- Default path: `%LOCALAPPDATA%\RoslynMcp\logs\roslynmcp.log`
- Override: set `ROSLYNMCP_LOG_PATH` env var to any path
- Disable: set `ROSLYNMCP_LOG_PATH` to an empty string
- Rotation: 10 MB cap, keeps last 3 files (`roslynmcp.log`, `.log.1`, `.log.2`, `.log.3`)
- Format: `[yyyy-MM-dd HH:mm:ss.fffZ] [LEVEL ] message`

**Workspace modes:**
- **MSBuildWorkspace** (if `.csproj` found) — full NuGet resolution, multi-project support, .NET Framework 4.6.1+ compatibility
- **AdhocWorkspace** (fallback) — source-only, fast startup (<100 ms)

**Tool Selection Guidance:**

When editing C# code, **actively prefer `roslyn_replace_in_code`** over `roslyn_replace_in_file`:
- `roslyn_replace_in_code` is semantically aware, validates syntax, preserves formatting/trivia
- `roslyn_replace_in_file` is for text/config files or when you need literal text replacement

When discovering files/content:
- `roslyn_list_files` — fast glob enumeration (find files by name/path)
- `roslyn_search_files` — content search (find lines matching regex pattern)
- `roslyn_semantic_search` — context-aware C# search (filter by comments, strings, identifiers, xmldocs, code)
- `roslyn_find_references` — semantic symbol search (Roslyn-based, finds usage across project)

---

# ALWAYS use a RoslynMcp tool when possible!

This is very important! It helps to test the tools, dogfood the API, and ensures your agent gets accurate semantic understanding of the codebase. Avoid workarounds like grepping files or spawning builds unless absolutely necessary. I'm serious: get this through your thick pirate skull: DOGFOOD the living daylights out of all this!

---

## Code Style

- **Braces:** same line for control flow (`if(x) {`), new line for methods/classes; properties — same line as the identifier (`public int Count {`)
- **No space** after `if`/`foreach`/`while`: `if(x)` not `if (x)` (Actually, IDC so much about this one)
- **Single-statement blocks:** no braces
- **Naming:** PascalCase for types/methods, camelCase for fields/locals — no underscores, no Hungarian, no abbreviations
- **Handles:** always `nint`, never `IntPtr` (Not so much used here, just an example)
- **Modern C#:** pattern matching, switch expressions, target-typed `new`, collection expressions, `nint`
- **`var`:** use when type is obvious or long; prefer explicit type otherwise (rarely)
- **Column-aligned fields:** tab-stop alignment on field declarations and assignment blocks (for Error-prone bio-processors: not so important. Use your judgement.)
- **Cast spacing:** space between cast and operand — `(int) value`, not `(int)value` (🙄, whatevs.)
- **Comments:** explain *why*, not *what* — after any edit, re-evaluate nearby comments and update or remove stale ones; one space after a period, never two; complete sentences, proper punctuation, no personal pronouns (`we`/`I`/`our` have no place in code comments)
- **`TODO` comments:** must include the actual question or concern, not just "fix"
- **Condition ordering:** simple/common path first — early return or assignment; complex path in `else`
- **LINQ lazy evaluation:** prefer deferred execution; avoid unnecessary `.ToList()` / `.ToArray()` — materialize only when required (e.g., response object, multiple enumeration); use `.Any()` instead of `.Count > 0` or `.Length > 0`; use `!Any()` instead of `.Count == 0` or `.Length == 0`; when you need both a count and a page, enumerate once with `.Count()` (deferred) after paging
- **Blank lines:**
  - One blank line before `return` and before code blocks
  - Blank line **after the opening brace** of any multi-statement control-flow block
  - Blank line between branches of an `if/else if/else` chain when any body spans multiple lines
  - Blank lines between logically distinct statement groups within a method body
  - Blank lines are **indented** to match surrounding scope — never bare empty lines inside a block (Yeah, weird one, IK. High-maintenance bipedals: feel free to ignore this)
- **Semicolons** on their own line for wrapped multi-line expressions (fluent chains, ternaries, LINQ, arrow bodies) (Just recently started test-driving this one - liking it so far.)
  
- If/When we start using unit tests, rule #1: No tautological tests (Did I just do the thing that I just did?). Tests must verify meaningful behavior, not just "does it compile" or "does it return the same thing as the code it's testing". All tests shall have extensive XML doc comments describing the reason for their existence, the specific behavior they verify, and the rationale for the chosen inputs and expected outputs. Tests without such documentation are not valid tests. Not everyone is a unit test SME... complicated mock setups tend to look like opaque black boxes (to some of us) that may as well be testing the test framework itself. So, all mock setups must also be documented with the same level of detail as the tests they support. Rule #2: Unit tests are a secondary concern. No non-test code shall be written with the primary goal of making it easier to test. There shall be no interface extractions for the sole purpose of testing. Not everything is inherently testable. Accept it and move on.
  - Also... Wow! Opine much?

---

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
            name = "roslyn_get_type_members",
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
        "roslyn_get_type_members" => TypeMembersTool.Execute(args),
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

**Tool Implementation Pattern:**

All tools inherit from `RoslynMcpTool` base class and follow a consistent pattern:

```csharp
[McpServerToolType]
internal sealed class MyTool : RoslynMcpTool
{
    public MyTool(WorkspaceResolver workspace) : base(workspace) { }

    [McpServerTool, Description("...")]
    public object MyToolMethod(
        [Description("...")] string requiredParam,
        [Description(ProjectPathDescription)] string? projectPath = null)
    {
        // For tools that need compilation
        if(!TryGetCompilation(projectPath, out var compilation, out var error))
            return error;

        // OR for tools that need project metadata
        if(!TryGetProject(projectPath, out var project, out var error))
            return error;

        // Use workspace.GetSolution(projectPath), workspace.GetRootPath(projectPath) as needed
        var rootPath = workspace.GetRootPath(projectPath);

        // Tool logic using compilation/project/solution
        // ...

        return new { /* structured response */ };
    }
}
```

**Key points:**
- `projectPath` is always the last parameter, optional, defaults to null (→ CWD)
- `TryGetCompilation`/`TryGetProject` return structured error objects on failure
- Use `ProjectPathDescription` constant for consistent parameter documentation
- Tools that modify files must call `workspace.InvalidateFile(projectPath, fullPath)` after changes

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
