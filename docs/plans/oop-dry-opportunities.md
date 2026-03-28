# OOP & DRY Opportunities

Analysis of the RoslynMcp codebase for duplication, extraction candidates, and structural improvements.
Assessed at v0.7.0-alpha (post `FindSymbol`/`FormatSymbolName` extraction).

---

## Phase 1 — High Value, Low Risk

### 1. Signature Formatting → `SymbolFormatter` utility

`FormatMethod()`, `FormatProperty()`, `FormatField()`, `FormatEvent()` are duplicated across 3 tools:
- `FileOutlineTool.cs`
- `GetSymbolDefinitionTool.cs`
- `TypeMembersTool.cs`

**Extract to:** `SymbolFormatter.cs` (static utility class)
**Effort:** Trivial (~30 duplicated lines)

### 2. XML Documentation Parsing → `DocumentationExtractor` utility

`ExtractDocSummary()` duplicated in `GetSymbolDefinitionTool` and `TypeMembersTool`. Full `ParseDocumentation()` in `GetSymbolDocumentationTool`.

**Extract to:** `DocumentationExtractor.cs` with `ExtractSummary()` and `Parse()` methods + `DocumentationComment` record
**Effort:** Small

### 3. SyntaxTree Lookup → `FindSyntaxTree` base class helper

Multiple tools repeat the pattern:
```csharp
var normalized = NormalizePath(filePath);
var tree = compilation.SyntaxTrees
    .FirstOrDefault(t => t.FilePath.EndsWith(normalized, ...));
```

Found in: `SymbolInfoTool`, `ReadFileTool`, `ReplaceInCodeTool`, `GetTriviaTool`, `GetUsingsTool`, `FileOutlineTool`, `GetLineCountTool`, `GetSymbolsInScopeTool`

**Extract to:** `RoslynMcpTool.FindSyntaxTree(compilation, filePath)`
**Effort:** Trivial (5 min add, 10 min rewire 8 call sites)

---

## Phase 2 — Medium Value

### 4. Error Response Factories → `ToolErrors` utility

Tools construct ad-hoc error objects with inconsistent field names and messages:
```csharp
new { error = "symbol_not_found", message = "..." }     // some tools
new { error = $"Symbol '{name}' not found." }            // other tools
scope.Failed("symbol not found", new { error = "..." })  // yet others
```

**Extract to:** `ToolErrors.cs` with factory methods:
- `SymbolNotFound(symbolName)`
- `FileNotFound(filePath)`
- `TypeNotFound(typeName)`

**Effort:** Small-Medium (audit all tools for error patterns)
**Note:** Standardizes user-facing error messages without changing tool logic.

### 5. SemanticSearch Filter Methods

`SearchInComments()`, `SearchInStrings()`, `SearchInIdentifiers()`, `SearchInXmlDocs()` follow the same structure (iterate descendants → check kind → regex match → yield result).

**Extract to:** Generic `SearchWithFilter(root, text, regex, context, predicate)` within `SemanticSearchTool`
**Effort:** Small
**Note:** Internal to one tool, not cross-tool. Worth doing if the tool grows more search contexts.

---

## Phase 3 — Future Work (aligns with scratchpad "replace anonymous types with records")

### 6. Response Records → `ToolResponses.cs`

Common response shapes that could become typed records:
- Paginated response: `items`, `total`, `skip`, `take`, `page_token`, `has_more`
- Symbol info response: `symbol_name`, `symbol_kind`, `file`, `line`, `column`
- Error response: `error`, `message`, `hint`

**Note:** Response shapes are intentionally slightly different per tool. Records should be guidance, not forced — tools with unique fields can extend or compose. Aligns with the scratchpad item for replacing anonymous types.

### 7. Enum Discovery Builders

`RoslynMcpTool.Discovery.cs` has `ListSyntaxKinds()` and `ListTriviaKinds()` with identical patterns (reflect over enum, filter, sort, return). Could be a generic `ListEnumValues<T>()`.

**Effort:** Trivial
**Note:** Low priority — only one file, only runs on explicit agent request.

---

## Already Well-Designed (No Action Needed)

- **`ToolScope` pattern** — consistent across all tools, well-structured
- **`TryGetCompilation`/`TryGetProject`** — intentional per-tool boilerplate, centralizing would hide error handling specifics
- **Diagnostic formatting** — `DiagnosticsTool` and `BuildTool` intentionally differ (Roslyn vs MSBuild output)
- **Project resolution** — `WorkspaceResolver` facade is clean, no further extraction needed
- **`Paginate<T>`/`PaginateAndStore<T>`/`TryServeCachedPage<T>`** — already extracted to base class

---

## Summary

| # | Opportunity | Priority | Effort | Files |
|---|------------|----------|--------|-------|
| 1 | SymbolFormatter utility | High | Trivial | 3 |
| 2 | DocumentationExtractor utility | High | Small | 3 |
| 3 | FindSyntaxTree base helper | High | Trivial | 8 |
| 4 | ToolErrors factory | Medium | Small-Med | All |
| 5 | SemanticSearch filter DRY | Medium | Small | 1 |
| 6 | Response records | Future | Medium | All |
| 7 | Enum discovery builder | Low | Trivial | 1 |
