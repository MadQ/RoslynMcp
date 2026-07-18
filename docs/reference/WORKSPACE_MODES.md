# Workspace Modes Reference

RoslynMcp automatically detects the best workspace mode based on your project structure. This document explains the three workspace modes (SDK, VS, and Adhoc), when each is used, and their tradeoffs. The mode can be overridden with the `--workspace` CLI flag or the `ROSLYNMCP_WORKSPACE` environment variable.

---

## Overview

| Mode | Trigger / Override | Typical Cost | Type Resolution | Multi-Project | Best For |
|------|-------------------|--------------|-----------------|---------------|----------|
| **MSBuildWorkspace (SDK)** | `.csproj` found + auto-detected SDK-style project, or `--workspace sdk` | Highest | Full (NuGet + source) | ✅ Yes | Modern SDK-style projects |
| **MSBuildWorkspace (VS)** | Auto-detected legacy/Framework-style project, or `--workspace vs` | Higher | Full (NuGet + source) | ✅ Yes | .NET Framework / legacy Visual Studio projects |
| **AdhocWorkspace** | No `.csproj` found, or `--workspace adhoc` | Lowest | Source-only | ❌ No | Quick scripts, demos |

RoslynMcp **automatically selects** the appropriate mode—you don't need to configure anything for most projects. To override, pass `--workspace sdk|vs|adhoc|auto` on the command line, or set the `ROSLYNMCP_WORKSPACE` environment variable to the same values.

---

## MSBuildWorkspace (SDK) — Full Resolution, SDK MSBuild

### When Used

Activated when RoslynMcp finds a `.csproj` file and either:
- auto-detection (`WorkspaceMode.Auto`) classifies it as SDK-style via `MSBuildBootstrap.DetectProjectStyle()`, or
- you explicitly force `--workspace sdk`.

MSBuild discovery then runs through `MSBuildBootstrap.EnsureReady()`.

### Capabilities

- ✅ **NuGet package type resolution** — `List<T>`, `HttpClient`, Entity Framework, etc. all resolve correctly
- ✅ **Multi-project support** — follows `<ProjectReference>` and loads referenced projects
- ✅ **.NET 6+ projects** — best fit for modern SDK-style projects
- ✅ **Correct preprocessor symbols** — respects `<DefineConstants>` from `.csproj`
- ✅ **Project-specific language version** — uses `<LangVersion>` from project file
- ✅ **Nullable reference types** — respects `<Nullable>` setting
- ✅ **ImplicitUsings** — resolves global usings from SDK

### Requirements

- **MSBuild discoverable** — most commonly because the .NET SDK is on PATH
- **Or** `DOTNET_ROOT` points at a valid SDK install
- **Or** `ROSLYNMCP_MSBUILD_PATH` points at a directory containing `MSBuild.dll`
- **NuGet packages restored** — run `dotnet restore` before first use (or use `roslyn_restore_packages` tool)

### Startup Performance

- **Initial load:** varies with project size, restore state, and SDK discovery path
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

**Solution:** Install .NET SDK. MSBuild is included.

---

## MSBuildWorkspace (VS) — Full Resolution, Visual Studio MSBuild

### When Used

Activated via `--workspace vs` / `ROSLYNMCP_WORKSPACE=vs`, or auto-detected when `DetectProjectStyle()` sees legacy project markers such as `ToolsVersion=` or `TargetFrameworkVersion`. Uses the Visual Studio MSBuild instance located via `vswhere`.

### Capabilities

Same as SDK mode, plus:
- ✅ **.NET Framework projects** — supports .NET Framework 4.6.1+ (requires Visual Studio)
- ✅ **Legacy project formats** — handles non-SDK-style `.csproj` files

### Requirements

- **Windows** — VS mode is Windows-only
- **Visual Studio installed** — any edition or Build Tools installation discoverable via `vswhere`
- **`vswhere.exe`** — bundled with Visual Studio; used to locate the MSBuild instance

### Startup Performance

- **Initial load:** usually slower than SDK mode because Visual Studio MSBuild discovery adds overhead
- **Subsequent calls:** Instant (workspace is cached in memory)

### When to Force This Mode

