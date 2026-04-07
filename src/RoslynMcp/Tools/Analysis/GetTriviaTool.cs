using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class GetTriviaTool : RoslynMcpTool
{
    public GetTriviaTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }

    [McpServerTool(Name = "roslyn_get_trivia", ReadOnly = true, Title = "Get Trivia", OpenWorld = false, Idempotent = true)]
    [Description(
        "⚠️ EXPERIMENTAL — this tool may be removed or significantly changed in future releases without notice. " +
        "Do not build workflows that depend on its output structure remaining stable. " +
        "Use this when you need to inspect whitespace, blank lines, comment placement, or indentation trivia " +
        "in a C# file — for example, to determine the indentation level at a specific line before inserting new code. " +
        "For most code-understanding tasks, prefer roslyn_read_file (content) or roslyn_get_file_outline (structure). " +
        "Start a session with listSyntaxKinds=true or listTriviaKinds=true to discover valid filter values before " +
        "passing syntaxKind or triviaKind — unknown values return a helpful error with suggestions. " +
        "Returns trivia entries grouped by syntax node, each with kind, span, truncated node text, and leading/trailing " +
        "trivia arrays. Paged with default take=100, max take=500.")]
    public async Task<object> GetTrivia(
        [Description(ProjectPathDescription)] string projectPath,
        [Description("Relative file path, e.g. 'Core/WindowTracker.cs'. Required for trivia analysis; omit only when using listSyntaxKinds or listTriviaKinds.")] string? filePath = null,
        [Description("Optional 1-based starting line to restrict analysis. Default: start of file.")] int? startLine = null,
        [Description("Optional 1-based ending line to restrict analysis. Default: end of file.")] int? endLine = null,
        [Description("Optional syntax node kind filter (e.g., 'IfStatement', 'MethodDeclaration'). Use listSyntaxKinds=true first to see all valid values.")] string? syntaxKind = null,
        [Description("Optional trivia kind filter (e.g., 'WhitespaceTrivia', 'SingleLineCommentTrivia'). Use listTriviaKinds=true first to see all valid values.")] string? triviaKind = null,
        [Description("Include leading trivia (whitespace/comments before each node). Default: true.")] bool includeLeading = true,
        [Description("Include trailing trivia (whitespace/comments after each node). Default: true.")] bool includeTrailing = true,
        [Description("Number of results to skip. Default: 0.")] int skip = 0,
        [Description("Maximum results to return. Default: 100, max: 500.")] int take = 100,
        [Description("Token from a previous response to get the next page without re-executing the query.")] string? page_token = null,
        [Description("Pass true to return all available C# syntax kind names instead of analyzing trivia. No filePath needed.")] bool listSyntaxKinds = false,
        [Description("Pass true to return all available trivia kind names instead of analyzing trivia. No filePath needed.")] bool listTriviaKinds = false)
    {
        using var scope = BeginTool("roslyn_get_trivia", filePath);

        if(TryHandleDiscovery(listSyntaxKinds, listTriviaKinds, listMemberKinds: false, listTypeKinds: false, listSearchContexts: false, out var discovery))
            return scope.Outcome("discovery", discovery);

        if(scope.TryServeCachedPage<object>(page_token, ref skip, ref take, 500, out var cached))
            return scope.Outcome("cached page", cached);

        if(string.IsNullOrEmpty(filePath))
            return scope.Error(new ErrorResult("filePath is required unless using listSyntaxKinds or listTriviaKinds"));

        if(!TryGetCompilation(projectPath, out var compilation, out var error))
            return scope.Error(error!);

        if(take <= 0 || take > 500)
            return scope.Error(new ErrorResult("take must be between 1 and 500"));

        var normalizedPath = NormalizePath(filePath);
        var tree		   = compilation.SyntaxTrees.FirstOrDefault(t =>
            t.FilePath.EndsWith(normalizedPath, StringComparison.OrdinalIgnoreCase)
        );

        if(tree is null)
            return scope.Error(new ErrorResult($"File '{filePath}' not found in the compilation."));

        var root	   = await tree.GetRootAsync();
        var sourceText = await tree.GetTextAsync();

        TextSpan span;
		
        if(startLine.HasValue || endLine.HasValue) {

            var lineCount = sourceText.Lines.Count;
            var sl = Math.Clamp(startLine ?? 1, 1, lineCount);
            var el = Math.Clamp(endLine ?? lineCount, 1, lineCount);

            var start = sourceText.Lines[sl - 1].Start;
            var end   = sourceText.Lines[el - 1].End;

            span = TextSpan.FromBounds(start, end);
        }
        else
            span = root.FullSpan;

        // ToList() is intentional — nodesInSpan may be reassigned in-place below when filtering by syntaxKind.
        var nodesInSpan = root
            .DescendantNodes(span, descendIntoTrivia: false)
            .Where(n => span.Contains(n.Span))
            .ToList()
        ;

        if(!string.IsNullOrEmpty(syntaxKind)) {

            nodesInSpan = nodesInSpan.Where(n => n.Kind().ToString() == syntaxKind).ToList();

            if(nodesInSpan.Count == 0) {

                return scope.Error(new GetTriviaNoMatchResult(
                    "no_matching_nodes",
                    $"No syntax nodes of kind '{syntaxKind}' found in the specified range.",
                    "Use listSyntaxKinds=true to see all available syntax kinds, or check spelling (e.g., 'IfStatement' not 'if').",
                    syntaxKind,
                    GetCommonSyntaxKinds()
                ));
            }
        }

        var totalNodes = nodesInSpan.Count;
        var results	   = new List<object>();

        foreach(var node in nodesInSpan) {

            var leadingTriviaList  = includeLeading
                ? FilterTrivia(node.GetLeadingTrivia(), triviaKind)
                : [];
            var trailingTriviaList = includeTrailing
                ? FilterTrivia(node.GetTrailingTrivia(), triviaKind)
                : [];

            // Skip nodes with no trivia after filtering
            if(leadingTriviaList.Length == 0 && trailingTriviaList.Length == 0)
                continue;

            var lineSpan = tree.GetLineSpan(node.Span);

            results.Add(new TriviaNodeResult(
                node.Kind().ToString(),
                new TriviaNodeSpan(
                    node.Span.Start,
                    node.Span.End,
                    lineSpan.StartLinePosition.Line + 1,
                    lineSpan.EndLinePosition.Line + 1
                ),
                TruncateText(node.ToString(), 80),
                leadingTriviaList,
                trailingTriviaList
            ));
        }

        object[] allResults = [.. results];
        var result = PaginateAndStore(allResults, ref skip, take);

        return scope.Outcome("trivia", new GetTriviaResult(
            filePath,
            totalNodes,
            allResults.Length,
            skip, take,
            result.Items,
            result.PageToken,
            result.HasMore
        ));
    }

    private static object[] FilterTrivia(SyntaxTriviaList triviaList, string? kindFilter)
    {
        var filtered = kindFilter is not null
            ? triviaList.Where(t => t.Kind().ToString() == kindFilter)
            : triviaList
		;

        return [..
            filtered.Select(t => new TriviaEntry(
                t.Kind().ToString(),
                t.ToString(),
                new TriviaSpan(t.Span.Start, t.Span.End)
            ))
            .Cast<object>()
        ];
    }

    private static string TruncateText(string text, int maxLength)
    {
        if(text.Length <= maxLength)
            return text;

        return text.Substring(0, maxLength) + "…";
    }
}
