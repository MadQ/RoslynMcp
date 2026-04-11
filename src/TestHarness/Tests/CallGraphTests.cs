using System.Text.Json.Nodes;

static class CallGraphTests
{
	internal static async Task RunAsync(TestContext ctx, List<(bool pass, string message)> tests)
	{
		Console.WriteLine("\nCall Graph Tools (2 tests)");
		Console.WriteLine("─────────────────────────────────────────────────────────────");
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_find_callers: find callers of GetCompilation",
			"roslyn_find_callers",
			new { symbolName = "GetCompilation", containingType = "WorkspaceManager", projectPath = ctx.TargetPath },
			data => data?["total_callers"]?.GetValue<int>() > 0
				 && data?["callers"]?.AsArray().Count > 0
				 && data?["callers"]?[0]?["caller"] is not null
				 && data?["callers"]?[0]?["file"] is not null
		));
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_get_call_graph: outgoing calls from GetCompilation",
			"roslyn_get_call_graph",
			new { symbolName = "GetCompilation", containingType = "WorkspaceManager", projectPath = ctx.TargetPath },
			data => data?["method"]?.GetValue<string>().Contains("GetCompilation") == true
				 && data?["total_calls"]?.GetValue<int>() > 0
				 && data?["calls"]?.AsArray().Count > 0
				 && data?["calls"]?[0]?["callee"] is not null
		));
	}
}
