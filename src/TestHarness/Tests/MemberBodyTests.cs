using System.Text.Json.Nodes;

static class MemberBodyTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		
		var tests = new List<TestCase> {
			
			new("roslyn_get_member_body: single method",
				() => ctx.RunTestAsync(
					"roslyn_get_member_body",
					new { symbolName = "GetCompilation", containingType = "WorkspaceManager", projectPath = ctx.TargetPath },
					data => data?["body"]?.GetValue<string>().Contains("GetCompilation") == true
						&& data?["start_line"]?.GetValue<int>() > 0
						&& data?["symbol_kind"]?.GetValue<string>() == "method")),
			
			new("roslyn_get_member_body: partial class (multiple parts)",
				() => ctx.RunTestAsync(
					"roslyn_get_member_body",
					new { symbolName = "WorkspaceManager", projectPath = ctx.TargetPath },
					data => data?["parts"]?.AsArray().Count > 1
						&& data?["note"]?.GetValue<string>().Contains("Partial") == true)),
		};
		
		return new TestGroup($"Member Body Tools ({tests.Count} tests)", tests);
	}
}
