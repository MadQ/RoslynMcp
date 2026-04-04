# Roslyn Code Audit — `src/RoslynMcp`

Comprehensive analysis of Roslyn API usage correctness across all source files.
Audit date: 2026-03-27.

---

## Critical Issues (likely bugs)

### 1. `WorkspaceResolver.GetRootPath` returns wrong directory for AdhocWorkspace — **Fixed**

**File:** `WorkspaceResolver.cs` (fixed: now delegates to `GetWorkspaceInfo` which returns the correct `rootPath` directly)

---

### 2. MSBuildWorkspace does NOT watch files automatically

**File:** `WorkspaceManager.cs:399`

```csharp
// MSBuildWorkspace watches files via Roslyn's internal mechanisms — no manual watcher needed.
```

This comment is **incorrect**. `MSBuildWorkspace` is a snapshot workspace — it loads project state at `OpenProjectAsync` time and does not automatically detect file changes. There is no internal FileSystemWatcher. Only the AdhocWorkspace path has a watcher (line 423). External edits (e.g. user saves in their IDE while an agent is querying) will not be reflected until `InvalidateFile` is explicitly called by a tool.

---

### 3. Five tool methods are missing `[McpServerTool]` attributes — silently unregistered — **Fixed**

These tools have `[McpServerToolType]` on the class but **no** `[McpServerTool]` on the method, so the MCP framework never registers them:

| Tool class | Method | Expected MCP name |
|---|---|---|
| `FileOutlineTool.cs:13` | `GetFileOutline` | `roslyn_get_file_outline` |
| `GetSymbolsInScopeTool.cs:13` | `GetSymbolsInScope` | `roslyn_get_symbols_in_scope` |
| `GetUsingsTool.cs:13` | `GetUsings` | `roslyn_get_usings` |
| `GetLineCountTool.cs:13` | `GetLineCount` | `roslyn_get_line_count` |
| `SearchFilesTool.cs:13` | `SearchFiles` | `roslyn_search_files` |

These methods also lack `[Description]` on the method itself.

---

### 4. `AsSpan(skip, ...)` crashes when `skip >= array.Length`

Multiple tools use this pagination pattern without bounds-checking `skip`:

```csharp
var page = allResults.AsSpan(skip, Math.Min(take, allResults.Length - skip)).ToArray();
```

When `skip >= allResults.Length`, `allResults.Length - skip` is negative, and `AsSpan` throws `ArgumentOutOfRangeException`.

Affected tools:
- `FileOutlineTool.cs:38`
- `FindImplementationsTool.cs:61, 102`
- `FindReferencesTool.cs:63`
- `TypeHierarchyTool.cs:54`
- `TypeMembersTool.cs:49`

---

### 5. `FormatModifiers` omits `protected internal` and `private protected`

**Files:** `FileOutlineTool.cs:164-169`, `GetSymbolDefinitionTool.cs:176-181`, `TypeMembersTool.cs:175-181`

```csharp
var access = symbol.DeclaredAccessibility switch {
    Accessibility.Public    => "public",
    Accessibility.Private   => "private",
    Accessibility.Protected => "protected",
    Accessibility.Internal  => "internal",
    _                       => null   // ProtectedOrInternal, ProtectedAndInternal silently dropped
};
```

`Accessibility.ProtectedOrInternal` (`protected internal`) and `Accessibility.ProtectedAndInternal` (`private protected`) both fall to the `_` arm and produce no modifier text. The member appears to have no access modifier in tool output.

---

## High Severity (correctness concerns)

### 6. `SemanticSearchTool.SearchInCode` has dead code — trivia kind checks on tokens never match

**File:** `SemanticSearchTool.cs:278-280`

```csharp
if(token.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
   token.IsKind(SyntaxKind.MultiLineCommentTrivia))
    continue;
```

