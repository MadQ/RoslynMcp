using Microsoft.CodeAnalysis.CSharp;
using System.Diagnostics.CodeAnalysis;

namespace RoslynMcp.Tools;

/// <summary>
///     Discovery helpers for RoslynMcpTool — provides educational/enumeration capabilities
///     for tools that accept Roslyn enum values as parameters.
/// </summary>
internal abstract partial class RoslynMcpTool
{
    // ── Try-Pattern Discovery Handler ────────────────────────────────────────────

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

    // ── Individual Discovery Methods ─────────────────────────────────────────────

    /// <summary>
    ///     Returns all available C# syntax kinds with common ones highlighted.
    ///     Use this for tools that accept syntaxKind parameters.
    /// </summary>
    private static object ListSyntaxKinds()
    {
        string[] all = [..
            Enum.GetValues(typeof(SyntaxKind))
                .Cast<SyntaxKind>()
                .Where(k => k != SyntaxKind.None && k != SyntaxKind.List)
                .Select(k => k.ToString())
                .OrderBy(s => s)
        ];

        return new DiscoveryKindsResult(
            "list_syntax_kinds",
            all.Length,
            GetCommonSyntaxKinds(),
            all
        );
    }

    /// <summary>
    ///     Returns curated list of most commonly used syntax kinds.
    ///     Control flow, declarations, statements.
    /// </summary>
    protected static string[] GetCommonSyntaxKinds()
    {
        return [
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
    }

    // ── Discovery: Trivia Kinds ──────────────────────────────────────────────────

    /// <summary>
    ///     Returns all available trivia kinds (whitespace, comments, etc.).
    ///     Use this for tools that accept triviaKind parameters.
    /// </summary>
    private static object ListTriviaKinds()
    {
        string[] all = [..
            Enum.GetValues(typeof(SyntaxKind))
                .Cast<SyntaxKind>()
                .Where(k => k.ToString().EndsWith("Trivia"))
                .Select(k => k.ToString())
                .OrderBy(s => s)
        ];

        return new DiscoveryKindsResult(
            "list_trivia_kinds",
            all.Length,
            GetCommonTriviaKinds(),
            all
        );
    }

    /// <summary>
    ///     Returns curated list of most commonly used trivia kinds.
    ///     Whitespace, line breaks, comments.
    /// </summary>
    protected static string[] GetCommonTriviaKinds()
    {
        return [

            "WhitespaceTrivia",
            "EndOfLineTrivia",
            "SingleLineCommentTrivia",
            "MultiLineCommentTrivia",
            "SingleLineDocumentationCommentTrivia",
            "MultiLineDocumentationCommentTrivia",
            "DisabledTextTrivia",
            "PreprocessingMessageTrivia"
        ];
    }

    // ── Discovery: Member Kinds ──────────────────────────────────────────────────

    /// <summary>
    ///     Returns all available member kinds for type member filtering.
    ///     Use this for tools that accept memberKind parameters.
    /// </summary>
    private static object ListMemberKinds()
    {
        return new DiscoveryValuesResult(
            "list_member_kinds",
            ["field", "property", "method", "event", "enum"],
            "Use these values with roslyn_get_type_members memberKind parameter"
        );
    }

    // ── Discovery: Type Kinds ────────────────────────────────────────────────────

    /// <summary>
    ///     Returns all available type kinds for type listing/filtering.
    ///     Use this for tools that accept kindFilter or typeKind parameters.
    /// </summary>
    private static object ListTypeKinds()
    {
        return new DiscoveryValuesResult(
            "list_type_kinds",
            ["class", "interface", "enum", "struct", "record", "delegate"],
            "Use these values with roslyn_list_types kindFilter parameter"
        );
    }

    // ── Discovery: Search Contexts ───────────────────────────────────────────────

    /// <summary>
    ///     Returns all available search contexts for semantic search filtering.
    ///     Use this for tools that accept context parameters.
    /// </summary>
    private static object ListSearchContexts()
    {
        return new DiscoveryContextsResult(
            "list_search_contexts",
            ["comments", "strings", "identifiers", "code", "xmldocs", "all"],
            "Use these values with roslyn_semantic_search context parameter"
        );
    }

    // ── Error Helpers: No Matching Nodes ─────────────────────────────────────────

    /// <summary>
    ///     Returns a helpful error when syntax kind filtering returns no results.
    ///     Suggests using discovery and provides common values.
    /// </summary>
    protected static object NoMatchingSyntaxKindError(string providedKind)
    {
        return new DiscoveryNoMatchResult(
            "no_matching_nodes",
            $"No syntax nodes of kind '{providedKind}' found in the specified range.",
            "Use listSyntaxKinds=true to see all available syntax kinds, or check spelling (e.g., 'IfStatement' not 'if').",
            providedKind,
            GetCommonSyntaxKinds()
        );
    }

    /// <summary>
    ///     Returns a helpful error when member kind filtering returns no results.
    ///     Suggests valid values.
    /// </summary>
    protected static object NoMatchingMemberKindError(string providedKind)
    {
        return new DiscoveryNoMatchResult(
            "invalid_member_kind",
            $"Invalid member kind: '{providedKind}'.",
            "Use listMemberKinds=true to see all available member kinds.",
            providedKind,
            ["field", "property", "method", "event", "enum"]
        );
    }

    /// <summary>
    ///     Returns a helpful error when type kind filtering returns no results.
    ///     Suggests valid values.
    /// </summary>
    protected static object NoMatchingTypeKindError(string providedKind)
    {
        return new DiscoveryNoMatchResult(
            "invalid_type_kind",
            $"Invalid type kind: '{providedKind}'.",
            "Use listTypeKinds=true to see all available type kinds.",
            providedKind,
            ["class", "interface", "enum", "struct", "record", "delegate"]
        );
    }
}
