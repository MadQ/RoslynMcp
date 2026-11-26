using System.Text.Json.Nodes;

static class CallGraphTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		
		var tests = new List<TestCase> {
			
			new("roslyn_find_callers: find callers of GetCompilation",
				() => ctx.RunTestAsync(
					"roslyn_find_callers",
					new { symbolName = "GetCompilation", containingType = "WorkspaceManager", projectPath = ctx.TargetPath },
					data => data?["total_callers"]?.GetValue<int>() > 0
						// Each calling line is under the declaration that calls (#487).
						&& SearchHits.In(data?["callers"]) is [{ File.Length: > 0, Declaration: not null, Text.Length: > 0 }, ..])),
			
			new("roslyn_get_call_graph: outgoing calls from GetCompilation",
				() => ctx.RunTestAsync(
					"roslyn_get_call_graph",
					new { symbolName = "GetCompilation", containingType = "WorkspaceManager", projectPath = ctx.TargetPath },
					data => data?["method"]?.GetValue<string>().Contains("GetCompilation") == true
						&& data?["total_calls"]?.GetValue<int>() > 0
						&& data?["calls"]?.AsArray().Count > 0
						&& data?["calls"]?[0]?["callee"] is not null
						&& data?["calls"]?[0]?["text"] is not null
						&& data?["file"]?.GetValue<string>().EndsWith("WorkspaceManager.cs") == true
						&& data?["lines"] is not null)),
		};
		
		return new TestGroup($"Call Graph Tools ({tests.Count} tests)", tests);
	}
}
