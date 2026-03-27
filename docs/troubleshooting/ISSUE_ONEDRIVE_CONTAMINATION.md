# RoslynMcp Tools - Workspace Resolution Catastrophic Failure

## ⚠️ CRITICAL SEVERITY ⚠️

RoslynMcp is being started from **DRIVE ROOTS** and **SYSTEM DIRECTORIES**, causing it to enumerate entire drives and access protected system paths.

## Summary

RoslynMcp server is intermittently started with incorrect working directories (CWD), including:
- **Drive roots** (`J:\`, `C:\`)
- **User home directories** (`C:\Users\madq4`)
- **Correct project directory** (`J:\Projects\RoslynMcp`) ← only sometimes!

When CWD is wrong, Roslyn/MSBuild workspace enumeration attempts to:
- Scan entire drives for `.csproj` files
- Access system directories (`System Volume Information`, `Temporary Internet Files`)
- Load unrelated projects from OneDrive, other repositories, etc.

**This is a CRITICAL security and stability issue.**

## Evidence

### Log Entries

**CATASTROPHIC: Server started from drive root**
```
[2026-03-25 10:41:42.318Z] [START ] pid=18904 cwd="J:\" log="..."
[2026-03-25 10:41:43.050Z] [ERROR ] TryGetCompilation — UnauthorizedAccessException: Access to the path 'J:\System Volume Information' is denied.
[2026-03-25 10:41:43.051Z] [TOOL  ] roslyn_list_types(RoslynMcp.Tools) 3ms OK   
```

**CRITICAL: Server started from user home directory**
```
[2026-03-25 09:55:45.896Z] [START ] pid=18620 cwd="C:\Users\madq4" log="..."
[2026-03-25 09:55:47.042Z] [ERROR ] TryGetProject — UnauthorizedAccessException: Access to the path 'C:\Users\madq4\Local Settings\Temporary Internet Files\Content.IE5' is denied.
[2026-03-25 09:55:47.043Z] [TOOL  ] roslyn_get_project_info 998ms OK   
```

**OneDrive contamination**
```
[2026-03-25 23:18:32.144Z] [ERROR ] TryGetProject — IOException: The cloud file provider is not running. : 'C:\Users\madq4\OneDrive\Desktop\panimB\MainForm.Designer.cs'.
[2026-03-25 23:18:32.144Z] [TOOL  ] roslyn_get_project_info 2887ms OK   
```

**CORRECT: Server started from project directory (rare!)**
```
[2026-03-26 00:37:33.287Z] [START ] pid=8040 cwd="J:\Projects\RoslynMcp" log="..."
[no errors in subsequent tool calls]
```

### Key Observations

1. **Server CWD is catastrophically wrong:**
   - `cwd="J:\"` — DRIVE ROOT (worst case!)
   - `cwd="C:\Users\madq4"` — User home directory
   - `cwd="J:\Projects\RoslynMcp"` — Correct (only sometimes!)

2. **System directory access attempts:**
   - `J:\System Volume Information` — Windows system folder (UnauthorizedAccessException)
   - `C:\Users\madq4\Local Settings\Temporary Internet Files\Content.IE5` — IE cache (UnauthorizedAccessException)

3. **Unrelated project contamination:**
   - `C:\Users\madq4\OneDrive\Desktop\panimB` — Completely separate project
   - Not in RoslynMcp solution, no relationship to current work

4. **Tools succeed anyway:**
   - Despite catastrophic errors, tools return `OK` status
   - Indicates fallback logic masks the severity

5. **Pattern: Wrong CWD → directory enumeration explosion:**
   - When CWD is `J:\`, AdhocWorkspace tries to enumerate entire drive
   - FileSystemWatcher with `IncludeSubdirectories = true` compounds the issue
   - MSBuildLocator or Roslyn discovery scans for `.csproj` files

## Root Cause Analysis

### Hypothesis 1: Wrong Working Directory (PRIMARY)

**Evidence:**
```
[2026-03-25 23:14:12.900Z] [START ] pid=22648 cwd="C:\Users\madq4" log="..."
[2026-03-26 00:37:33.287Z] [START ] pid=8040 cwd="J:\Projects\RoslynMcp" log="..."
[2026-03-26 20:43:21.094Z] [START ] pid=29844 cwd="C:\Users\madq4" log="..."
```

**Analysis:**
- Server is sometimes started from `C:\Users\madq4` (home directory) instead of `J:\Projects\RoslynMcp`
- When CWD is home directory, Roslyn workspace enumeration may traverse user directories
- MSBuildLocator or Roslyn discovery could be finding `.csproj` files in unexpected locations

**Why this happens:**
- MCP client (GitHub Copilot, Claude) may spawn server from different working directories
- No explicit project root enforcement in `Program.cs` or workspace initialization
- `ResolveProjectPath()` defaults to `Environment.CurrentDirectory` when no path provided

### Hypothesis 2: Global Roslyn Metadata Cache

**Analysis:**
- Roslyn maintains a global metadata cache across processes
- If user previously opened `panimB` in Visual Studio or another IDE, metadata might be cached
- WorkspaceManager's LRU cache is per-process, but Roslyn's internal caches are not

**Less likely:** Cache would be per-VS-instance, not cross-process MCP server

### Hypothesis 3: MSBuildLocator Discovery

**Analysis:**
- `MSBuildLocator.RegisterDefaults()` scans for MSBuild installations
- May enumerate `.csproj` files during discovery
- However, this happens once at registration, not per-tool-call

**Less likely:** Timing doesn't match (errors occur during tool invocations, not startup)

### Hypothesis 4: FileSystemWatcher Scope Too Broad

**Analysis:**
- AdhocWorkspace uses `FileSystemWatcher` with `IncludeSubdirectories = true`
- If watching from `C:\Users\madq4`, could recurse into OneDrive directories
- OneDrive "Files On-Demand" triggers "cloud file provider" errors when accessing stubs

**Likely contributor:** If server CWD is `C:\Users\madq4` and AdhocWorkspace is created without `.csproj`, watcher scope explodes

## OneDrive "Cloud File Provider" Error

### What It Means

```
IOException: The cloud file provider is not running.
```

**Cause:** OneDrive Files On-Demand feature
- Files exist as "stubs" on disk (small placeholder files)
- Actual content is in the cloud until accessed
- If OneDrive sync is paused/offline, accessing stubs throws this exception

**Impact:**
- Roslyn/MSBuild tries to read `MainForm.Designer.cs`
- OneDrive can't hydrate the file (sync offline or error)
- IOException propagates up, caught by RoslynMcp error handling

### Why Roslyn Is Accessing This File

**Most likely:** Workspace enumeration when CWD is wrong
- Server started from `C:\Users\madq4`
- Roslyn/MSBuild discovers nearby `.csproj` files
- Attempts to load `panimB` project metadata
- Hits OneDrive stub file, throws IOException

## Affected Tools

Based on log evidence:
- `roslyn_get_project_info`
- `roslyn_list_types`
- `roslyn_get_diagnostics`

**Pattern:** Tools that use `TryGetCompilation` or `TryGetProject`

## Why Tools Still Succeed

**RoslynMcp's error handling:**
```csharp
// From RoslynMcpTool base class
protected bool TryGetCompilation(string? projectPath, out Compilation compilation, out object error)
{
    try {
        // ... attempt to get compilation
    }
    catch(Exception ex) {
        Logger.LogError($"TryGetCompilation — {ex.GetType().Name}: {ex.Message}");
        error = new { error = "compilation_failed", message = ex.Message };
        compilation = null!;
        return false;
    }
}
```

**Why it succeeds despite error:**
- Exception is caught, logged, but tool continues
- May retry with different workspace or use cached compilation
- Tools may have fallback logic (e.g., use AdhocWorkspace instead of MSBuild)

**This is good resilience but masks the root issue.**

## Impact

### Current Impact (Low-Medium)

✅ **Tools function correctly** — errors don't break functionality  
⚠️ **Performance hit** — 700-2800ms wasted attempting to access OneDrive files  
⚠️ **Log pollution** — Misleading errors make debugging harder  
⚠️ **User confusion** — "Why is RoslynMcp accessing my OneDrive projects?"  

### Future Risk (High)

❌ **Security concern** — Without filesystem boundaries (Issue #9), could access sensitive projects  
❌ **Performance degradation** — Large OneDrive hierarchies could cause severe slowdowns  
❌ **Reliability** — OneDrive sync issues could cause tool failures instead of graceful degradation  

## Recommendations

### Immediate (v0.3.0-alpha or hotfix)

1. **Log the resolved project path** in every tool invocation:
   ```csharp
   Logger.LogInfo($"{toolName} — projectPath={projectPath}, resolved={resolvedPath}, cwd={Environment.CurrentDirectory}");
   ```
   This will help diagnose when/why wrong directories are used.

2. **Validate CWD on startup** in `Program.cs`:
   ```csharp
   // After command-line parsing, before building host
   var cwd = Environment.CurrentDirectory;
   if(!Directory.Exists(Path.Combine(cwd, ".git")) && !Directory.Exists(Path.Combine(cwd, "src"))) {
       Console.Error.WriteLine($"WARNING: Unexpected working directory: {cwd}");
       Console.Error.WriteLine("RoslynMcp should be started from a repository root.");
   }
   ```

3. **Add root directory protection** to WorkspaceManager (already planned for Issue #9):
   ```csharp
   // In ResolveProjectPath
   if(new DirectoryInfo(fullPath).Parent == null)
       throw new InvalidProjectPathException(fullPath, "Cannot use root directory");
   ```

### Short-term (v0.4.0 with Issue #9)

4. **Implement filesystem boundaries:**
   - Whitelist allowed project roots in MCP config
   - Reject paths outside allowed roots
   - Block OneDrive, network shares, system directories

5. **Explicit project root in MCP config:**
   ```json
   {
     "servers": {
       "roslyn": {
         "command": "RoslynMcp.exe",
         "args": ["--project-root", "J:\\Projects\\RoslynMcp"]
       }
     }
   }
   ```

6. **Scope FileSystemWatcher to project root:**
   ```csharp
   // Don't watch CWD, watch resolved project root
   watcher = new FileSystemWatcher(projectRoot, "*.cs") { ... };
   ```

### Long-term (v0.5.0+)

7. **WorkspaceManager telemetry:**
   - Track which directories are accessed
   - Alert on unexpected paths (OneDrive, C:\, network shares)
   - Auto-evict "bad" workspaces from cache

8. **OneDrive detection:**
   ```csharp
   bool IsOneDrivePath(string path) =>
       path.Contains("OneDrive", StringComparison.OrdinalIgnoreCase);

   // In ResolveProjectPath
   if(IsOneDrivePath(fullPath))
       throw new InvalidProjectPathException(fullPath, "OneDrive paths not supported");
   ```

## Open Questions

1. **How is the server being started with wrong CWD?**
   - Is the MCP client (Copilot/Claude) changing directories?
   - Is there a shell script or wrapper involved?
   - Can we force CWD in the MCP server config?

2. **Why these specific tools?**
   - All three use `TryGetCompilation` or `TryGetProject`
   - Is there a common code path that triggers workspace enumeration?

3. **Is the error recoverable?**
   - Tools succeed despite errors — what fallback is being used?
   - Is this by design or accidental resilience?

4. **Other external projects affected?**
   - Only `panimB` seen so far
   - Could other OneDrive projects cause similar issues?

## Testing

To reproduce:
1. Start RoslynMcp from `C:\Users\madq4` (wrong CWD)
2. Call `roslyn_get_diagnostics` without explicit `projectPath`
3. Observe log for external project access attempts

Expected:
- Error logged about OneDrive file
- Tool should warn about unexpected CWD
- Tool should either fail fast or explicitly fall back to CWD-based discovery

## Related Issues

- **Issue #9** — Filesystem security boundaries (this is a perfect example why it's needed!)
- **Future: Issue #14** — Add telemetry/monitoring for unexpected workspace access

---

**Status:** Documented, root cause identified (wrong CWD), recommendations provided.  
**Next Step:** Implement immediate fixes (logging, CWD validation) in v0.3.0-alpha or hotfix.

**ARRRRRRRRR!** Found the treasure (and the curse)! 🏴‍☠️
