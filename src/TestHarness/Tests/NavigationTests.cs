using System.Text.Json.Nodes;

static class NavigationTests
{
	internal static async Task RunAsync(TestContext ctx, List<(bool pass, string message)> tests)
	{
		Console.WriteLine("\nNavigation Tools (3 tests)");
		Console.WriteLine("─────────────────────────────────────────────────────────────");
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_get_symbol_info: resolve symbol at location",
			"roslyn_get_symbol_info",
			new { filePath = "Program.cs", line = 10, column = 10, projectPath = ctx.TargetPath },
			data => data?["kind"] is not null && data?["name"] is not null
		));
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_find_references: locate WorkspaceManager usages",
			"roslyn_find_references",
			new { symbolName = "WorkspaceManager", projectPath = ctx.TargetPath },
			data => data?["total_references"]?.GetValue<int>() > 0 && data?["references"]?.AsArray().Count > 0
		));
		
		// ── Pagination token test: page 1 with small take, then page 2 via token ──
		{
			Console.Write($"  {"roslyn_find_references: pagination token (page 1)",-50} ");
			var sw1 = System.Diagnostics.Stopwatch.StartNew();
			
			await ctx.SendAsync(new {
				
				jsonrpc = "2.0",
				id      = ctx.NextId(),
				method  = "tools/call",
				@params = new { name = "roslyn_find_references", arguments = new { symbolName = "WorkspaceManager", projectPath = ctx.TargetPath, take = 2 } }
			});
			
			var resp1 = await ctx.ReceiveAsync();
			sw1.Stop();
			var content1 = resp1?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
			var page1    = content1 is not null ? JsonNode.Parse(content1) : null;
			var token    = page1?["page_token"]?.GetValue<string>();
			var hasMore  = page1?["has_more"]?.GetValue<bool>() == true;
			var page1Refs = page1?["references"]?.AsArray();
			
			if(token is not null && hasMore && page1Refs?.Count == 2) {
				
				tests.Add((true, $"PASS  [{sw1.ElapsedMilliseconds}ms]"));
				Console.WriteLine($"PASS  [{sw1.ElapsedMilliseconds}ms]");
			}
			else {
				
				tests.Add((false, $"FAIL  (no page_token or has_more) [{sw1.ElapsedMilliseconds}ms]"));
				Console.WriteLine($"FAIL  (no page_token or has_more) [{sw1.ElapsedMilliseconds}ms]");
			}
			
			Console.Write($"  {"roslyn_find_references: pagination token (page 2)",-50} ");
			var sw2 = System.Diagnostics.Stopwatch.StartNew();
			
			await ctx.SendAsync(new {
				
				jsonrpc = "2.0",
				id      = ctx.NextId(),
				method  = "tools/call",
				@params = new { name = "roslyn_find_references", arguments = new { symbolName = "WorkspaceManager", projectPath = ctx.TargetPath, skip = 2, take = 2, page_token = token } }
			});
			
			var resp2 = await ctx.ReceiveAsync();
			sw2.Stop();
			var content2 = resp2?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
			var page2    = content2 is not null ? JsonNode.Parse(content2) : null;
			var page2Items = page2?["items"]?.AsArray();
			var page2Token = page2?["page_token"]?.GetValue<string>();
			
			if(page2Items?.Count > 0 && page2Token == token) {
				
				tests.Add((true, $"PASS  [{sw2.ElapsedMilliseconds}ms]"));
				Console.WriteLine($"PASS  [{sw2.ElapsedMilliseconds}ms]");
			}
			else {
				
				tests.Add((false, $"FAIL  (page 2 missing items or wrong token) [{sw2.ElapsedMilliseconds}ms]"));
				Console.WriteLine($"FAIL  (page 2 missing items or wrong token) [{sw2.ElapsedMilliseconds}ms]");
			}
		}
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_get_symbol_definition: find WorkspaceManager declaration",
			"roslyn_get_symbol_definition",
			new { symbolName = "WorkspaceManager", projectPath = ctx.TargetPath },
			data => data?["file"]?.GetValue<string>().Contains("WorkspaceManager.cs") == true));
	}
}
