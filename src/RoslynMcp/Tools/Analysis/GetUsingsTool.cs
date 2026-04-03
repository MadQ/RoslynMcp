using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class GetUsingsTool : RoslynMcpTool
{
	public GetUsingsTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_get_usings", ReadOnly = true, Title = "Get Usings", OpenWorld = false, Idempotent = true)]
	[Description(
		"Use this to inspect what namespaces and types a file imports — both explicit using directives declared in the file " +
		"and implicit global usings contributed by the project (via <ImplicitUsings> or global using directives in other files). " +
		"Returns two lists: file-level usings (with optional alias, e.g. 'using Alias = Type') and project-level global usings. " +
		"Prefer this over roslyn_read_file when you only need import information — it is more structured and includes globals " +
		"that do not appear in the file source. " +
		"Note: global usings are discovered by scanning all source trees; SDK-injected implicit usings that have no source file " +
		"representation may not appear in the list.")]
	public async Task<object> GetUsings(
		[Description("Relative path to the C# file to inspect, e.g. 'Core/WindowTracker.cs'. Must exist in the compilation.")] string filePath,
		[Description(ProjectPathDescription)] string projectPath)
	{
		using var scope = BeginTool("roslyn_get_usings", filePath);
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return error;
		
		var rootPath   = workspace.GetRootPath(projectPath);
		var normalized = NormalizePath(filePath);
		
		var tree = compilation.SyntaxTrees
			.FirstOrDefault(t => t.FilePath.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
		;
		
		if(tree is null)
			
			return scope.Failed("file not found", new ErrorResult($"File '{filePath}' not found in the compilation."));
		
		var root = await tree.GetRootAsync();
		
		// Extract using directives from the file.
		UsingDirective[] usings = [..
			root.DescendantNodes()
				.OfType<UsingDirectiveSyntax>()
				.Select(u => new UsingDirective(
					Namespace: u.Name?.ToString(),
					Alias:     u.Alias?.Name.ToString()
				))
				.Where(u => u.Namespace is not null || u.Alias is not null)
		]
		;
		
		// Extract global usings from compilation options.
		var globalUsings = compilation.Options.SyntaxTreeOptionsProvider is { } provider
			? ExtractGlobalUsings(compilation)
			: [];
		
		return scope.Outcome(filePath, new GetUsingsResult(
			Path.GetRelativePath(rootPath, tree.FilePath),
			usings,
			globalUsings,
			AdhocCaution(projectPath)
		));
	}
	
	private static string[] ExtractGlobalUsings(Compilation compilation)
	{
		// Global usings come from <Using> items in the project or ImplicitUsings.
		// They're baked into the compilation as invisible using directives.
		// We can approximate by looking at all syntax trees for global using directives.
		var globalUsings = new HashSet<string>(StringComparer.Ordinal)
		;
		
		foreach(var tree in compilation.SyntaxTrees) {
			
			var root = tree.GetRoot();
			
			foreach(var u in root.DescendantNodes().OfType<UsingDirectiveSyntax>()) {
				
				if(u.GlobalKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GlobalKeyword)
					&& u.Name is not null)
					globalUsings.Add(u.Name.ToString());
			}
		}
		
		return [.. globalUsings.Order()];
	}
	
	internal sealed record UsingDirective(string? Namespace, string? Alias);
}
