# Working-Tree Rollback — Known Issue

A recurring issue where files that are edited in-memory during a Copilot session
appear committed but are actually zero bytes (or pre-session content) on disk.

---

## Symptoms

- `(Get-Item path\to\file.cs).Length` returns 0 after a commit that was supposed to add content
- `git show HEAD:path/to/file.cs` returns empty content despite the commit message referencing the file
- `roslyn_get_file_outline` returns an empty type list for a file that was just written
- `git status` shows "up to date" but files are obviously broken at runtime

## When It Happens

Observed primarily when:

1. A file is edited in-memory via Roslyn tools (which operate on the in-memory workspace),
   but the corresponding disk file was never actually written.
2. A merge or branch switch discards un-persisted in-memory state.
3. A `git add -A` followed by `git commit` picks up the disk file (which is empty/old),
   not the in-memory version the Roslyn workspace was showing.

This has bitten us multiple times:
- #154 (`CleanSolutionTool.cs`, `RestorePackagesTool.cs`) — zeroed before commit
- #157 merge (`InsertLinesTool.cs`, `LocalHistoryTool.cs`, `ReplaceInCodeTool.cs`,
  `ReplaceInFileTool.cs`, `WriteFileTool.cs`) — zeroed by merge, caught post-commit

## Detection

After any commit that modified `.cs` files, run a size check:

```powershell
Get-ChildItem src\RoslynMcp -Recurse -Filter *.cs |
    Where-Object { $_.Length -lt 50 } |
    Select-Object FullName, Length
```

Files under 50 bytes are suspicious. Legitimate near-empty `.cs` files are rare; most
should be well above 500 bytes.

Also safe to check specific files by name:

```powershell
git show HEAD:src/RoslynMcp/Tools/Build/CleanSolutionTool.cs | Measure-Object -Line
```

## Recovery

### Option A — RoslynMcp backup store (try first; no git needed)

`roslyn_write_file` takes a crash-safe backup before every write. Backups live under:

```
C:\Users\madq4\AppData\Local\RoslynMcp\backups\
```

Naming convention: `{originalFileName}_{unixMs}.bak`  
Example: `RestorePackagesTool.cs_1775570742174.bak`

**Do not use `roslyn_*` tools for this** — if the file is 0 bytes on disk the Roslyn
workspace is equally stale. Use PowerShell directly.

1. Find all non-empty backups for the zeroed file:
   ```powershell
   $f = "RestorePackagesTool.cs"
   $dir = "C:\Users\madq4\AppData\Local\RoslynMcp\backups"
   Get-ChildItem $dir -Recurse |
       Where-Object { $_.Name -like "${f}_*.bak" -and $_.Length -gt 0 } |
       Sort-Object LastWriteTime -Descending |
       Select-Object Name, LastWriteTime, Length
   ```
   Some backups may themselves be 0 bytes — those were taken after the file was already
   truncated. Sort descending and pick the most recent non-zero entry.

2. Preview the best candidate:
   ```powershell
   $best = Get-ChildItem $dir -Recurse |
       Where-Object { $_.Name -like "${f}_*.bak" -and $_.Length -gt 0 } |
       Sort-Object LastWriteTime -Descending |
       Select-Object -First 1
   Get-Content $best.FullName
   ```

3. Restore it (overwrite the 0-byte file):
   ```powershell
   Copy-Item $best.FullName "src\RoslynMcp\Tools\Build\RestorePackagesTool.cs"
   ```

4. Verify, then commit the fix:
   ```
   fix: restore <file> zeroed by working-tree rollback (from RoslynMcp backup)
   ```

### Option B — git history (when no backup is useful)

1. Identify the last good commit:
   ```powershell
   git log --oneline -- path/to/file.cs
   ```

2. Restore from that commit:
   ```powershell
   git checkout <good-sha> -- path/to/file.cs
   ```

3. Verify content is restored, then commit the fix:
   ```
   fix: restore <file> zeroed by working-tree rollback
   ```

## Root Cause (Hypothesis)

The Roslyn MCP server operates on an in-memory `MSBuildWorkspace`. When Copilot edits a
`.cs` file via `roslyn_replace_in_code` or similar, the change is applied to the workspace
in memory. The server does NOT automatically flush changes back to disk. If the Copilot
tool that was supposed to write the file (e.g., `roslyn_write_file`) was not called or
returned before the in-memory state was lost, the disk file remains at its prior state.

A `git add -A` then commits the disk version — which may be empty or old.

## Workaround / Prevention

Until this is root-caused and fixed:

1. **After every commit**, run the size-check snippet above.
2. **Before merging**, verify that all files touched in the feature branch have non-zero
   size both on disk and in `git show HEAD:...`.
3. **After a merge**, run the size check again — merges are a common trigger.
4. If Copilot reports it "wrote" a file via `roslyn_replace_in_code`, confirm with
   `roslyn_get_file_outline` that the file has types, not just with a line count.

---

## Tracking

This issue is not yet filed as a GitHub issue. When we return to it, consider:
- Adding a post-commit git hook that fails on zero-byte `.cs` files
- Investigating whether `roslyn_replace_in_code` always flushes to disk
- Checking if the Roslyn MCP server has a "save all" or "flush" operation
