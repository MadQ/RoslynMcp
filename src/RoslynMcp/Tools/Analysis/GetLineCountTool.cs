using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class GetLineCountTool : RoslynMcpTool
{
    public GetLineCountTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }

    [McpServerTool(Name = "roslyn_get_line_count", ReadOnly = true)]
    [Description(
        "Returns the line count for one or more files. Accepts a single path or comma-separated list.")]
    public async Task<object> GetLineCount(
		[Description("Relative file path or comma-separated list of paths, e.g. 'WorkspaceManager.cs' or 'Foo.cs,Bar.cs,appsettings.json'.")] string filePaths,
		[Description(ProjectPathDescription)] string projectPath)
	{
		using var scope = BeginTool("roslyn_get_line_count", filePaths);
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return error;

		var rootPath = workspace.GetRootPath(projectPath);
		var paths    = filePaths
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
		;

		var results = new List<object>(paths.Length);

		foreach(var filePath in paths) {

			var normalized = NormalizePath(filePath);
			var isCs       = normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

			if(isCs) {

				var tree = compilation.SyntaxTrees
					.FirstOrDefault(t => t.FilePath.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
				;

				if(tree is null) {
					results.Add(new { file = filePath, line_count = (int?) null, error = "not found in compilation" });
					continue;
				}

				var text = await tree.GetTextAsync();
				results.Add(new { file = Path.GetRelativePath(rootPath, tree.FilePath), line_count = (int?) text.Lines.Count, error = (string?) null });
			}
			else {

				var fullPath = ResolveFilePath(filePath, rootPath);

				if(fullPath is null) {
					results.Add(new { file = filePath, line_count = (int?) null, error = "file not found on disk" });
					continue;
				}

				try {

					var lineCount = await CountLinesAsync(fullPath);
					results.Add(new { file = Path.GetRelativePath(rootPath, fullPath), line_count = (int?) lineCount, error = (string?) null });
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
					results.Add(new { file = filePath, line_count = (int?) null, error = ex.Message });
				}
			}
		}

		var total      = results.Count;
		var filesArr   = results.ToArray();

		return scope.Outcome($"{total} file(s)", new {
			files    = filesArr,
			_caution = AdhocCaution(projectPath)
		});
	}

    /// <summary>
    ///     Counts lines by scanning for newline chars without allocating a full string per line.
    /// </summary>
    private static async Task<int> CountLinesAsync(string fullPath)
    {
        var buffer    = new byte[8192];
        var lineCount = 1; // A non-empty file has at least one line.
        var isEmpty   = true;

        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buffer.Length, useAsync: true);

        int read;

        while((read = await stream.ReadAsync(buffer)) > 0) {

            isEmpty = false;

            for(var i = 0; i < read; i++) {

                if(buffer[i] == '\n')
                    lineCount++;
            }
        }

        return isEmpty ? 0 : lineCount;
    }
}
