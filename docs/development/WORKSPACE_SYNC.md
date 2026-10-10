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
directly to disk and `InvalidateFile` classifies the path (see
[`InvalidateFile` Contract](#invalidatefile-contract)): a tracked `AdditionalFiles` item (e.g. a
`.txt` declared as one) applies incrementally via `WithAdditionalDocumentText` (#276); otherwise
`RequiresReload` decides (see [What flags a reload](#what-flags-a-reload)) — an evaluation input
such as a `.csproj`, `Directory.Build.props`, or a tracked `.editorconfig` flags a full reload;
anything else — `CHANGELOG.md`, an undeclared `.txt`, an unrelated `.json` — is a no-op for the
workspace, and only the pagination cache is cleared. No FSW suppression is needed for these writes
because the watcher's filter is `*.cs`.
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

### A write through a symbolic link (#338)

When `fullPath` is a symbolic link, the write lands in the file the link leads to, and the
file system reports the change at **that** path. The caller only knows the link, so
`WriteAndInvalidate` works the second path out itself, before the write (a write may replace
the link, and then there is nothing left to follow):

1. `LinkTargetOf(fullPath)` resolves the link and spells the result under the workspace root
   as it was opened — the form watcher events and document paths use. The fully resolved form
   differs when the root sits under a link (on macOS `/var` is `/private/var`).
2. The target is added to `ignoredPaths` for the same window as `fullPath`, and released in the
   same `finally`.
3. After `InvalidateFile(fullPath)`, still inside the window, `InvalidateLinkTarget` brings the
   target up to date:
   - a tracked document gets its new text, whether or not a watcher covers it;
   - an evaluation input flags its reload;
   - a file the write created is treated as new;
   - a `.cs` file that existed before and that no project compiles is **left alone**. To
     `InvalidateFile` it would be a new document and cost a full reload. Its size and write
     time are recorded in `ownedLinkTargets`. The watcher reports such a write on its own
     schedule — after the window has closed, and on Windows more than once for a replaced
     file — and `FlushMSBuild` drops every report for the path while the file still has that
     stamp. The entry is forgotten as soon as the stamp no longer matches: someone else has
     written the file. A one-shot record, like `rmOwnedWriteSizes`, would be used up by the
     first report and leave a second one to be taken for a new document.

The ordering rules above are unchanged: suppress before the write, invalidate inside the
window, release in `finally`.

---

## `InvalidateFile` Contract

`InvalidateFile(projectPath, fullPath)` syncs the workspace with a file RoslynMcp has just
written. It classifies the path (issue #273):

1. **Tracked source or additional document** (`GetDocumentIdsWithFilePath` non-empty, and
   `ClassifyTrackedDocument` says `Source` or `Additional`) → the new text is applied incrementally
   via `WithDocumentText` (source) or `WithAdditionalDocumentText` (additional, #276) +
   `ApplyChangesWithFswSuppressed`. No reload. If the apply fails, a reload is flagged (logged
   `Reload — Flagged (incremental apply failed): <path>`). A defensive `catch(InvalidOperationException)`
   also flags a reload instead of propagating, for the theoretical case where a path's ids span
   mixed kinds across projects.
2. **Tracked analyzer-config document** (`ClassifyTrackedDocument` says `AnalyzerConfig`) — always
   falls through to reload (case 3 below), even though it already has a `DocumentId`.
   `MSBuildWorkspace.CanApplyChange(ApplyChangesKind.ChangeAnalyzerConfigDocument)` is `false` as of
   Roslyn 5.3.0 (verified), so `TryApplyChanges` throws `NotSupportedException` for an
   `.editorconfig`/`.globalconfig` edit — there is no incremental path for this kind (#276).
3. **Otherwise `RequiresReload(solution, path)`** decides — see [What flags a reload](#what-flags-a-reload).
   A compilation or evaluation input bumps `reloadVersion` (logged with path and reason) and the
   next compilation-needing call reloads from disk.
4. **Anything else** (`CHANGELOG.md`, a `.txt` not declared as an `AdditionalFiles` item, an
   unrelated `.json`) is a no-op for the workspace. `WorkspaceResolver.InvalidateFile` still clears
   the pagination cache, since search/list pages may have changed.

**Always call after writing to disk** from any path not going through `WriteAndInvalidate`.
It is cheap for irrelevant files and the only way a new `.cs` or a changed `.csproj` written by
RoslynMcp itself reaches the workspace — the FileSystemWatcher filter is `*.cs`, so *external*
edits to evaluation inputs are not detected today (issue #275).

---

## What flags a reload

`RequiresReload` is consulted only for paths that are **not already applied incrementally** —
i.e. not a tracked source or additional document (see [`InvalidateFile` Contract](#invalidatefile-contract))
— by both `InvalidateFile` (RM-owned writes) and `FlushMSBuild` (watcher batches). First hit wins:

| # | Test | Result |
|---|------|--------|
| 1 | under `bin`/`obj`/`.git`/`.vs`/`node_modules`/`packages` (`IsExcludedDirectoryName`) | no-op — runs first because `obj/` holds NuGet's generated `*.nuget.g.props` and `*.GeneratedMSBuildEditorConfig.editorconfig` |
| 2 | extension `.cs` | reload — `new document` |
| 3 | extension `.csproj` `.props` `.targets` `.sln` `.slnx` `.slnf` `.editorconfig` `.globalconfig` `.ruleset` `.resx`, or file name `global.json` `nuget.config` `packages.lock.json` `packages.config` | reload — `evaluation input` |
| 4 | path in any `Project.AdditionalDocuments` | reload — `additional document`. Reachable only for a brand-new `AdditionalFiles` match the workspace has not loaded yet (no `DocumentId` yet, so `ClassifyTrackedDocument` never gets a chance to run) — an *already-tracked* additional document is routed incrementally before this predicate runs (#276) |
| 5 | path in any `Project.AnalyzerConfigDocuments` | reload — `analyzer config document`. This one *does* fire for an already-tracked `.editorconfig`/`.globalconfig`, since `InvalidateFile`/`FlushMSBuild` route `AnalyzerConfig`-classified paths here deliberately — `MSBuildWorkspace` has no incremental apply for that kind (#276) |
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
       └─ on debounce timer fire (FlushPendingChanges → FlushMSBuild):
            ├─ reported deleted, but the file is there under that exact name
            │     → it was replaced, not removed: handled as changed (below)
            ├─ deleted tracked document      → FlagReload("tracked document deleted")
            ├─ changed tracked source doc     → WithDocumentText, incremental — no reload
            ├─ changed tracked additional doc → WithAdditionalDocumentText, incremental — no reload (#276)
            ├─ changed tracked analyzer-config doc → RequiresReload? (always reloads — no incremental apply for this kind, #276)
            └─ changed unknown path          → RequiresReload?
                 ├─ yes (new .cs, evaluation input, new additional/analyzer-config doc)
                 │     → FlagReload(reason, path)   [logged: Reload — Flagged (<reason>): <path>]
                 └─ no (build output, anything non-compilation) → dropped
            → any flag: lazy reload on next GetCompilation() call
```

The watcher's filter is `*.cs`, so today only `.cs` paths reach this flow in practice — the
tracked-additional/analyzer-config branches above are reachable today only for the unusual case of
an `AdditionalFiles` item that is itself a `.cs` file (`<AdditionalFiles Include="Template.cs" />`);
they exist for parity with `InvalidateFile` and so widening the watcher filter (issue #275) needs no
second rule. The classifier (`ClassifyTrackedDocument`) is shared with `InvalidateFile`.

The debounce window (300ms) prevents rapid successive FSW events from triggering multiple
reloads during a batch write operation.

**A delete report for a file that exists.** Every atomic write ends by replacing the file,
and on Windows the watcher sometimes reports the old file's removal as `Deleted`. Deletes are
not covered by `ignoredPaths`, so that report used to reach the flush as "tracked document
deleted" and cost a full reload after one of the server's own writes — and after an editor's
atomic save. `FlushPendingChanges` now checks each deleted path before dispatching: a file that
is there under exactly that name was replaced, and is handled as changed (text compared,
applied incrementally if it differs). The name is compared exactly (`ExistsAsNamed`) because a
case-only rename, `Foo.cs` → `foo.cs`, is reported as a delete of the old name on a file system
that ignores case, and that one must still reload.

---

## Key Invariants

1. **`ignoredPaths[path]` is always decremented** — the decrement is always in a `finally` block, so it runs even if the write throws.
2. **`InvalidateFile` runs before `ignoredPaths--`** — so the FSW can never see an unsuppressed event between the write completion and the invalidation.
3. **`ownedDeletePaths` is consumed by `ScheduleDebounced` for rename old-path deletes** — it is not cleared in `WriteAndInvalidate`'s `finally` block.
4. **`CurrentSolution` is always consistent** — Roslyn's `ApplyChanges` is synchronous; the in-memory tree is never stale after a successful apply.
5. **`TryRecoverTruncation` rewrites the intended bytes supplied by the caller** — not disk, and not by re-reading `CurrentSolution`.
6. **A reload is flagged only when incremental apply is unsupported or unavailable** — for a path `RequiresReload` classifies as a compilation or evaluation input, or for a tracked analyzer-config document (`MSBuildWorkspace` has no incremental apply for that kind). `InvalidateFile` and `FlushMSBuild` share the same classification (`ClassifyTrackedDocument` + `RequiresReload`), and every flag is logged with path and reason (#273, #276).

---

## Watch roots (#309)

A workspace no longer holds one recursive `FileSystemWatcher` on its root. A watcher cannot
exclude a directory, and on Linux a recursive one registers an inotify watch per directory below
it, so a root watcher walked all of `node_modules` and `.git` on the first load — and again on
every `TryApplyChanges`, which toggles `EnableRaisingEvents`.

`WatchPlanner` picks the roots, in order of coverage:

1. **`root`** — the whole workspace root, split around the never-input directories
   (`IgnoreRules.IsNeverInput`: `node_modules`, `.git`, `.vs`, `packages`, configured names). A
   directory with such a directory somewhere below it is watched non-recursively; every other child
   is watched recursively. Coverage is what the single watcher had, minus the trees whose events
   `ScheduleDebounced` dropped anyway. With nothing to skip this is one recursive root, as before.
2. **`projects`** — when that needs more than 32 watchers: project directories only (split the same
   way), plus their ancestors up to the root and the directories of documents linked from
   elsewhere, non-recursively. An imported `.props`/`.targets` outside those is not watched.
3. **`fallback`** — a single recursive watcher on the root.

`WorkspaceWatchSet` starts the planned watchers on a background thread and funnels every event into
`ScheduleDebounced`, so nothing downstream changed. Three things follow from that:

- **Tool calls do not wait for the watchers.** Once they are live, `ReconcileMissedChanges` queues
  every tracked document (outside `bin`/`obj`) whose mtime is later than the start of construction,
  covering edits made during the load and the watcher start. New files created in that window are
  not detected.
- **Suppression is unchanged in meaning.** `ApplyChangesWithFswSuppressed` calls
  `Suspend`/`Resume`, which disable and re-enable every watcher under the same ref-count rule.
- **Non-recursive roots adopt new directories.** A directory created or moved under one gets a
  recursive watcher, and the files already in it are reported — `mkdir` and the first write into it
  are milliseconds apart.

A workspace never holds more than 32 watchers: when adopting a directory would pass that, the
watch set plans again instead, which moves it to the `projects` or `fallback` shape.

The plan is recomputed after a reload only when the project or document directories changed, and
edits made while watchers are being replaced are reconciled by timestamp the same way as after
the initial load. A `node_modules` created under a recursive root after the plan was made is still
watched until the next re-plan; its events are dropped as before.

---

*Investigation notes: `files/stage1-findings.md` through `files/stage5-findings.md` in the
session state contain the full per-stage analysis. This document distills the architectural
conclusions.*
