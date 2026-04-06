# roslyn_get_trivia (EXPERIMENTAL)

**Status:** Experimental — may be removed or significantly changed in future releases.

## Purpose

Returns whitespace, comments, and formatting trivia from C# source files. Useful for understanding indentation context, blank line patterns, and comment placement without parsing the entire file manually.

## When to Use

- **Understanding indentation context** — "What's the indentation level at line X?"
- **Analyzing comment placement** — "Are there XML doc comments on this method?"
- **Debugging formatting issues** — "Why is this blank line unindented?"
- **Building style tools** — Extract trivia patterns for analysis

## When NOT to Use

- **General style enforcement** — Use `.\scripts\Test-CodeStyle.ps1` instead
- **Reformatting code** — Use `roslyn_replace_in_code` or text tools
- **Semantic analysis** — Use dedicated semantic tools (`roslyn_get_symbol_info`, etc.)

## Parameters

```typescript
{
  projectPath: string;      // Required: Project or directory path
  filePath?: string;        // Required for analysis; omit when listing kinds
  startLine?: number;       // Optional: 1-based starting line
  endLine?: number;         // Optional: 1-based ending line
  syntaxKind?: string;      // Optional: Filter by node kind (e.g., 'IfStatement')
  triviaKind?: string;      // Optional: Filter by trivia kind (e.g., 'WhitespaceTrivia')
  includeLeading?: boolean; // Include leading trivia (default: true)
  includeTrailing?: boolean;// Include trailing trivia (default: true)
  skip?: number;            // Skip N results for pagination (default: 0)
  take?: number;            // Max results (default: 100, max: 500)
  page_token?: string;      // Pagination token from a previous response
  listSyntaxKinds?: boolean;// List all available syntax kinds (educational)
  listTriviaKinds?: boolean;// List all available trivia kinds (educational)
}
```

### Discovery: Listing Available Kinds

**Not sure what values to use?** The tool includes built-in discovery:

```json
// List all syntax kinds (IfStatement, ForEachStatement, etc.)
{
  "projectPath": "src/RoslynMcp",
  "listSyntaxKinds": true
}
```

Returns:
```json
{
  "mode": "list_syntax_kinds",
  "count": 387,
  "commonKinds": [
    "IfStatement", "ForEachStatement", "MethodDeclaration",
    "ClassDeclaration", "TryStatement", ...
  ],
  "allKinds": [ /* all 387 kinds */ ]
}
```

```json
// List all trivia kinds (WhitespaceTrivia, EndOfLineTrivia, etc.)
{
  "projectPath": "src/RoslynMcp",
  "listTriviaKinds": true
}
```

Returns:
```json
{
  "mode": "list_trivia_kinds",
  "count": 23,
  "commonKinds": [
    "WhitespaceTrivia", "EndOfLineTrivia",
    "SingleLineCommentTrivia", "MultiLineCommentTrivia",
    "SingleLineDocumentationCommentTrivia", ...
  ],
  "allKinds": [ /* all 23 kinds */ ]
}
```

**Pro tip:** Use `commonKinds` for the most frequently used values. Use `allKinds` when you need something specific.

### Common Syntax Kinds

**Most frequently used (use these first):**
- Control flow: `IfStatement`, `ElseClause`, `ForStatement`, `ForEachStatement`, `WhileStatement`, `DoStatement`
- Switch: `SwitchStatement`, `SwitchExpression`
- Error handling: `TryStatement`, `CatchClause`, `FinallyClause`
- Declarations: `MethodDeclaration`, `PropertyDeclaration`, `FieldDeclaration`
- Types: `ClassDeclaration`, `InterfaceDeclaration`, `StructDeclaration`, `RecordDeclaration`
- Statements: `ReturnStatement`, `ThrowStatement`, `LocalDeclarationStatement`

**Need something else?** Call the tool with `listSyntaxKinds=true` to see all 387 available kinds.

### Common Trivia Kinds

**Most frequently used (use these first):**
- `WhitespaceTrivia` — tabs and spaces
- `EndOfLineTrivia` — `\r\n` or `\n`
- `SingleLineCommentTrivia` — `// comments`
- `MultiLineCommentTrivia` — `/* comments */`
- `SingleLineDocumentationCommentTrivia` — `/// XML docs`
- `MultiLineDocumentationCommentTrivia` — `/** XML docs */`

**Need something else?** Call the tool with `listTriviaKinds=true` to see all 23 available kinds.

## Response Format

