using System.Text.Json.Nodes;

/// <summary>
///     What happens when a file watcher fails while it is running (#499). #495 covered a watcher
///     that cannot start; one that starts and fails later was still only logged.
///     <para>
///         Two of the four cases can be made for real and are tested here: a buffer overflow, and
///         a root that is deleted and comes back. The other two cannot. A watcher that Windows
///         switches off while its directory stays needs a volume that goes away, and a root
///         watched only in part needs Linux's limit on watches to be reached, which a test cannot
///         lower for itself. Those two paths have no test.
///     </para>
/// </summary>
static class WatcherFailureTests
{
	internal static TestGroup Build(TestContext ctx, string serverLogDir, string serverLogGlob)
	{
		var burst = TestFixtures.NewMsBuildProject("WatcherOverflow", targetFrameworks: "net10.0");
		var gone  = TestFixtures.NewMsBuildProject("WatcherRootGone", targetFrameworks: "net10.0");
		
		// The noise directory exists before the load, so that the files written into it later
		// are events of a watcher that is already there and nothing has to be adopted first.
		burst.Write("Probe.cs", "class Probe { }\n");
		burst.Write("noise/first.txt", "noise\n");
		gone.Write("Probe.cs", "class Probe { }\n");
		
		async Task<int> WatchRootsAsync(FixtureProject fx)
			=> (await ctx.CallToolJsonAsync("roslyn_info", new { }))?["workspaces"]?.AsArray()
				.FirstOrDefault(workspace => workspace?["root"]?.GetValue<string>().Contains(Path.GetFileName(fx.Dir), StringComparison.Ordinal) == true)
				?["watch_roots"]?.GetValue<int>() ?? -1;
		
		// Loads the fixture and waits for its watchers, which start on a background thread.
		async Task<bool> WatchedAsync(FixtureProject fx, int seconds)
		{
			await ctx.CallToolJsonAsync("roslyn_get_diagnostics", new { projectPath = fx.Csproj, severity = "errors", take = 0 });
			
			return await UntilAsync(async () => await WatchRootsAsync(fx) >= 1, seconds);
		}
		
		static async Task<bool> UntilAsync(Func<Task<bool>> met, int seconds)
		{
			var deadline = DateTime.UtcNow.AddSeconds(seconds);
			
			while(DateTime.UtcNow < deadline) {
				
				if(await met())
					
					return true;
				
				await Task.Delay(200);
			}
			
			return false;
		}
		
		async Task<bool> ReloadPendingAsync(FixtureProject fx)
			=> (await ctx.CallToolJsonAsync("roslyn_check_drift", new { projectPath = fx.Csproj }))?["reload_pending"]?.GetValue<bool>() == true;
		
		// The index of the first log line at or after 'from' that holds every one of the texts, or -1.
		int LogLine(int from, params string[] texts)
		{
			if(!ServerLog.TryRead(serverLogDir, serverLogGlob, 1, out var lines, out _))
				
				return -1;
			
			for(var i = Math.Max(from, 0); i < lines.Length; i++)
				if(texts.All(text => lines[i].Contains(text, StringComparison.Ordinal)))
					
					return i;
			
			return -1;
		}
		
		static void CopyTree(string from, string to)
		{
			Directory.CreateDirectory(to);
			
			foreach(var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
				Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
			
			foreach(var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
				File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
		}
		
		var tests = new List<TestCase> {
			
			// An overflow drops events and says nothing about which. The server used to log a
			// warning and go on, so whatever was in the dropped events stayed unknown until
			// something else touched the file. Now it scans for what it missed.
			//
			// The overflow is made for real: thousands of files written at once, with long names,
			// since the buffer is filled by the bytes of the names. Whether that overflows depends
			// on the system and on how fast the server drains its buffer, so a run in which it did
			// not is a skip, never a pass. Where it did, two things are required: the scan ran
			// after it (the server's log says so, naming this workspace), and the one source file
			// among the noise, written last and so the likeliest to be in a dropped event, is
			// known to the server, which shows as a pending reload.
			new("watcher failures: after a buffer overflow the server scans for what it missed",
				async () => {
					
					if(!await WatchedAsync(burst, 20))
						
						return (false, "FAIL  (the fixture's watcher did not start within 20 s)");
					
					var name  = Path.GetFileName(burst.Dir);
					var start = ServerLog.TryRead(serverLogDir, serverLogGlob, 1, out var before, out var failure) ? before.Length : -1;
					
					if(start < 0)
						
						return (false, failure);
					
					const int Files = 6000;
					
					var padding = new string('x', 120);
					
					for(var i = 0; i < Files; i++)
						File.WriteAllBytes(burst.PathOf($"noise/{i:D5}-{padding}.txt"), []);
					
					burst.Write("Burst.cs", "class Burst { }\n");
					
					var overflow = -1;
					
					if(!await UntilAsync(() => Task.FromResult((overflow = LogLine(start, "dropped events", name)) >= 0), 5))
						
						return Skip.NotApplicable($"writing {Files} files at once did not overflow the watcher's buffer on this system");
					
					var scanned = await UntilAsync(() => Task.FromResult(LogLine(overflow, "unseen by the watchers", name) >= 0), 15);
					var pending = await UntilAsync(() => ReloadPendingAsync(burst), 10);
					
					return scanned && pending
						? (true, "PASS")
						: (false, $"FAIL  (after the overflow: scan logged: {scanned}, the new source file flagged a reload: {pending})");
				}),
			
			// A workspace root that is deleted and created again: a checkout removed and cloned
			// anew under a running server. Windows reports the deletion as a watcher error and
			// the server retires the watcher. It used to keep the plan's key, though, so the next
			// reload took the plan as already in place and the new directory was never watched:
			// every later change made outside the server went unseen, with nothing saying so.
			//
			// The tree is copied aside and put back, build output included, so the workspace that
			// reloads is the one that was loaded. Windows only: on Linux and macOS the watcher is
			// not told that its directory was deleted, so there is no event to act on.
			new("watcher failures: a root that was deleted and came back is watched again after the next reload",
				async () => {
					
					if(!OperatingSystem.IsWindows())
						
						return Skip.NotApplicable("only Windows tells a watcher that its directory was deleted");
					
					if(!await WatchedAsync(gone, 20))
						
						return (false, "FAIL  (the fixture's watcher did not start within 20 s)");
					
					var aside = gone.Dir + ".aside";
					
					try {
						
						CopyTree(gone.Dir, aside);
						
						try {
							Directory.Delete(gone.Dir, recursive: true);
						}
						catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
							
							return Skip.SetupUnavailable($"the fixture's directory could not be deleted under the server: {ex.Message}");
						}
						
						if(!await UntilAsync(async () => await WatchRootsAsync(gone) == 0, 10))
							
							return (false, "FAIL  (the watcher of the deleted root was not retired within 10 s)");
						
						// The name stays taken until the watcher's handle is closed.
						var restored = await UntilAsync(() => {
							
							try {
								
								CopyTree(aside, gone.Dir);
								
								return Task.FromResult(true);
							}
							catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
								
								return Task.FromResult(false);
							}
						}, 10);
						
						if(!restored)
							
							return Skip.SetupUnavailable("the fixture's directory could not be created again after it was deleted");
						
						// A new source file flags a reload, the next call that needs the
						// compilation runs it, and the watch plan is applied again after it.
						await ctx.CallToolJsonAsync("roslyn_write_file", new { filePath = "Later.cs", content = "class Later { }\n", createNew = true, projectPath = gone.Csproj });
						await ctx.CallToolJsonAsync("roslyn_get_diagnostics", new { projectPath = gone.Csproj, severity = "errors", take = 0 });
						
						if(!await UntilAsync(async () => await WatchRootsAsync(gone) >= 1, 20))
							
							return (false, "FAIL  (the root that came back was not watched again within 20 s of the reload)");
						
						// Settled first, so that the pending reload below can only be the new file's.
						var settled = await UntilAsync(async () => {
							
							await ctx.CallToolJsonAsync("roslyn_get_diagnostics", new { projectPath = gone.Csproj, severity = "errors", take = 0 });
							
							return !await ReloadPendingAsync(gone);
						}, 20);
						
						if(!settled)
							
							return (false, "FAIL  (a reload was still pending 20 s after the root came back, so a new file's reload could not be told from it)");
						
						gone.Write("External.cs", "class External { }\n");
						
						return await UntilAsync(() => ReloadPendingAsync(gone), 10)
							? (true, "PASS")
							: (false, "FAIL  (a source file created outside the server in the root that came back was not noticed within 10 s)");
					}
					finally {
						TestFixtures.DeleteTree(aside);
					}
				}),
		};
		
		return new TestGroup($"Watcher Failures ({tests.Count} tests)", tests, Teardown: () => {
			
			burst.Dispose();
			gone.Dispose();
			
			return Task.CompletedTask;
		});
	}
}
