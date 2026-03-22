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
2. **Follow the code style** (see `.github/copilot-instructions.md`)
3. **Add tests** for new tools or significant changes
4. **Update documentation** (README.md, AGENTS.md, etc.)
5. **Run the test suite** and ensure all tests pass:
   ```bash
   dotnet build RoslynMcp/RoslynMcp.csproj
   dotnet run --project TestHarness/TestHarness.csproj
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
dotnet build RoslynMcp/RoslynMcp.csproj -f net10.0
```

### Running Tests

```bash
# Run the comprehensive test suite (16 tests covering all 18 tools)
dotnet run --project TestHarness/TestHarness.csproj
```

Tests run RoslynMcp against itself (dogfooding). All tests should pass before submitting a PR.

### Testing Locally with MCP Client

**Option 1: Use Debug/Release build (testing against other projects)**

1. Build RoslynMcp:
   ```bash
   dotnet build RoslynMcp/RoslynMcp.csproj
   ```

2. Configure your MCP client to use `dotnet run`:
   ```json
   {
     "servers": {
       "roslyn": {
         "type": "stdio",
         "command": "dotnet",
         "args": ["run", "--no-build", "--project", "/absolute/path/to/RoslynMcp/RoslynMcp.csproj", "-f", "net10.0", "--", "/path/to/other/project"]
       }
     }
   }
   ```

3. Test tool calls interactively

**Option 2: Use published executable (production-like, or for dogfooding RoslynMcp on itself)**

1. Publish RoslynMcp:
   ```bash
   dotnet publish RoslynMcp/RoslynMcp.csproj -c Release -f net10.0 -o ./publish/net10.0
   ```

2. Configure your MCP client to use the executable:
   ```json
   {
     "servers": {
       "roslyn": {
         "type": "stdio",
         "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe",
         "args": ["/absolute/path/to/RoslynMcp"]
       }
     }
   }
   ```

**Note:** 
- Use the published executable when testing RoslynMcp against itself — `dotnet run` creates a process conflict
- Avoid Visual Studio's "Publish" UI (may trigger `WebToolsException`). Use `dotnet publish` command line

---

## Code Style Guidelines

**See [`.github/copilot-instructions.md`](.github/copilot-instructions.md) for complete style guide.**

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

---

## Adding a New Tool

1. **Create tool class** in `RoslynMcp/Tools/`:
   ```csharp
   [McpServerToolType]
   internal sealed class MyNewTool(WorkspaceManager workspace)
   {
       [McpServerTool, Description("...")]
       public object MyToolMethod(
           [Description("...")] string parameter)
       {
           var compilation = workspace.GetCompilation();
           // Use Roslyn APIs here
           return new { result = "..." };
       }
   }
   ```

2. **Register tool** in `Program.cs`:
   ```csharp
   builder.Services.AddTransient<MyNewTool>();
   ```

3. **Add tests** in `TestHarness/Program.cs`

4. **Update documentation**:
   - README.md (tools table)
   - AGENTS.md (architecture table)
   - `.github/copilot-instructions.md` (architecture table)

---

## Project Structure

```
RoslynMcp/
├── RoslynMcp/              # Main MCP server project
│   ├── Tools/              # Tool implementations
│   ├── Program.cs          # MCP protocol + DI setup
│   ├── WorkspaceManager.cs # Compilation management
│   ├── ApprovalStore.cs    # Rename approval state
│   └── SolutionDiff.cs     # Unified diff generation
├── TestHarness/            # Test suite
├── .github/                # GitHub-specific files
├── README.md               # Main documentation
├── AGENTS.md               # Agent-specific rules
├── INSTALLATION.md         # Setup instructions
└── TEST_RESULTS.md         # Test results log
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
