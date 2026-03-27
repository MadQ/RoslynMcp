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
        var all = Enum.GetValues(typeof(SyntaxKind))
            .Cast<SyntaxKind>()
            .Where(k => k != SyntaxKind.None && k != SyntaxKind.List)
            .Select(k => k.ToString())
            .OrderBy(s => s)
            .ToArray();

        return new {

            mode = "list_syntax_kinds",
            count = all.Length,
            commonKinds = GetCommonSyntaxKinds(),
            allKinds = all
        };
    }

    /// <summary>
    ///     Returns curated list of most commonly used syntax kinds.
    ///     Control flow, declarations, statements.
    /// </summary>
    protected static string[] GetCommonSyntaxKinds()
    {
        return new[] {
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
        };
    }

    // ── Discovery: Trivia Kinds ──────────────────────────────────────────────────

    /// <summary>
    ///     Returns all available trivia kinds (whitespace, comments, etc.).
    ///     Use this for tools that accept triviaKind parameters.
    /// </summary>
    private static object ListTriviaKinds()
    {
        var all = Enum.GetValues(typeof(SyntaxKind))
            .Cast<SyntaxKind>()
            .Where(k => k.ToString().EndsWith("Trivia"))
            .Select(k => k.ToString())
            .OrderBy(s => s)
            .ToArray();

        return new {

            mode = "list_trivia_kinds",
            count = all.Length,
            commonKinds = GetCommonTriviaKinds(),
            allKinds = all
        };
    }

    /// <summary>
    ///     Returns curated list of most commonly used trivia kinds.
    ///     Whitespace, line breaks, comments.
    /// </summary>
    protected static string[] GetCommonTriviaKinds()
    {
        return new[] {

            "WhitespaceTrivia",
            "EndOfLineTrivia",
            "SingleLineCommentTrivia",
            "MultiLineCommentTrivia",
            "SingleLineDocumentationCommentTrivia",
            "MultiLineDocumentationCommentTrivia",
            "DisabledTextTrivia",
            "PreprocessingMessageTrivia"
        };
    }

    // ── Discovery: Member Kinds ──────────────────────────────────────────────────

    /// <summary>
    ///     Returns all available member kinds for type member filtering.
    ///     Use this for tools that accept memberKind parameters.
    /// </summary>
    private static object ListMemberKinds()
    {
        return new {

            mode = "list_member_kinds",
            kinds = new[] { "field", "property", "method", "event", "enum" },
            hint = "Use these values with roslyn_get_type_members memberKind parameter"
        };
    }

    // ── Discovery: Type Kinds ────────────────────────────────────────────────────

    /// <summary>
    ///     Returns all available type kinds for type listing/filtering.
    ///     Use this for tools that accept kindFilter or typeKind parameters.
    /// </summary>
    private static object ListTypeKinds()
    {
        return new {

            mode = "list_type_kinds",
            kinds = new[] { "class", "interface", "enum", "struct", "record", "delegate" },
            hint = "Use these values with roslyn_list_types kindFilter parameter"
        };
    }

    // ── Discovery: Search Contexts ───────────────────────────────────────────────

    /// <summary>
    ///     Returns all available search contexts for semantic search filtering.
    ///     Use this for tools that accept context parameters.
    /// </summary>
    private static object ListSearchContexts()
    {
        return new {

            mode = "list_search_contexts",
            contexts = new[] { "comments", "strings", "identifiers", "code", "xmldocs", "all" },
            hint = "Use these values with roslyn_semantic_search context parameter"
        };
    }

    // ── Error Helpers: No Matching Nodes ─────────────────────────────────────────

    /// <summary>
    ///     Returns a helpful error when syntax kind filtering returns no results.
    ///     Suggests using discovery and provides common values.
    /// </summary>
    protected static object NoMatchingSyntaxKindError(string providedKind)
    {
        return new {

            error = "no_matching_nodes",
            message = $"No syntax nodes of kind '{providedKind}' found in the specified range.",
            hint = "Use listSyntaxKinds=true to see all available syntax kinds, or check spelling (e.g., 'IfStatement' not 'if').",
            providedKind,
            commonKinds = GetCommonSyntaxKinds()
        };
    }

    /// <summary>
    ///     Returns a helpful error when member kind filtering returns no results.
    ///     Suggests valid values.
    /// </summary>
    protected static object NoMatchingMemberKindError(string providedKind)
    {
        return new {

            error = "invalid_member_kind",
            message = $"Invalid member kind: '{providedKind}'.",
            hint = "Use listMemberKinds=true to see all available member kinds.",
            providedKind,
            validKinds = new[] { "field", "property", "method", "event", "enum" }
        };
    }

    /// <summary>
    ///     Returns a helpful error when type kind filtering returns no results.
    ///     Suggests valid values.
    /// </summary>
    protected static object NoMatchingTypeKindError(string providedKind)
    {
        return new {

            error = "invalid_type_kind",
            message = $"Invalid type kind: '{providedKind}'.",
            hint = "Use listTypeKinds=true to see all available type kinds.",
            providedKind,
            validKinds = new[] { "class", "interface", "enum", "struct", "record", "delegate" }
        };
    }
}
