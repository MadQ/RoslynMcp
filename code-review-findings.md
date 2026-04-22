# RoslynMcp Code Review — Findings

Reviewed 2026-04-15. All source files in `src/RoslynMcp/` examined using the Roslyn MCP tools
themselves (dogfooding). No changes were made to any source file.

Severity scale: **High** = data loss or silent corruption · **Medium** = wrong output, misleading
results, or latent crash · **Low** = edge-case incorrect behaviour, UX gap, or observable
inconsistency · **Note** = design concern with no immediate correctness impact.

---

## Bugs

### [Medium] `SolutionDiff.BuildHunkList` — trailing-context loop corrupts hunk header

**File:** `src/RoslynMcp/SolutionDiff.cs`

The trailing-context loop at the end of `BuildHunkList` increments both `oi` (old-line cursor) and
`ni` (new-line cursor) unconditionally:

```csharp
for (var c = 0; c < context && oi < oldLen; c++, oi++, ni++, lcsIdx++)
```

The loop guard is `oi < oldLen`, but there is no corresponding `ni < newLen` guard. When the old
file has more trailing lines than the new file (a deletion at the end), `ni` advances past `newLen`.
The `Hunk` constructor computes `NewLines = ni - hunkNewStart`, producing an inflated count that is
written into the `@@ -a,b +c,d @@` header.

**Impact:** The diff is display-only and is never applied as a patch, so no file is corrupted.
However, the unified-diff output shown to an agent (or logged) will have an incorrect `+c,d` hunk
header when deletions trail the last changed region. Agents that parse the diff to reason about
line counts will get wrong numbers.

**Fix:** Add `ni < newLen` as a second guard on the trailing-context loop, or clamp `ni` to
`newLen` before computing `NewLines`.

---

### [Medium] `GetCallGraphTool` — property accessor calls silently omitted despite documented inclusion

**File:** `src/RoslynMcp/Tools/Analysis/GetCallGraphTool.cs`

The tool description explicitly states:

> "Includes constructors, extension methods, and **property accessor calls** made within the body."

The implementation walks the `IOperation` tree and pattern-matches on two operation kinds:

```csharp
ISymbol? callee = descendant switch {
    IInvocationOperation inv      => inv.TargetMethod,
    IObjectCreationOperation ctor => ctor.Constructor,
    _ => null
};
```

`IPropertyReferenceOperation` — which Roslyn uses for `foo.Bar` getter/setter accesses — is not
handled. Property accesses inside the analysed method body are silently dropped from the result.

**Impact:** An agent calling `roslyn_get_call_graph` on a method that accesses properties (the
common case) will receive an incomplete dependency graph. The tool description actively misleads
callers into expecting property calls to be present.

**Fix:** Extend the switch to include `IPropertyReferenceOperation`:

```csharp
IPropertyReferenceOperation prop => prop.Property,
```

and expose the property symbol as the callee. Consider also handling
`IEventReferenceOperation` for parity with the description's intent.

---

### [Low] `InsertLinesTool` — backup token is discarded; undo-by-token unavailable

**File:** `src/RoslynMcp/Tools/Editing/InsertLinesTool.cs`

`SavePreAsync` returns a token string that uniquely identifies the pre-insert snapshot:

```csharp
var preToken = await backups.SavePreAsync(fullPath, projectPath, toolName);
// preToken is only checked for != null; it is never stored or returned
```

The `InsertLinesResult` response type has no `BackupToken` field. An agent that inserts lines and
immediately wants to undo the change must fall back to `roslyn_local_history` with `action: list`
to discover the token by file path. By contrast, `WriteFileTool` exposes its `BackupToken`
directly in the response.

**Impact:** Inconvenient but not incorrect; the backup is saved and is reachable via list.

**Fix:** Add a `BackupToken` field to `InsertLinesResult` and populate it from `preToken`, matching
the `WriteFileTool` pattern.

---

### [Low] `SemanticSearchTool.DetermineContext` — raw and UTF-8 string tokens labelled as "code"

