# Discovery Pattern for RoslynMcp Tools

**Location:** `src/RoslynMcp/Tools/RoslynMcpTool.Discovery.cs`

## Overview

The discovery pattern allows tools to self-document by listing available parameter values. This makes tools usable by non-Roslyn experts without requiring external documentation.

## Usage Pattern

### 1. Add Discovery Parameters to Your Tool

```csharp
[McpServerTool(Name = "roslyn_my_tool")]
public object MyTool(
    [Description(ProjectPathDescription)] string projectPath,
    [Description("Filter by syntax kind. Use listSyntaxKinds=true to see options.")] 
    string? syntaxKind = null,
    [Description("Set to true to list all available syntax kinds.")] 
    bool listSyntaxKinds = false)
{
    using var scope = BeginTool("roslyn_my_tool");

    // Handle discovery requests using Try pattern
    if(TryHandleDiscovery(listSyntaxKinds, false, false, false, false, out var discovery))
        return discovery;

    // Normal tool execution...
}
```

### 2. Use Shared Error Helpers

When filtering returns no results, return a helpful error:

```csharp
if(!string.IsNullOrEmpty(syntaxKind)) {

    var filtered = nodes.Where(n => n.Kind().ToString() == syntaxKind).ToList();

    if(filtered.Count == 0)
        return NoMatchingSyntaxKindError(syntaxKind);

    nodes = filtered;
}
```

## Available Discovery Types

### `TryHandleDiscovery` Signature

```csharp
protected static bool TryHandleDiscovery(
    bool listSyntaxKinds,      // All C# syntax node types
    bool listTriviaKinds,      // Whitespace, comments, etc.
    bool listMemberKinds,      // field, property, method, event, enum
    bool listTypeKinds,        // class, interface, enum, struct, record, delegate
    bool listSearchContexts,   // comments, strings, identifiers, code, xmldocs, all
    [NotNullWhen(true)] out object? discovery)
```

### Usage Examples

```csharp
// Single discovery type
if(TryHandleDiscovery(listSyntaxKinds, false, false, false, false, out var discovery))
    return discovery;

// Multiple discovery types
if(TryHandleDiscovery(listSyntaxKinds, listTriviaKinds, false, false, false, out var discovery))
    return discovery;

// All discovery types
if(TryHandleDiscovery(
    listSyntaxKinds, 
    listTriviaKinds, 
    listMemberKinds, 
    listTypeKinds, 
    listSearchContexts, 
    out var discovery))
    return discovery;
```

## Available Error Helpers

### `NoMatchingSyntaxKindError(string providedKind)`

Returns structured error with:
- Clear error message
- Hint to use `listSyntaxKinds=true`
- List of common syntax kinds
- Provided value for debugging

### `NoMatchingMemberKindError(string providedKind)`

Returns error for invalid member kind with valid options.

### `NoMatchingTypeKindError(string providedKind)`

Returns error for invalid type kind with valid options.

## Common Syntax Kinds (Curated List)

The base class provides `GetCommonSyntaxKinds()` returning ~20 most useful kinds:
- Control flow: `IfStatement`, `ForEachStatement`, `WhileStatement`, etc.
- Error handling: `TryStatement`, `CatchClause`, `FinallyClause`
- Declarations: `MethodDeclaration`, `ClassDeclaration`, `PropertyDeclaration`, etc.
- Statements: `ReturnStatement`, `LocalDeclarationStatement`, etc.

## Common Trivia Kinds (Curated List)

The base class provides `GetCommonTriviaKinds()` returning ~8 most useful kinds:
- `WhitespaceTrivia`, `EndOfLineTrivia`
- `SingleLineCommentTrivia`, `MultiLineCommentTrivia`
- `SingleLineDocumentationCommentTrivia`, etc.

## When to Add Discovery

### ✅ Add Discovery When:
- Tool accepts enum-like string parameters (syntax kinds, member kinds, etc.)
- Valid values aren't obvious to non-Roslyn experts
- There are many possible values (>5)
- Helpful error messages with suggestions would improve UX

### ❌ Skip Discovery When:
- Parameters are self-explanatory (file paths, line numbers, booleans)
- Only 2-3 possible values (document in parameter description instead)
- Values are user-defined (project-specific, not framework enums)

## Tools Currently Using Discovery

1. **`roslyn_get_trivia`** (EXPERIMENTAL)
   - `listSyntaxKinds`, `listTriviaKinds`
   - First tool to use the pattern

## Tools That Should Add Discovery

High-value candidates:

1. **`roslyn_semantic_search`**
   - Add `listSearchContexts` for `context` parameter
   - Values: comments, strings, identifiers, code, xmldocs, all

2. **`roslyn_get_type_members`**
   - Add `listMemberKinds` for `memberKind` parameter
   - Values: field, property, method, event, enum

3. **`roslyn_list_types`**
   - Add `listTypeKinds` for `kindFilter` parameter
   - Values: class, interface, enum, struct, record, delegate

4. **`roslyn_replace_in_code`**
   - Add `listSyntaxKinds` for `nodeKind` parameter
   - Same values as `roslyn_get_trivia`

## Implementation Checklist

When adding discovery to a tool:

- [ ] Add `bool list*` parameter(s) to tool signature
- [ ] Document parameter with description and example
- [ ] Call `TryHandleDiscovery()` at start of method
- [ ] Return discovery object if `TryHandleDiscovery` returns true
- [ ] Use error helpers (`NoMatching*Error`) when filtering fails
- [ ] Update tool documentation with discovery examples
- [ ] Test discovery parameters return expected output

## Benefits

**For Users:**
- Self-documenting tools
- No need to memorize Roslyn enums
- Just-in-time learning (discover when needed)
- Helpful errors with suggestions

**For Maintainers:**
- Consistent discovery UX across all tools
- Shared implementation (DRY)
- Easy to add to new tools
- Reduced support burden (fewer "what values are valid?" questions)

---

**See also:**
- [docs/tools/roslyn_get_trivia.md](../../docs/tools/roslyn_get_trivia.md) — Complete example with discovery
- [docs/tools/roslyn_get_trivia_quickref.md](../../docs/tools/roslyn_get_trivia_quickref.md) — Quick reference