`SingleLineCommentTrivia` and `MultiLineCommentTrivia` are **trivia** kinds, never **token** kinds. A `SyntaxToken` will never have kind `SingleLineCommentTrivia`. This check never matches. (Comments are already excluded because `DescendantTokens()` doesn't descend into structured trivia by default, so the end behavior is correct — but the code is misleading.)

---

### 7. `ReplaceInCodeTool` — fallback `ParseExpression` is wrong for many syntax kinds

**File:** `ReplaceInCodeTool.cs:117`

```csharp
_ => (SyntaxNode?) SyntaxFactory.ParseExpression(replacement)
```

For any syntax kind not in the explicit switch arms (e.g. `UsingDirective`, `NamespaceDeclaration`, `LocalDeclarationStatement`, `ReturnStatement`, `IfStatement`, etc.), the replacement is parsed as an **expression**. This will either fail to parse or produce garbage for most statement/directive kinds.

---

### 8. `SimpleNameFinder` ignores namespace qualification for dotted names

**File:** `SymbolVisitors.cs:9-12`

```csharp
private readonly string simpleName = name.Contains('.')
    ? name[(name.LastIndexOf('.') + 1)..]
    : name;
```

When searching for `"MyNamespace.MyClass"`, only `"MyClass"` is matched. The namespace part `"MyNamespace"` is completely discarded. If two types in different namespaces share the simple name, the **first one found** (arbitrary walk order) is returned, which may be the wrong type.

---

### 9. `TypeHierarchyTool` uses `FindDerivedClassesAsync` which returns nothing for interfaces

**File:** `TypeHierarchyTool.cs:44`

```csharp
var derivedRefs = await RoslynSymbolFinder.FindDerivedClassesAsync(type, solution);
```

`FindDerivedClassesAsync` finds classes that *derive from* a given type. For interfaces, there is no class derivation (only implementation), so this always returns empty. The `total_derived_types` field will always be 0 for interface queries. Should use `FindImplementationsAsync` for interfaces.

---

### 10. `PreviewRenameTool` — symbol found from compilation may be invalid in the solution

**File:** `PreviewRenameTool.cs:37-48`

```csharp
var symbol   = FindSymbol(compilation, symbolName, containingType);  // from GetCompilation
var solution = workspace.GetSolution(projectPath);                   // potentially different state
var newSolution = await Renamer.RenameSymbolAsync(solution, symbol, ...);
```

The `symbol` is obtained from a `Compilation` and then passed to `Renamer.RenameSymbolAsync` along with a separately fetched `Solution`. If the workspace was invalidated between these calls, the symbol may not exist in that solution, causing `RenameSymbolAsync` to fail or produce incorrect results.

---

### 11. `ApplyRenameTool` — stale solution overwrites post-preview file changes

**File:** `ApplyRenameTool.cs:43-44`

```csharp
var oldSolution = workspace.GetSolution(projectPath);
await SolutionDiff.ApplyToDiskAsync(oldSolution, op.NewSolution);
```

`op.NewSolution` was computed at **preview** time. `oldSolution` is fetched at **apply** time. If files were modified between preview and apply (by the agent or editor), `ApplyToDiskAsync` writes `op.NewSolution`'s document content to disk, **overwriting** those intermediate changes without warning.

---

### 12. `ListTypesTool` walks the entire global namespace, including all referenced assemblies

**File:** `ListTypesTool.cs:29`

```csharp
CollectTypes(compilation.GlobalNamespace, allTypes);
```

For MSBuild workspaces, `GlobalNamespace` includes types from every referenced assembly (NuGet packages, .NET runtime). Without a `namespaceFilter`, this returns thousands of types (`System.String`, `System.Collections.Generic.List`, etc.) from framework and package assemblies.

---

### 13. `PreviewRenameTool.SymbolKey` doesn't distinguish overloaded methods

**File:** `PreviewRenameTool.cs:73-74`

```csharp
=> $"{symbol.ContainingType?.ToDisplayString() ?? ...}::{symbol.Name}";
```

No parameter types in the key. Approving a rename for `MyClass::Method(int)` session-wide also auto-approves `MyClass::Method(string)`.

---

## Medium Severity (performance / robustness)

### 14. `GetCompilation`/`GetSolution`/`GetProject` hold `cacheLock` during workspace loading

**File:** `WorkspaceManager.cs:57-84`

MSBuildWorkspace loading (`OpenProjectAsync`) can take seconds to minutes. The lock is held for the entire duration, blocking **all** other tool calls from even looking up already-cached workspaces.

---

### 15. `SolutionDiff.LongestCommonSubsequence` uses O(n*m) memory

**File:** `SolutionDiff.cs:88`

```csharp
var dp = new int[m + 1, n + 1];
```

For two 10,000-line files, this allocates a 100M-element array (~400 MB). Large file diffs could cause `OutOfMemoryException`.

---

### 16. `DiagnosticsTool` calls `compilation.GetDiagnostics()` even for single-file queries

**File:** `DiagnosticsTool.cs:28`

`compilation.GetDiagnostics()` computes diagnostics for the **entire project**, then filters by file path. For single-file queries, using `compilation.GetSemanticModel(tree).GetDiagnostics()` would be significantly faster.

---

### 17. `TryApplyChanges` return value silently ignored

**Files:** `WorkspaceManager.cs:463, 628`

`workspace.TryApplyChanges(newSolution)` returns `bool`, but the result is never checked. If the change fails to apply (concurrent modification, unsupported change type), the workspace silently stays at the old state.

---

### 18. `GetTriviaTool` — no bounds checking on `startLine`/`endLine` — **Fixed**

**File:** `GetTriviaTool.cs` (fixed: now uses `Math.Clamp` to clamp both values into `[1, lineCount]` before indexing)

---

### 19. `msbuildRegistered` flag lacks `volatile` in double-checked locking

**File:** `WorkspaceManager.cs:38`

```csharp
static bool msbuildRegistered;
```

Read at line 337 without lock, written inside lock at line 354. The .NET memory model (weaker than x86) doesn't guarantee visibility without `volatile` or `Interlocked`. On x86/x64 this happens to work, but it's technically incorrect for ARM or future .NET implementations.

---

### 20. `SolutionDiff` splits lines on `\n` only — `\r` bleeds into diff output

**File:** `SolutionDiff.cs:64-65`

```csharp
var oldLines = oldText.Split('\n');
```

On Windows (where files have `\r\n`), each line retains a trailing `\r`. Diff output like `" \tvar x = 1;\r"` has a spurious `\r` in every line.

---

### 21. `DiagnosticsTool` error wraps anonymous object in `.ToString()` — unreadable output

**File:** `DiagnosticsTool.cs:25`

```csharp
return new[] { error.ToString()! };
```

`error` is an anonymous object. `.ToString()` produces `"{ error = ..., message = ... }"` as a raw string inside a `string[]`. Other tools return the error object directly. This tool returns a less useful error representation.

---

### 22. `SemanticSearchTool.SearchInCode` only matches individual tokens, not multi-token patterns

**File:** `SemanticSearchTool.cs:272-303`

The regex is applied to each **individual token's** text. A search for `"new List"` or `"public static"` will never match because these span multiple tokens (`new` + `List`, `public` + `static`). The `SearchInAll` mode handles this by searching entire lines.

---

### 23. `ApprovalStore` has no eviction for unconsumed pending operations

**File:** `ApprovalStore.cs:12`

Each `PendingOperation` holds a full `Solution` snapshot. Unclaimed previews accumulate without bound. Long-running sessions with many previews could consume significant memory.

---

### 24. FSW feedback loop: `TryApplyChanges` writes trigger re-detection

**File:** `WorkspaceManager.Instance.cs` (FlushMSBuild)

`MSBuildWorkspace.TryApplyChanges(WithDocumentText)` writes the updated text back to the file on disk. This triggers the FileSystemWatcher's `Changed` event, creating an infinite cycle throttled only by the 300ms debounce. Each cycle writes the same content but triggers another event.

**Fix (v0.6.0):** Suppress FSW events during `TryApplyChanges` via `watcher.EnableRaisingEvents = false/true`.

**Future consideration:** Self-write tracking — track paths written by `TryApplyChanges` and skip FSW events for those paths within a short window. More precise, no lost events. See [Issue #49](https://github.com/MadQ/RoslynMcp/issues/49).
