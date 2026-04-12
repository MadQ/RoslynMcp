using System.Diagnostics;
using System.Text.Json.Nodes;

static class NavigationTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		
		// Shared between the two pagination tests — page 1 sets it, page 2 reads it.
		string? pageToken = null
		;
		
		var tests = new List<TestCase> {
			
			new("roslyn_get_symbol_info: resolve symbol at location",
				() => ctx.RunTestAsync(
					"roslyn_get_symbol_info",
					new { filePath = "Program.cs", line = 10, column = 10, projectPath = ctx.TargetPath },
					data => data?["kind"] is not null && data?["name"] is not null)),
			
			new("roslyn_find_references: locate WorkspaceManager usages",
				() => ctx.RunTestAsync(
					"roslyn_find_references",
					new { symbolName = "WorkspaceManager", projectPath = ctx.TargetPath },
					data => data?["total_references"]?.GetValue<int>() > 0 && data?["references"]?.AsArray().Count > 0)),
			
			new("roslyn_find_references: pagination token (page 1)",
				async () => {
					
					var sw = Stopwatch.StartNew();
					
					await ctx.SendAsync(new {
						
						jsonrpc = "2.0",
						id      = ctx.NextId(),
						method  = "tools/call",
						@params = new { name = "roslyn_find_references", arguments = new { symbolName = "WorkspaceManager", projectPath = ctx.TargetPath, take = 2 } }
					});
					
					var resp1    = await ctx.ReceiveAsync();
					sw.Stop();
					var content1  = resp1?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
					var page1     = content1 is not null ? JsonNode.Parse(content1) : null;
					pageToken     = page1?["page_token"]?.GetValue<string>();
					var hasMore   = page1?["has_more"]?.GetValue<bool>() == true;
					var page1Refs = page1?["references"]?.AsArray();
					
					return pageToken is not null && hasMore && page1Refs?.Count == 2
						? (true,  $"PASS  [{sw.ElapsedMilliseconds}ms]")
						: (false, $"FAIL  (no page_token or has_more) [{sw.ElapsedMilliseconds}ms]");
				}),
			
			new("roslyn_find_references: pagination token (page 2)",
				async () => {
					
					var sw = Stopwatch.StartNew();
					
					await ctx.SendAsync(new {
						
						jsonrpc = "2.0",
						id      = ctx.NextId(),
						method  = "tools/call",
						@params = new { name = "roslyn_find_references", arguments = new { symbolName = "WorkspaceManager", projectPath = ctx.TargetPath, skip = 2, take = 2, page_token = pageToken } }
					});
					
					var resp2      = await ctx.ReceiveAsync();
					sw.Stop();
					var content2   = resp2?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
					var page2      = content2 is not null ? JsonNode.Parse(content2) : null;
					var page2Items = page2?["items"]?.AsArray();
					var page2Token = page2?["page_token"]?.GetValue<string>();
					
					return page2Items?.Count > 0 && page2Token == pageToken
						? (true,  $"PASS  [{sw.ElapsedMilliseconds}ms]")
						: (false, $"FAIL  (page 2 missing items or wrong token) [{sw.ElapsedMilliseconds}ms]");
				}),
			
			new("roslyn_get_symbol_definition: find WorkspaceManager declaration",
				() => ctx.RunTestAsync(
					"roslyn_get_symbol_definition",
					new { symbolName = "WorkspaceManager", projectPath = ctx.TargetPath },
					data => data?["file"]?.GetValue<string>().Contains("WorkspaceManager.cs") == true)),
		};
		
		return new TestGroup($"Navigation Tools ({tests.Count} tests)", tests);
	}
}
