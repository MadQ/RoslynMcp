# Installation Guide

# RoslynMcp Installation Guide

Complete setup instructions for all major MCP-compatible AI coding assistants.

> **⚠️ Not all configurations have been verified in production.**  
> GitHub Copilot and Claude Desktop are tested and confirmed working. Other clients follow documented MCP patterns but may require adjustments. Contributions and corrections welcome!

> **Configuration reference:** See [Configuration section in README.md](README.md#configuration) for detailed examples and troubleshooting.

> **Quick start:** Most clients use one of two patterns:
> - **Workspace config**: `.mcp.json` or similar file in your project root
> - **Global config**: Client-specific settings file in your home directory

---

## Installation

### Step 1: Build RoslynMcp

```bash
git clone https://github.com/MadQ/RoslynMcp.git
cd RoslynMcp
dotnet publish src/RoslynMcp/RoslynMcp.csproj -c Release -f net10.0 -o ./publish/net10.0
```

**Choose your framework:**
- `net8.0` — .NET 8 (LTS)
- `net10.0` — .NET 10 (recommended)
- `net11.0` — .NET 11 (preview, if SDK installed locally)

### Step 2: Configure Your MCP Client

Point your MCP client to the published executable. Examples for each client below.

**Published NuGet tool (coming soon):**
```bash
dotnet tool install --global RoslynMcp
# Then use: "command": "roslyn-mcp"
```

---

## Requirements

- .NET 8, 10, or 11 SDK
- MSBuild on PATH (installed with .NET SDK or Visual Studio) for full project resolution

---

## GitHub Copilot (Visual Studio / VS Code)

Add to `.mcp.json` at your workspace root:

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

**Notes:**
- Use absolute path to `RoslynMcp.exe`
- `.` means analyze the current workspace root
- Or specify absolute path to a different project

**Restart:** Reload window or restart GitHub Copilot extension after editing `.mcp.json`.

---

## Claude Desktop

Add to your Claude Desktop MCP settings file:

| Platform | Path |
|----------|------|
| **Windows** | `%APPDATA%\Claude\claude_desktop_config.json` |
| **macOS** | `~/Library/Application Support/Claude/claude_desktop_config.json` |
| **Linux** | `~/.config/Claude/claude_desktop_config.json` |

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "dotnet",
      "args": ["run", "--no-build", "--project", "/absolute/path/to/RoslynMcp/RoslynMcp.csproj", "--", "/absolute/path/to/your/project/src"]
    }
  }
}
```

**Path notes:**
- Use **absolute paths** for both RoslynMcp project and target source directory
- Replace `/absolute/path/to/RoslynMcp/RoslynMcp.csproj` with the full path to the `.csproj` file
- Replace `/absolute/path/to/your/project/src` with the directory you want to analyze

**Restart:** Quit and relaunch Claude Desktop.

---

## Cursor

**Option 1: Workspace config** (recommended)

Add to `.cursor/mcp.json` in your project root:

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "dotnet",
      "args": ["run", "--no-build", "--project", "/absolute/path/to/RoslynMcp/RoslynMcp.csproj", "--", "${workspaceFolder}"]
    }
  }
}
```

**Option 2: Global config**

1. Open Cursor Settings: **File → Preferences → Cursor Settings**
2. Navigate to **Tools & Integrations**
3. In **MCP Tools** section, select **New MCP Server**
4. Add the configuration to the `mcp.json` file that opens

**Path notes:**
- `${workspaceFolder}` auto-resolves to current workspace directory
- Use absolute path to `RoslynMcp.csproj`

**Restart:** Reload window (Cmd/Ctrl+Shift+P → "Developer: Reload Window").

---

## Windsurf

**Option 1: Workspace config** (recommended)

Add to `.windsurf/mcp_config.json` in your project root:

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "dotnet",
      "args": ["run", "--no-build", "--project", "/absolute/path/to/RoslynMcp/RoslynMcp.csproj", "--", "${workspaceFolder}"]
    }
  }
}
```

**Option 2: Global config**

1. Open Windsurf Settings: **File → Preferences → Windsurf Settings**
2. Select **Manage MCPs**
3. Select **View raw config** to edit `mcp_config.json`
4. Add the configuration

**Global config path:**
- **Windows**: `%USERPROFILE%\.codeium\windsurf\mcp_config.json`
- **macOS/Linux**: `~/.codeium/windsurf/mcp_config.json`

**Restart:** Reload window or restart Windsurf.

---

## Cline (VS Code)

**Option 1: Extension settings** (recommended)

1. Open VS Code Settings: **Settings → Extensions → Cline → MCP Servers**
2. Edit the JSON configuration directly
3. Add:

```json
{
  "roslyn": {
    "command": "dotnet",
    "args": ["run", "--no-build", "--project", "/absolute/path/to/RoslynMcp/RoslynMcp.csproj", "--", "${workspaceFolder}"]
  }
}
```

**Option 2: Workspace config**

Add to `.vscode/mcp.json` or `.cline/mcp_settings.json` in your project root (exact filename depends on Cline version):

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "dotnet",
      "args": ["run", "--no-build", "--project", "/absolute/path/to/RoslynMcp/RoslynMcp.csproj", "--", "${workspaceFolder}"]
    }
  }
}
```

**Restart:** Reload VS Code window.

---

## Continue (VS Code / JetBrains)

Add to `.continue/config.json` in your project root:

