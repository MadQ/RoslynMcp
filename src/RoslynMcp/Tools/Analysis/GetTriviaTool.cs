using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class GetTriviaTool : RoslynMcpTool
{
    public GetTriviaTool(WorkspaceResolver workspace, FileLogger logger) : base(workspace, logger) { }

    [McpServerTool(Name = "roslyn_get_trivia", ReadOnly = true)]
    [Description(
        "**EXPERIMENTAL:** Returns whitespace, comments, and formatting trivia from C# source files. " +
        "Useful for understanding indentation context, blank lines, and comment placement. " +
        "This tool may be removed or significantly changed in future releases. " +
        "Supports whole-file analysis or filtering by line range, syntax kind, or trivia type. " +
        "Use listSyntaxKinds=true or listTriviaKinds=true to discover available filter values.")]
    public object GetTrivia(
        [Description(ProjectPathDescription)] string projectPath,
        [Description("Relative file path, e.g. 'Core/WindowTracker.cs'. Omit when using list parameters.")] string? filePath = null,
        [Description("Optional: Starting line number (1-based) to scope analysis.")] int? startLine = null,
        [Description("Optional: Ending line number (1-based) to scope analysis.")] int? endLine = null,
        [Description("Optional: Filter by syntax node kind (e.g., 'IfStatement', 'MethodDeclaration', 'ForEachStatement'). Use listSyntaxKinds=true to see all options.")] string? syntaxKind = null,
        [Description("Optional: Filter by trivia kind (e.g., 'WhitespaceTrivia', 'EndOfLineTrivia', 'SingleLineCommentTrivia', 'MultiLineCommentTrivia'). Use listTriviaKinds=true to see all options.")] string? triviaKind = null,
        [Description("Include leading trivia (whitespace/comments before nodes). Default: true.")] bool includeLeading = true,
        [Description("Include trailing trivia (whitespace/comments after nodes). Default: true.")] bool includeTrailing = true,
        [Description("Maximum number of results to return. Default: 100, max: 500.")] int take = 100,
        [Description("Set to true to list all available C# syntax kinds (IfStatement, ForEachStatement, etc.) instead of analyzing trivia.")] bool listSyntaxKinds = false,
        [Description("Set to true to list all available trivia kinds (WhitespaceTrivia, EndOfLineTrivia, etc.) instead of analyzing trivia.")] bool listTriviaKinds = false)
    {
        using var scope = BeginTool("roslyn_get_trivia", filePath);

            if(TryHandleDiscovery(listSyntaxKinds, listTriviaKinds, listMemberKinds:  false, listTypeKinds:  false, listSearchContexts:  false, out var discovery))
                return discovery;

            if(string.IsNullOrEmpty(filePath))
            return new { error = "invalid_parameter", message = "filePath is required unless using listSyntaxKinds or listTriviaKinds" };

        if(!TryGetCompilation(projectPath, out var compilation, out var error))
            return error;

        if(take <= 0 || take > 500)
            return new { error = "invalid_parameter", message = "take must be between 1 and 500" };

        var normalizedPath = filePath.Replace('/', Path.DirectorySeparatorChar);
        var tree = compilation.SyntaxTrees.FirstOrDefault(t => 
            t.FilePath.EndsWith(normalizedPath, StringComparison.OrdinalIgnoreCase)
        );

        if(tree is null)
            return new { error = "file_not_found", message = $"File '{filePath}' not found in compilation" };

        var root = tree.GetRoot();
        var sourceText = tree.GetText();

        TextSpan span;
        if(startLine.HasValue || endLine.HasValue) {

            var start = startLine.HasValue 
                ? sourceText.Lines[startLine.Value - 1].Start 
                : 0;
            var end = endLine.HasValue 
                ? sourceText.Lines[endLine.Value - 1].End 
                : sourceText.Length;

            span = TextSpan.FromBounds(start, end);
        }
        else {
            span = root.FullSpan;
        }

        var nodesInSpan = root.DescendantNodes(span, descendIntoTrivia: false)
            .Where(n => span.Contains(n.Span))
            .ToList();

        if(!string.IsNullOrEmpty(syntaxKind)) {

            nodesInSpan = nodesInSpan.Where(n => n.Kind().ToString() == syntaxKind).ToList();

            if(nodesInSpan.Count == 0) {

                return new {

                    error = "no_matching_nodes",
                    message = $"No syntax nodes of kind '{syntaxKind}' found in the specified range.",
                    hint = "Use listSyntaxKinds=true to see all available syntax kinds, or check spelling (e.g., 'IfStatement' not 'if').",
                    providedKind = syntaxKind,
                    commonKinds = GetCommonSyntaxKinds()
                };
            }
        }

        var totalNodes = nodesInSpan.Count;
        var results = new List<object>();

        foreach(var node in nodesInSpan.Take(take)) {

            var leadingTriviaList = includeLeading 
                ? FilterTrivia(node.GetLeadingTrivia(), triviaKind) 
                : Array.Empty<object>();

            var trailingTriviaList = includeTrailing 
                ? FilterTrivia(node.GetTrailingTrivia(), triviaKind) 
                : Array.Empty<object>();

            // Skip nodes with no trivia after filtering
            if(leadingTriviaList.Length == 0 && trailingTriviaList.Length == 0)
                continue;

            var lineSpan = tree.GetLineSpan(node.Span);

            results.Add(new {

                nodeKind = node.Kind().ToString(),
                nodeSpan = new {

                    start = node.Span.Start,
                    end = node.Span.End,
                    startLine = lineSpan.StartLinePosition.Line + 1,
                    endLine = lineSpan.EndLinePosition.Line + 1
                },
                nodeText = TruncateText(node.ToString(), 80),
                leadingTrivia = leadingTriviaList,
                trailingTrivia = trailingTriviaList
            });
        }

        return new {

            file = filePath,
            totalNodes,
            filteredNodes = results.Count,
            results = results.ToArray()
        };
    }

    private static object[] FilterTrivia(SyntaxTriviaList triviaList, string? kindFilter)
    {
        var filtered = kindFilter is not null
            ? triviaList.Where(t => t.Kind().ToString() == kindFilter)
            : triviaList;

        return filtered
            .Select(t => new {

                kind = t.Kind().ToString(),
                text = t.ToString(),
                span = new { start = t.Span.Start, end = t.Span.End }
            })
            .Cast<object>()
            .ToArray();
    }

    private static string TruncateText(string text, int maxLength)
    {
        if(text.Length <= maxLength)
            return text;

        return text.Substring(0, maxLength) + "…";
    }
}
