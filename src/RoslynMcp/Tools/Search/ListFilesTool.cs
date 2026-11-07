using System.ComponentModel;
using System.IO.Enumeration;
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
		"Skips node_modules, .git, .vs, packages, bin, obj, directories the root .gitignore lists by name, and any configured ignore names — " +
		"unless the pattern names the directory literally (e.g. 'bin/**/*.dll' or '**/obj/project.assets.json'). " +
		"A name before the first wildcard applies only at that path ('frontend/node_modules/**' enters that one directory); a name after a wildcard applies at any depth. " +
		"For searching file content (lines matching a pattern), use roslyn_search_files instead. " +
		"For filename/path matching with no content search, this is the right tool.")]
	public object ListFiles(
		[Description(OptionalProjectPathDescription)] string? projectPath = null,
		[Description("Glob pattern matching file NAMES/PATHS, not file content (e.g., '*.cs', 'Tools/*Tool.cs', '**/*.json', '*.{cs,csproj}'). {a,b} groups may nest and may appear in directory segments ('{src,tests}/**/*.cs'). Default: '**/*'.")] string? pattern = null,
		[Description("Include subdirectories. Default: true.")] bool recursive = true,
		[Description("Number of files to skip (for paging). Default: 0.")] int skip = 0,
		[Description("Maximum number of results. Default: 100, max: 500.")] int take = 100,
		[Description(PageTokenDescription)] string? page_token = null
	)
	{
		using var scope = BeginTool("roslyn_list_files", pattern, new { projectPath, recursive, skip, take });
		
		if(!TryResolveProjectArg(projectPath, out projectPath, out var projectError))
			
			return scope.Error(projectError);
		
		pattern ??= "**/*";
		
		if(scope.TryServeCachedPage<string>(page_token, ref skip, ref take, 100, 500, out var cached))
			
			return scope.Outcome("cached page", cached);
		
		if(!TryResolveRoot(projectPath, out var rootPath, out var rootError))
			
			return scope.Error(rootError);
		
		// Matcher has no brace expansion: each expansion of '{a,b}' is an include of its own (#294).
		if(!GlobBraces.TryExpand(pattern, "pattern", out var patterns, out var globError))
			
			return scope.Error(new ErrorResult(globError));
		
		var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
		
		foreach(var expanded in patterns)
			matcher.AddInclude(expanded);
		
		string[] allRelativePaths;
		
		try {
			
			// Ignored directories are never entered — unless the pattern spells one out, which is
			// how an agent reaches bin/ or obj/ on purpose. With braces, a directory one expansion
			// names ('{bin,src}/**') counts as named.
			var rules = IgnoreRules.ForRoot(rootPath, logger);
			NamedDirectorySet[] named = [.. patterns.Select(expanded => NamedDirectories(expanded, anchorLeadingSegments: true))];
			
			// Materialize relative paths upfront — reused by close-match hint on miss.
			allRelativePaths = WorkspaceWalker.EnumerateFiles(
				rootPath,
				"*",
				recursive,
				(ref FileSystemEntry directory) =>
					!IsNamed(named, WorkspaceWalker.RelativeParent(ref directory), directory.FileName)
					&& rules.IsPrunedFromListing(WorkspaceWalker.RelativeParent(ref directory), directory.FileName)
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
			
			var closeMatches = BuildCloseMatches(allRelativePaths, patterns);
			
			return scope.Outcome("no files matched", new ListFilesEmptyResult([], 0, closeMatches) { Caution = AdhocCaution(projectPath) });
		}
		
		var shape  = PageShape(AdhocCaution(projectPath));
		var result = PaginateAndStore(allResults, ref skip, take, shape);
		
		return scope.Outcome($"{result.Items.Length}/{result.Total} file(s)", shape(result));
	}
	
	// Builds every page of one query, first and cached alike, so a cached page has the same fields.
	static Func<PaginatedResult<string>, object> PageShape(string? caution)
		=> page => new ListFilesResult(
			page.Items,
			page.Total,
			page.Skip, page.Take,
			page.PageToken,
			page.HasMore)
		{
			Caution = caution
		}
	;
	
	static bool IsNamed(NamedDirectorySet[] named, ReadOnlySpan<char> relativeParent, ReadOnlySpan<char> name)
	{
		foreach(var set in named)
			if(set.Contains(relativeParent, name))
				
				return true;
		
		return false;
	}
	
	// The close matches of every expansion, at most five.
	static string[]? BuildCloseMatches(string[] allRelativeFiles, string[] patterns)
	{
		var candidates = FindBroadenedMatches(allRelativeFiles, patterns);
		
		return candidates.Length > 0 ? candidates : null;
	}
	
	// The first files, up to 'limit', that any of 'includes' matches: one matcher, one scan.
	static string[] MatchAny(string[] allRelativeFiles, IEnumerable<string> includes, int limit)
	{
		var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
		
		foreach(var include in includes)
			matcher.AddInclude(include);
		
		return allRelativeFiles
			.Where(f => matcher.Match(f.Replace('\\', '/')).HasMatches)
			.Take(limit)
			.ToArray()
		;
	}
	
	/// <summary>
	///     Tries progressively broader patterns to surface close matches when the original found nothing.
	///     Strategy 1: strip directory constraints (try <c>**/{filename}</c>).
	///     Strategy 2: loosen to extension only (shows what kinds of files exist nearby).
	/// </summary>
	static string[] FindBroadenedMatches(string[] allRelativeFiles, string[] patterns)
	{
		var broad      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		
		foreach(var pattern in patterns) {
			
			var filename = Path.GetFileName(pattern);
			
			if(string.IsNullOrEmpty(filename))
				continue;
			
			// Strategy 1 applies only to a pattern with a directory part that is not already the
			// broad form: '**/{filename}' finds the filename pattern anywhere in the tree.
			var broadPattern = $"**/{filename}";
			
			if(pattern.Contains('/') && broadPattern != pattern)
				broad.Add(broadPattern);
			
			var ext = Path.GetExtension(filename);
			
			if(!string.IsNullOrEmpty(ext) && ext != filename)
				extensions.Add(ext);
		}
		
		if(broad.Count > 0) {
			
			var found = MatchAny(allRelativeFiles, broad, 5);
			
			if(found.Length > 0)
				
				return found;
		}
		
		// Strategy 2: fall back to just the extension — tells the agent what kinds of files exist.
		return extensions.Count > 0 ? MatchAny(allRelativeFiles, extensions.Select(ext => $"**/*{ext}"), 3) : [];
	}
}
