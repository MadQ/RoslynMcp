# RoslynMcp Installation Guide

Complete setup instructions for all major MCP-compatible AI coding assistants.

> **⚠️ Security Note:** RoslynMcp runs with your user permissions and currently has unrestricted filesystem access. Only use with trusted agents and on projects you control. See [Issue #9](https://github.com/MadQ/RoslynMcp/issues/9).

> **⚠️ Not all configurations have been verified in production.**  
> GitHub Copilot and Claude Desktop are tested and confirmed working. Other clients follow documented MCP patterns but may require adjustments. Contributions and corrections welcome!

> **Also see:** [README.md](README.md) for the tool catalog, agent instructions, and architecture details.

> All `roslyn_*` tools support multi-project workflows via the required `projectPath` parameter. Individual tool calls can target different projects without restarting the server.

---

## Quick Start

1. **Get the binary:** Download [RoslynMcp-vX.Y.Z-net10.0.zip](https://github.com/MadQ/RoslynMcp/releases/latest) and extract it anywhere.

2. **Add to your client config.** Most clients take a JSON block like this (the outer key name varies — `"mcpServers"` for Claude, `"servers"` for Copilot, etc.):

   ```json
   {
     "roslyn": {
       "type": "stdio",
       "command": "/absolute/path/to/RoslynMcp.exe"
     }
   }
   ```

3. **Restart your client.** That's it.

> Each `roslyn_*` tool call specifies `projectPath` directly — do NOT pass project paths as `args`.

See the client sections below for exact config file locations and JSON structure.

---

## Full Installation

### Step 1: Get RoslynMcp

**Option A — Download and extract** (simplest, no SDK required):

Download the latest release from the [Releases page](https://github.com/MadQ/RoslynMcp/releases/latest) — grab `RoslynMcp-vX.Y.Z-net10.0.zip` (or `net8.0`). Extract it anywhere and note the full path to `RoslynMcp.exe`.

**Option B — Clone and build** (requires .NET 8, 10, or 11 SDK):

```bash
git clone https://github.com/MadQ/RoslynMcp.git
cd RoslynMcp
dotnet publish src/RoslynMcp/RoslynMcp.csproj -c Release -f net10.0 -o ./publish/net10.0
```

**Choose your framework:**
- `net8.0` — .NET 8 (LTS)
- `net10.0` — .NET 10 (recommended)
- `net11.0` — .NET 11 (auto-added when .NET 11 SDK is detected)

### Step 2: Configure Your MCP Client

Point your MCP client to the published executable. Examples for each client below.

**Published NuGet tool (coming soon):**
```bash
dotnet tool install --global RoslynMcp
# Then use: "command": "roslyn-mcp"
```

---

## Requirements

- .NET 8 or .NET 10 SDK (net11.0 target added automatically if .NET 11 SDK is present)
- MSBuild on PATH (installed with .NET SDK or Visual Studio) for full project resolution

---

## GitHub Copilot (Visual Studio / VS Code / CLI)

Add to `.mcp.json` at your workspace root:

```json
{
  "servers": {
    "roslyn": {
      "type": "stdio",
      "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
    }
  }
}
```

**Notes:**
- Use absolute path to `RoslynMcp.exe`
- Do NOT pass project paths as `args` — the agent must specify `projectPath` parameter in each tool invocation

**Global config alternative:** Add the same `"servers"` block to `~/.copilot/mcp-config.json` (`%USERPROFILE%\.copilot\mcp-config.json` on Windows) to make the server available across all projects.

**Restart:** Reload window or restart GitHub Copilot extension after editing `.mcp.json`.

---

## Claude Code

**Project config** — create `.mcp.json` in your project root:

```json
{
  "mcpServers": {
    "roslyn": {
      "type": "stdio",
      "command": "/absolute/path/to/RoslynMcp.exe"
    }
  }
}
```

**Global config** — add the same block to `~/.claude.json` (`%USERPROFILE%\.claude.json` on Windows) to make the server available across all projects. Note: do **not** put MCP config in `~/.claude/settings.json` — it is silently ignored there.

**Notes:**
- Use absolute path to `RoslynMcp.exe`
- Do NOT pass project paths as `args` — the agent must specify `projectPath` parameter in each tool invocation
- Project config (`.mcp.json`) takes precedence over global (`~/.claude.json`)

**Restart:** Restart the Claude Code session after editing either config file.

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
      "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
    }
  }
}
```

**Path notes:**
- Use **absolute path** for the executable
- Replace `/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe` with the full path to the published executable
- Do NOT pass project paths as `args` — the agent must specify `projectPath` parameter in each tool invocation

**Restart:** Quit and relaunch Claude Desktop.

---

## Cursor

**Option 1: Workspace config** (recommended)

Add to `.cursor/mcp.json` in your project root:

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
    }
  }
}
```

