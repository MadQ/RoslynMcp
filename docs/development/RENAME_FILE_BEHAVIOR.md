# Rename File Behavior

## Summary

When renaming a top-level named type whose source file stem matches the type name, `roslyn_apply_rename` automatically renames the file to match the new type name.

This is fully explicit — the Roslyn `RenameFile` API option has no effect at this level (see below).

---

## How It Works

### Step 1: Preview (`roslyn_preview_rename`)

At preview time, `PreviewRenameTool.ComputeFileRename` checks whether a file rename should be paired with the symbol rename:

- Symbol must be a top-level named type (`INamedTypeSymbol` with no `ContainingType`)
- Symbol must have exactly one source location (partial types spanning multiple files are skipped — renaming any one is ambiguous)
- The source file's stem must exactly match the type's current name (Ordinal comparison)

If all conditions pass, the `(OldPath, NewPath)` pair is stored in `PendingOperation.FileRename` alongside the Roslyn solution snapshots. No file I/O happens during preview.

### Step 2: Apply (`roslyn_apply_rename`)

Apply writes the renamed content first (via Roslyn's solution diff), then executes the file move:

1. Write all changed documents to disk (type rename is already in the content at the old path)
2. If `operation.FileRename` is set:
   - **Collision check**: if the destination file already exists (and it's not a case-only rename), fail with an actionable error — the symbol rename is already written, only the file rename failed
   - **Normal rename**: `FileWriter.Move(oldPath, newPath)` wrapped in `WriteAndInvalidate` to suppress the FSW `Renamed` event for the new path
   - **Case-only rename** (e.g. `Foo.cs` → `foo.cs`): two-step via temp file, because Windows' case-insensitive filesystem won't move a file to a name that differs only in case in a single step

The result includes `filesRenamed: 1` when a file rename occurred.

---

## Why `RenameFile = true` Is a No-Op

The Roslyn `Renamer.RenameSymbolAsync` API accepts a `SymbolRenameOptions` with a `RenameFile` flag. This flag has **no effect** at the API level:

- Verified experimentally: `RenameFile = true` and `RenameFile = false` produce identical results — always `changedDocs=1, addedDocIds=0, removedDocs=0`
- The Roslyn source's obsolete overload hardcodes `RenameFile: false`
- File renaming is an **IDE-level concern**: Visual Studio handles it by prompting the user separately after the symbol rename completes

This means `Renamer.RenameSymbolAsync` never adds or removes documents from the solution. File renaming must be done explicitly, which is what `ApplyRenameTool` does using `FileWriter.Move`.

---

## FSW Suppression

`File.Move(oldPath, newPath)` triggers two FSW events:

| Event | Path | Suppressed? |
|-------|------|-------------|
| `Renamed` (old side, `deleted=true`) | `oldFilePath` | No — `deleted=true` always bypasses suppression. The workspace correctly sees the old path as gone. |
| `Renamed` (new side) | `newFilePath` | Yes — `WriteAndInvalidate` adds `newFilePath` to the ignored paths set before the move. |

After the move, `WriteAndInvalidate` calls `InvalidateFile(newFilePath)`. For MSBuild workspaces, if the new path isn't in the project file (SDK glob picks it up lazily), this increments `reloadVersion` and forces a full workspace reload on the next compilation request.

---

## Edge Cases

| Scenario | Behavior |
|----------|----------|
| File stem does not match type name | No file rename — symbol content only |
| Partial type (multiple source files) | No file rename — ambiguous which file to rename |
| Destination file already exists | Hard error; symbol rename is committed, file rename is not |
| Case-only rename (`Foo.cs` → `foo.cs`) | Two-step via `.roslynmcp_rename_tmp` intermediate |
| Nested type (has `ContainingType`) | No file rename |
| Method, field, property, parameter | No file rename — only named types trigger it |
