using System.Text.Json.Nodes;

static class RefactoringTests
{
	internal static async Task RunAsync(TestContext ctx, List<(bool pass, string message)> tests)
	{
		Console.WriteLine("\nRefactoring Tools (6 tests)");
		Console.WriteLine("─────────────────────────────────────────────────────────────");
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_preview_rename: generate diff for renaming compilation",
			"roslyn_preview_rename",
			new { symbolName = "compilation", newName = "compilation2", containingType = "WorkspaceInstance", projectPath = ctx.TargetPath },
			data => data?["token"] is not null || data?["message"] is not null
		));
		
		// ── change_signature tests ──────────────────────────────────────────────
		
		// 1. Basic: add a parameter, verify diff has [Obsolete] and forwarding overload.
		tests.Add(await ctx.RunTestAsync(
			"roslyn_change_signature: add parameter with default",
			"roslyn_change_signature",
			new {
				
				methodName     = "NormalizePath",
				containingType = "RoslynMcpTool",
				addParameters  = """[{"name":"toLower","type":"bool","defaultValue":"false"}]""",
				projectPath    = ctx.TargetPath
			},
			data => data?["token"] is not null
				 && data?["diff"]?.GetValue<string>().Contains("Obsolete") == true
				 && data?["parameters_added"]?.AsArray().Count == 1
				 && data?["deprecation_message"]?.GetValue<string>().Contains("NormalizePath") == true
		));
		
		// 2. Error: non-method symbol.
		tests.Add(await ctx.RunTestAsync(
			"roslyn_change_signature: reject non-method symbol",
			"roslyn_change_signature",
			new {
				
				methodName    = "WorkspaceManager",
				addParameters = """[{"name":"x","type":"int"}]""",
				projectPath   = ctx.TargetPath
			},
			data => data?["error"]?.GetValue<string>().Contains("not a method") == true
		));
		
		// 3. Error: no parameters provided.
		tests.Add(await ctx.RunTestAsync(
			"roslyn_change_signature: reject empty addParameters",
			"roslyn_change_signature",
			new {
				
				methodName     = "NormalizePath",
				containingType = "RoslynMcpTool",
				projectPath    = ctx.TargetPath
			},
			data => data?["error"]?.GetValue<string>().Contains("No parameters") == true
		));
		
		// 4. Error: invalid JSON for addParameters.
		tests.Add(await ctx.RunTestAsync(
			"roslyn_change_signature: reject invalid JSON",
			"roslyn_change_signature",
			new {
				
				methodName     = "NormalizePath",
				containingType = "RoslynMcpTool",
				addParameters  = "not valid json",
				projectPath    = ctx.TargetPath
			},
			data => data?["error"]?.GetValue<string>().Contains("parse") == true
		));
		
		// 5. Error: duplicate parameter name.
		tests.Add(await ctx.RunTestAsync(
			"roslyn_change_signature: reject duplicate parameter name",
			"roslyn_change_signature",
			new {
				
				methodName     = "NormalizePath",
				containingType = "RoslynMcpTool",
				addParameters  = """[{"name":"filePath","type":"string"}]""",
				projectPath    = ctx.TargetPath
			},
			data => data?["error"]?.GetValue<string>().Contains("already exists") == true
		));
	}
}