**Note:** Do NOT pass project paths as command-line `args`. The agent must specify `projectPath` parameter in each tool invocation.

**Option 2: Global config**

1. Open Cursor Settings: **File → Preferences → Cursor Settings**
2. Navigate to **Tools & Integrations**
3. In **MCP Tools** section, select **New MCP Server**
4. Add the configuration to the `mcp.json` file that opens

**Path notes:**
- `${workspaceFolder}` auto-resolves to current workspace directory
- Use absolute path to the published `RoslynMcp.exe` executable

**Restart:** Reload window (Cmd/Ctrl+Shift+P → "Developer: Reload Window").

---

## Windsurf

**Option 1: Workspace config** (recommended)

Add to `.windsurf/mcp_config.json` in your project root:

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
    }
  }
}
```

**Note:** Do NOT pass project paths as command-line `args`. The agent must specify `projectPath` parameter in each tool invocation.

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
    "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
  }
}
```

**Note:** Do NOT pass project paths as command-line `args`. The agent must specify `projectPath` parameter in each tool invocation.

**Option 2: Workspace config**

Add to `.vscode/mcp.json` or `.cline/mcp_settings.json` in your project root (exact filename depends on Cline version):

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
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
          "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
        }
      }
    ]
  }
}
```

**Notes:**
- Use absolute path to `RoslynMcp.exe`
- Do NOT pass project paths as `args` — the agent must specify `projectPath` in each tool invocation

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
      "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
    }
  }
}
```

**Notes:**
- Use absolute path to `RoslynMcp.exe`
- Do NOT pass project paths as `args` — the agent must specify `projectPath` in each tool invocation

**Restart:** Reload VS Code window.

---

## Zed

Add to `~/.config/zed/settings.json`:

```json
{
  "context_servers": {
    "roslyn": {
      "settings": {
        "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
      }
    }
  }
}
```

**Path notes:**
- Use absolute path to `RoslynMcp.exe`
- Do NOT pass project paths as `args` — the agent must specify `projectPath` in each tool invocation
- Config file location:
  - **macOS/Linux**: `~/.config/zed/settings.json`
  - **Windows**: `%APPDATA%\Zed\settings.json`

**Restart:** Quit and relaunch Zed.

---

## Direct CLI Usage

Run RoslynMcp directly from the command line:

```bash
# Minimal — no preloading
/path/to/RoslynMcp.exe

# Preload a workspace for faster first tool call
/path/to/RoslynMcp.exe /path/to/your/project

# Force a specific workspace mode
/path/to/RoslynMcp.exe --workspace adhoc /path/to/your/project
```

