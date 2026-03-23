using System.ComponentModel;
using Microsoft.Extensions.FileSystemGlobbing;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ListFilesTool : RoslynMcpTool
{
    public ListFilesTool(WorkspaceResolver workspace) : base(workspace) { }

    [McpServerTool(Name = "roslyn_list_files", ReadOnly = true)]
    [Description(
        "Lists files matching a glob pattern. Returns relative paths without content. " +
        "Use this to enumerate files by name/extension before analyzing them with other tools. " +
        "Complements search_files (content search) with fast file enumeration."
    )]
    public object ListFiles(
        [Description("Glob pattern (e.g., '*.cs', 'Tools/*Tool.cs', '**/*.json'). Default: '**/*'.")] string? pattern = null,
        [Description("Include subdirectories. Default: true.")] bool recursive = true,
        [Description("Maximum number of results. Default: 100, max: 500.")] int take = 100,
        [Description(ProjectPathDescription)] string? projectPath = null
    )
    {
        pattern ??= "**/*";
        take = Math.Clamp(take, 1, 500);

        var rootPath = workspace.GetRootPath(projectPath);

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(pattern);

        IEnumerable<string> allFiles;

        try {
            allFiles = Directory.EnumerateFiles(
                rootPath,
                "*",
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly
            );
        }
        catch(Exception ex) when(ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException) {

            return new {
                error = "Failed to enumerate files",
                details = ex.Message
            };
        }

        var matchedFiles = new List<string>();

        foreach(var fullPath in allFiles) {

            var relativePath = Path.GetRelativePath(rootPath, fullPath);

            // Match against relative path with forward slashes (glob convention)
            var normalizedPath = relativePath.Replace('\\', '/');

            if(matcher.Match(normalizedPath).HasMatches) {

                matchedFiles.Add(relativePath);

                if(matchedFiles.Count >= take)
                    break;
            }
        }

        var truncated = matchedFiles.Count == take && allFiles.Skip(take).Any();

        return new {
            files = matchedFiles.ToArray(),
            count = matchedFiles.Count,
            truncated
        };
    }
}
