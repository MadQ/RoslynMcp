using Microsoft.CodeAnalysis.CSharp;
using System.Diagnostics.CodeAnalysis;

namespace RoslynMcp.Tools;

/// <summary>
///     Discovery helpers for RoslynMcpTool — provides educational/enumeration capabilities
///     for tools that accept Roslyn enum values as parameters.
/// </summary>
internal abstract partial class RoslynMcpTool
{
// ── Precomputed Kind Arrays ───────────────────────────────────────────────────

// Computed once at startup to avoid repeated Enum.GetValues allocations per tool call.
private static readonly string[] AllSyntaxKinds = [..
Enum.GetValues(typeof(SyntaxKind))
.Cast<SyntaxKind>()
.Where(k => k != SyntaxKind.None && k != SyntaxKind.List)
.Select(k => k.ToString())
.OrderBy(s => s)
]
;

private static readonly string[] AllTriviaKinds = [..
Enum.GetValues(typeof(SyntaxKind))
.Cast<SyntaxKind>()
.Where(k => k.ToString().EndsWith("Trivia"))
.Select(k => k.ToString())
.OrderBy(s => s)
]
;

protected static readonly string[] CommonSyntaxKinds = [
// Control flow
"IfStatement", "ElseClause",
"ForStatement", "ForEachStatement", "WhileStatement", "DoStatement",
"SwitchStatement", "SwitchExpression",

// Error handling
"TryStatement", "CatchClause", "FinallyClause",

// Declarations
"MethodDeclaration", "PropertyDeclaration", "FieldDeclaration",
"ClassDeclaration", "InterfaceDeclaration", "StructDeclaration", "RecordDeclaration",
"NamespaceDeclaration", "FileScopedNamespaceDeclaration",

// Statements
"UsingDirective", "LocalDeclarationStatement",
"ReturnStatement", "ThrowStatement", "YieldReturnStatement"
];

protected static readonly string[] CommonTriviaKinds = [
"WhitespaceTrivia",
"EndOfLineTrivia",
"SingleLineCommentTrivia",
"MultiLineCommentTrivia",
"SingleLineDocumentationCommentTrivia",
"MultiLineDocumentationCommentTrivia",
"DisabledTextTrivia",
"PreprocessingMessageTrivia"
];

// ── Try-Pattern Discovery Handler ─────────────────────────────────────────────

/// <summary>
///     Handles discovery requests using the Try pattern. Returns true if a discovery
///     parameter was set, with the discovery result in the out parameter.
/// </summary>
protected static bool TryHandleDiscovery(
bool listSyntaxKinds,
bool listTriviaKinds,
bool listMemberKinds,
bool listTypeKinds,
bool listSearchContexts,
[NotNullWhen(true)] out object? discovery)
{
discovery = null;

if(listSyntaxKinds) {

discovery = ListSyntaxKinds();
return true;
}

if(listTriviaKinds) {

discovery = ListTriviaKinds();
return true;
}

if(listMemberKinds) {

discovery = ListMemberKinds();
return true;
}

if(listTypeKinds) {

discovery = ListTypeKinds();
return true;
}

if(listSearchContexts) {

discovery = ListSearchContexts();
return true;
}

return false;
}

// ── Individual Discovery Methods ──────────────────────────────────────────────

private static object ListSyntaxKinds()
{
return new DiscoveryKindsResult(
"list_syntax_kinds",
AllSyntaxKinds.Length,
CommonSyntaxKinds,
AllSyntaxKinds
);
}

private static object ListTriviaKinds()
{
return new DiscoveryKindsResult(
"list_trivia_kinds",
AllTriviaKinds.Length,
CommonTriviaKinds,
AllTriviaKinds
);
}

private static object ListMemberKinds()
{
return new DiscoveryValuesResult(
"list_member_kinds",
["field", "property", "method", "event", "enum"])
{
Hint = "Use these values with roslyn_get_type_members memberKind parameter"
};
}

private static object ListTypeKinds()
{
return new DiscoveryValuesResult(
"list_type_kinds",
["class", "interface", "enum", "struct", "record", "delegate"])
{
Hint = "Use these values with roslyn_list_types kindFilter parameter"
};
}

private static object ListSearchContexts()
{
return new DiscoveryContextsResult(
"list_search_contexts",
["comments", "strings", "identifiers", "code", "xmldocs", "all"])
{
Hint = "Use these values with roslyn_semantic_search context parameter"
};
}

// ── Error Helpers: No Matching Nodes ──────────────────────────────────────────

protected static object NoMatchingSyntaxKindError(string providedKind)
{
return new DiscoveryNoMatchResult(
$"No syntax nodes of kind '{providedKind}' found in the specified range.",
providedKind,
CommonSyntaxKinds)
{
Error = "no_matching_nodes",
Hint  = "Use listSyntaxKinds=true to see all available syntax kinds, or check spelling (e.g., 'IfStatement' not 'if')."
};
}

protected static object NoMatchingMemberKindError(string providedKind)
{
return new DiscoveryNoMatchResult(
$"Invalid member kind: '{providedKind}'.",
providedKind,
["field", "property", "method", "event", "enum"])
{
Error = "invalid_member_kind",
Hint  = "Use listMemberKinds=true to see all available member kinds."
};
}

protected static object NoMatchingTypeKindError(string providedKind)
{
return new DiscoveryNoMatchResult(
$"Invalid type kind: '{providedKind}'.",
providedKind,
["class", "interface", "enum", "struct", "record", "delegate"])
{
Error = "invalid_type_kind",
Hint  = "Use listTypeKinds=true to see all available type kinds."
};
}
}
