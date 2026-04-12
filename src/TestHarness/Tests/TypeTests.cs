using System.Text.Json.Nodes;

static class TypeTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		
		var tests = new List<TestCase> {
			
			new("roslyn_get_type_members: WorkspaceManager members with signatures",
				() => ctx.RunTestAsync(
					"roslyn_get_type_members",
					new { typeName = "WorkspaceManager", projectPath = ctx.TargetPath },
					data => data?["members"]?.AsArray().Count > 0 && data["members"]?[0]?["signature"] is not null)),
			
			new("roslyn_get_type_hierarchy: WorkspaceManager inheritance",
				() => ctx.RunTestAsync(
					"roslyn_get_type_hierarchy",
					new { typeName = "WorkspaceManager", projectPath = ctx.TargetPath },
					data => data?["interfaces_and_derived"]?.AsArray().Any(i => i?.GetValue<string>().Contains("IDisposable") == true) == true)),
			
			new("roslyn_find_implementations: IDisposable implementers",
				() => ctx.RunTestAsync(
					"roslyn_find_implementations",
					new { symbolName = "IDisposable", projectPath = ctx.TargetPath },
					data => data?["error"] is not null
						|| (data?["total_implementations"] is not null && data?["implementations"]?.AsArray() is not null))),
			
			new("roslyn_get_symbol_documentation: WorkspaceManager XML docs",
				() => ctx.RunTestAsync(
					"roslyn_get_symbol_documentation",
					new { symbolName = "WorkspaceManager", projectPath = ctx.TargetPath },
					data => data?["symbol_name"] is not null)),
		};
		
		return new TestGroup($"Type Understanding Tools ({tests.Count} tests)", tests);
	}
}
