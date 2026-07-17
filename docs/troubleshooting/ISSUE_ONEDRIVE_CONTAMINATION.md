# OneDrive Contamination via Over-Broad `projectPath`

## Summary

This was originally observed as RoslynMcp wandering into unrelated OneDrive content when the
effective workspace root was far too broad.

The current source code has important mitigations:
- every Roslyn tool requires an explicit `projectPath`
- `WorkspaceManager.ResolveProjectPath()` rejects dangerous roots such as drive roots and known system directories
- Adhoc enumeration skips hidden/system directories plus `node_modules`, `bin`, `obj`, `.git`, `.vs`, and `packages`

But the issue is **not completely impossible**: if you explicitly pass a broad directory such as
your home directory or a OneDrive root, AdhocWorkspace will still treat that directory as the
workspace root and recurse through it looking for `*.cs`.

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

## What the current code actually does

### Path resolution

`ResolveProjectPath()` now requires a non-empty `projectPath` and resolves it explicitly:
- `.csproj` path → use it directly
- directory with a `.csproj` in that directory → MSBuildWorkspace
- directory with **no** `.csproj` → AdhocWorkspace rooted at that directory
- source file path → walk upward looking for a `.csproj`

Relevant source behavior:
- empty path throws `missing_project_path`
- drive roots / known system directories are rejected as invalid project locations
- relative paths are still resolved against the server's working directory

### Adhoc file enumeration

When RoslynMcp loads AdhocWorkspace, `EnumerateFilesWithErrorHandling()`:
- skips hidden and system directories
- skips `node_modules`, `bin`, `obj`, `.git`, `.vs`, and `packages`
- swallows `UnauthorizedAccessException` and `DirectoryNotFoundException`

That makes the old "scan the entire drive root" scenario much less likely, but **does not**
make a broad user-selected root safe.

## Root cause

The contamination pattern is now narrower and simpler than this file originally described:

1. A caller passes a `projectPath` that resolves to a directory far above the intended repo
2. That directory contains no `.csproj`, so RoslynMcp chooses AdhocWorkspace
3. Adhoc enumeration and the `FileSystemWatcher(rootPath, "*.cs")` both operate under that broad root
4. OneDrive placeholder files or unrelated repos inside that tree can surface I/O noise

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

**Most likely today:** AdhocWorkspace was rooted somewhere too broad (for example a home
directory or OneDrive-backed folder), so recursive `*.cs` enumeration crossed into OneDrive content.

## Affected Tools

Based on log evidence:
- `roslyn_get_project_info`
- `roslyn_list_types`
- `roslyn_get_diagnostics`

**Pattern:** Tools that call `TryGetCompilation` or `TryGetProject`, because those are the entry
points that force workspace resolution/loading.

## Why Tools Still Succeed

`TryGetCompilation` / `TryGetProject` convert many path-resolution failures into structured tool
errors (`missing_project_path`, invalid project path, ambiguous project path, etc.). I/O noise
inside an already-accepted broad Adhoc root can still appear in logs because enumeration is
best-effort and exception-tolerant.

## Impact

### Current Impact (Low-Medium)

✅ **Tools function correctly** — errors don't break functionality  
⚠️ **Performance hit** — 700-2800ms wasted attempting to access OneDrive files  
⚠️ **Log pollution** — Misleading errors make debugging harder  
⚠️ **User confusion** — "Why is RoslynMcp accessing my OneDrive projects?"  

## Recommended practice

1. **Pass the `.csproj` path directly whenever possible**
   - Best: `src\RoslynMcp\RoslynMcp.csproj`
   - Avoid: `C:\Users\madq4`

2. **Do not rely on server CWD**
   - relative `projectPath` values still resolve against the server working directory
   - global client configs should prefer absolute repo paths when practical

3. **Avoid broad Adhoc roots**
   - do not point RoslynMcp at home directories, OneDrive roots, or repository parents
   - if you truly want Adhoc mode, use a narrowly scoped source directory

4. **Check the log when contamination is suspected**
   - startup entries include `cwd="..."`
   - workspace/path failures are logged under `TryGetCompilation`, `TryGetProject`, or workspace load events

## Reproduction

To reproduce the modern form of this issue:

1. Pass a broad directory with no `.csproj` as `projectPath`
2. Ensure that directory tree contains OneDrive-backed or placeholder `.cs` files
3. Call a tool that loads a workspace, such as `roslyn_get_diagnostics`
4. Observe log noise or load failures from files inside that broad tree

---

**Status:** Partially mitigated in source. The old implicit-CWD failure mode is much harder to hit because
`projectPath` is required and dangerous roots are rejected, but broad Adhoc roots can still pull in OneDrive
content if the caller chooses them.

**ARRRRRRRRR!** Found the treasure (and the curse)! 🏴‍☠️
