# Quick Reference: roslyn_get_trivia

**EXPERIMENTAL** — Trivia extraction tool for understanding C# formatting and whitespace.

## TL;DR

```json
// Step 1: Discover available values
{ "projectPath": ".", "listSyntaxKinds": true }
{ "projectPath": ".", "listTriviaKinds": true }

// Step 2: Analyze specific patterns
{
  "projectPath": ".",
  "filePath": "MyFile.cs",
  "syntaxKind": "IfStatement",        // Filter by node type
  "triviaKind": "WhitespaceTrivia"    // Filter by trivia type
}
```

## Common Use Cases

| Task | Parameters |
|------|------------|
| **Discover options** | `listSyntaxKinds: true` or `listTriviaKinds: true` |
| **Check indentation** | `triviaKind: "WhitespaceTrivia"` |
| **Find blank lines** | `triviaKind: "EndOfLineTrivia"` |
| **Extract comments** | `triviaKind: "SingleLineCommentTrivia"` |
| **XML doc comments** | `triviaKind: "SingleLineDocumentationCommentTrivia"` |
| **Format of `if` blocks** | `syntaxKind: "IfStatement"` |
| **Specific line range** | `startLine: 50, endLine: 100` |

## Response Format

```json
{
  "file": "MyFile.cs",
  "totalNodes": 47,
  "filteredNodes": 12,
  "results": [
    {
      "nodeKind": "IfStatement",
      "nodeSpan": { "start": 234, "end": 456, "startLine": 12, "endLine": 18 },
      "nodeText": "if(condition)",
      "leadingTrivia": [
        { "kind": "WhitespaceTrivia", "text": "\t\t", "span": {...} }
      ],
      "trailingTrivia": [ ... ]
    }
  ]
}
```

## Top 10 Syntax Kinds

1. `IfStatement` / `ElseClause`
2. `ForEachStatement` / `ForStatement` / `WhileStatement`
3. `MethodDeclaration`
4. `ClassDeclaration`
5. `TryStatement` / `CatchClause` / `FinallyClause`
6. `SwitchStatement` / `SwitchExpression`
7. `PropertyDeclaration`
8. `ReturnStatement`
9. `LocalDeclarationStatement`
10. `NamespaceDeclaration`

## Top 6 Trivia Kinds

1. `WhitespaceTrivia` — tabs/spaces
2. `EndOfLineTrivia` — line breaks
3. `SingleLineCommentTrivia` — `// comments`
4. `MultiLineCommentTrivia` — `/* comments */`
5. `SingleLineDocumentationCommentTrivia` — `/// XML docs`
6. `MultiLineDocumentationCommentTrivia` — `/** XML docs */`

## When to Use This Tool

✅ **Use when:**
- Understanding indentation context for code generation
- Analyzing comment placement patterns
- Debugging why formatter produces specific output
- Building custom style analysis tools

❌ **Don't use when:**
- Enforcing style rules → Use `.\scripts\Test-CodeStyle.ps1`
- Editing code → Use `roslyn_replace_in_code` or `roslyn_replace_in_file`
- Semantic analysis → Use dedicated tools (`roslyn_get_symbol_info`, etc.)

## Helpful Errors

If you use an invalid kind name, the tool returns suggestions:

```json
{
  "error": "no_matching_nodes",
  "message": "No syntax nodes of kind 'if' found...",
  "hint": "Use listSyntaxKinds=true to see all available syntax kinds, or check spelling (e.g., 'IfStatement' not 'if').",
  "commonKinds": [ "IfStatement", "ForEachStatement", ... ]
}
```

## Full Documentation

See [docs/tools/roslyn_get_trivia.md](roslyn_get_trivia.md) for complete details, all examples, and design rationale.
