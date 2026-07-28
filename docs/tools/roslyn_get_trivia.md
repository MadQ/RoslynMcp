# roslyn_get_trivia (EXPERIMENTAL)

**Tool name:** `roslyn_get_trivia`  
**Status:** Experimental — may be removed or significantly changed in future releases.

## Purpose

Returns whitespace, comments, and formatting trivia from C# source files. Use it when you need trivia grouped by syntax node rather than raw file text — for example, to inspect indentation, blank lines, or comment placement in context.

For most code-understanding tasks, prefer:
- `roslyn_read_file` for content
- `roslyn_get_file_outline` for structure

## Parameters

```typescript
{
  projectPath: string;       // Required
  filePath?: string;         // Required for analysis; omit only for discovery
  startLine?: number;        // Optional 1-based start line; default: start of file
  endLine?: number;          // Optional 1-based end line; default: end of file
  syntaxKind?: string;       // Optional syntax node kind filter
  triviaKind?: string;       // Optional trivia kind filter
  includeLeading?: boolean;  // Default: true
  includeTrailing?: boolean; // Default: true
  skip?: number;             // Default: 0
  take?: number;             // Default: 100, max: 500
  page_token?: string;       // Optional pagination token
  listSyntaxKinds?: boolean; // Default: false; discovery mode
  listTriviaKinds?: boolean; // Default: false; discovery mode
}
```

## Discovery: list valid kinds first

```json
{
  "projectPath": "src/RoslynMcp/RoslynMcp.csproj",
  "listSyntaxKinds": true
}
```

Returns:

```json
{
  "mode": "list_syntax_kinds",
  "count": 568,
  "common_kinds": [
    "IfStatement",
    "ElseClause",
    "ForStatement",
    "ForEachStatement",
    "WhileStatement"
  ],
  "all_kinds": [ "AbstractKeyword", "AccessorList", "AddAccessorDeclaration" ]
}
```

```json
{
  "projectPath": "src/RoslynMcp/RoslynMcp.csproj",
  "listTriviaKinds": true
}
```

Returns:

```json
{
  "mode": "list_trivia_kinds",
  "count": 31,
  "common_kinds": [
    "WhitespaceTrivia",
    "EndOfLineTrivia",
    "SingleLineCommentTrivia",
    "MultiLineCommentTrivia",
    "SingleLineDocumentationCommentTrivia"
  ],
  "all_kinds": [ "BadDirectiveTrivia", "ConflictMarkerTrivia", "DefineDirectiveTrivia" ]
}
```

Use `common_kinds` as the quick shortlist and `all_kinds` when you need an exact Roslyn kind name.

## Response format

```json
{
  "file": "Tools/Analysis/GetTriviaTool.cs",
  "total_nodes": 34,
  "filtered_nodes": 20,
  "skip": 0,
  "take": 3,
  "results": [
    {
      "node_kind": "AttributeList",
      "node_span": {
        "start": 440,
        "end": 560,
        "start_line": 14,
        "end_line": 14
      },
      "node_text": "[McpServerTool(Name = \"roslyn_get_trivia\", ReadOnly = true, Title = \"Get Trivia\"…",
      "leading_trivia": [
        {
          "kind": "WhitespaceTrivia",
          "text": "    ",
          "span": { "start": 436, "end": 440 }
        }
      ],
      "trailing_trivia": []
    }
  ],
  "page_token": "abc123",
  "has_more": true
}
```

### Notes

- Results are grouped by syntax node.
- `node_text` is truncated for readability.
- `filtered_nodes` is the number of nodes that still have matching trivia after filters are applied.
- Use `page_token` to fetch the next page without re-running the query.

## Common use cases

### Analyze indentation in a range

```json
{
  "projectPath": "src/RoslynMcp/RoslynMcp.csproj",
  "filePath": "Tools/Analysis/GetTriviaTool.cs",
  "startLine": 14,
  "endLine": 20,
  "triviaKind": "WhitespaceTrivia"
}
```

### Inspect only `if` statement trivia

```json
{
  "projectPath": "src/RoslynMcp/RoslynMcp.csproj",
  "filePath": "Tools/Analysis/DiagnosticsTool.cs",
  "syntaxKind": "IfStatement"
}
```

### Extract XML doc comments

```json
{
  "projectPath": "src/RoslynMcp/RoslynMcp.csproj",
  "filePath": "Tools/RoslynMcpTool.cs",
  "triviaKind": "SingleLineDocumentationCommentTrivia",
  "includeTrailing": false
}
```

## Helpful error shape

If `syntaxKind` matches no nodes in the selected range, the tool returns:

```json
{
  "error": "no_matching_nodes",
  "message": "No syntax nodes of kind 'if' found in the specified range.",
  "hint": "Use listSyntaxKinds=true to see all available syntax kinds, or check spelling (e.g., 'IfStatement' not 'if').",
  "provided_kind": "if",
  "common_kinds": [ "IfStatement", "ElseClause", "ForStatement" ]
}
```

## Limits and behavior

- C# files only
- `filePath` is required unless using `listSyntaxKinds` or `listTriviaKinds`
- Default page size is 100; maximum is 500
- `includeLeading` and `includeTrailing` default to `true`

## When not to use this tool

- General content reading → `roslyn_read_file`
- Structural overview → `roslyn_get_file_outline`
- Semantic symbol analysis → `roslyn_get_symbol_info`, `roslyn_find_references`, etc.

## See also

- [roslyn_get_trivia_quickref.md](roslyn_get_trivia_quickref.md)
- [docs/development/CODE_STYLE_ENFORCEMENT.md](../development/CODE_STYLE_ENFORCEMENT.md)
