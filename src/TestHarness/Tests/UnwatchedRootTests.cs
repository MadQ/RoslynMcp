using System.Text.Json.Nodes;

/// <summary>
///     A file watcher that cannot start is said, not only logged (#495). On Linux every
///     <see cref="FileSystemWatcher"/> is an inotify instance, and a user has a fixed number of them
///     (128 by default). When they run out, a workspace loads as ever and its watchers do not
///     start: edits made outside the server were then not noticed, and nothing in any answer said
///     so. A full harness run hit exactly that on the CI runner, where it showed as a test of
///     another group failing.
///     <para>
///         The test makes the state for real, with nothing in the server to help it: the harness
///         takes inotify instances of its own until the system refuses one, loads a fixture, and
///         gives them back. The limit is per user, so the server the harness started is refused
///         as well. Linux only: the limit does not exist elsewhere, and no other way to make a
///         watcher fail is reliable.
///     </para>
/// </summary>
static class UnwatchedRootTests
{
	// Far above any limit a machine is given; a run that gets this far has not found one.
	const int MostWatchers = 20_000;
	
	internal static TestGroup Build(TestContext ctx)
	{
		var fx      = TestFixtures.NewMsBuildProject("Unwatched", targetFrameworks: "net10.0");
		var held    = new List<FileSystemWatcher>();
		var holding = Directory.CreateDirectory(Path.Combine(fx.Dir, "held")).FullName;
		
		fx.Write("Probe.cs", "class Probe { }\n");
		
		void Release()
		{
			foreach(var watcher in held)
				watcher.Dispose();
			
			held.Clear();
		}
		
		// Takes inotify instances until the system gives no more. False when it never refused.
		bool Exhaust()
		{
			for(var taken = 0; taken < MostWatchers; taken++) {
				
				FileSystemWatcher? watcher = null;
				
				try {
					watcher = new FileSystemWatcher(holding) { EnableRaisingEvents = true };
					held.Add(watcher);
				}
				catch(IOException) {
					
					watcher?.Dispose();
					
					return true;
				}
			}
			
			return false;
		}
		
		async Task<JsonNode?> DriftAsync() => await ctx.CallToolJsonAsync("roslyn_check_drift", new { projectPath = fx.Csproj });
		
		static bool Unwatched(JsonNode? drift) => drift?["unwatched_roots"]?.AsArray() is { Count: > 0 };
		
		var tests = new List<TestCase> {
			
			// The whole course in one test, because each step is the precondition of the next and
			// the instances must not stay taken if one fails. With no instance left, the fixture's
			// watchers cannot start: roslyn_check_drift names the directory and the reason, its
			// hint names the limit, one answer carries the news unasked, and roslyn_info lists the
			// workspace with the root that is not watched. With the instances given back, the next
			// reload starts the watchers and the directory is no longer listed: the retry that a
			// partly failed plan used to never get.
			new("unwatched roots: a watcher that cannot start is named, announced once, and retried at the next reload",
				async () => {
					
					if(!OperatingSystem.IsLinux())
						
						return Skip.NotApplicable("the inotify instance limit exists on Linux only");
					
					try {
						
						if(!Exhaust())
							
							return Skip.NotApplicable($"the system gave {MostWatchers} inotify instances without refusing one");
						
						JsonNode? drift   = null;
						var announced     = false;
						var deadline      = DateTime.UtcNow.AddSeconds(20);
						
						// The watchers start on a background thread after the load, so the state
						// shows a moment after the first call.
						while(DateTime.UtcNow < deadline) {
							
							drift      = await DriftAsync();
							announced |= drift?["_caution"]?.GetValue<string>().Contains("File watching is off", StringComparison.Ordinal) == true;
							
							if(Unwatched(drift) && announced)
								break;
							
							await Task.Delay(200);
						}
						
						var root   = drift?["unwatched_roots"]?[0];
						var info   = await ctx.CallToolJsonAsync("roslyn_info", new { });
						var listed = info?["workspaces"]?.AsArray().Any(workspace => workspace?["unwatched_roots"]?.AsArray() is { Count: > 0 }) == true;
						
						if(!Unwatched(drift)
							|| root?["reason"]?.GetValue<string>() is not { Length: > 0 }
							|| drift?["hint"]?.GetValue<string>().Contains("inotify", StringComparison.Ordinal) != true
							|| !announced
							|| !listed)
							
							return (false, $"FAIL  (announced: {announced} | listed in roslyn_info: {listed} | drift: {drift})");
						
						Release();
						
						// A new source file flags a reload, and the next call that needs the
						// compilation runs it; the watch plan is applied again after it.
						await ctx.CallToolJsonAsync("roslyn_write_file", new { filePath = "Later.cs", content = "class Later { }\n", createNew = true, projectPath = fx.Csproj });
						await ctx.CallToolJsonAsync("roslyn_get_diagnostics", new { projectPath = fx.Csproj, severity = "errors", take = 0 });
						
						deadline = DateTime.UtcNow.AddSeconds(20);
						
						while(DateTime.UtcNow < deadline) {
							
							drift = await DriftAsync();
							
							if(drift is not null && !Unwatched(drift))
								
								return (true, "PASS");
							
							await Task.Delay(200);
						}
						
						return (false, $"FAIL  (still unwatched after the instances were given back and the workspace reloaded: {drift})");
					}
					finally {
						Release();
					}
				}),
		};
		
		return new TestGroup($"Unwatched Roots ({tests.Count} tests)", tests, Teardown: () => {
			
			Release();
			fx.Dispose();
			
			return Task.CompletedTask;
		});
	}
}
