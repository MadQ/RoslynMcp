using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class GetLineCountTool : RoslynMcpTool
{
    public GetLineCountTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }

    [McpServerTool(Name = "roslyn_get_line_count", ReadOnly = true, Title = "Get Line Count", OpenWorld = false, Idempotent = true)]
    [Description(
        "Returns the line count for one or more files — useful for gauging file size before deciding " +
        "whether to read the full content with roslyn_read_file. " +
        "Accepts a comma-separated list of paths for efficient batch queries in a single call. " +
        "C# files (.cs) are counted from the in-memory Roslyn workspace; all other files are read " +
        "from disk, so counts reflect the saved state for non-C# files. " +
        "Files not found return a null count with a per-entry error message rather than failing the " +
        "whole request — safe to use on mixed lists where some files may be absent.")]
    public async Task<object> GetLineCount(
		[Description("Relative file path or comma-separated list of paths, e.g. 'WorkspaceManager.cs' or 'Foo.cs,Bar.cs,appsettings.json'. Paths are trimmed of whitespace.")] string filePaths,
		[Description(ProjectPathDescription)] string projectPath)
	{
		using var scope = BeginTool("roslyn_get_line_count", filePaths);
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return scope.Error(error!);
		
		var rootPath = workspace.GetRootPath(projectPath);
		var boundary = workspace.GetSecurityBoundary(projectPath);
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
					
					results.Add(new LineCountEntry(filePath, null, "not found in compilation"));
					continue;
				}
				
				var text = await tree.GetTextAsync();
				
				results.Add(new LineCountEntry(Path.GetRelativePath(rootPath, tree.FilePath), text.Lines.Count, null));
			}
			
			else {
				
				var fullPath = ResolveFilePath(filePath, rootPath, boundary);
				
				if(fullPath is null) {
					
					results.Add(new LineCountEntry(filePath, null, "file not found on disk"));
					continue;
				}
				
				try {
					
					var lineCount = await CountLinesAsync(fullPath);
					results.Add(new LineCountEntry(Path.GetRelativePath(rootPath, fullPath), lineCount, null));
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
					results.Add(new LineCountEntry(filePath, null, ex.Message));
				}
			}
		}
		
		var total      = results.Count;
		var filesArr   = results.ToArray();
		
		return scope.Outcome($"{total} file(s)", new LineCountResult(
			Files: filesArr)
		{
			Caution = AdhocCaution(projectPath)
		});
	}

    /// <summary>
    ///     Counts lines by scanning for newline chars without allocating a full string per line.
    /// </summary>
    private static async Task<int> CountLinesAsync(string fullPath)
    {
        var buffer    = new byte[8192];
        var lineCount = 1; // A non-empty file has at least one line.
        var isEmpty   = true
;

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
