# Troubleshooting Guide

Common issues and solutions when setting up and using RoslynMcp.

---

## Installation & Setup Issues

### "Command not found: dotnet"

**Symptom:** MCP client reports `dotnet` command not found when starting RoslynMcp.

**Cause:** .NET SDK not installed or not on PATH.

**Solution:**
1. Install .NET SDK from [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download)
2. Verify installation: `dotnet --version`
3. Restart your terminal/IDE after installation
4. Ensure .NET SDK bin directory is on your PATH

---

### "Could not execute because the specified command or file was not found"

**Symptom:** MCP client fails to start RoslynMcp.

**Cause:** The `command` path in your MCP configuration is incorrect or points to a non-existent file.

**Solution:**
1. Verify the path to `RoslynMcp.exe` is **absolute** (not relative)
2. Check the file exists at that location
3. Ensure you've published RoslynMcp first:
   ```bash
   dotnet publish src/RoslynMcp/RoslynMcp.csproj -c Release -f net10.0 -o ./publish/net10.0
   ```
4. Update your `.mcp.json` with the correct path

**Example (Windows):**
```json
{
  "command": "J:/Projects/RoslynMcp/publish/net10.0/RoslynMcp.exe"
}
```

---

### MCP server not appearing in client

**Symptom:** RoslynMcp tools don't appear in your MCP client's tool list.

**Cause:** Configuration file not in the correct location or invalid JSON.

**Solution:**
1. **Verify config file location:**
   - GitHub Copilot: `.mcp.json` in workspace root
   - Claude Desktop: `%APPDATA%\Claude\claude_desktop_config.json` (Windows) or `~/Library/Application Support/Claude/claude_desktop_config.json` (macOS)
   - See [INSTALLATION.md](../../INSTALLATION.md) for other clients

2. **Validate JSON syntax:**
   - No trailing commas
   - Proper quotes (double quotes `"`, not single `'`)
   - Use a JSON validator if unsure

3. **Restart client completely:**
   - Don't just reload window — fully quit and relaunch
   - Some clients require this to pick up config changes

4. **Check client logs:**
   - Look for error messages about MCP server startup
   - Check stderr output for RoslynMcp-specific errors

---

### MCP client doesn't see tools

**Symptom:** Server starts but `tools/list` returns empty or fails.

**Checklist:**
1. ✅ Server process started successfully (check client logs)
2. ✅ MCP session initialized (`initialize` request succeeded)
3. ✅ `projectPath` values are explicit and correct — every Roslyn tool requires one
4. ✅ RoslynMcp is built/published correctly

**Verification:**
```bash
# Test server manually
/path/to/RoslynMcp.exe .
# Should start and wait for input (stdio mode)
# Press Ctrl+C to exit
```

---

## Workspace & Type Resolution Issues

### "MSBuild not found"

**Symptom:** Error message about MSBuild not being available.

**Actual source messages:**
- `MSBuild not found. Install .NET SDK or Visual Studio Build Tools. If installed in a non-standard location, set DOTNET_ROOT or ROSLYNMCP_MSBUILD_PATH.`
- `Visual Studio MSBuild not found. Install Visual Studio or Build Tools.`

**Cause:** RoslynMcp could not resolve an MSBuild instance for the selected workspace mode.

**Solution:**
1. **Install MSBuild support** via one of:
   - .NET SDK (includes MSBuild) — recommended
   - Visual Studio or Build Tools

2. **Or set environment variables** to point at your install:
   ```bash
   # Windows
   set DOTNET_ROOT=C:\Program Files\dotnet

   # Optional direct override
   set ROSLYNMCP_MSBUILD_PATH=C:\Program Files\dotnet\sdk\<sdk-version>

   # macOS/Linux
   export DOTNET_ROOT=/usr/local/share/dotnet
   ```

3. **Choose the right mode explicitly:**
   - `--workspace sdk` for modern SDK-style projects
   - `--workspace vs` for Windows + Visual Studio MSBuild
   - `--workspace adhoc` only if you intentionally want reduced semantics

