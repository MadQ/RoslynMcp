using System.Text.Json.Nodes;

static class DiscoveryTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		
		var tests = new List<TestCase> {
			
			new("roslyn_search_files: find 'WorkspaceManager' in .cs files",
				() => ctx.RunTestAsync(
					"roslyn_search_files",
					new { pattern = "WorkspaceManager", filePattern = "*.cs", take = 10, projectPath = ctx.TargetPath },
					data => data?["matches"]?.AsArray().Count > 0)),
			
			new("roslyn_semantic_search: find TODO comments only",
				() => ctx.RunTestAsync(
					"roslyn_semantic_search",
					new { pattern = "TODO", context = "comments", take = 10, projectPath = ctx.TargetPath },
					data => data?["matches"]?.AsArray().Count > 0 && data?["matches"]?[0]?["context"]?.GetValue<string>() == "comment")),
			
			new("roslyn_list_types: enumerate types in RoslynMcp.Tools namespace",
				() => ctx.RunTestAsync(
					"roslyn_list_types",
					new { namespaceFilter = "RoslynMcp.Tools", projectPath = ctx.TargetPath },
					data => data?["types"]?.AsArray().Count > 10 && data?["page_token"] is not null)),
			
			new("roslyn_list_files: enumerate tool files with glob pattern",
				() => ctx.RunTestAsync(
					"roslyn_list_files",
					new { pattern = "**/*Tool.cs", take = 50, projectPath = ctx.TargetPath },
					data => data?["count"]?.GetValue<int>() > 20
						&& data?["files"]?.AsArray().Any(f => f?.GetValue<string>().Contains("Tool.cs") == true) == true)),
			
			new("roslyn_get_file_outline: WorkspaceManager structure",
				() => ctx.RunTestAsync(
					"roslyn_get_file_outline",
					new { filePath = "WorkspaceManager.cs", projectPath = ctx.TargetPath },
					data => data?["types"]?.AsArray().Count > 0)),
			
			new("roslyn_get_project_info: verify TFM and packages",
				() => ctx.RunTestAsync(
					"roslyn_get_project_info",
					new { projectPath = ctx.TargetPath },
					data => data?["target_framework"]?.GetValue<string>()?.StartsWith("net") == true)),
			
			new("roslyn_get_usings: extract using directives from Program.cs",
				() => ctx.RunTestAsync(
					"roslyn_get_usings",
					new { filePath = "Program.cs", projectPath = ctx.TargetPath },
					data => data?["usings"]?.AsArray().Count > 0)),
		};
		
		return new TestGroup($"Discovery Tools ({tests.Count} tests)", tests);
	}
}