**File:** `src/RoslynMcp/Tools/Search/SemanticSearchTool.cs`

`DetermineContext` is called in `SearchInAll` mode to label each match with a context tag
(`"string"`, `"comment"`, `"identifier"`, `"code"`). Its string-token check covers only:

```csharp
if (token.IsKind(SyntaxKind.StringLiteralToken) ||
    token.IsKind(SyntaxKind.InterpolatedStringTextToken))
    return "string";
```

The seven additional string-token kinds introduced in C# 11–14 are not listed:
`SingleLineRawStringLiteralToken`, `MultiLineRawStringLiteralToken`,
`Utf8StringLiteralToken`, `Utf8SingleLineRawStringLiteralToken`,
`Utf8MultiLineRawStringLiteralToken`, `SingleLineRawStringLiteralToken` (verbatim).
Matches inside raw strings or UTF-8 strings will be tagged `"code"` instead of `"string"` in
"all" mode.

**Impact:** The context label in the `SearchInAll` result is wrong for these token kinds.
`SearchInStrings` (used when `context="strings"` is passed explicitly) is not affected — that
method has the full set of token kinds and correctly skips non-string tokens.

**Fix:** Add the missing `SyntaxKind` values to the `DetermineContext` string-token check, mirroring
the list already present in `SearchInStrings` and `BuildExcludedSpans`.

---

### [Low] `CheckForTruncation` threshold inconsistency — 1–4 byte truncations missed by editing tools

**File:** `src/RoslynMcp/Tools/RoslynMcpTool.cs` (base), callers in `ReplaceInFileTool.cs`,
`InsertLinesTool.cs`, `ReplaceInCodeTool.cs`

`WriteFileTool` uses a conservative custom truncation heuristic:

```csharp
if (writeBytes.Length > 4 && new FileInfo(fullPath).Length <= 4)
    // … truncation error
```

The shared base-class `CheckForTruncation` used by the other three editing tools only fires when
the on-disk length is exactly zero:

```csharp
expectedLength > 0 && new FileInfo(fullPath).Length == 0
```

A filesystem-level partial truncation that leaves 1–3 bytes on disk (e.g. a BOM with the rest
dropped) would be caught by `WriteFileTool`'s check but would pass silently through the
`ReplaceInFile` / `InsertLines` / `ReplaceInCode` path.

**Impact:** Very rare in practice; requires exotic filesystem or AV interference. When it does
occur the agent receives a success response for what is effectively a corrupted file.

**Fix:** Lower the base-class threshold to match `WriteFileTool`'s `<= 4` (or `<= max BOM size`)
so all editing tools apply the same heuristic.

---

## Design Concerns

### [Note] Approval token consumed before backup on `ApplyRenameTool` / `ApplySignatureChangeTool`

**Files:** `src/RoslynMcp/Tools/Rename/ApplyRenameTool.cs`,
`src/RoslynMcp/Tools/Refactoring/ApplySignatureChangeTool.cs`

Both tools call `approvals.Consume(token)` first, then attempt the backup write. If the backup
fails (disk full, permissions), the approval token has already been removed from the
`ApprovalStore`; the user must re-run the preview tool to generate a new token before retrying.
The error message communicates this, but the round-trip is avoidable.

A safer ordering would be: save backup → consume token → apply changes. If the backup save itself
must be atomic-with-consumption to prevent double-apply, the token could be restored to the store
on backup failure.

---

### [Note] `BackupStore.TryRestore` [Obsolete] bypasses `WorkspaceManager`

**File:** `src/RoslynMcp/BackupStore.cs`

The `[Obsolete]` `TryRestore` method writes directly to disk without going through
`WorkspaceManager.WriteAndInvalidate`, leaving the in-memory Roslyn compilation stale until the
file-system watcher fires (300 ms debounce). The correct path (`TryCheckAsync` + caller-supplied
`WriteAndInvalidate`) is used by `LocalHistoryTool.HandleApply`, which is the only current caller.

