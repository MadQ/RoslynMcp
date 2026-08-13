# Quick Reference: roslyn_get_trivia

**Tool name:** `roslyn_get_trivia`  
**Status:** **EXPERIMENTAL**

## TL;DR

```json
// Discovery
{ "projectPath": "src/RoslynMcp/RoslynMcp.csproj", "listSyntaxKinds": true }
{ "projectPath": "src/RoslynMcp/RoslynMcp.csproj", "listTriviaKinds": true }

// Analysis
{
  "projectPath": "src/RoslynMcp/RoslynMcp.csproj",
  "filePath": "Tools/Analysis/GetTriviaTool.cs",
  "syntaxKind": "IfStatement",
  "triviaKind": "WhitespaceTrivia"
}
```

Discovery returns:
- `list_syntax_kinds` → `count: 568`, plus `common_kinds` and `all_kinds`
- `list_trivia_kinds` → `count: 31`, plus `common_kinds` and `all_kinds`

## Common use cases

| Task | Parameters |
|------|------------|
| Discover valid kinds | `listSyntaxKinds: true` or `listTriviaKinds: true` |
| Check indentation | `triviaKind: "WhitespaceTrivia"` |
| Find blank lines | `triviaKind: "EndOfLineTrivia"` |
| Extract comments | `triviaKind: "SingleLineCommentTrivia"` |
| Extract XML docs | `triviaKind: "SingleLineDocumentationCommentTrivia"` |
| Inspect `if` formatting | `syntaxKind: "IfStatement"` |
| Limit to a range | `startLine: 50, endLine: 100` |

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
      "node_span": { "start": 440, "end": 560, "start_line": 14, "end_line": 14 },
      "node_text": "[McpServerTool(Name = \"roslyn_get_trivia\", ReadOnly = true, Title = \"Get Trivia\"…",
      "leading_trivia": [
        { "kind": "WhitespaceTrivia", "text": "    ", "span": { "start": 436, "end": 440 } }
      ],
      "trailing_trivia": []
    }
  ],
  "page_token": "abc123",
  "has_more": true
}
```

## Common kinds

### Syntax

`IfStatement`, `ElseClause`, `ForStatement`, `ForEachStatement`, `WhileStatement`, `DoStatement`, `SwitchStatement`, `SwitchExpression`, `TryStatement`, `MethodDeclaration`

### Trivia

`WhitespaceTrivia`, `EndOfLineTrivia`, `SingleLineCommentTrivia`, `MultiLineCommentTrivia`, `SingleLineDocumentationCommentTrivia`, `MultiLineDocumentationCommentTrivia`

## Helpful error

```json
{
  "error": "no_matching_nodes",
  "message": "No syntax nodes of kind 'if' found in the specified range.",
  "hint": "Use listSyntaxKinds=true to see all available syntax kinds, or check spelling (e.g., 'IfStatement' not 'if').",
  "provided_kind": "if",
  "common_kinds": [ "IfStatement", "ElseClause", "ForStatement" ]
}
```

## Use / avoid

✅ Use for:
- indentation context
- blank-line/comment analysis
- trivia-aware formatting investigation

❌ Avoid for:
- normal file reading → `roslyn_read_file`
- structure-only browsing → `roslyn_get_file_outline`
- semantic symbol analysis → `roslyn_get_symbol_info`, `roslyn_find_references`

## Full docs

See [roslyn_get_trivia.md](roslyn_get_trivia.md).
