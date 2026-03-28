using System.ComponentModel;
using Microsoft.Extensions.FileSystemGlobbing;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ListFilesTool : RoslynMcpTool
{
	public ListFilesTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }

	[McpServerTool(Name = "roslyn_list_files", ReadOnly = true)]
	[Description(
		"Lists files matching a glob pattern. Returns relative paths without content. " +
		"Use this to enumerate files by name/extension before analyzing them with other tools. " +
		"Complements search_files (content search) with fast file enumeration."
	)]
	public object ListFiles(
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Glob pattern (e.g., '*.cs', 'Tools/*Tool.cs', '**/*.json'). Default: '**/*'.")] string? pattern = null,
		[Description("Include subdirectories. Default: true.")] bool recursive = true,
		[Description("Number of files to skip (for paging). Default: 0.")] int skip = 0,
		[Description("Maximum number of results. Default: 100, max: 500.")] int take = 100,
		[Description("Token from a previous response to get the next page without re-executing the query.")] string? page_token = null
	)
	{
		using var scope = BeginTool("roslyn_list_files", pattern);
		pattern ??= "**/*";

		var cachedPage = TryServeCachedPage<string>(scope, page_token, ref skip, ref take, 500);
		if(cachedPage is not null)
			return cachedPage;

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
				error   = "Failed to enumerate files",
				details = ex.Message
			};
		}

		var allResults = allFiles
			.Select(fullPath => Path.GetRelativePath(rootPath, fullPath))
			.Where(relativePath => matcher.Match(relativePath.Replace('\\', '/')).HasMatches)
			.ToArray()
		;

		if(allResults.Length == 0)
			return new { files = Array.Empty<string>(), count = 0, _caution = AdhocCaution(projectPath) };

		var result = PaginateAndStore(allResults, ref skip, take);

		return new {
			files      = result.Items,
			count      = result.Total,
			skip, take,
			page_token = result.PageToken,
			has_more   = result.HasMore,
			_caution   = AdhocCaution(projectPath)
		};
	}
}