This starts the MCP server on stdio — useful for testing or custom integrations. See [CLI flags](#cli-flags) for all options.

---

## Troubleshooting

See **[Troubleshooting Guide](docs/guides/TROUBLESHOOTING.md)** for comprehensive solutions.

### Server fails to start

**Error:** `Could not execute because the specified command or file was not found.`

**Fix:** Verify `command` in your config points to the published `RoslynMcp.exe`. Use an absolute path. Ensure you've built or extracted the release first.

### MSBuild not found

**Cause:** MSBuildWorkspace requires MSBuild on PATH.

**Fix:** Install .NET SDK or Visual Studio — both include MSBuild. Or set `MSBUILD_EXE_PATH`. RoslynMcp automatically falls back to AdhocWorkspace (source-only) if MSBuild isn't available.

### No type resolution (AdhocWorkspace fallback)

**Symptom:** NuGet types (`List<T>`, `HttpClient`) not resolved.

**Cause:** No `.csproj` in the target directory.

**Fix:** Ensure `projectPath` points to a directory containing a `.csproj`. See [Workspace Modes Reference](docs/reference/WORKSPACE_MODES.md).

### MCP client doesn't see tools

**Checklist:**
1. Server process started (check client logs)
2. MCP session initialized — `tools/list` returns the full `roslyn_*` tool set
3. Config file is valid JSON and in the correct location for your client
4. Client restarted completely after editing config (not just "reload window" in some clients)

### Changes not detected

**Fix:** RoslynMcp auto-detects file changes via `FileSystemWatcher`. If diagnostics aren't updating, verify `projectPath` points to your source directory.

---

## Advanced Configuration

### CLI flags

```bash
RoslynMcp.exe [path...] [--workspace sdk|vs|adhoc|auto]
```

| Argument | Description |
|----------|-------------|
| `path...` | One or more paths to preload on startup (optional). Useful for reducing first-call latency. Each tool call still requires a `projectPath` parameter regardless. |
| `--workspace` | Override workspace mode: `auto` (default), `sdk`, `vs`, `adhoc`. See [Workspace Modes Reference](docs/reference/WORKSPACE_MODES.md). |

### Environment variables

| Variable | Default | Description |
|----------|---------|-------------|
| `ROSLYNMCP_WORKSPACE` | `auto` | Same as `--workspace` flag — `sdk`, `vs`, `adhoc`, or `auto` |
| `ROSLYNMCP_LOG_PATH` | `%LOCALAPPDATA%\RoslynMcp\logs\roslynmcp.log` | Log file path. Set to empty string to disable logging. |
| `ROSLYNMCP_BACKUP_PATH` | `%LOCALAPPDATA%\RoslynMcp\backups` | Backup store root for `roslyn_write_file` / `roslyn_local_history`. Set to empty to disable backups. |
| `ROSLYNMCP_MAX_CACHED_WORKSPACES` | `5` | LRU workspace cache size. Increase for large multi-project workflows. |
| `ROSLYNMCP_MSBUILD_PATH` | *(auto-detected)* | Force a specific MSBuild installation path. |
| `ROSLYNMCP_DISABLE_PATH_CACHE` | `false` | Set to `true` to disable the path resolution cache (useful for debugging workspace issues). |

### Multi-project workspaces

All `roslyn_*` tools require a `projectPath` parameter, enabling multi-project workflows without restarting the server.

RoslynMcp can analyze multiple projects if they're part of a `.sln` file or linked via `<ProjectReference>`. Point the command-line argument at the solution directory or primary project directory for pre-loading.

**See [Workspace Modes Reference](docs/reference/WORKSPACE_MODES.md) for details on MSBuildWorkspace vs AdhocWorkspace.**

### Custom compilation options

RoslynMcp uses project-defined settings when using MSBuildWorkspace. In AdhocWorkspace mode, defaults to C# preview with `DEBUG` defined.

**See [Workspace Modes Reference](docs/reference/WORKSPACE_MODES.md) for mode details and tradeoffs.**

### Performance tuning

For large codebases (>100K LOC), consider:
- Using MSBuildWorkspace (incremental compilation)
- Excluding test projects if not needed
- Pointing to a specific subdirectory instead of solution root

---

## Next Steps

- **Tell your agent to use RoslynMcp** — agents default to file reads and grep. Add a few lines to your `CLAUDE.md`, `AGENTS.md`, or `.github/copilot-instructions.md`. See [Agent Instructions](README.md#agent-instructions) in README.md for ready-to-paste snippets, or [docs/AGENT-INSTRUCTIONS.md](docs/AGENT-INSTRUCTIONS.md) for the full version.
- Try the [tool catalog](README.md#tool-catalog) to see what RoslynMcp can do
- Read the [Workspace Modes Reference](docs/reference/WORKSPACE_MODES.md) for MSBuildWorkspace vs AdhocWorkspace details
- Review the [refactoring tools](README.md#refactoring) for semantic rename and signature change

---

## Support

- **Documentation:** [README.md](README.md), [AGENTS.md](AGENTS.md)
- **Issues:** [GitHub Issues](https://github.com/MadQ/RoslynMcp/issues)
- **Contributing:** [CONTRIBUTING.md](CONTRIBUTING.md)

