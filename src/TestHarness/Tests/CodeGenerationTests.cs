using System.Text.Json.Nodes;

static class CodeGenerationTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		
		var tests = new List<TestCase> {
			
			new("roslyn_get_symbols_in_scope: enumerate symbols at location",
				() => ctx.RunTestAsync(
					"roslyn_get_symbols_in_scope",
					new { filePath = "WorkspaceManager.cs", line = 80, column = 10, projectPath = ctx.TargetPath },
					data => data?["fields"] is not null || data?["methods"] is not null)),
		};
		
		return new TestGroup($"Code Generation Tools ({tests.Count} tests)", tests);
	}
}
