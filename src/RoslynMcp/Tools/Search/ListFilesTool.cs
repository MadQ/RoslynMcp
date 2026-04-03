using System.ComponentModel;
using Microsoft.Extensions.FileSystemGlobbing;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ListFilesTool : RoslynMcpTool
{
	public ListFilesTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }

	[McpServerTool(Name = "roslyn_list_files", ReadOnly = true, Title = "List Files", OpenWorld = false, Idempotent = true)]
	public object ListFiles(
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Glob pattern (e.g., '*.cs', 'Tools/*Tool.cs', '**/*.json', '*.{cs,csproj}'). Default: '**/*'.")] string? pattern = null,
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

		string[] allRelativePaths;

		try {

			// Materialize relative paths upfront — reused by close-match hint on miss.
			allRelativePaths = Directory.EnumerateFiles(
				rootPath,
				"*",
				recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly
			)
			.Select(fullPath => Path.GetRelativePath(rootPath, fullPath))
			.ToArray()
			;
		}
		catch(Exception ex) when(ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException) {

			return scope.Error(new ErrorResult($"Failed to enumerate files: {ex.Message}"));
		}

		var allResults = allRelativePaths
			.Where(relativePath => matcher.Match(relativePath.Replace('\\', '/')).HasMatches)
			.ToArray()
		;

		if(allResults.Length == 0) {

			var closeMatches = BuildCloseMatches(allRelativePaths, pattern);
			return scope.Outcome("no files matched", new ListFilesEmptyResult([], 0, closeMatches, AdhocCaution(projectPath)));
		}

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

	static string[]? BuildCloseMatches(string[] allRelativeFiles, string pattern)
	{
		var candidates = FindBroadenedMatches(allRelativeFiles, pattern);

		return candidates.Length > 0 ? candidates : null;
	}

	/// <summary>
	///     Tries progressively broader patterns to surface close matches when the original found nothing.
	///     Strategy 1: strip directory constraints (try <c>**/{filename}</c>).
	///     Strategy 2: loosen to extension only (shows what kinds of files exist nearby).
	/// </summary>
	static string[] FindBroadenedMatches(string[] allRelativeFiles, string pattern)
	{
		var filename = Path.GetFileName(pattern);
		if(string.IsNullOrEmpty(filename))
			return [];

		// Strategy 1: ignore directory prefix — find the filename pattern anywhere in the tree.
		// Skip if pattern already has no directory component or is already a ** recursive pattern.
		var broadPattern = $"**/{filename}";

		if(pattern.Contains('/') && broadPattern != pattern) {

			var broadMatcher = new Matcher(StringComparison.OrdinalIgnoreCase);
			broadMatcher.AddInclude(broadPattern);

			var found = allRelativeFiles
				.Where(f => broadMatcher.Match(f.Replace('\\', '/')).HasMatches)
				.Take(5)
				.ToArray()
			;

			if(found.Length > 0)
				return found;
		}

		// Strategy 2: fall back to just the extension — tells the agent what kinds of files exist.
		var ext = Path.GetExtension(filename);

		if(!string.IsNullOrEmpty(ext) && ext != filename) {

			var extMatcher = new Matcher(StringComparison.OrdinalIgnoreCase);
			extMatcher.AddInclude($"**/*{ext}");

			return allRelativeFiles
				.Where(f => extMatcher.Match(f.Replace('\\', '/')).HasMatches)
				.Take(3)
				.ToArray()
			;
		}

		return [];
	}
}
