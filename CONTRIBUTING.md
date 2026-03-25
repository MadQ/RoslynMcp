# Contributing to RoslynMcp

Thank you for your interest in contributing to RoslynMcp! This document provides guidelines and instructions for contributing.

---

## Recognition

**All contributors will be acknowledged!** Your contributions — whether code, documentation, bug reports, or ideas — help make RoslynMcp better.

- **Code contributors:** Listed in release notes and CHANGELOG.md
- **Significant contributions:** May be acknowledged in README.md or a dedicated CONTRIBUTORS file
- **First-time contributors:** Especially welcome — we're happy to help you get started!

By contributing, you're helping AI agents work better with C# code. That's worth celebrating. 🎉

---

## How to Contribute

### Reporting Issues

- **Search existing issues** before creating a new one
- Use the issue templates provided
- Include:
  - Clear description of the problem or feature request
  - Steps to reproduce (for bugs)
  - Expected vs. actual behavior
  - Environment details (.NET version, OS, MCP client)
  - Relevant logs or error messages

### Submitting Pull Requests

1. **Fork the repository** and create a feature branch from `dev`
2. **Follow the code style** (see `AGENTS.md`)
3. **Add tests** for new tools or significant changes
4. **Update documentation** (README.md, AGENTS.md, CHANGELOG.md, etc.)
5. **Run the test suite** and ensure all tests pass:
   ```bash
   dotnet build src/RoslynMcp/RoslynMcp.csproj
   dotnet run --project src/TestHarness/TestHarness.csproj
   ```
6. **Commit with clear messages** — describe *what* and *why*, not *how*
7. **Submit PR against `dev` branch** (not `main`)

---

## Development Setup

### Prerequisites

- .NET 8, 10, or 11 SDK
- Git
- Your preferred code editor (Visual Studio, VS Code, Rider, etc.)

### Building

```bash
# Clone your fork
git clone https://github.com/MadQ/RoslynMcp.git
cd RoslynMcp

# Build all targets
dotnet build RoslynMcp.slnx

# Or build a specific target
dotnet build src/RoslynMcp/RoslynMcp.csproj -f net10.0
```

### Running Tests

```bash
# Run the comprehensive test suite (23 tests covering all 24 tools)
dotnet run --project src/TestHarness/TestHarness.csproj
```

Tests run RoslynMcp against itself (dogfooding). All tests should pass before submitting a PR.

### Testing Locally with MCP Client

Publish a Release build and configure your MCP client to use it:

1. Publish RoslynMcp:
   ```bash
   dotnet publish src/RoslynMcp/RoslynMcp.csproj -c Release -f net10.0 -o ./publish/net10.0
   ```

2. Configure your MCP client:
   ```json
   {
     "servers": {
       "roslyn": {
         "type": "stdio",
         "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe",
         "args": ["/path/to/test/project"]
       }
     }
   }
   ```

3. Test tool calls interactively

**Note:** Avoid Visual Studio's "Publish" UI (may trigger `WebToolsException`). Use `dotnet publish` command line.

---

## Code Style Guidelines

**See [`AGENTS.md`](AGENTS.md) for the complete style guide.**

These are guidelines, not laws. The codebase values *clarity* and *intent* over rigid consistency. If you see a better way to express something — even if it deviates from the guide — do it, and explain why in a comment or commit message. Thoughtful departures help the style evolve.

That said, some patterns make collaboration easier:

### General C# Conventions

- **Braces:** same line for control flow, new line for methods/classes
- **No space** after `if`/`foreach`/`while`
- **Naming:** PascalCase for types/methods, camelCase for fields/locals
- **Modern C#:** pattern matching, target-typed `new`, collection expressions
- **Comments:** explain *why*, not *what* — no personal pronouns
- **Blank lines:** indented to match scope

### Roslyn-Specific Patterns

- Use `ISymbol`, `INamedTypeSymbol`, `SemanticModel`, etc. for code analysis
- Prefer `async` Roslyn APIs (`GetCompilationAsync`, `FindReferencesAsync`)
- Return structured objects (anonymous types) from tools, not strings
- Handle metadata symbols gracefully (e.g., external types like `System.IDisposable`)

**When in doubt:** match the surrounding code. If the file uses a different convention consistently, follow that instead of the guide.

### Code Quality Tools

**No linting or automated style enforcement.** The project's style guidelines are deliberate and don't align with standard linter rulesets. A linter that disrespects your guidelines is worse than no linter at all.

**PRs that add `.editorconfig` files will not be approved.** These files are just as opinionated as linters and create the same conflicts with the project's intentional style choices.

**Custom analyzers are acceptable** if they enforce narrow, high-value rules aligned with the project style. Example: `RoslynMcp.Analyzers` contains analyzers that gently suggest modern C# alternatives:
- **RMCP001**: Prefer `nint` over `IntPtr` (with code fix)
- **RMCP002**: Prefer `nuint` over `UIntPtr` (with code fix)

All analyzer diagnostics are warnings only, never errors. Code fixes are provided for one-click replacements.

---

## Adding a New Tool

