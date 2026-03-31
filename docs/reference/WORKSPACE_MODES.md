# Workspace Modes Reference

RoslynMcp automatically detects the best workspace mode based on your project structure. This document explains the two modes, when each is used, and their tradeoffs.

---

## Overview

| Mode | Trigger | Startup Time | Type Resolution | Multi-Project | Best For |
|------|---------|--------------|-----------------|---------------|----------|
| **MSBuildWorkspace** | `.csproj` file found | 1-2 seconds | Full (NuGet + source) | ✅ Yes | Production codebases |
| **AdhocWorkspace** | No `.csproj` file | <100 ms | Source-only | ❌ No | Quick scripts, demos |

RoslynMcp **automatically selects** the appropriate mode—you don't need to configure anything. Just point it at your project directory.

---

## MSBuildWorkspace (Full Resolution)

### When Used

Activated when RoslynMcp finds a `.csproj` file in the target directory.

### Capabilities

- ✅ **NuGet package type resolution** — `List<T>`, `HttpClient`, Entity Framework, etc. all resolve correctly
- ✅ **Multi-project support** — follows `<ProjectReference>` and loads referenced projects
- ✅ **.NET Framework projects** — supports .NET Framework 4.6.1+ (requires MSBuild)
- ✅ **Correct preprocessor symbols** — respects `<DefineConstants>` from `.csproj`
- ✅ **Project-specific language version** — uses `<LangVersion>` from project file
- ✅ **Nullable reference types** — respects `<Nullable>` setting
- ✅ **ImplicitUsings** — resolves global usings from SDK

### Requirements

- **MSBuild on PATH** — installed with .NET SDK or Visual Studio
- **NuGet packages restored** — run `dotnet restore` before first use (or use `roslyn_restore_packages` tool)

### Startup Performance

- **Initial load:** 1-2 seconds (depends on project size and NuGet package count)
- **Subsequent calls:** Instant (workspace is cached in memory)
- **File changes:** Automatically detected and incrementally updated

### Example Projects

Perfect for:
- ASP.NET Core applications
- Class libraries with NuGet dependencies
- Multi-project solutions
- Production codebases with complex dependencies

### Troubleshooting

**Issue:** Types from NuGet packages not resolving

**Solution:** Ensure packages are restored:
```bash
dotnet restore YourProject.csproj
# OR use the tool:
roslyn_restore_packages --projectPath src/YourProject
```

**Issue:** "MSBuild not found"

**Solution:** Install .NET SDK or Visual Studio. MSBuild is included with both.

---

## AdhocWorkspace (Fast, Source-Only)

### When Used

Activated when RoslynMcp doesn't find a `.csproj` file in the target directory. Scans for `.cs` files directly.

### Capabilities

- ✅ **Fast startup** — <100 ms, no MSBuild overhead
- ✅ **Source-defined type resolution** — all types in loaded `.cs` files resolve
- ✅ **Syntax analysis** — full syntax tree parsing
- ✅ **Semantic analysis** — symbol resolution for source-defined types
- ✅ **File watching** — detects `.cs` file changes via `FileSystemWatcher`

### Limitations

- ❌ **No NuGet type resolution** — `List<T>` appears as `List<T>` (generic), `HttpClient` unresolved
- ❌ **Fixed parse options** — C# preview language version, `DEBUG` preprocessor symbol defined
- ❌ **Single directory tree** — doesn't follow project references
- ❌ **No project-specific settings** — can't respect nullable/language version from project file

### Startup Performance

- **Initial load:** <100 ms (just scans for `.cs` files)
- **Subsequent calls:** Instant
- **File changes:** Automatically detected via `FileSystemWatcher`

### Example Use Cases

Perfect for:
- Quick C# scripts without project files
- Demonstration code snippets
- Learning/tutorial code
- Temporary scratch files

### Safety Features

AdhocWorkspace includes protection against accidental misuse:

- **Root directory protection** — fails fast if you point it at a drive root like `C:\` or `D:\` (prevents scanning entire drives)
- **UnauthorizedAccessException handling** — skips directories it can't access
- **Exclusion patterns** — automatically skips:
  - `node_modules/`
  - `bin/`, `obj/`
  - `.git/`
  - Hidden directories
  - System directories

---

## Choosing a Mode

### Use MSBuildWorkspace When:

- ✅ You have a `.csproj` file
- ✅ You need NuGet type resolution
- ✅ You're working on production code
- ✅ You need multi-project support
- ✅ You need project-specific settings (nullable, language version)

**How:** Just point RoslynMcp at a directory containing a `.csproj` file. It auto-detects.

### Use AdhocWorkspace When:

- ✅ You're working with standalone `.cs` files
- ✅ You need fastest possible startup
- ✅ You're doing quick experiments or demos
- ✅ You don't need external dependencies to resolve

**How:** Point RoslynMcp at a directory containing `.cs` files but no `.csproj`. It auto-detects.

---

## Switching Modes

You don't manually "switch" modes—RoslynMcp detects the mode per project path.

### Multi-Project Workflows (v0.3.0+)

All 31 tools require a `projectPath` parameter, so you can work with **both modes in a single session**:

```json
// Example: workspace with both project types
{
  "servers": {
    "roslyn": {
      "command": "/path/to/RoslynMcp.exe",
      "args": ["."]
    }
  }
}
```

Then in tool calls:
```javascript
// MSBuildWorkspace project (has .csproj)
roslyn_get_diagnostics({ projectPath: "src/MyApp" })

