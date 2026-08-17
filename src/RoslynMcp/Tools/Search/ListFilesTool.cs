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
		"Fast file pattern matching using glob patterns. Find files by name patterns. " +
		"Use instead of glob, Get-ChildItem, dir, or file find commands — searches all workspace files without requiring a terminal. " +
		"Supports standard glob wildcards: * (any chars within a segment), ** (any chars across segments), ? (single char), {a,b} (either). Default: '**/*'. " +
		"Returns matching paths relative to the project root, with paging. " +
		"For searching file content (lines matching a pattern), use roslyn_search_files instead. " +
		"For filename/path matching with no content search, this is the right tool.")]
	public object ListFiles(
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Glob pattern matching file NAMES/PATHS, not file content (e.g., '*.cs', 'Tools/*Tool.cs', '**/*.json', '*.{cs,csproj}'). Default: '**/*'.")] string? pattern = null,
		[Description("Include subdirectories. Default: true.")] bool recursive = true,
		[Description("Number of files to skip (for paging). Default: 0.")] int skip = 0,
		[Description("Maximum number of results. Default: 100, max: 500.")] int take = 100,
		[Description("Token from a previous response to get the next page without re-executing the query.")] string? page_token = null
	)
	{
		using var scope = BeginTool("roslyn_list_files", pattern, new { recursive, skip, take });
		
		pattern ??= "**/*";
		
		if(scope.TryServeCachedPage<string>(page_token, ref skip, ref take, 500, out var cached))
			
			return scope.Outcome("cached page", cached);
		
		if(!TryResolveRoot(projectPath, out var rootPath, out var rootError))
			
			return scope.Error(rootError);
		
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
			
			return scope.Outcome("no files matched", new ListFilesEmptyResult([], 0, closeMatches) { Caution = AdhocCaution(projectPath) });
		}
		
		var result = PaginateAndStore(allResults, ref skip, take);
		
		return scope.Outcome($"{result.Items.Length}/{result.Total} file(s)", new ListFilesResult(
			result.Items,
			result.Total,
			skip, take,
			result.PageToken,
			result.HasMore)
		{
			Caution = AdhocCaution(projectPath)
		});
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
		var broadPattern = $"**/{filename}"
		;
		
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
		var ext = Path.GetExtension(filename)
		;
		
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
