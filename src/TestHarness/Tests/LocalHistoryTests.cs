using System.Text.Json.Nodes;

static class LocalHistoryTests
{
	internal static async Task<TestGroup> BuildAsync(TestContext ctx)
	{
		
		// The fixture lives in a temp project (#274): original content on disk, then overwritten
		// via the server to produce a backup token before tests run.
		var fx              = TestFixtures.NewMsBuildProject("LocalHistory");
		var tempHistoryFile = "history.txt"
		;
		
		fx.Write(tempHistoryFile, "// original content\n");
		
		// Write new content via the server — this triggers BackupStore.Save and returns backup_token.
		string? historyToken = null
		;
		
		await ctx.SendAsync(new {
			
			jsonrpc = "2.0",
			id      = ctx.NextId(),
			method  = "tools/call",
			@params = new {
				
				name      = "roslyn_write_file",
				arguments = new {
					
					filePath    = tempHistoryFile,
					projectPath = fx.Csproj,
					content     = "// modified content\n"
				}
			}
		});
		
		var resp       = await ctx.ReceiveAsync();
		var content    = resp?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
		var setupData  = content is not null ? JsonNode.Parse(content) : null;
		historyToken   = setupData?["backupToken"]?.GetValue<string>();
		
		var tests = new List<TestCase> {
			
			new("roslyn_local_history: list backups for temp file",
				() => ctx.RunTestAsync(
					"roslyn_local_history",
					new { action = "list", filePath = tempHistoryFile, projectPath = fx.Csproj },
					data => data?["items"]?.AsArray().Count > 0 && data?["count"]?.GetValue<int>() > 0)),
			
			new("roslyn_local_history: preview backup token",
				() => ctx.RunTestAsync(
					"roslyn_local_history",
					new { action = "preview", token = historyToken ?? "invalid", projectPath = fx.Csproj },
					data => data?["token"] is not null && data?["absolutePath"] is not null)),
			
			new("roslyn_local_history: apply restores original content",
				() => ctx.RunTestAsync(
					"roslyn_local_history",
					new { action = "apply", token = historyToken ?? "invalid", projectPath = fx.Csproj },
					data => data?["restored"]?.GetValue<bool>() == true && data?["absolutePath"] is not null)),
		};
		
		return new TestGroup($"Local History Tools ({tests.Count} tests)", tests, Teardown: () =>
		{
			
			fx.Dispose();
			
			return Task.CompletedTask;
		});
	}
}