// AdhocWorkspace project (no .csproj)
roslyn_get_diagnostics({ projectPath: "scripts" })
```

---

## Performance Comparison

| Operation | MSBuildWorkspace | AdhocWorkspace |
|-----------|------------------|----------------|
| **Initial load** | 1-2 seconds | <100 ms |
| **Type resolution** | Full (NuGet + source) | Source-only |
| **Memory usage** | ~200-500 MB (depends on project size) | ~50-100 MB |
| **File change detection** | Roslyn internal + FileSystemWatcher | FileSystemWatcher |
| **Multi-project** | ✅ Yes | ❌ No |

---

## Technical Details

### MSBuildWorkspace

**Implementation:** `Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace`

**Initialization:**
```csharp
var workspace = MSBuildWorkspace.Create();
var project = await workspace.OpenProjectAsync(projectPath);
```

**Project loading:**
- Resolves NuGet packages via MSBuild
- Follows `<ProjectReference>` and loads dependencies
- Respects all project settings (language version, nullable, preprocessor symbols)
- Uses Roslyn's internal file change detection

### AdhocWorkspace

**Implementation:** `Microsoft.CodeAnalysis.AdhocWorkspace`

**Initialization:**
```csharp
var workspace = new AdhocWorkspace();
var project = workspace.AddProject("MyProject", LanguageNames.CSharp);
```

**File loading:**
- Enumerates `.cs` files in directory tree
- Creates in-memory project with fixed parse options
- Uses `FileSystemWatcher` for change detection
- No external dependency resolution

### WorkspaceManager

RoslynMcp's `WorkspaceManager` (split into `WorkspaceManager.cs`, `.Resolution.cs`, `.Instance.cs`) loads the full solution when a `.sln`/`.slnx` is found, and caches workspace instances with LRU eviction:

- **Cache key:** Solution path (or .csproj/directory if no solution found)
- **Cache size:** Configurable via `ROSLYNMCP_MAX_CACHED_WORKSPACES` (default 5)
- **Thread-safety:** Lock-free cache lookups; loading outside lock to avoid contention
- **Invalidation:** FileSystemWatcher (both MSBuild and Adhoc) + manual via `InvalidateFile()`
- **Per-project compilation cache** — each project in a solution has its own cached compilation

---

## FAQ

### Q: Can I force MSBuildWorkspace even if no .csproj exists?

**A:** No. AdhocWorkspace is the fallback when no `.csproj` is found. To use MSBuildWorkspace, create a minimal `.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>preview</LangVersion>
  </PropertyGroup>
</Project>
```

### Q: Can I use AdhocWorkspace for a project with .csproj?

**A:** Not directly. RoslynMcp prioritizes MSBuildWorkspace when a `.csproj` is found. You'd need to temporarily rename or move the `.csproj` file (not recommended).

### Q: Why is MSBuildWorkspace slower on first load?

**A:** MSBuildWorkspace:
1. Invokes MSBuild to resolve the project
2. Downloads/restores NuGet packages (if not cached)
3. Parses all source files
4. Builds the semantic model

Subsequent calls are instant because the workspace is cached.

### Q: Does AdhocWorkspace support C# 13/14 features?

**A:** Yes. AdhocWorkspace uses `LangVersion.Preview` by default, so all latest C# features are supported at the syntax level. Semantic features that require external types (e.g., `required` with data annotations) may not resolve if those types aren't in loaded source files.

### Q: What happens if I have both .csproj and loose .cs files?

**A:** MSBuildWorkspace takes precedence. The `.cs` files included in the `.csproj` via `<Compile Include="...">` (or implicitly via SDK glob patterns) will be loaded. Loose `.cs` files not referenced by the project are ignored.

---

## Related

- [README.md § Workspace modes](../../README.md#workspace-modes) — quick overview
- [INSTALLATION.md § Troubleshooting](../../INSTALLATION.md#troubleshooting) — setup issues
- [AGENTS.md § Architecture](../../AGENTS.md#architecture) — WorkspaceManager implementation details

---

**Last Updated:** 2026-03-28 (v0.7.0-alpha)
