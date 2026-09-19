using System.Text.Json.Nodes;

/// <summary>
///     Covers #276: <c>WorkspaceInstance.InvalidateFile</c> applies edits to an already-tracked
///     <c>AdditionalFiles</c> item incrementally via <c>Solution.WithAdditionalDocumentText</c>
///     instead of flagging a full MSBuild reload. Also guards the crash bug this change exposed and
///     fixed as a side effect: before #276, editing an EXISTING tracked additional or analyzer-config
///     document called <c>Solution.WithDocumentText</c> on a non-source <c>DocumentId</c>, which
///     throws <c>InvalidOperationException</c> — uncaught by the existing
///     <c>IOException</c>/<c>UnauthorizedAccessException</c> filter. <c>MSBuildWorkspace</c> does not
///     support <c>TryApplyChanges</c> for analyzer-config documents (verified against Roslyn 5.3.0), so
///     an <c>.editorconfig</c> edit must still fall back to a full reload — this group asserts that
///     fallback happens cleanly (no protocol error) rather than propagating the exception. Fixtures
///     live in a throwaway MSBuild project under %TEMP% per #274.
/// </summary>
static class IncrementalAdditionalDocTests
{
	internal static async Task<TestGroup> BuildAsync(TestContext ctx)
	{
		
		var fx = TestFixtures.NewMsBuildProject("IncrementalAdditionalDoc",
			extraProjectXml: """<ItemGroup><AdditionalFiles Include="Notes.txt" /></ItemGroup>""");
		
		var csproj = fx.Csproj!;
		
		// Written before the first tool call so the initial load tracks Notes.txt as an additional
		// document and .editorconfig as an analyzer-config document — no FSW timing involved.
		fx.Write("Probe.cs",       "class Probe { }\n");
		fx.Write("Notes.txt",      "note = 1\n");
		fx.Write(".editorconfig", "root = true\nprobe = 1\n");
		
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
		
		async Task<bool?> ReloadPendingAsync()
		{
			
			var (ok, data) = await Call("roslyn_check_drift", new { projectPath = csproj });
			
			if(!ok || data?["drifted"] is null)
				
				return null;
			
			return data["reload_pending"]?.GetValue<bool>() ?? false;
		}
		
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
		
		async Task<(bool pass, string message)> AssertFlag(string filePath, string pattern, string replacement, bool expectFlagged, string what)
		{
			
			if(!await SettleAsync())
				
				return (false, "FAIL  (precondition: reload_pending never cleared before the write)");
			
			var (ok, data) = await Call("roslyn_replace_in_file",
				new { filePath, pattern, replacement, projectPath = csproj });
			
			if(!ok || data?["error"] is not null)
				
				return (false, $"FAIL  (roslyn_replace_in_file failed: {data?["error"]?.GetValue<string>() ?? "protocol error"})");
			
			var pending = await ReloadPendingAsync();
			
			if(pending is null)
				
				return (false, "FAIL  (roslyn_check_drift returned no reload_pending)");
			
			if(pending != expectFlagged)
				
				return (false, $"FAIL  ({what}: expected reload_pending={expectFlagged}, got {pending})");
			
			return (true, $"PASS  ({what}: reload_pending={pending})");
		}
		
		// Pay the MSBuild load now so the first test measures the edit, not the load.
		await fx.WarmAsync(ctx);
		
		var tests = new List<TestCase> {
			
			// The headline case. Pre-fix, an existing tracked AdditionalFiles item took the same
			// full-reload path as a brand-new input; post-fix it applies via
			// WithAdditionalDocumentText and never flags a reload.
			new("incremental additional doc: editing a tracked AdditionalFiles item does not flag a reload",
				() => AssertFlag("Notes.txt", "note = 1", "note = 2",
					expectFlagged: false, what: "AdditionalFiles edit")),
			
			// MSBuildWorkspace.CanApplyChange(ChangeAnalyzerConfigDocument) is false, so this must
			// still take the full-reload path — but cleanly. Pre-fix this called WithDocumentText on
			// an analyzer-config DocumentId, throwing InvalidOperationException uncaught by the
			// existing IOException/UnauthorizedAccessException filter; that regression is what this
			// assertion (ok == true, no error) guards against.
			new("incremental additional doc: editing a tracked .editorconfig still flags a reload cleanly",
				() => AssertFlag(".editorconfig", "probe = 1", "probe = 2",
					expectFlagged: true, what: ".editorconfig edit")),
		};
		
		return new TestGroup($"Incremental Additional/AnalyzerConfig Doc ({tests.Count} tests)", tests, Teardown: async () =>
		{
			
			// Leave the server settled for the next group, then drop the temp project.
			await SettleAsync();
			
			fx.Dispose();
		});
	}
}
