using System.Text.Json.Nodes;

/// <summary>
///     Covers which writes flag a workspace reload (#273). Before the fix,
///     <c>WorkspaceInstance.InvalidateFile</c> treated every path that was not a tracked source
///     document as a new compilation input, so editing <c>CHANGELOG.md</c> through
///     <c>roslyn_insert_lines</c> cost a full MSBuild reload on the next compilation-needing call.
///     The observable is <c>roslyn_check_drift.reload_pending</c>, which peeks at the flag without
///     servicing it. Fixtures live in a throwaway MSBuild project under %TEMP% so these tests never
///     write into the dogfood project (#274) — which would itself flag reloads in the developer's
///     live server watching the repo.
/// </summary>
static class ReloadFlaggingTests
{
	internal static async Task<TestGroup> BuildAsync(TestContext ctx)
	{
		
		var fx     = TestFixtures.NewMsBuildProject("ReloadFlagging");
		var csproj = fx.Csproj!;
		
		// Written before the first tool call so the initial load includes Probe.cs — no FSW timing
		// involved. Probe.editorconfig and Probe.props are inert for MSBuild (only a file literally
		// named .editorconfig is honored; nothing imports the .props) but carry the extensions the
		// classifier keys on.
		fx.Write("Probe.cs",           "class Probe { }\n");
		fx.Write("Probe.md",           "# probe\n");
		fx.Write("Probe.editorconfig", "probe = 1\n");
		fx.Write("Probe.props",        "<Project><PropertyGroup><Probe>1</Probe></PropertyGroup></Project>\n");
		
		// Calls a tool and returns (no-protocol-error, parsed JSON data).
		async Task<(bool ok, JsonNode? data)> Call(string tool, object args)
		{
			
			await ctx.SendAsync(new { jsonrpc = "2.0", id = ctx.NextId(), method = "tools/call",
				@params = new { name = tool, arguments = args } });
			
			var resp = await ctx.ReceiveAsync();
			var text = resp?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
			
			JsonNode? data = null;
			
			try { data = JsonNode.Parse(text); }
			catch { }
			
			return (resp?["error"] is null && text.Length > 0, data);
		}
		
		// Null means the probe itself failed. reload_pending is omitted from the JSON when false
		// (WhenWritingDefault), so its absence on an otherwise valid result reads as false.
		async Task<bool?> ReloadPendingAsync()
		{
			
			var (ok, data) = await Call("roslyn_check_drift", new { projectPath = csproj });
			
			if(!ok || data?["drifted"] is null)
				
				return null;
			
			return data["reload_pending"]?.GetValue<bool>() ?? false;
		}
		
		// Services any pending reload (a compilation-needing call) and waits for reload_pending to
		// clear. A reload discarded under the #235 back-off can keep it pending — that is a
		// precondition failure of the environment, reported as such rather than blamed on the test.
		async Task<bool> SettleAsync()
		{
			
			var deadline = DateTime.UtcNow.AddSeconds(15);
			
			while(DateTime.UtcNow < deadline) {
				
				await Call("roslyn_get_diagnostics", new { projectPath = csproj, take = 0, severity = "errors" });
				
				if(await ReloadPendingAsync() == false)
					
					return true;
				
				await Task.Delay(250);
			}
			
			return false;
		}
		
		// One assertion shape for all five tests: settle, write, read the flag.
		async Task<(bool pass, string message)> AssertFlag(string tool, object args, bool expectFlagged, string what)
		{
			
			if(!await SettleAsync())
				
				return (false, "FAIL  (precondition: reload_pending never cleared before the write)");
			
			var (ok, data) = await Call(tool, args);
			
			if(!ok || data?["error"] is not null)
				
				return (false, $"FAIL  ({tool} failed: {data?["error"]?.GetValue<string>() ?? "protocol error"})");
			
			var pending = await ReloadPendingAsync();
			
			if(pending is null)
				
				return (false, "FAIL  (roslyn_check_drift returned no reload_pending)");
			
			if(pending != expectFlagged)
				
				return (false, $"FAIL  ({what}: expected reload_pending={expectFlagged}, got {pending})");
			
			return (true, $"PASS  ({what}: reload_pending={pending})");
		}
		
		// Pay the MSBuild load now so the first test measures the flag, not the load.
		await fx.WarmAsync(ctx);
		
		var tests = new List<TestCase> {
			
			// The headline case. Pre-fix, InvalidateFile's untracked-path branch bumped reloadVersion
			// for a .md exactly as for a new .cs, so this reported reload_pending=true.
			new("reload flagging: .md edit via roslyn_insert_lines does not flag a reload",
				() => AssertFlag("roslyn_insert_lines",
					new { filePath = "Probe.md", text = "probe line", atLine = 1, projectPath = csproj },
					expectFlagged: false, what: ".md insert")),
			
			// Guards against the name rules over-matching: a .json that is not global.json or a
			// lock file is not an evaluation input.
			new("reload flagging: unrelated .json write does not flag a reload",
				() => AssertFlag("roslyn_write_file",
					new { filePath = "Probe.json", content = "{}", createNew = true, projectPath = csproj },
					expectFlagged: false, what: ".json write")),
			
			// A new .cs is the one case the old code got right and must keep getting right: the
			// write goes through WriteAndInvalidate → InvalidateFile synchronously, so the flag is
			// visible before the tool returns.
			new("reload flagging: new .cs via roslyn_write_file flags reload_pending",
				() => AssertFlag("roslyn_write_file",
					new { filePath = "_ReloadProbe_.cs", content = "class _ReloadProbe_ { }\n", createNew = true, projectPath = csproj },
					expectFlagged: true, what: "new .cs")),
			
			// Evaluation inputs by extension — an .editorconfig feeds analyzer config into the
			// compilation, so a change must reload even though Roslyn holds no document for it.
			new("reload flagging: .editorconfig edit via roslyn_replace_in_file flags reload_pending",
				() => AssertFlag("roslyn_replace_in_file",
					new { filePath = "Probe.editorconfig", pattern = "probe = 1", replacement = "probe = 2", projectPath = csproj },
					expectFlagged: true, what: ".editorconfig edit")),
			
			// Same for a .props — MSBuild evaluation input, never a Roslyn document.
			new("reload flagging: .props edit via roslyn_replace_in_file flags reload_pending",
				() => AssertFlag("roslyn_replace_in_file",
					new { filePath = "Probe.props", pattern = "<Probe>1</Probe>", replacement = "<Probe>2</Probe>", projectPath = csproj },
					expectFlagged: true, what: ".props edit")),
		};
		
		return new TestGroup($"Reload Flagging ({tests.Count} tests)", tests, Teardown: async () =>
		{
			
			// Leave the server settled for the next group, then drop the temp project.
			await SettleAsync();
			
			fx.Dispose();
		});
	}
}
