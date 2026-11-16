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
///         takes inotify instances of its own until the system refuses one, gives one back, loads
///         a fixture that needs several, and later gives back the rest. The limit is per user, so
///         the server the harness started is refused as well. Linux only: the limit does not exist
///         elsewhere, and no other way to make a watcher fail is reliable.
///     </para>
/// </summary>
static class UnwatchedRootTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		var fx      = TestFixtures.NewMsBuildProject("Unwatched", targetFrameworks: "net10.0");
		var held    = new List<FileSystemWatcher>();
		var holding = Directory.CreateDirectory(Path.Combine(fx.Dir, "held")).FullName;
		
		// A directory the server never watches (node_modules) makes the planner watch the root
		// without its subdirectories and each other subdirectory on its own: several watch roots,
		// which is what lets one start while the others are refused.
		fx.Write("Probe.cs", "class Probe { }\n");
		fx.Write("Sub/Other.cs", "class Other { }\n");
		fx.Write("node_modules/pkg/index.js", "// never watched\n");
		
		void Release()
		{
			foreach(var watcher in held)
				watcher.Dispose();
			
			held.Clear();
		}
		
		// Takes inotify instances until the system gives no more. False when it never refused.
		bool Exhaust(int most)
		{
			for(var taken = 0; taken < most; taken++) {
				
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
		
		// The user's inotify instance limit and this process's open-file limit, or null when the
		// system does not say. Every instance is also an open file of the process that holds it.
		static (int Instances, int OpenFiles)? Limits()
		{
			try {
				
				var instances = int.Parse(File.ReadAllText("/proc/sys/fs/inotify/max_user_instances").Trim());
				var openFiles = File.ReadLines("/proc/self/limits")
					.Where(line => line.StartsWith("Max open files", StringComparison.Ordinal))
					.Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
					.Select(parts => int.TryParse(parts[3], out var soft) ? soft : int.MaxValue)
					.First();
				
				return (instances, openFiles);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or IndexOutOfRangeException) {
				return null;
			}
		}
		
		async Task<JsonNode?> DriftAsync() => await ctx.CallToolJsonAsync("roslyn_check_drift", new { projectPath = fx.Csproj });
		
		static bool Unwatched(JsonNode? drift) => drift?["unwatched_roots"]?.AsArray() is { Count: > 0 };
		
		async Task<JsonNode?> WorkspaceInfoAsync()
			=> (await ctx.CallToolJsonAsync("roslyn_info", new { }))?["workspaces"]?.AsArray()
				.FirstOrDefault(workspace => workspace?["root"]?.GetValue<string>().Contains(Path.GetFileName(fx.Dir), StringComparison.Ordinal) == true);
		
		var tests = new List<TestCase> {
			
			// The whole course in one test, because each step is the precondition of the next and
			// the instances must not stay taken if one fails. With one instance left, one of the
			// fixture's watch roots starts and the others cannot: roslyn_check_drift names the
			// directories and the reason, its hint names the limit, one answer carries the news
			// unasked, and roslyn_info lists the workspace with a live root beside the unwatched
			// ones. That part of the plan is live is the point: a plan with nothing live was
			// already tried again at the next reload, one that was partly live never was. With the
			// instances given back, the next reload starts the rest and nothing is listed any more.
			new("unwatched roots: a watcher that cannot start is named, announced once, and retried at the next reload",
				async () => {
					
					if(!OperatingSystem.IsLinux())
						
						return Skip.NotApplicable("the inotify instance limit exists on Linux only");
					
					if(Limits() is not var (instances, openFiles))
						
						return Skip.NotApplicable("the system does not say what its inotify instance limit is");
					
					// Otherwise the harness is refused for its own open files while the server,
					// with a limit of its own, still gets instances.
					if(openFiles <= instances + 64)
						
						return Skip.NotApplicable($"this process may open {openFiles} files, which is not above the {instances} inotify instances a user has");
					
					try {
						
						if(!Exhaust(instances + 64))
							
							return Skip.NotApplicable($"the system gave more inotify instances than its limit of {instances}");
						
						// One instance for the server: one of its roots starts, the rest are refused.
						held[^1].Dispose();
						held.RemoveAt(held.Count - 1);
						
						JsonNode? drift = null;
						var announced   = false;
						var deadline    = DateTime.UtcNow.AddSeconds(20);
						
						// The watchers start on a background thread after the load, so the state
						// shows a moment after the first call.
						while(DateTime.UtcNow < deadline) {
							
							drift      = await DriftAsync();
							announced |= drift?["_caution"]?.GetValue<string>().Contains("File watching is off", StringComparison.Ordinal) == true;
							
							if(Unwatched(drift) && announced)
								break;
							
							await Task.Delay(200);
						}
						
						var root      = drift?["unwatched_roots"]?[0];
						var workspace = await WorkspaceInfoAsync();
						var live      = workspace?["watch_roots"]?.GetValue<int>() ?? -1;
						var listed    = workspace?["unwatched_roots"]?.AsArray() is { Count: > 0 };
						
						if(!Unwatched(drift)
							|| root?["reason"]?.GetValue<string>() is not { Length: > 0 }
							|| root["path"]?.GetValue<string>() is null or "." or ""
							|| drift?["hint"]?.GetValue<string>().Contains("inotify", StringComparison.Ordinal) != true
							|| !announced
							|| !listed
							|| live < 1)
							
							return (false, $"FAIL  (announced: {announced} | roslyn_info: {workspace} | drift: {drift})");
						
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
						
						// News that was posted and not taken would ride on the answer of a later
						// test, some of which require an answer without a caution. These answers
						// take what is left; the queue holds four at most.
						for(var taken = 0; taken < 5; taken++)
							await ctx.CallToolJsonAsync("roslyn_info", new { });
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