1. **Create tool class** in `src/RoslynMcp/Tools/`:
   ```csharp
   [McpServerToolType]
   internal sealed class MyNewTool : RoslynMcpTool
   {
       public MyNewTool(WorkspaceResolver workspace) : base(workspace) { }

       [McpServerTool, Description("...")]
       public object MyToolMethod(
           [Description("...")] string parameter,
           [Description(ProjectPathDescription)] string? projectPath = null)
       {
           if(!TryGetCompilation(projectPath, out var compilation, out var error))
               return error;

           // Use Roslyn APIs here
           return new { result = "..." };
       }
   }
   ```

2. **No manual DI registration needed** — `WithToolsFromAssembly()` in `Program.cs` auto-discovers all `[McpServerToolType]` classes

3. **Add tests** in `src/TestHarness/Program.cs`

4. **Update documentation**:
   - README.md (tools table)
   - AGENTS.md (architecture table)
   - `.github/copilot-instructions.md` (architecture table)

---

## Exception Handling in Tools

Tools are the user-facing API surface. Exception handling must be explicit, informative, and never silent.

### Rules for User-Facing Tools

**✅ Do:**
- Catch **specific exception types** (`ArgumentException`, `IOException`, `UnauthorizedAccessException`, etc.)
- Return structured error objects:
  ```csharp
  return new {
      error = "Short description",
      details = ex.Message
  };
  ```
- Document expected exceptions in code comments

**❌ Don't:**
- Bare `catch { }` without explanation — always catch specific types or add a comment explaining why broad catch is needed
- Swallow exceptions that indicate programming errors (`NullReferenceException`, `InvalidOperationException` from bugs)
- Return generic "something went wrong" messages — be specific about what failed

### Examples

**Good** — specific exception, structured error:
```csharp
try {
    var regex = new Regex(pattern);
}
catch(ArgumentException ex) {
    return new {
        error = "Invalid regex pattern",
        details = ex.Message
    };
}
```

**Acceptable** — broad catch with clear justification:
```csharp
try {
    AddOrUpdateDocument(adhoc, projectId, fullPath);
}
catch {
    // File may be locked mid-write by another process;
    // next FileSystemWatcher event will retry automatically.
}
```

**Bad** — silent, broad catch with no context:
```csharp
try {
    return ParseXmlDocumentation(symbol);
}
catch {
    return null;  // Why? What failed? Should the user know?
}
```

### Internal Helpers vs User-Facing Tools

**User-facing tools** (methods with `[McpServerTool]`) must return explicit errors.

**Internal helpers** (private methods, file watchers, background processing) may use broader catches if:
1. Failure is non-fatal and recoverable
2. A comment explains the failure scenario and why it's safe to ignore
3. The outer system remains in a valid state

When in doubt, catch specific types and log or return the error.

### Exception Filters

Exception filters (`when` clauses) have a reputation for being obscure or "too clever." That's mostly cargo-cult thinking. They're just another tool — and a good one when the alternative is copy-pasting the same `catch` block five times. Try them. You might be pleasantly surprised.

**Use `when` clauses to consolidate multiple related exception types or add conditional logic:**

```csharp
// Consolidate multiple file system exceptions
catch(Exception ex) when(ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException) {
    return new {
        error = "File system error",
        details = ex.Message
    };
}

// Conditional catch based on exception state
catch(IOException ex) when(IsTransientError(ex)) {
    // Retry logic or ignore
}

// Log-and-rethrow pattern (filter returns false, so catch never executes)
catch(Exception ex) when(LogError(ex)) {
    // Never reached — filter logs and returns false
}

static bool LogError(Exception ex) {
    Console.Error.WriteLine($"[ERROR] {ex}");
    return false; // Don't catch, just observe
}
```

Exception filters are especially useful for:
- **DRYing up multiple catch blocks** with similar handling
- **Conditional catching** based on exception properties (error codes, inner exceptions)
- **Logging without catching** (filter returns false after logging)

---

## Project Structure

```
RoslynMcp/
├── src/
│   ├── RoslynMcp/              # Main MCP server project
│   │   ├── Tools/              # Tool implementations
│   │   ├── Program.cs          # MCP protocol + DI setup
│   │   ├── WorkspaceManager.cs # Compilation management
│   │   ├── ApprovalStore.cs    # Rename approval state
│   │   └── SolutionDiff.cs     # Unified diff generation
│   └── TestHarness/            # Test suite
├── .github/                # GitHub-specific files
├── .meta/                  # Project metadata
├── docs/                   # Project documentation
│   ├── process/            # Checklists and process guides
│   ├── sessions/           # Session handoff notes (gitignored, local only)
│   ├── MSBUILD_API_ANALYSIS.md  # MSBuild vs Roslyn architecture rationale
│   └── ScratchPad.md       # Owner scratchpad (gitignored, local only)
├── README.md               # Main documentation
├── INSTALLATION.md         # Setup instructions
└── CHANGELOG.md            # Version history
```

---

## Questions or Need Help?

- **Open an issue** for questions or discussions
- **Check existing issues** and documentation first
- Be respectful and constructive

---

## Code of Conduct

- Be professional and respectful
- Focus on technical merit
- Assume good intent
- Help others learn

---

## License

By contributing, you agree that your contributions will be licensed under the MIT License.

**You retain copyright** to your contributions, but grant the project and users the rights specified in the MIT License. Your name will appear in the git history and (for significant contributions) in release notes and acknowledgments.