```bash
# CLI flag
RoslynMcp.exe --workspace vs .

# Environment variable
$env:ROSLYNMCP_WORKSPACE = "vs"
RoslynMcp.exe .
```

---

## AdhocWorkspace (Fast, Source-Only)

### When Used

Activated when RoslynMcp resolves your `projectPath` to a directory with no `.csproj`, or when you explicitly force `--workspace adhoc`. It creates an in-memory C# project rooted at that directory and scans for `.cs` files directly.

### Capabilities

- ✅ **Fast startup** — no MSBuild overhead
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

- **Initial load:** usually much faster than MSBuild modes because no MSBuild discovery or project evaluation runs
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
  - `.vs/`
  - `packages/`
  - Hidden directories
  - System directories

---

## Overriding the Mode

By default RoslynMcp auto-detects the best mode. To override:

| Method | Syntax |
|--------|--------|
| **CLI flag** | `RoslynMcp.exe --workspace sdk\|vs\|adhoc\|auto <path>` |
| **Environment variable** | `ROSLYNMCP_WORKSPACE=sdk\|vs\|adhoc\|auto` |

Priority: CLI flag → env var → auto-detect.

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

RoslynMcp auto-detects the mode per `projectPath`. To force a specific mode for a session, use the `--workspace` flag or `ROSLYNMCP_WORKSPACE` env var (see [Overriding the Mode](#overriding-the-mode) above).

### Multi-Project Workflows

All tools require a `projectPath` parameter, so you can work with **multiple modes in a single session**:

```json
// Example: workspace with both project types
{
  "servers": {
    "MadQ.RoslynMcp": {
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

| Operation | MSBuildWorkspace (SDK) | MSBuildWorkspace (VS) | AdhocWorkspace |
|-----------|------------------------|----------------------|----------------|
| **Initial load** | Variable | Variable (usually slower than SDK) | Usually fastest |
| **Type resolution** | Full (NuGet + source) | Full (NuGet + source) | Source-only |
| **Memory usage** | Project-dependent | Project-dependent | Lower, but still project-dependent |
| **File change detection** | Roslyn internal + FileSystemWatcher | Roslyn internal + FileSystemWatcher | FileSystemWatcher |
| **Multi-project** | ✅ Yes | ✅ Yes | ❌ No |
| **.NET Framework** | ⚠️ Limited | ✅ Yes | ❌ No |

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

RoslynMcp's `WorkspaceManager` (split into `WorkspaceManager.cs`, `.Resolution.cs`, `.Instance.cs`) loads the full solution when a single `.slnx` or `.sln` is found while walking upward from a resolved `.csproj` path, and caches workspace instances with LRU eviction:

- **Cache key:** Solution path (or .csproj/directory if no solution found)
- **Cache size:** Configurable via `ROSLYNMCP_MAX_CACHED_WORKSPACES` (default 5)
- **Path inference cache:** Relative-path inference can be disabled via `ROSLYNMCP_DISABLE_PATH_CACHE=true`
- **Thread-safety:** Lock-protected cache lookups with short critical section; loading outside lock to avoid contention
- **Invalidation:** FileSystemWatcher (both MSBuild and Adhoc) + manual via `InvalidateFile()`
- **Per-project compilation cache** — each project in a solution has its own cached compilation

---

## FAQ

### Q: Can I force MSBuildWorkspace even if no .csproj exists?

**A:** No. The `--workspace sdk` and `--workspace vs` flags select *which* MSBuild instance to use, but both still require a `.csproj` to load. Without one, RoslynMcp falls back to AdhocWorkspace regardless of the flag. To use MSBuildWorkspace, create a minimal `.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>preview</LangVersion>
  </PropertyGroup>
</Project>
```

### Q: Can I use AdhocWorkspace for a project with .csproj?

**A:** Yes — pass `--workspace adhoc` (or set `ROSLYNMCP_WORKSPACE=adhoc`) to force AdhocWorkspace even when a `.csproj` exists. This skips MSBuild entirely for faster startup with reduced semantics (no NuGet resolution, no project references).

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

**Last Updated:** 2026-07-18 (v0.8.1-beta)
