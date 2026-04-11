using System.Text.Json.Nodes;

static class DiscoveryTests
{
	internal static async Task RunAsync(TestContext ctx, List<(bool pass, string message)> tests)
	{
		Console.WriteLine("\nDiscovery Tools (7 tests)");
		Console.WriteLine("─────────────────────────────────────────────────────────────");
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_search_files: find 'WorkspaceManager' in .cs files",
			"roslyn_search_files",
			new { pattern = "WorkspaceManager", filePattern = "*.cs", take = 10, projectPath = ctx.TargetPath },
			data => data?["matches"]?.AsArray().Count > 0
		));
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_semantic_search: find TODO comments only",
			"roslyn_semantic_search",
			new { pattern = "TODO", context = "comments", take = 10, projectPath = ctx.TargetPath },
			data => data?["matches"]?.AsArray().Count > 0 && data?["matches"]?[0]?["context"]?.GetValue<string>() == "comment"
		));
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_list_types: enumerate types in RoslynMcp.Tools namespace",
			"roslyn_list_types",
			new { namespaceFilter = "RoslynMcp.Tools", projectPath = ctx.TargetPath },
			data => data?["types"]?.AsArray().Count > 10 && data?["page_token"] is not null
		));
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_list_files: enumerate tool files with glob pattern",
			"roslyn_list_files",
			new { pattern = "**/*Tool.cs", take = 50, projectPath = ctx.TargetPath },
			data => data?["count"]?.GetValue<int>() > 20 && data?["files"]?.AsArray().Any(f => f?.GetValue<string>().Contains("Tool.cs") == true) == true
		));
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_get_file_outline: WorkspaceManager structure",
			"roslyn_get_file_outline",
			new { filePath = "WorkspaceManager.cs", projectPath = ctx.TargetPath },
			data => data?["types"]?.AsArray().Count > 0
		));
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_get_project_info: verify TFM and packages",
			"roslyn_get_project_info",
			new { projectPath = ctx.TargetPath },
			data => data?["target_framework"]?.GetValue<string>()?.StartsWith("net") == true
		));
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_get_usings: extract using directives from Program.cs",
			"roslyn_get_usings",
			new { filePath = "Program.cs", projectPath = ctx.TargetPath },
			data => data?["usings"]?.AsArray().Count > 0
		));
	}
}
