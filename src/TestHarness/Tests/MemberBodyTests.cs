using System.Text.Json.Nodes;

static class MemberBodyTests
{
	internal static async Task RunAsync(TestContext ctx, List<(bool pass, string message)> tests)
	{
		Console.WriteLine("\nMember Body Tools (2 tests)");
		Console.WriteLine("─────────────────────────────────────────────────────────────");
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_get_member_body: single method",
			"roslyn_get_member_body",
			new { symbolName = "GetCompilation", containingType = "WorkspaceManager", projectPath = ctx.TargetPath },
			data => data?["body"]?.GetValue<string>().Contains("GetCompilation") == true
				 && data?["start_line"]?.GetValue<int>() > 0
				 && data?["symbol_kind"]?.GetValue<string>() == "method"
		));
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_get_member_body: partial class (multiple parts)",
			"roslyn_get_member_body",
			new { symbolName = "WorkspaceManager", projectPath = ctx.TargetPath },
			data => data?["parts"]?.AsArray().Count > 1
				 && data?["note"]?.GetValue<string>().Contains("Partial") == true
		));
	}
}
