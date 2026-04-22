# Workspace Sync — FSW/Roslyn Write Path Architecture

> ⚠️ **DO NOT CHANGE THE CODE THIS DOCUMENT DESCRIBES WITHOUT READING IT FIRST.**
>
> The logic in `WorkspaceManager.Instance.cs` around FSW suppression, `InvalidateFile`,
> and `WriteAndInvalidate` is the result of a 4-stage disciplined analysis (Stages 1–5 of
> the FSW sync investigation, issues #179). The code is subtle, counter-intuitive in places,
> and was arrived at by carefully tracing every write path and every FSW trigger. Casual
> edits have a high probability of re-introducing the race conditions described below.
>
> If you need to change it, read this document completely, re-read
> `WorkspaceManager.Instance.cs`, and consider opening a discussion issue first.

---

## Background

The Roslyn workspace (`WorkspaceInstance`) holds an in-memory view of the project. Changes
to `.cs` files on disk are detected by a `FileSystemWatcher` (FSW) and trigger a workspace
reload. Tools that *write* `.cs` files also need the workspace to reflect the new content.

The challenge: when a tool writes a file, the FSW fires for that same write. If not
suppressed, the FSW will trigger an extra reload — potentially discarding in-memory edits
that were already applied (by `TryApplyChanges`) but not yet observed by the next tool call.

---

## The Six Write Paths

Every mutation of a `.cs` file in the workspace flows through one of these paths:

| # | Path | Tool | Mechanism |
|---|------|------|-----------|
| 1 | Roslyn-managed write | `ApplyRenameTool`, `ApplySignatureChangeTool` | `workspace.ApplyChanges()` → in-memory first, then `WriteAndInvalidate` for disk sync |
| 2 | Text replacement | `ReplaceInFileTool` | `.cs`: `ApplyTextChange()` (FSW-suppressed Roslyn write, or `WriteAndInvalidate` fallback for AdhocWorkspace). Non-`.cs`: `FileWriter.WriteAllTextAsync` + `InvalidateFile` |
| 3 | Code node replacement | `ReplaceInCodeTool` | Tracked `.cs`: `workspace.ApplyChanges()` (same as Path 1). Untracked `.cs`: `WriteAndInvalidate` + `FileWriter.WriteAllTextAsync` |
| 4 | Line insertion | `InsertLinesTool` | `.cs`: `ApplyTextChange()` (same as Path 2). Non-`.cs`: `FileWriter.WriteAllText` + `InvalidateFile` |
| 5 | Full file write | `WriteFileTool` | `.cs`: `WriteAndInvalidate` + atomic `FileWriter.WriteAllBytesAsync` + `Move`. Non-`.cs`: same atomic write + `InvalidateFile` |
| 6 | External writes | User, git, IDE | FSW fires → `ScheduleDebounced` → reload |

Paths 1–5 are **owned writes**: the tool knows it is writing. For `.cs` files, all paths
suppress spurious FSW events using the `ignoredPaths` counter (directly via `WriteAndInvalidate`
or `ApplyChangesWithFswSuppressed`). For non-`.cs` files (paths 2, 4, 5), the write goes
directly to disk and `InvalidateFile` marks the cached workspace stale — no FSW suppression
is needed since the FSW only triggers `.cs` reloads. Path 6 is an **external write**: the
FSW fires normally and triggers a reload.

---

## Suppression Mechanism — `ignoredPaths`

`ignoredPaths` is a `ConcurrentDictionary<string, int>` that tracks in-flight owned writes.

```
ignoredPaths[path]++      // before write starts
...write file to disk...
ignoredPaths[path]--      // in finally (always runs)
```

`ScheduleDebounced` checks `ignoredPaths[path] > 0` before scheduling a reload. If the
count is positive, the FSW event is ignored.

**Why a counter, not a flag?** Two concurrent writes to the same file would incorrectly
clear a boolean. The counter ensures both writes must complete before the path is no longer
suppressed.

---

## Bug #2 — Unsuppressed FSW Deleted Event During File Rename

**Symptom:** `ApplyRenameTool` renames `A.cs` → `B.cs`. The OS fires:
1. FSW `Changed`/`Created` on `B.cs` — suppressed via `ignoredPaths[B]`
2. FSW `Deleted` on `A.cs` — **not suppressed**, because `ignoredPaths[A]` was never set

The unsuppressed Deleted event caused an extra full workspace reload cycle after every
file rename.

**Fix:** `WriteAndInvalidate(newPath, oldPath, write)` accepts an optional `movedFromPath`.
When provided, it adds `movedFromPath` to `ignoredPaths` before the write, and removes it
after. This suppresses the Deleted event for the old path during a rename.

See `WorkspaceManager.Instance.cs` — `WriteAndInvalidate` overloads and `ownedDeletePaths`.

---

## Bug #3 — Timing Race Between `ignoredPaths` Decrement and `InvalidateFile`

**Symptom (before fix):**

```
try { await write(); }
finally { ignoredPaths[path]-- }   // ← suppression window ends here
InvalidateFile(path);               // ← FSW can fire here, before InvalidateFile runs
```

If the FSW fires in the window between `ignoredPaths--` (in `finally`) and `InvalidateFile`,
it lands as an unsuppressed event. This triggers a reload from a potentially inconsistent
on-disk state (write partially flushed).

**Fix:** Move `InvalidateFile` inside the `try` block, before `finally`:

```
try {
    await write();
    InvalidateFile(path);           // ← runs while ignoredPaths[path] > 0 (suppressed)
}
finally { ignoredPaths[path]-- }   // ← suppression window ends after InvalidateFile
```

This ensures `InvalidateFile` always executes while the path is still suppressed.

---

## Bug #1 — Disproven: `CurrentSolution` Divergence After Truncation

**Hypothesis (before Stage 4 analysis):** If disk write truncates (partial write), Roslyn's
`CurrentSolution` might hold the pre-write in-memory tree, diverging permanently from disk.

**Finding (Stage 4):** `workspace.ApplyChanges()` (MSBuildWorkspace) always updates
`CurrentSolution` synchronously, even when the subsequent disk write truncates. The
in-memory tree always reflects the intended edit. `TryRecoverTruncation` handles the rare
truncation case by re-writing the full content.

**Conclusion:** No permanent `CurrentSolution` divergence exists. The workspace self-heals.

---

## `TryRecoverTruncation` — The Self-Healing Path

After `ApplyChanges`, the tool writes each changed document to disk. If the write produces
a file shorter than the original (`< originalLength * 0.9`), `TryRecoverTruncation` kicks in:

1. Gets the current source text from `CurrentSolution` (always up-to-date — see Bug #1)
2. Writes the full content back to disk
3. Calls `InvalidateFile` to force a reload on next access

This corrects the rare truncation without requiring a full workspace rebuild.

---

## `WriteAndInvalidate` Contract

`WriteAndInvalidate(fullPath, movedFromPath, write)` is the authoritative write entry point
for all rename/refactoring operations. It:

1. Adds `fullPath` (and optionally `movedFromPath`) to `ignoredPaths`
2. Calls `write()` (which writes to disk and calls `workspace.ApplyChanges`)
3. Calls `InvalidateFile(fullPath)` inside `try` (Bug #3 fix)
4. Decrements `ignoredPaths` for both paths in `finally`

All `ApplyRenameTool` and `ApplySignatureChangeTool` write calls go through this method.

---

## `InvalidateFile` Contract

`InvalidateFile(projectPath, fullPath)` marks a specific file as stale in the workspace
cache. On the next tool call that needs a compilation for that project, the workspace is
reloaded from disk.

**Always call after writing to disk** from any path not going through `WriteAndInvalidate`.
Failure to call it leaves the Roslyn workspace serving stale content.

---

## FSW Event Flow

```
FSW fires (Changed/Created/Deleted/Renamed)
  │
  ├─ path in ignoredPaths[path] > 0?  → suppress (owned write, in progress)
  │
  └─ otherwise → ScheduleDebounced(300ms)
       │
       └─ on debounce timer fire:
            reloadVersion++
            → lazy reload on next GetCompilation() call
```

The debounce window (300ms) prevents rapid successive FSW events from triggering multiple
reloads during a batch write operation.

---

## Key Invariants

1. **`ignoredPaths[path]` is always decremented** — the decrement is always in a `finally` block, so it runs even if the write throws.
2. **`InvalidateFile` runs before `ignoredPaths--`** — so the FSW can never see an unsuppressed event between the write completion and the invalidation.
3. **`ownedDeletePaths` mirrors `ignoredPaths` for rename old-paths** — same counter semantics, same `finally` guarantee.
4. **`CurrentSolution` is always consistent** — Roslyn's `ApplyChanges` is synchronous; the in-memory tree is never stale after a successful apply.
5. **`TryRecoverTruncation` uses `CurrentSolution` as source of truth** — not disk, since disk may be partially written.

---

*Investigation notes: `files/stage1-findings.md` through `files/stage4-findings.md` in the
session state contain the full per-stage analysis. This document distills the architectural
conclusions.*
