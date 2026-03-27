using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ReadFileTool : RoslynMcpTool
{
    public ReadFileTool(WorkspaceResolver workspace, FileLogger logger) : base(workspace, logger) { }

    [McpServerTool(Name = "roslyn_read_file", ReadOnly = true)]
    [Description(
        "Returns the contents of a file with 1-based line numbers. " +
        "For .cs files, reads from the in-memory Roslyn workspace (no disk I/O, always current). " +
        "For all other file types, reads from disk. " +
        "Supports optional line range via startLine/endLine to avoid dumping entire large files.")]
    public async Task<object> ReadFile(
        [Description("Relative file path, e.g. 'Core/WindowTracker.cs' or 'Directory.Build.props'.")] string filePath,
        [Description(ProjectPathDescription)] string projectPath,
        [Description("1-based line to start reading from. Default: 1.")] int startLine = 1,
        [Description("1-based line to stop reading at (inclusive). Default: read to end of file.")] int endLine = int.MaxValue)
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
                return scope.Failed("file not found", new { error = $"File '{filePath}' not found in the compilation." });

            sourceText    = await tree.GetTextAsync();
            canonicalPath = tree.FilePath;
        }
        else {

            // Non-.cs: fall back to disk.
            var fullPath = Path.IsPathRooted(filePath)
                ? filePath
                : Path.GetFullPath(Path.Combine(rootPath, normalized))
            ;

            if(!File.Exists(fullPath))
                return scope.Failed("file not found", new { error = $"File not found: {filePath}" });

            sourceText    = SourceText.From(await File.ReadAllTextAsync(fullPath));
            canonicalPath = fullPath;
        }

        var lines      = sourceText.Lines;
        var totalLines = lines.Count;

        // Clamp range to actual file bounds.
        var first = Math.Clamp(startLine, 1, totalLines);
        var last  = Math.Clamp(endLine,   1, totalLines);

        if(first > last)
            return scope.Failed("invalid range", new { error = $"startLine ({startLine}) must be ≤ endLine ({endLine})." });

        var result = new string[last - first + 1];

        for(var i = first; i <= last; i++)
            result[i - first] = lines[i - 1].ToString();

        var relative = Path.GetRelativePath(rootPath, canonicalPath);

        return scope.Outcome($"{result.Length}/{totalLines} line(s)", new {
            file        = relative,
            source      = isCs ? "roslyn" : "disk",
            total_lines = totalLines,
            start_line  = first,
            end_line    = last,
            lines       = result,
            _caution    = AdhocCaution(projectPath)
        });
    }
}
