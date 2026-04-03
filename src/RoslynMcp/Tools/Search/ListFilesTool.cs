using System.ComponentModel;
using Microsoft.Extensions.FileSystemGlobbing;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ListFilesTool : RoslynMcpTool
{
	public ListFilesTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }

	[McpServerTool(Name = "roslyn_list_files", ReadOnly = true, Title = "List Files", OpenWorld = false, Idempotent = true)]
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

			return scope.Error(new ErrorResult($"Failed to enumerate files: {ex.Message}"));
		}

		var allResults = allFiles
			.Select(fullPath => Path.GetRelativePath(rootPath, fullPath))
			.Where(relativePath => matcher.Match(relativePath.Replace('\\', '/')).HasMatches)
			.ToArray()
		;

		if(allResults.Length == 0)
			return scope.Error(new ListFilesEmptyResult([], 0, AdhocCaution(projectPath)));

		var result = PaginateAndStore(allResults, ref skip, take);

		return new ListFilesResult(
			result.Items,
			result.Total,
			skip, take,
			result.PageToken,
			result.HasMore,
			AdhocCaution(projectPath)
		);
	}
}