```json
{
  "file": "Core/WindowTracker.cs",
  "total_nodes": 47,
  "filtered_nodes": 12,
  "skip": 0,
  "take": 100,
  "results": [
    {
      "node_kind": "IfStatement",
      "node_span": {
        "start": 234,
        "end": 456,
        "start_line": 12,
        "end_line": 18
      },
      "node_text": "if(windowKey is null)",
      "leading_trivia": [
        {
          "kind": "WhitespaceTrivia",
          "text": "\t\t\t",
          "span": { "start": 231, "end": 234 }
        }
      ],
      "trailing_trivia": [
        {
          "kind": "WhitespaceTrivia",
          "text": " ",
          "span": { "start": 456, "end": 457 }
        }
      ]
    }
  ],
  "page_token": "abc123",
  "has_more": false
}
```

## Examples

### Example 0: Discover Available Options (Start Here!)

**If you're not a Roslyn expert**, start by discovering what filter values are available:

```json
{
  "projectPath": "src/RoslynMcp",
  "listSyntaxKinds": true
}
```

Returns a categorized list of all syntax node kinds you can filter by. The `commonKinds` array contains the most useful ones.

```json
{
  "projectPath": "src/RoslynMcp",
  "listTriviaKinds": true
}
```

Returns all available trivia kinds (whitespace, comments, etc.).

**Pro tip:** Run these once to understand what's available, then use the specific values in your analysis calls.

### Example 1: Get all trivia in a file

```json
{
  "projectPath": "src/RoslynMcp",
  "filePath": "Tools/Build/BuildTool.cs"
}
```

Returns all leading and trailing trivia for all syntax nodes in the file (up to 100 results).

### Example 2: Analyze indentation on specific lines

```json
{
  "projectPath": "src/RoslynMcp",
  "filePath": "WorkspaceManager.cs",
  "startLine": 50,
  "endLine": 75,
  "triviaKind": "WhitespaceTrivia"
}
```

Returns only whitespace trivia for syntax nodes between lines 50-75.

### Example 3: Find all XML doc comments

```json
{
  "projectPath": "src/RoslynMcp",
  "filePath": "Tools/RoslynMcpTool.cs",
  "triviaKind": "SingleLineDocumentationCommentTrivia",
  "includeTrailing": false
}
```

Returns only `///` XML documentation comments (leading trivia only).

### Example 4: Analyze `if` statement formatting

```json
{
  "projectPath": "src/RoslynMcp",
  "filePath": "Tools/Analysis/DiagnosticsTool.cs",
  "syntaxKind": "IfStatement"
}
```

Returns trivia for all `if` statements in the file. Useful for checking indentation consistency.

### Example 5: Check blank line patterns in a method

```json
{
  "projectPath": "src/RoslynMcp",
  "filePath": "WorkspaceManager.cs",
  "startLine": 100,
  "endLine": 150,
  "triviaKind": "EndOfLineTrivia"
}
```

Returns all line breaks in the specified range. Useful for analyzing blank line patterns.

## Performance Notes

- **Fast** — Parsing is cached in Roslyn's workspace
- **Efficient** — Only walks specified nodes/trivia, not entire tree
- **Paging** — Use `take` parameter to limit results for large files

## Limitations

- **C# files only** — Not applicable to non-C# files
- **Syntax-node based** — Trivia is attached to syntax nodes, not standalone
- **Truncated node text** — Node text is limited to 80 characters for readability

## Why Experimental?

This tool is **exploring** whether trivia analysis via MCP tools is useful for AI agents. Potential outcomes:

1. **Useful** — Agents use it frequently for style/formatting tasks → stabilize and document thoroughly
2. **Niche** — Only useful for specific edge cases → keep as experimental
3. **Redundant** — PowerShell scripts work better → remove in future release

**Feedback welcome!** If you find this tool useful (or useless), let us know.

## Alternative: Text-Based Tools

For most style enforcement tasks, **text-based tools are faster and simpler**:

- `.\scripts\Test-CodeStyle.ps1` — Automated style checking with auto-fix
- `roslyn_replace_in_file` — Text-level editing with regex
- PowerShell one-liners — Fast, simple, no Roslyn overhead

Use `roslyn_get_trivia` when you need **semantic context** (e.g., "Is this whitespace inside a `try` block or a method?"). Use text tools when you just need to process characters.

---

**See also:**
- [docs/development/CODE_STYLE_ENFORCEMENT.md](../development/CODE_STYLE_ENFORCEMENT.md) — Style enforcement guide
- [AGENTS.md § Code Style](../../AGENTS.md#code-style) — Complete style rules