The method should be removed or sealed to `private` to prevent future callers from inadvertently
reintroducing the stale-workspace window. The `[Obsolete]` attribute alone does not prevent use.

---

### [Note] `PaginationCache.EvictExpired` — linear scan on every `Store` call

**File:** `src/RoslynMcp/PaginationCache.cs`

`EvictExpired` walks all entries on every `Store` invocation. The cache is capped at 50 entries
and the scan holds `lock(syncRoot)` for the duration, so this is not a throughput concern in
production. It is worth noting if the cap is ever raised: the scan is O(n), not O(expired), and
all entries are checked even when none are expired. A sorted-by-expiry structure (e.g. a
`SortedList<DateTimeOffset, string>`) would make eviction O(expired) and reduce lock contention.

---

### [Note] `SemanticSearchTool` — multi-TFM first-document-wins misses conditional compilation blocks

**File:** `src/RoslynMcp/Tools/Search/SemanticSearchTool.cs`

The `seenPaths` deduplication set ensures each physical `.cs` file is searched exactly once:

```csharp
if (document.FilePath is null || !seenPaths.Add(document.FilePath))
    continue;
```

In a multi-targeted project (`net8.0;net10.0`), the same source file appears in two Roslyn
`Project` objects with different `#if` preprocessor symbols active. The second occurrence is
silently skipped. A pattern that exists only in a `#if NET10_0_OR_GREATER` block will be found or
missed depending on which TFM's document was encountered first (solution project enumeration order
is not guaranteed).

The code comment acknowledges this limitation. An agent relying on `roslyn_semantic_search` to
find all occurrences of a symbol that is conditionally compiled for a specific TFM may receive
false-negative results. `roslyn_search_files` (text-based, no Roslyn parse) is not affected.

---

### [Note] Blocking `.GetAwaiter().GetResult()` calls in workspace loading

**Files:** `src/RoslynMcp/WorkspaceManager.Instance.cs` (lines ~530, ~542, ~566, ~1216)

Several points in workspace construction call async Roslyn APIs synchronously:

```csharp
OpenProjectAsync(…).GetAwaiter().GetResult()
GetCompilationAsync(…).GetAwaiter().GetResult()
```

These are inside constructors or synchronous code paths where `await` is not available, making
this the only practical option given the Roslyn API surface. The calls are intentional and
documented with inline comments. No deadlock risk exists because the server runs on the default
thread pool (no `SynchronizationContext` that would cause `.Result` to deadlock).

Listed here for completeness: if a future refactor allows the workspace loading path to be fully
`async`, these blocking calls should be converted to `await` to avoid monopolising a thread-pool
thread during potentially slow MSBuild evaluation.

---

## Minor Observations

- **`FindSolutionFileUpwards` returns `null` on ambiguity** (multiple `.sln`/`.slnx` files at the
  same directory level). This is correct per spec but means the entire solution is silently
  downgraded to single-project resolution rather than surfacing an error to the caller.

- **`ApprovalStore` max-10 LRU eviction is per-process, not per-session.** In a long-running
  server with many concurrent rename previews, an older preview can be silently evicted before the
  user calls apply. The 10-entry limit is generous for typical use, but a token-not-found error
  after eviction is indistinguishable from a token that was never generated.

- **`HooksInstalled()` uses `File.ReadAllText(claudeSettings).Contains("roslynmcp")`** as a
  heuristic. A `~/.claude.json` that happens to mention `roslynmcp` in any context (e.g. a
  comment or unrelated project name) will be treated as "hooks installed". The check is cached
  process-wide after the first call; any configuration change during a running server session is
  not picked up until restart.

- **`AddParameterEditor` uses `FirstOrDefault()` on `DeclaringSyntaxReferences`** (line ~47 of
  `SignatureChangePlanner.cs`). For partial methods the second partial declaration is skipped; the
  forwarding overload is inserted only in the file that contains the chosen declaration. Whether
  this is intentional depends on how partial methods should be handled by the signature-change
  workflow, but it is not documented.
