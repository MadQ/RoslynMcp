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
		"For text documents Roslyn tracks — .cs source, declared AdditionalFiles items, and .editorconfig/.globalconfig — " +
		"content is served from the in-memory workspace, always reflecting the latest state of the compilation " +
		"(including edits not yet written to disk). Every other file (.csproj, an untracked .json, .props, …) is read from disk. " +
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
		using var scope = BeginTool("roslyn_read_file", filePath, new { startLine, endLine });
		
		if(!TryResolveFileContext(projectPath, out var rootPath, out var boundary, out var resolveError))
			
			return scope.Error(resolveError);
		
		SourceText sourceText;
		string     canonicalPath;
		string     source;
		
		var normalized = NormalizePath(filePath);
		var fullPath   = ResolveFilePath(filePath, rootPath, boundary);
		
		if(fullPath is not null) {
			
			// Soft resolve: a file on disk reads correctly with no workspace at all, so a transient
			// load failure must degrade to the disk path rather than failing the whole call. Only a
			// tracked document needs the solution — and it is classified against that same snapshot,
			// never a second one, so the ids can never belong to a solution that has moved on.
			var textDocument = TryResolveSolution(projectPath, out var solution, out _)
				? WorkspaceTextDocumentInfo.Resolve(solution, fullPath).GetDocument(solution)
				: null;
			
			if(textDocument is not null) {
				
				sourceText    = await textDocument.GetTextAsync();
				canonicalPath = textDocument.FilePath ?? fullPath;
				source        = "roslyn";
			}
			else {
				
				// Stream directly - avoids the ReadAllTextAsync string SourceText double-buffer.
				using var stream = File.OpenRead(fullPath);
				
				sourceText    = SourceText.From(stream);
				canonicalPath = fullPath;
				source        = "disk";
			}
		}
		else if(IsCSharpSourcePath(normalized)) {
			
			// Source files can exist in Roslyn's workspace even when the direct disk lookup misses.
			if(!TryGetCompilation(projectPath, out var compilation, out var error))
				
				return scope.Error(error!);
			
			var tree = FindSyntaxTree(compilation, filePath);
			
			if(tree is null)
				
				return scope.Failed("file not found", new ErrorResult($"File '{filePath}' not found in the compilation."));
			
			sourceText    = await tree.GetTextAsync();
			canonicalPath = tree.FilePath;
			source        = "roslyn";
		}
		else {
			
			return scope.Failed("file not found", new ErrorResult($"File not found: {filePath}"));
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
			File:       relative,
			Source:     source,
			TotalLines: totalLines,
			StartLine:  first,
			EndLine:    last,
			Lines:      result)
		{
			Caution = AdhocCaution(projectPath)
		});
	}
}