> RoslynMcp does **not** auto-fallback from a failed MSBuild initialization into a full MSBuildWorkspace equivalent. If you want source-only behavior, force `adhoc`.

---

### No type resolution (AdhocWorkspace fallback)

**Symptom:** NuGet types like `List<T>`, `HttpClient`, etc. don't resolve correctly.

**Cause:** Your `projectPath` resolved to a directory with no `.csproj`, or you explicitly selected `--workspace adhoc`, so RoslynMcp used AdhocWorkspace (source-only mode).

**Solution:**
1. **Ensure .csproj exists:**
   - Check your target directory contains a `.csproj` file
   - RoslynMcp needs the project file for full NuGet resolution

2. **Point to correct directory:**
   ```json
   {
     "args": ["src/MyApp"]  // Directory containing .csproj
   }
   ```

3. **Restore NuGet packages:**
   ```bash
   dotnet restore src/MyApp/MyApp.csproj
   # OR use the tool:
   roslyn_restore_packages --projectPath src/MyApp
   ```

**See also:** [Workspace Modes Reference](../reference/WORKSPACE_MODES.md) for details on MSBuildWorkspace vs AdhocWorkspace.

---

### "Could not find project file"

**Symptom:** One of these path-resolution errors:
- `missing_project_path`
- `No .csproj file found in or above: ...`
- `Multiple .csproj files found in ...`
- `Invalid project path '...': ...`

**Cause:** `projectPath` is missing, ambiguous, or points at the wrong place.

**Solution:**
1. **Always pass `projectPath` explicitly** — Roslyn tools require it
2. **Prefer the `.csproj` path directly** when you have one
3. **If you pass a source file path,** RoslynMcp walks upward looking for exactly one `.csproj`
4. **If you pass a directory,** RoslynMcp checks that directory for `.csproj`; no match means AdhocWorkspace
5. **Check directory structure:**
   ```bash
   ls src/MyApp/MyApp.csproj  # Should exist
   ```

---

### Stale compilation after file changes

**Symptom:** Changes to `.cs` files don't reflect in tool results.

**Cause:** FileSystemWatcher may miss rapid changes or be disabled.

**Solution:**
1. **Wait a moment** — tools automatically rebuild on next call
2. **Call any tool again** — forces recompilation
3. **Use `roslyn_build_project`** — explicitly triggers rebuild
4. **Check file watcher** — ensure no antivirus/security software blocking file system events

---

## Build & Diagnostic Issues

### NETSDK1209 warnings in output

**Symptom:** Warning about Visual Studio not supporting target .NET version.

**Example:**
```
NETSDK1209: The current Visual Studio version does not support targeting .NET 11
```

**Impact:** None — these are informational warnings only.

**Solution:**
- These warnings are automatically filtered from `roslyn_build_project` output
- No action needed — your build still works

---

### Changes not detected

**Symptom:** File changes don't trigger recompilation, diagnostics are stale.

**Cause:** File watcher disabled or not monitoring the correct directory.

**Solution:**
1. **Verify target directory:**
   - Check the `projectPath` you passed to the tool
   - Prefer the `.csproj` path directly to avoid ambiguity

2. **Manually invalidate:**
   - Make a trivial edit and save
   - Call any RoslynMcp tool — forces recompilation

3. **Check FileSystemWatcher:**
   - Antivirus software may block file system events
   - Large directory trees (>10K files) may exceed watcher limits

---

## Performance Issues

### Slow startup (>5 seconds)

**Symptom:** RoslynMcp takes a long time to start.

**Cause:** Large project with many NuGet dependencies.

**Solution:**
1. **Use MSBuildWorkspace** — caching makes subsequent calls instant
2. **Restore packages first:**
   ```bash
   dotnet restore src/MyApp/MyApp.csproj
   ```
3. **Point to subdirectory** — if you only need one project, don't point at solution root:
   ```json
   {
     "args": ["src/MyApp"]  // Not "."
   }
   ```

---

### High memory usage (>1 GB)

**Symptom:** RoslynMcp process consumes excessive memory.

