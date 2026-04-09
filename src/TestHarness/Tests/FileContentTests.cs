using System.Text.Json.Nodes;

static class FileContentTests
{
	internal static async Task RunAsync(TestContext ctx, List<(bool pass, string message)> tests)
	{
		Console.WriteLine("\nFile Content Tools (4 tests)");
		Console.WriteLine("─────────────────────────────────────────────────────────────");
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_read_file: read .cs file from Roslyn in-memory",
			"roslyn_read_file",
			new { filePath = "WorkspaceManager.cs", projectPath = ctx.TargetPath },
			data => data?["source"]?.GetValue<string>() == "roslyn"
				 && data?["total_lines"]?.GetValue<int>() > 100
				 && data?["lines"]?.AsArray().Count > 0
		));
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_read_file: read with line range",
			"roslyn_read_file",
			new { filePath = "WorkspaceManager.cs", startLine = 1, endLine = 10, projectPath = ctx.TargetPath },
			data => data?["lines"]?.AsArray().Count == 10
				 && data?["start_line"]?.GetValue<int>() == 1
				 && data?["end_line"]?.GetValue<int>() == 10
		));
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_read_file: read non-.cs file from disk",
			"roslyn_read_file",
			new { filePath = "RoslynMcp.csproj", projectPath = ctx.TargetPath },
			data => data?["source"]?.GetValue<string>() == "disk"
				 && data?["total_lines"]?.GetValue<int>() > 0
		));
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_get_line_count: single and multi-file",
			"roslyn_get_line_count",
			new { filePaths = "WorkspaceManager.cs,RoslynMcp.csproj", projectPath = ctx.TargetPath },
			data => data?["files"]?.AsArray().Count == 2
				 && data?["files"]?[0]?["line_count"]?.GetValue<int>() > 100
		));
	}
}
