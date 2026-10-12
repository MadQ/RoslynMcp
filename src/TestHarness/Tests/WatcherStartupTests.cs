using System.Text.Json.Nodes;

/// <summary>
///     Covers a file that is created or deleted in the moment between a workspace's load and
///     its file watchers going live (#348). The watchers start on a background thread once the
///     workspace has been read from disk. A change made before they are live raises no event,
///     so a catch-up pass runs when they are. It used to look only at the documents it already
///     had, and only at when they were written: a new file was never found, and neither was a
///     deleted one. The workspace then stayed out of step with the disk, with no reload pending
///     and nothing for a drift check to see.
///     <para>
///         Each test makes its change <b>straight after</b> the tool call that loads the
///         workspace, with no wait and no probe — that is the window. Each has a fixture of
///         its own, because the window exists once per load. The result is polled for,
///         because whichever way the change is noticed, by a watcher or by the catch-up pass,
///         it goes through the debounce.
///     </para>
///     <para>
///         <b>What these tests can and cannot show.</b> On Windows and Linux the window is a
///         few milliseconds and the watchers are usually live before the test's change, so the
///         tests pass there with or without the catch-up pass. On macOS the window is long
///         enough that they fall into it: both shapes failed there before the fix (see PR #346,
///         where a delete and then a probe file went unseen). They are kept on every platform
///         because the behaviour has to hold on every platform, and the three-OS gate is what
///         gives them their teeth.
///     </para>
/// </summary>
static class WatcherStartupTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		
		var adhocNew  = TestFixtures.NewAdhocDir("StartupAdhocCreate");
		var adhocGone = TestFixtures.NewAdhocDir("StartupAdhocDelete");
		var projNew   = TestFixtures.NewMsBuildProject("StartupProjectCreate");
		var projGone  = TestFixtures.NewMsBuildProject("StartupProjectDelete");
		
		adhocNew.Write("There.cs",  "class ThereAtLoad { }\n");
		adhocGone.Write("Stays.cs", "class StaysPut { }\n");
		projNew.Write("There.cs",   "class ThereAtLoad { }\n");
		projGone.Write("Stays.cs",  "class StaysPut { }\n");
		
		var adhocDoomed = adhocGone.Write("Doomed.cs", "class GoesAway { }\n");
		var projDoomed  = projGone.Write("Doomed.cs",  "class GoesAway { }\n");
		
		// Calls a tool and returns the first content block parsed as JSON.
		async Task<JsonNode?> Call(string tool, object args)
		{
			
			await ctx.SendAsync(new { jsonrpc = "2.0", id = ctx.NextId(), method = "tools/call",
				@params = new { name = tool, arguments = args } });
			
			var resp = await ctx.ReceiveAsync();
			var text = resp?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
			
			if(text is null)
				
				return null;
			
			try { return JsonNode.Parse(text); }
			catch { return null; }
		}
		
		// Whether the workspace serves this file from a document of its own.
		async Task<bool> IsDocumentAsync(string? projectPath, string filePath)
			=> (await Call("roslyn_read_file", new { filePath, projectPath }))?["source"]?.GetValue<string>() == "roslyn";
		
		// reload_pending is omitted from the JSON when false.
		async Task<bool> ReloadPendingAsync(string? projectPath)
			=> (await Call("roslyn_check_drift", new { projectPath }))?["reload_pending"]?.GetValue<bool>() == true;
		
		// Polls until the condition holds, for at most fifteen seconds.
		static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
		{
			
			var deadline = DateTime.UtcNow.AddSeconds(15);
			
			while(DateTime.UtcNow < deadline) {
				
				if(await condition())
					
					return true;
				
				await Task.Delay(250);
			}
			
			return false;
		}
		
		var tests = new List<TestCase> {
			
			// A workspace without a project takes every source file for a document, so a new
			// file has to become one.
			new("watcher start-up: a file created straight after the load becomes a document in a workspace without a project",
				async () => {
					
					if(!await IsDocumentAsync(adhocNew.Dir, "There.cs"))
						
						return (false, "FAIL  (precondition: the workspace did not load There.cs)");
					
					File.WriteAllText(adhocNew.PathOf("Arrived.cs"), "class ArrivedLate { }\n");
					
					return await EventuallyAsync(() => IsDocumentAsync(adhocNew.Dir, "Arrived.cs"))
						? (true,  "PASS")
						: (false, "FAIL  (Arrived.cs is not a document fifteen seconds after it was created)");
				}),
			
			new("watcher start-up: a file deleted straight after the load stops being a document in a workspace without a project",
				async () => {
					
					if(!await IsDocumentAsync(adhocGone.Dir, "Doomed.cs"))
						
						return (false, "FAIL  (precondition: the workspace did not load Doomed.cs)");
					
					File.Delete(adhocDoomed);
					
					return await EventuallyAsync(async () => !await IsDocumentAsync(adhocGone.Dir, "Doomed.cs"))
						? (true,  "PASS")
						: (false, "FAIL  (Doomed.cs is still a document fifteen seconds after it was deleted)");
				}),
			
			// In a project a new source file cannot be added in place: the workspace has to
			// reload. The flag is the observable — asking for it does not trigger the reload.
			new("watcher start-up: a source file created straight after the load flags a reload in a project",
				async () => {
					
					if(!await IsDocumentAsync(projNew.Csproj, "There.cs") || await ReloadPendingAsync(projNew.Csproj))
						
						return (false, "FAIL  (precondition: the workspace did not load There.cs, or a reload was pending already)");
					
					File.WriteAllText(projNew.PathOf("Arrived.cs"), "class ArrivedLate { }\n");
					
					return await EventuallyAsync(() => ReloadPendingAsync(projNew.Csproj))
						? (true,  "PASS")
						: (false, "FAIL  (no reload pending fifteen seconds after Arrived.cs was created)");
				}),
			
			new("watcher start-up: a source file deleted straight after the load flags a reload in a project",
				async () => {
					
					if(!await IsDocumentAsync(projGone.Csproj, "Doomed.cs") || await ReloadPendingAsync(projGone.Csproj))
						
						return (false, "FAIL  (precondition: the workspace did not load Doomed.cs, or a reload was pending already)");
					
					File.Delete(projDoomed);
					
					return await EventuallyAsync(() => ReloadPendingAsync(projGone.Csproj))
						? (true,  "PASS")
						: (false, "FAIL  (no reload pending fifteen seconds after Doomed.cs was deleted)");
				}),
		};
		
		return new TestGroup($"Watcher Start-up ({tests.Count} tests)", tests, Teardown: () => {
			
			adhocNew.Dispose();
			adhocGone.Dispose();
			projNew.Dispose();
			projGone.Dispose();
			
			return Task.CompletedTask;
		});
	}
}
