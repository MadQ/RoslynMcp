# Workspace Sync — FSW/Roslyn Write Path Architecture

> ⚠️ **DO NOT CHANGE THE CODE THIS DOCUMENT DESCRIBES WITHOUT READING IT FIRST.**
>
> The logic in `WorkspaceManager.Instance.cs` around FSW suppression, `InvalidateFile`,
> and `WriteAndInvalidate` is the result of a 5-stage disciplined analysis (Stages 1–5 of
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
directly to disk and `InvalidateFile` classifies the path with `RequiresReload` (see
[What flags a reload](#what-flags-a-reload)): an evaluation input such as a `.csproj`,
`Directory.Build.props`, or `.editorconfig` flags a full reload; anything else — `CHANGELOG.md`,
a `.txt`, an unrelated `.json` — is a no-op for the workspace, and only the pagination cache is
cleared. No FSW suppression is needed for these writes because the watcher's filter is `*.cs`.
Path 6 is an **external write**: the FSW fires normally and triggers a reload.

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
When provided, it adds `movedFromPath` to `ownedDeletePaths` before the write. The later
FSW `Deleted(oldPath)` callback consumes that entry inside `ScheduleDebounced` and returns
without scheduling a reload.

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
in-memory tree always reflects the intended edit. The truncation recovery path does **not**
re-read `CurrentSolution`; it rewrites the caller-supplied intended bytes through
`workspace.WriteAndInvalidate(...)`.

**Conclusion:** No permanent `CurrentSolution` divergence exists. The workspace self-heals.

---

## `TryRecoverTruncation` — The Self-Healing Path

After a write, tools call `TryRecoverTruncation(...)` when the resulting file is empty.
That helper:

1. Verifies the target file exists but has length `0`
2. Rewrites the intended `contentBytes` through `workspace.WriteAndInvalidate(...)`
3. Returns an error only if the recovery attempt also leaves the file empty or fails with I/O

This corrects the zero-byte truncation case without requiring a full workspace rebuild.

---

## `WriteAndInvalidate` Contract

`WriteAndInvalidate(fullPath, movedFromPath, write)` is the authoritative write entry point
for all rename/refactoring operations. It:

1. Adds `fullPath` (and optionally `movedFromPath`) to `ignoredPaths`
2. Adds `movedFromPath` to `ownedDeletePaths` when present
3. Calls `write()` (which writes to disk and calls `workspace.ApplyChanges`)
4. Calls `InvalidateFile(fullPath)` inside `try` (Bug #3 fix)
5. Decrements `ignoredPaths[fullPath]` in `finally`

All `ApplyRenameTool` and `ApplySignatureChangeTool` write calls go through this method.

---

## `InvalidateFile` Contract

`InvalidateFile(projectPath, fullPath)` syncs the workspace with a file RoslynMcp has just
written. It classifies the path (issue #273):

1. **Tracked source document** (`GetDocumentIdsWithFilePath` non-empty) → the new text is applied
   incrementally via `WithDocumentText` + `ApplyChangesWithFswSuppressed`. No reload. If the apply
   fails, a reload is flagged (logged `Reload — Flagged (incremental apply failed): <path>`).
2. **Otherwise `RequiresReload(solution, path)`** decides — see [What flags a reload](#what-flags-a-reload).
   A compilation or evaluation input bumps `reloadVersion` (logged with path and reason) and the
   next compilation-needing call reloads from disk.
3. **Anything else** (`CHANGELOG.md`, a `.txt`, an unrelated `.json`) is a no-op for the workspace.
   `WorkspaceResolver.InvalidateFile` still clears the pagination cache, since search/list pages
   may have changed.

**Always call after writing to disk** from any path not going through `WriteAndInvalidate`.
It is cheap for irrelevant files and the only way a new `.cs` or a changed `.csproj` written by
RoslynMcp itself reaches the workspace — the FileSystemWatcher filter is `*.cs`, so *external*
edits to evaluation inputs are not detected today (issue #275).

---

## What flags a reload

`RequiresReload` is consulted only for paths that are **not** tracked source documents, by both
`InvalidateFile` (RM-owned writes) and `FlushMSBuild` (watcher batches). First hit wins:

| # | Test | Result |
|---|------|--------|
| 1 | under `bin`/`obj`/`.git`/`.vs`/`node_modules`/`packages` (`IsExcludedDirectoryName`) | no-op — runs first because `obj/` holds NuGet's generated `*.nuget.g.props` and `*.GeneratedMSBuildEditorConfig.editorconfig` |
| 2 | extension `.cs` | reload — `new document` |
| 3 | extension `.csproj` `.props` `.targets` `.sln` `.slnx` `.slnf` `.editorconfig` `.globalconfig` `.ruleset` `.resx`, or file name `global.json` `nuget.config` `packages.lock.json` `packages.config` | reload — `evaluation input` |
| 4 | path in any `Project.AdditionalDocuments` | reload — `additional document` |
| 5 | path in any `Project.AnalyzerConfigDocuments` | reload — `analyzer config document` |
| 6 | everything else | **no-op** |

Comparisons are case-insensitive. Paths outside the workspace root are still classified
(`Directory.Build.props` above a csproj-mode root is a legitimate input); only the
excluded-directory walk is root-relative. Adhoc workspaces need no predicate —
`AddOrUpdateDocument` already ignores non-`.cs` paths, and `ReloadIfNeeded` clears any flag
without loading.

**Known limitation:** MSBuild `EmbeddedResource` items are not exposed by Roslyn's `Project`, and
`MSBuildWorkspace` discards the project instance after the design-time build, so a resource of
arbitrary type cannot be recognized. Resources do not affect any Roslyn-served result — only
`dotnet build`, which reads disk. `.resx` is matched by extension as the common case. A `.cs`
excluded via `<Compile Remove>` still flags a reload (it is an unknown document) — pre-existing.

Every flag is logged once under the `Reload` category as `Flagged (<reason>): <path>`, so a log
always shows which file caused a reload, not just that one happened.

---

## FSW Event Flow

```
FSW fires (Changed/Created/Deleted/Renamed)
  │
  ├─ path in ignoredPaths[path] > 0?  → suppress (owned write, in progress)
  │
  └─ otherwise → ScheduleDebounced(300ms)
       │
       └─ on debounce timer fire (FlushMSBuild):
            ├─ deleted tracked document      → FlagReload("tracked document deleted")
            ├─ changed tracked document      → WithDocumentText, incremental — no reload
            └─ changed unknown path          → RequiresReload?
                 ├─ yes (new .cs, evaluation input, additional/analyzer-config doc)
                 │     → FlagReload(reason, path)   [logged: Reload — Flagged (<reason>): <path>]
                 └─ no (build output, anything non-compilation) → dropped
            → any flag: lazy reload on next GetCompilation() call
```

The watcher's filter is `*.cs`, so today only `.cs` paths reach this flow; the classifier is
shared with `InvalidateFile` so widening the filter (issue #275) needs no second rule.

The debounce window (300ms) prevents rapid successive FSW events from triggering multiple
reloads during a batch write operation.

---

## Key Invariants

1. **`ignoredPaths[path]` is always decremented** — the decrement is always in a `finally` block, so it runs even if the write throws.
2. **`InvalidateFile` runs before `ignoredPaths--`** — so the FSW can never see an unsuppressed event between the write completion and the invalidation.
3. **`ownedDeletePaths` is consumed by `ScheduleDebounced` for rename old-path deletes** — it is not cleared in `WriteAndInvalidate`'s `finally` block.
4. **`CurrentSolution` is always consistent** — Roslyn's `ApplyChanges` is synchronous; the in-memory tree is never stale after a successful apply.
5. **`TryRecoverTruncation` rewrites the intended bytes supplied by the caller** — not disk, and not by re-reading `CurrentSolution`.
6. **A reload is flagged only for paths `RequiresReload` classifies as compilation or evaluation inputs** — `InvalidateFile` and `FlushMSBuild` share that one predicate, and every flag is logged with path and reason (#273).

---

*Investigation notes: `files/stage1-findings.md` through `files/stage5-findings.md` in the
session state contain the full per-stage analysis. This document distills the architectural
conclusions.*
