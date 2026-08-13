# Code Fix Architecture

RoslynMcp exposes bundled Roslyn code fixes through a two-step preview/apply workflow:

```text
roslyn_preview_code_fix
        -> resolve a diagnostic
        -> collect bundled provider actions
        -> calculate one selected action in memory
        -> validate the complete solution change
        -> build a unified diff and exact physical plan
        -> return an approval token

roslyn_apply_code_fix
        -> validate approval, workflow, and workspace binding
        -> reject stale physical files
        -> prepare exact pre/post recovery snapshots
        -> atomically claim the token
        -> revalidate inside the physical-apply gate
        -> stage and atomically replace files
        -> verify exact hashes and report per-file results
```

Preview never writes source files. Apply never recalculates the selected `CodeAction` or re-encodes its changed text.

## Phase 1 boundary

Phase 1 supports targeted changes for a single diagnostic. Every physical effect must modify an existing source file that is:

- A `.cs` file
- Inside the canonical workspace
- Writable
- Non-generated
- Represented by exactly one Roslyn document

Preview rejects the proposal without issuing a token if it attempts:

- File creation, deletion, moves, or renames
- Linked or external source-file changes
- Non-C# or generated-file changes
- Read-only or otherwise non-writable files
- Project additions or removals
- Project, metadata, or analyzer-reference changes
- Compilation-option, parse-option, or project-identity changes
- Additional-document or analyzer-config changes
- Empty, custom, mixed, or multiple `CodeActionOperation` shapes

Fix All, project-system edits, arbitrary external providers, and file add/remove support are deferred.

## Provider boundary

`CodeFixHost` owns a closed catalog of `CodeFixProvider` instances bundled with RoslynMcp. It does not load providers from the target project's analyzer references, NuGet packages, or arbitrary assemblies.

External analyzers may still produce diagnostics. If no bundled provider supports a diagnostic, preview returns `no fixes` and does not create a token.

Phase 1 accepts exactly one `ApplyChangesOperation`. Provider registration or action-calculation failures return structured errors. Cancellation propagates as cancellation rather than being converted into a provider failure.

## Preview and exact bytes

Roslyn's `ChangedSolution` describes the selected semantic change in memory. Preview first validates the full difference from `BaseSolution`; a permitted change is then converted into a physical plan.

For every changed file, preview stores:

- Canonical physical path
- Original SHA-256 hash
- Exact intended byte array
- Intended SHA-256 hash

The intended byte array preserves the original `SourceText.Encoding` and whether the original file used that encoding's preamble/BOM. The same byte array is used for the post-change recovery snapshot, temporary-file write, and final verification. Apply never derives a second encoding result.

The unified diff is the review representation. The exact intended bytes are the persistence representation.

## Approval tokens

The pending operation contains the reviewed solutions and diff, exact file states, operation key, workflow type, and canonical workspace binding.

Tokens follow an atomic lifecycle:

```text
Pending -> Applying -> Consumed
```

This prevents concurrent callers from applying the same token. A rename or signature token cannot be used by code-fix apply, and a token previewed in one workspace cannot be applied through another workspace.

Approval `n` removes a pending token without writing. If another request already claimed it, rejection reports the token as unavailable instead of falsely reporting that an in-progress apply was rejected.

## Stale-state and race protection

Preview hashes the original disk bytes. Apply checks that each target still exists, remains supported and writable, and still matches its original hash.

Physical operations are serialized through a process-wide apply gate. After acquiring it, apply validates the plan again. For each file it then:

1. Writes the approved intended bytes to a unique temporary file in the target directory.
2. Preserves the target's Unix file mode when applicable.
3. Rechecks the target hash immediately before replacement.
4. Atomically replaces the existing target.
5. Hashes the resulting bytes and compares them with the intended hash.

The gate coordinates code-fix applies within the process. The final hash check and atomic replacement narrow the race with external processes while avoiding partial truncation of the original target.

## Recovery snapshots

Backups are prepared before source mutation:

| Snapshot | Contents |
|---|---|
| `pre` | Exact original bytes |
| `post` | Exact approved intended bytes |

Backup failure occurs before mutation and leaves the token available for a safe retry. A pre snapshot rolls back a verified write; a post snapshot completes the exact approved result when recovery is necessary.

## Physical results

The physical applier verifies and reports each file independently:

| State | Meaning |
|---|---|
| `written` | Final bytes match the intended hash |
| `untouched` | File still matches the original hash |
| `truncated` | File is missing or unexpectedly empty |
| `uncertain` | File matches neither original nor intended bytes |

Responses contain operation-aware recovery guidance. Automatic rollback is intentionally avoided because rollback can fail or overwrite concurrent user work.

## Test coverage

The focused harness covers:

- Successful Adhoc and MSBuild preview/apply
- Single- and multi-file verified writes
- Multiple action selection
- Unsupported and throwing operation shapes
- Invalid approval and explicit rejection
- Workspace and workflow token mismatches
- Concurrent token claims
- Stale modified and deleted targets
- Generated, read-only, linked, and external file rejection
- Create/delete and project-system change rejection
- Bundled-provider restrictions
- UTF-8 with and without BOM
- UTF-16 LE with BOM

Fix All must reuse this preview, approval, physical-plan, backup, and verification pipeline rather than introduce a separate persistence path.
