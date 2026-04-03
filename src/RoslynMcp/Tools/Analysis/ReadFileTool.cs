using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ReadFileTool : RoslynMcpTool
{
    public ReadFileTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }

    [McpServerTool(Name = "roslyn_read_file", ReadOnly = true, Title = "Read File", OpenWorld = false, Idempotent = true)]
    [Description(
        "Use this to read the raw content of any file in the project with 1-based line numbers. " +
        "For .cs files, content is served from the in-memory Roslyn workspace — no disk I/O, always reflecting " +
        "the latest state of the compilation (including edits not yet written to disk). " +
        "For all other file types (.csproj, .json, .props, etc.), content is read from disk. " +
        "Always use startLine/endLine to narrow the range for large files — returning the full file of a large .cs " +
        "file can overflow the context window. Use roslyn_get_file_outline to find the line range of a specific member first. " +
        "For reading a single method or property body, prefer roslyn_get_member_body — it's more token-efficient. " +
        "The response includes the source field ('roslyn' or 'disk'), total line count, and the requested line range.")]
    public async Task<object> ReadFile(
        [Description("Relative path to the file, e.g. 'Core/WindowTracker.cs' or 'Directory.Build.props'. Path is relative to the project root.")] string filePath,
        [Description(ProjectPathDescription)] string projectPath,
        [Description("1-based line to start reading from. Default: 1 (start of file). Combine with endLine to read a specific section.")] int startLine = 1,
        [Description("1-based line to stop reading at (inclusive). Default: end of file. Use roslyn_get_file_outline to find a member's line range.")] int endLine = int.MaxValue)
    {
        using var scope = BeginTool("roslyn_read_file", filePath);

        var rootPath   = workspace.GetRootPath(projectPath);
        var normalized = NormalizePath(filePath);
        var isCs       = normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

        SourceText sourceText;
        string     canonicalPath;

        if(isCs) {

            // .cs files: serve from in-memory compilation — no disk I/O, always reflects unsaved edits.
            if(!TryGetCompilation(projectPath, out var compilation, out var error))

                return error;

            var tree = compilation.SyntaxTrees
                .FirstOrDefault(t => t.FilePath.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
            ;

            if(tree is null)

                return scope.Failed("file not found", new ErrorResult($"File '{filePath}' not found in the compilation."));

            sourceText    = await tree.GetTextAsync();
            canonicalPath = tree.FilePath;
        }
        else {

            // Non-.cs: fall back to disk.
            var fullPath = ResolveFilePath(filePath, rootPath)
;

            if(fullPath is null)

                return scope.Failed("file not found", new ErrorResult($"File not found: {filePath}"));

            sourceText    = SourceText.From(await File.ReadAllTextAsync(fullPath));
            canonicalPath = fullPath
;
        }

        var lines      = sourceText.Lines;
        var totalLines = lines.Count;

        // Clamp range to actual file bounds.
        var first = Math.Clamp(startLine, 1, totalLines)
;
        var last  = Math.Clamp(endLine,   1, totalLines);

        if(first > last)

            return scope.Failed("invalid range", new ErrorResult($"startLine ({startLine}) must be ≤ endLine ({endLine})."));

        var result = new string[last - first + 1];

        for(var i = first; i <= last; i++)
            result[i - first] = lines[i - 1].ToString();

        var relative = Path.GetRelativePath(rootPath, canonicalPath);

        return scope.Outcome($"{result.Length}/{totalLines} line(s)", new ReadFileResult(
            File:        relative,
            Source:      isCs ? "roslyn" : "disk",
            TotalLines: totalLines,
            StartLine:  first,
            EndLine:    last,
            Lines:       result,
            Caution:    AdhocCaution(projectPath)
        ));
    }
}