```json
{
  "experimental": {
    "modelContextProtocolServers": [
      {
        "name": "roslyn",
        "transport": {
          "type": "stdio",
          "command": "dotnet",
          "args": ["run", "--no-build", "--project", "/absolute/path/to/RoslynMcp/RoslynMcp.csproj", "--", "${workspaceFolder}"]
        }
      }
    ]
  }
}
```

**Path notes:**
- Continue supports `${workspaceFolder}` variable
- Use absolute path to `RoslynMcp.csproj`

**Restart:** Reload window or restart Continue extension.

---

## Roo Code (VS Code)

Roo Code uses VS Code's standard MCP configuration.

Add to `.vscode/mcp.json` in your project root:

```json
{
  "servers": {
    "roslyn": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--no-build", "--project", "/absolute/path/to/RoslynMcp/RoslynMcp.csproj", "--", "${workspaceFolder}"]
    }
  }
}
```

**Restart:** Reload VS Code window.

---

## Zed

Add to `~/.config/zed/settings.json`:

```json
{
  "context_servers": {
    "roslyn": {
      "settings": {
        "command": "dotnet",
        "args": ["run", "--no-build", "--project", "/absolute/path/to/RoslynMcp/RoslynMcp.csproj", "--", "/absolute/path/to/your/project/src"]
      }
    }
  }
}
```

**Path notes:**
- Zed uses absolute paths (no variable expansion)
- Config file location:
  - **macOS/Linux**: `~/.config/zed/settings.json`
  - **Windows**: `%APPDATA%\Zed\settings.json`

**Restart:** Quit and relaunch Zed.

---



Run RoslynMcp directly from the command line:

```bash
/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe path/to/your/src
```

This starts the MCP server on stdio — useful for testing or custom integrations.

---

## Troubleshooting

### "Command not found: dotnet"

**Cause:** .NET SDK not installed or not on PATH.

**Fix:** Install .NET SDK from [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download) and ensure it's on your PATH.

### "MSBuild not found"

**Cause:** MSBuildWorkspace mode requires MSBuild on PATH.

**Fix:**
- Install Visual Studio (includes MSBuild)
- Or install .NET SDK (includes MSBuild)
- Or set `MSBUILD_EXE_PATH` environment variable

RoslynMcp automatically falls back to AdhocWorkspace (source-only mode) if MSBuild isn't available.

### "Could not find project file"

**Cause:** Path to `RoslynMcp.csproj` is incorrect.

**Fix:** Use absolute paths in global configs, verify relative paths from workspace root in workspace configs.

### MCP server not appearing in client

**Cause:** Configuration file not in the correct location or invalid JSON.

**Fix:**
- Verify config file path matches client documentation above
- Validate JSON syntax (no trailing commas, proper quotes)
- Restart client application completely (not just reload window)

### Changes not detected

**Cause:** File watcher disabled or not monitoring the correct directory.

**Fix:** RoslynMcp auto-detects file changes. If diagnostics aren't updating, verify the last argument points to your source directory.

---

## Advanced Configuration

### Multi-project workspaces

RoslynMcp can analyze multiple projects if they're part of a `.sln` file or linked via `<ProjectReference>`. Point the last argument at the solution directory or the primary project directory.

### Custom compilation options

RoslynMcp uses project-defined compilation options (language version, preprocessor symbols) when using MSBuildWorkspace. In AdhocWorkspace mode, it defaults to C# preview with `DEBUG` defined.

### Performance tuning

For large codebases (>100K LOC), consider:
- Using MSBuildWorkspace (incremental compilation)
- Excluding test projects if not needed
- Pointing to a specific subdirectory instead of solution root

---

## Next Steps

- Try the [tools reference](README.md#tools) to see what RoslynMcp can do
- Read about [workspace modes](README.md#workspace-modes) to understand MSBuildWorkspace vs AdhocWorkspace
- Check out the [write operations](README.md#write-operations) for rename previewing and applying

---

## Troubleshooting

### Server fails to start

**Error:** `Your project targets multiple frameworks. Specify which framework to run using '--framework'.`

**Solution:** Add `-f net10.0` (or net8.0/net11.0) to the args:
``json
`args`: [`run`, `--no-build`, `--project`, `path/to/RoslynMcp.csproj`, `-f`, `net10.0`, `--`, `.]`
````n
### No type resolution (AdhocWorkspace fallback)

**Symptom:** NuGet types (`List<T>`, `HttpClient`) not resolved.

**Cause:** No `.csproj` file in target directory.

**Solution:** Ensure RoslynMcp is pointed at a directory containing a `.csproj` file for full MSBuildWorkspace support.

### Stale compilation after file changes

**Cause:** FileSystemWatcher may miss rapid changes.

**Solution:** Tools automatically rebuild on next call. For immediate refresh, call any tool again.

### NETSDK1209 warnings in output

**Cause:** Targeting .NET 11 with older Visual Studio version.

**Impact:** None — these warnings are filtered from `build_project` output automatically.

### MCP client doesn't see tools

**Checklist:**
1. Server process started successfully (check client logs)
2. MCP session initialized (`tools/list` should return 18 tools)
3. Target directory is correct (check server stderr for `Target: ...`)
4. Rebuild RoslynMcp if code changed: `dotnet build RoslynMcp/RoslynMcp.csproj`

---

## Support

- **Documentation:** [README.md](README.md), [AGENTS.md](AGENTS.md)
- **Issues:** [GitHub Issues](https://github.com/MadQ/RoslynMcp/issues)
- **Contributing:** [CONTRIBUTING.md](CONTRIBUTING.md)

