# RoslynMcp Installation Guide

Complete setup instructions for all major MCP-compatible AI coding assistants.

> **⚠️ Security Note:** RoslynMcp runs with your user permissions and currently has unrestricted filesystem access. Only use with trusted agents and on projects you control. See [Issue #9](https://github.com/MadQ/RoslynMcp/issues/9).

> **⚠️ Not all configurations have been verified in production.**  
> GitHub Copilot and Claude Desktop are tested and confirmed working. Other clients follow documented MCP patterns but may require adjustments. Contributions and corrections welcome!

> **Also see:** [README.md](README.md) for the tool catalog, agent instructions, and architecture details.

> All `roslyn_*` tools support multi-project workflows via the required `projectPath` parameter. Individual tool calls can target different projects without restarting the server.

---

## Quick Start

1. **Get the binary:** Either download the latest `net10.0` release zip from the [Releases page](https://github.com/MadQ/RoslynMcp/releases/latest) and extract it anywhere, **or** install via dotnet tool:

   ```bash
   dotnet tool install -g MadQ.RoslynMcp --prerelease
   ```

2. **Add to your client config.** Most clients take a JSON block like this (the outer key name varies — `"mcpServers"` for Claude, `"servers"` for Copilot, etc.):

   ```json
   {
     "MadQ.RoslynMcp": {
       "type": "stdio",
       "command": "madq-roslynmcp"
     }
   }
   ```

   Or with an absolute path if you downloaded the zip:

   ```json
   {
     "MadQ.RoslynMcp": {
       "type": "stdio",
       "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
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

Download the latest release from the [Releases page](https://github.com/MadQ/RoslynMcp/releases/latest) — grab the `net10.0` asset. Extract it anywhere and note the full path to `RoslynMcp.exe`.

**Option B — dotnet tool** (recommended for .NET developers, requires .NET SDK):

```bash
dotnet tool install -g MadQ.RoslynMcp --prerelease
```

This installs `madq-roslynmcp` globally on PATH. Use `"madq-roslynmcp"` as the command in your client config — no path needed.

**Option C — Clone and build** (requires .NET 10 or 11 SDK):

```bash
git clone https://github.com/MadQ/RoslynMcp.git
cd RoslynMcp
dotnet publish src/RoslynMcp/RoslynMcp.csproj -c Release -f net10.0 -o ./publish/net10.0
```

**Choose your framework:**
- `net10.0` — .NET 10 (recommended)
- `net11.0` — .NET 11 (auto-added when .NET 11 SDK is detected)

### Step 2: Configure Your MCP Client

Point your MCP client to the server using one of these approaches:

**Option A / C — absolute path** (download or clone/build):

```json
{
  "MadQ.RoslynMcp": {
    "type": "stdio",
    "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
  }
}
```

**Option B — dotnet tool (global install):**

```json
{
  "MadQ.RoslynMcp": {
    "type": "stdio",
    "command": "madq-roslynmcp"
  }
}
```

**Local tool install (advanced — per-project version pinning):**

```bash
dotnet tool install --create-manifest-if-needed MadQ.RoslynMcp --prerelease
```

Local tools require `dotnet tool run` as the invocation, and your client config must set `cwd` to the project root so the tool manifest is found:

```json
{
  "MadQ.RoslynMcp": {
    "type": "stdio",
    "command": "dotnet",
    "args": ["tool", "run", "madq-roslynmcp"],
    "cwd": "/absolute/path/to/your/project"
  }
}
```

> Not all MCP clients support `cwd`. Global install (`-g`) is recommended for most users.

Do not pass project paths as args — each `roslyn_*` tool call specifies `projectPath` directly. Client-specific examples below.

---

## Requirements

- Running the published binary or release zip does not require a .NET SDK on PATH
- Building from source requires a .NET 10 SDK (`net11.0` is auto-added when a .NET 11 SDK is present)
- MSBuild on PATH (installed with .NET SDK or Visual Studio) for full project resolution

---

## GitHub Copilot (Visual Studio / VS Code / CLI)

Add to `.mcp.json` at your workspace root:

```json
{
  "servers": {
    "MadQ.RoslynMcp": {
      "type": "stdio",
      "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
    }
  }
}
```

**Global config alternative:** Add the same `"servers"` block to `~/.copilot/mcp-config.json` (`%USERPROFILE%\.copilot\mcp-config.json` on Windows) to make the server available across all projects.

**Restart:** Reload window or restart GitHub Copilot extension after editing `.mcp.json`.

---

## Claude Code

**Project config** — create `.mcp.json` in your project root:

```json
{
  "mcpServers": {
    "MadQ.RoslynMcp": {
      "type": "stdio",
      "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
    }
  }
}
```

**Global config** — add the same block to `~/.claude.json` (`%USERPROFILE%\.claude.json` on Windows) to make the server available across all projects. Note: do **not** put MCP config in `~/.claude/settings.json` — it is silently ignored there.

> **Note:** Project config (`.mcp.json`) takes precedence over global (`~/.claude.json`).

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
    "MadQ.RoslynMcp": {
      "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
    }
  }
}
```

**Restart:** Quit and relaunch Claude Desktop.

---

## Cursor

**Option 1: Workspace config** (recommended)

Add to `.cursor/mcp.json` in your project root:

```json
{
  "mcpServers": {
    "MadQ.RoslynMcp": {
      "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
    }
  }
}
```

**Option 2: Global config**

1. Open Cursor Settings: **File → Preferences → Cursor Settings**
2. Navigate to **Tools & Integrations**
3. In **MCP Tools** section, select **New MCP Server**
4. Add the configuration to the `mcp.json` file that opens

> `${workspaceFolder}` auto-resolves to the current workspace directory.

**Restart:** Reload window (Cmd/Ctrl+Shift+P → "Developer: Reload Window").

---

## Windsurf

**Option 1: Workspace config** (recommended)

Add to `.windsurf/mcp_config.json` in your project root:

```json
{
  "mcpServers": {
    "MadQ.RoslynMcp": {
      "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
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
  "MadQ.RoslynMcp": {
    "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
  }
}
```

**Option 2: Workspace config**

Add to `.vscode/mcp.json` or `.cline/mcp_settings.json` in your project root (exact filename depends on Cline version):

```json
{
  "mcpServers": {
    "MadQ.RoslynMcp": {
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
        "name": "MadQ.RoslynMcp",
        "transport": {
          "type": "stdio",
          "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
        }
      }
    ]
  }
}
```

**Restart:** Reload window or restart Continue extension.

---

## Roo Code (VS Code)

Roo Code uses VS Code's standard MCP configuration.

Add to `.vscode/mcp.json` in your project root:

```json
{
  "servers": {
    "MadQ.RoslynMcp": {
      "type": "stdio",
      "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
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
    "MadQ.RoslynMcp": {
      "settings": {
        "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe"
      }
    }
  }
}
```

**Config file location:**
- **macOS/Linux**: `~/.config/zed/settings.json`
- **Windows**: `%APPDATA%\Zed\settings.json`

**Restart:** Quit and relaunch Zed.

---

## Direct CLI Usage

Run RoslynMcp directly from the command line:

```bash
# Minimal — no preloading
/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe

# Preload a workspace for faster first tool call
/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe --preload /path/to/your/project

# Force a specific workspace mode
/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe --workspace adhoc --preload /path/to/your/project
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

**Fix:** Install .NET SDK or Visual Studio — both include MSBuild. If the SDK is in a non-standard location, set `ROSLYNMCP_MSBUILD_PATH` (or `DOTNET_ROOT`). RoslynMcp automatically falls back to AdhocWorkspace (source-only) if MSBuild isn't available.

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

### Tools re-prompt for approval after renaming the server key

**Symptom:** Previously "always allowed" write/build/rename tools ask for permission again after you change the MCP server key (e.g. upgrading from the legacy `roslyn` key to `MadQ.RoslynMcp`).

**Cause:** MCP clients cache tool approvals keyed to the server name, so the old approvals no longer match.

**Fix:** Either re-approve ("always allow") on the next prompt, or tell your agent to migrate the stale approvals to the new server name. See [Troubleshooting Guide → Tools re-prompt for approval after renaming the MCP server key](docs/guides/TROUBLESHOOTING.md#tools-re-prompt-for-approval-after-renaming-the-mcp-server-key) for the full procedure (including a copy-paste instruction for your agent).

---

## Advanced Configuration

### CLI flags

```bash
RoslynMcp.exe [options]
```

| Argument | Description |
|----------|-------------|
| `-p`, `--preload <path>` | Pre-warm a workspace on startup. Repeat the flag to preload multiple projects. Each tool call still requires a `projectPath` parameter regardless. |
| `--workspace` | Override workspace mode: `auto` (default), `sdk`, `vs`, `adhoc`. See [Workspace Modes Reference](docs/reference/WORKSPACE_MODES.md). |
| `--log-path` | Override the log file base path. Pass an empty string to disable logging. |
| `--msbuild-path` | Override the MSBuild installation path used for workspace loading. |
| `--elicit` | On an ambiguous symbol match, ask the user to pick interactively (MCP elicitation) instead of returning a structured candidate list. Opt-in; requires client elicitation support. Default: off. |
| `-v`, `--version` | Print the server version and exit. |
| `-h`, `--help` | Show CLI help and exit. |

### Environment variables

| Variable | Default | Description |
|----------|---------|-------------|
| `ROSLYNMCP_WORKSPACE` | `auto` | Same as `--workspace` flag — `sdk`, `vs`, `adhoc`, or `auto` |
| `ROSLYNMCP_LOG_PATH` | `%LOCALAPPDATA%\RoslynMcp\logs\roslynmcp.{pid}.log` | Log file base path. PID is always injected before the extension. Set to empty string to disable logging. |
| `ROSLYNMCP_BACKUP_PATH` | `%LOCALAPPDATA%\RoslynMcp\backups` | Backup store root for `roslyn_write_file` / `roslyn_local_history`. Set to empty to disable backups. |
| `ROSLYNMCP_LOG_MAX_AGE_DAYS` | `30` | Delete old per-PID log files after this many days. |
| `ROSLYNMCP_BACKUP_MAX_AGE_DAYS` | `90` | Delete old backup snapshots after this many days. |
| `ROSLYNMCP_PRUNE_MIN_RUNS` | `3` | Minimum server starts before log/backup pruning runs. |
| `ROSLYNMCP_MAX_CACHED_WORKSPACES` | `5` | LRU workspace cache size. Increase for large multi-project workflows. |
| `ROSLYNMCP_MSBUILD_PATH` | *(auto-detected)* | Force a specific MSBuild installation path. |
| `ROSLYNMCP_DISABLE_PATH_CACHE` | `false` | Set to `true` to disable the path resolution cache (useful for debugging workspace issues). |
| `ROSLYNMCP_ELICIT` | `false` | Set to `true` to enable interactive elicitation on ambiguous symbol matches (same as `--elicit`). Not all MCP clients support elicitation; unsupported clients fall back to the structured candidate list. |

**Ambiguous symbol handling.** By default, when a name in `roslyn_preview_rename` or `roslyn_change_signature` matches multiple symbols, the tool returns a structured `candidates` list and the agent retries with a `containingType` (or `filePath`+`line`) on its own — no interruption. Enabling `--elicit` (or `ROSLYNMCP_ELICIT=true`) instead prompts you to pick interactively via MCP elicitation, in clients that support it (Claude Code/Desktop do; many others don't and fall back to the candidate list). The `madq-roslynmcp setup` wizard offers this as an opt-in prompt; you can also add it by hand to the server entry's args: `"args": ["--elicit"]`.

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