**Cause:** Large solution with multiple projects loaded.

**Solution:**
1. **Target specific project** — don't load entire solution if unnecessary
2. **Use `projectPath` parameter** — prefer the exact `.csproj` you want to inspect
3. **Restart RoslynMcp** — clears workspace cache

**Expected memory usage:** highly project-dependent; multi-project MSBuild workspaces use
more memory than small Adhoc workspaces.

---

## Tool-Specific Issues

### `roslyn_build_project` stops after Roslyn errors

**Symptom:** Build tool reports:
`Roslyn reported errors — fix these first, then call roslyn_build_project again.`

**Cause:** Roslyn diagnostics detected errors, so MSBuild was skipped (fast path).

**Solution:**
1. **Fix Roslyn errors first** — use `roslyn_get_diagnostics` to see what's wrong
2. **Only use `forceBuild: true`** if you suspect an MSBuild-specific issue (restore, `.targets`, source generator crash):
   ```javascript
   roslyn_build_project({ forceBuild: true })
   ```

---

### `roslyn_preview_rename` shows no changes

**Symptom:** Rename preview returns empty diff.

**Cause:** Symbol not found or name unchanged.

**Solution:**
1. **Check symbol exists** — use `roslyn_find_references` first
2. **Verify symbol name** — case-sensitive, must match exactly
3. **Specify containing type** if ambiguous:
   ```javascript
   roslyn_preview_rename({
     symbolName: "DoWork",
     newName: "Execute",
     containingType: "MyService"
   })
   ```

---

### `roslyn_replace_in_code` fails with syntax error

**Symptom:** Semantic replacement fails with "invalid syntax" error.

**Cause:** Replacement text doesn't produce valid C# syntax.

**Solution:**
1. **Validate replacement syntax** — ensure it's valid C# for the node kind
2. **Use `roslyn_replace_in_file`** instead if you need literal text replacement
3. **Check node kind** — ensure you're matching the right syntax node type

---

## Platform-Specific Issues

### Windows: "Access denied" errors

**Symptom:** RoslynMcp can't access certain directories.

**Cause:** Insufficient permissions or system/hidden directories.

**Solution:**
1. **Run as Administrator** (not recommended)
2. **Point to user-accessible directory** — avoid `C:\Windows`, `C:\Program Files`
3. **AdhocWorkspace auto-skips** protected directories

---

### macOS/Linux: "Permission denied" on startup

**Symptom:** Can't execute `RoslynMcp.exe`.

**Cause:** Executable bit not set after build.

**Solution:**
```bash
chmod +x /path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe
```

---

### macOS: "RoslynMcp.exe cannot be opened because the developer cannot be verified"

**Symptom:** macOS Gatekeeper blocks execution.

**Solution:**
```bash
xattr -c /path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe
```

Or: **System Preferences → Security & Privacy → Allow**

---

## Getting Help

If none of the above solutions work:

1. **Check logs:**
   - Default location: your OS local-app-data folder under `RoslynMcp\logs\roslynmcp.{pid}.log`
   - Look for entries with `"level":"ERROR"` (log format is NDJSON; valid levels are START/STOP/TOOL/ERROR/INFO)

2. **Enable detailed logging:**
   - Set `ROSLYNMCP_LOG_PATH` environment variable to a custom path
   - Check for additional diagnostic information

3. **File an issue:**
   - [GitHub Issues](https://github.com/MadQ/RoslynMcp/issues)
   - Include:
     - OS and .NET version
     - MCP client (GitHub Copilot, Claude, etc.)
     - Relevant log snippets
     - Steps to reproduce

4. **Check documentation:**
   - [README.md](../../README.md) — overview and quick start
   - [INSTALLATION.md](../../INSTALLATION.md) — client-specific setup
   - [AGENTS.md](../../AGENTS.md) — technical details
   - [Workspace Modes Reference](../reference/WORKSPACE_MODES.md) — MSBuildWorkspace vs AdhocWorkspace

---

**Last Updated:** 2026-07-16 (v0.7.8-alpha)
