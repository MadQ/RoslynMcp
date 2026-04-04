# RoslynMcp v0.4.0-alpha: Implementation Plan

> **Status: Shipped as of v0.4.0**

## Step 1 — Fix High-Priority Bugs

### 1a. Missing `[McpServerTool]` on `GetSymbolsInScopeTool`
**File:** `src/RoslynMcp/Tools/Analysis/GetSymbolsInScopeTool.cs:13`
- Add `[McpServerTool(Name = "roslyn_get_symbols_in_scope", ReadOnly = true)]` to the `GetSymbolsInScope` method
- Verify the tool appears in the MCP tool list after rebuild

### 1b. `WorkspaceResolver.GetRootPath` wrong for AdhocWorkspaces
**File:** `src/RoslynMcp/WorkspaceResolver.cs:65`
- Change `return Path.GetDirectoryName(resolved)!;`
- To `return resolved.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(resolved)! : resolved;`
- Test: call any tool that outputs relative paths against a directory path (no .csproj)

### 1c. Missing LRU eviction in `GetSolution`/`GetProject`/`GetWorkspaceInfo`
**File:** `src/RoslynMcp/WorkspaceManager.cs` (three cache-miss blocks)
- Extract the LRU eviction block from `GetCompilation` into a private `EvictLruIfFull()` helper
- Call it in the cache-miss path of `GetSolution`, `GetProject`, and `GetWorkspaceInfo`
- Verify: set `ROSLYNMCP_MAX_CACHED_WORKSPACES=1`, load two different projects via `GetSolution`, confirm only one remains cached

---

## Step 2 — Fix Remaining Bugs

### 2a. `TypeHierarchyTool` — `FindDerivedClassesAsync` misses interface implementors
**File:** `src/RoslynMcp/Tools/Analysis/TypeHierarchyTool.cs:44`
- Replace the single `FindDerivedClassesAsync` call with a branch:
  - If `type.TypeKind == TypeKind.Interface` → use `SymbolFinder.FindImplementationsAsync`
  - Else → use `SymbolFinder.FindDerivedClassesAsync`
- Test against a known interface (e.g., `IDisposable` or a project interface) and verify implementing classes appear

### 2b. `SemanticSearchTool.SearchInCode` — dead comment-skip logic
**File:** `src/RoslynMcp/Tools/Search/SemanticSearchTool.cs:278–281`
- Remove the dead `token.IsKind(SyntaxKind.SingleLineCommentTrivia)` checks (trivia kinds are never true on tokens)
- Replace with a check against `token.LeadingTrivia` and `token.TrailingTrivia`, consistent with how `DetermineContext` already handles it
- Test: search with `context: "code"` for a pattern that also appears in comments; verify comments are excluded

### 2c. `DiagnosticsTool` — warnings before errors
**File:** `src/RoslynMcp/Tools/Analysis/DiagnosticsTool.cs:41`
- Change `.OrderBy(d => d.Severity)` to `.OrderByDescending(d => d.Severity)`
- Errors should appear before warnings

### 2d. `ApplyRenameTool` — stale solution in `GetChanges`
**File:** `src/RoslynMcp/Tools/Rename/ApplyRenameTool.cs:43–50`
- The `filesChanged` count computes `op.NewSolution.GetChanges(oldSolution)` where `oldSolution` is freshly fetched and may differ from the preview-time baseline
- Store the original solution snapshot inside `PendingOperation` (alongside `NewSolution`) at preview time
- Use the stored snapshot in `ApplyToDiskAsync` and `GetChanges` instead of re-fetching from the workspace
- Update `ApprovalStore` and `PendingOperation` record accordingly

---

## Step 3 — Fix `roslyn_semantic_search` Duplicate Results

**Root cause hypothesis:** MSBuildWorkspace loads each target framework (`net8.0`, `net10.0`) as a separate `Project` in the solution. `SemanticSearchTool` iterates `solution.Projects` → each TFM's project contains the same source documents → every file is processed twice.

**Investigation:**
- Log `solution.Projects.Select(p => p.Name)` to confirm two projects exist for the same source
- Confirm document `FilePath` values are identical across the two projects

**Fix:**
- In `SemanticSearchTool`, deduplicate documents by `FilePath` before processing:
  ```csharp
  var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach (var project in solution.Projects)
      foreach (var document in project.Documents) {
          if (document.FilePath is null || !seenPaths.Add(document.FilePath))
              continue;
          // ... existing processing
      }
  ```
- Verify `total_matches` is halved on a multi-targeted project

---

## Step 4 — Add `AGENTS.md`

Create `AGENTS.md` at the repository root covering:
- Project layout (solution structure, where each concern lives)
- The canonical `projectPath` value for working on RoslynMcp itself (`src/RoslynMcp/RoslynMcp.csproj`)
- How to run the test harness (`dotnet run --project TestHarness/TestHarness.csproj`)
- Tool-choice guidance specific to this codebase (prefer `roslyn_*` tools, use `roslyn_replace_in_code` for `.cs` edits)
- Notes on multi-targeting and workspace behavior when using RoslynMcp on itself
- Known gotchas (list_types needs namespaceFilter, find_references is more reliable with containingType)

---

## Step 5 — Implement `roslyn_get_member_body`

**New file:** `src/RoslynMcp/Tools/Analysis/GetMemberBodyTool.cs`

**Tool name:** `roslyn_get_member_body`

**Parameters:**
- `symbolName` — the member name (method, property, field, constructor)
- `projectPath` — standard project path parameter
- `containingType` (optional) — disambiguate when multiple types have the same member name
- `filePath` (optional) — further narrow if needed

**Implementation sketch:**
1. Call `TryGetCompilation` as usual
2. Resolve the symbol using the existing `FindSymbol` pattern (same as other analysis tools)
3. Get `symbol.DeclaringSyntaxReferences` — take the first (or all, if overloaded)
4. For each reference: get the syntax node, get its span, read the source text via `tree.GetText()`
5. Return: file path (relative), start line, end line, and the source text of the span

**Response shape:**
```json
{
  "symbol_name": "RoslynMcp.WorkspaceManager.GetCompilation",
  "symbol_kind": "method",
  "file": "WorkspaceManager.cs",
  "start_line": 53,
  "end_line": 85,
  "body": "public Compilation GetCompilation(...) {\n    ...\n}"
}
```

**Edge cases to handle:**
- Partial methods / partial classes (multiple `DeclaringSyntaxReferences`) → return all parts with a `part_index` field
- Metadata-only symbols (no source) → return a clear message, same pattern as `get_symbol_definition`
- Overloaded methods without `containingType` → return all overloads with a `note` field suggesting disambiguation

**README update:** Add `roslyn_get_member_body` to the tools table.

---

## Order of Execution

| Step | Effort | Risk | Value |
|------|--------|------|-------|
| 1a — `[McpServerTool]` attribute | Trivial | None | High (tool currently invisible) |
| 1b — `GetRootPath` adhoc fix | Trivial | None | High (silent path corruption) |
| 1c — LRU eviction | Small | Low | Medium |
| 2a — TypeHierarchy interfaces | Small | Low | Medium |
| 2b — SearchInCode dead code | Small | Low | Medium |
| 2c — Diagnostics ordering | Trivial | None | Low |
| 2d — ApplyRename stale solution | Medium | Medium | Low |
| 3 — Semantic search duplicates | Small | Low | High (broken tool) |
| 4 — AGENTS.md | Small | None | Medium |
| 5 — `get_member_body` | Medium | Low | High (token reduction) |
