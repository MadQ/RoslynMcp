using System.Text.Json.Nodes;

static class CodeGenerationTests
{
	internal static async Task RunAsync(TestContext ctx, List<(bool pass, string message)> tests)
	{
		Console.WriteLine("\nCode Generation Tools (1 test)");
		Console.WriteLine("─────────────────────────────────────────────────────────────");
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_get_symbols_in_scope: enumerate symbols at location",
			"roslyn_get_symbols_in_scope",
			new { filePath = "WorkspaceManager.cs", line = 80, column = 10, projectPath = ctx.TargetPath },
			data => data?["fields"] is not null || data?["methods"] is not null
		));
	}
}
