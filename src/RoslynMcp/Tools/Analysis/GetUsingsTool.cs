using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class GetUsingsTool : RoslynMcpTool
{
	public GetUsingsTool(WorkspaceResolver workspace, FileLogger logger) : base(workspace, logger) { }
	
	[McpServerTool(Name = "roslyn_get_usings", ReadOnly = true)]
	[Description(
		"Returns all 'using' directives in a file (namespaces and aliases), plus implicit global usings from the project. " +
		"Use this to understand what's in scope when generating or analyzing code.")]
	public async Task<object> GetUsings(
		[Description("Relative file path, e.g. 'Core/WindowTracker.cs'.")] string filePath,
		[Description(ProjectPathDescription)] string projectPath)
	{
		using var scope = BeginTool("roslyn_get_usings", filePath);
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return error;
		
		var rootPath   = workspace.GetRootPath(projectPath);
		var normalized = filePath.Replace('/', Path.DirectorySeparatorChar);
		
		var tree = compilation.SyntaxTrees
			.FirstOrDefault(t => t.FilePath.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
		;
		
		if(tree is null)
			return scope.Failed("file not found", new { error = $"File '{filePath}' not found in the compilation." });
		
		var root = await tree.GetRootAsync();
		
		// Extract using directives from the file.
		var usings = root.DescendantNodes()
			.OfType<UsingDirectiveSyntax>()
			.Select(u => new UsingDirective(
				Namespace: u.Name?.ToString(),
				Alias:     u.Alias?.Name.ToString()
			))
			.Where(u => u.Namespace is not null || u.Alias is not null)
			.ToArray()
		;
		
		// Extract global usings from compilation options.
		var globalUsings = compilation.Options.SyntaxTreeOptionsProvider is { } provider
			? ExtractGlobalUsings(compilation)
			: [];
		
		return new {
			file            = Path.GetRelativePath(rootPath, tree.FilePath),
			usings          = usings,
			global_usings   = globalUsings
		};
	}
	
	private static string[] ExtractGlobalUsings(Compilation compilation)
	{
		// Global usings come from <Using> items in the project or ImplicitUsings.
		// They're baked into the compilation as invisible using directives.
		// We can approximate by looking at all syntax trees for global using directives.
		var globalUsings = new HashSet<string>(StringComparer.Ordinal);
		
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
	
	private sealed record UsingDirective(string? Namespace, string? Alias);
}
