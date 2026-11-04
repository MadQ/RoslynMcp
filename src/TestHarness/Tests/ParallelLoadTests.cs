using System.Text.Json.Nodes;

/// <summary>
///     Covers two tool calls that need the same workspace arriving together on a cold server (#377).
///     <para>
///         <c>WorkspaceManager.GetOrLoadInstance</c> loads outside its cache lock, on purpose, and
///         used to settle a race only after the fact: both callers loaded the whole solution, the
///         second load was disposed. Two design-time builds of one solution race on its <c>obj\</c>
///         files — in the log of the issue one of them failed to write a generated file, dropped a
///         project's references, and sent both loads into their reload-once retry. Now the second
///         caller waits for the load already in progress.
///     </para>
///     <para>
///         The test starts a server of its own, so that its first two calls are the first the server
///         sees, and sends them without waiting for the first answer. They name the same workspace in
///         two ways — the <c>.slnx</c>, and a <c>.csproj</c> the solution above it is found for — so
///         the shared load is found by where the workspace is cached, not by the path given. The log
///         must then hold one <c>Loading solution</c> for the fixture and no <c>Released</c>: before,
///         it held two of the first and one of the second. Whether the two calls really overlap is
///         down to timing, but a solution load takes seconds and the calls are sent back to back.
///     </para>
/// </summary>
static class ParallelLoadTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		
		var fx = TestFixtures.NewMsBuildProject("ParallelLoad");
		
		fx.Write("ParallelLoad.slnx", $"<Solution>\n  <Project Path=\"{Path.GetFileName(fx.Csproj)}\" />\n</Solution>\n");
		fx.Write("Loaded.cs", "namespace ParallelLoad;\n\npublic class Loaded { }\n");
		
		var tests = new List<TestCase> {
			
			new("parallel load: two first calls for one workspace load it once",
				async () => {
					
					var logStem         = $"parallelload-{Guid.NewGuid():N}";
					var logDir          = Path.Combine(TestFixtures.TempRoot, "logs");
					var (process, server) = await DefaultWorkspaceTests.StartServerLoggingToAsync(ctx, fx.Dir, Path.Combine(logDir, logStem + ".log"));
					
					try {
						
						if(server is null)
							
							return (false, "FAIL  (the server did not answer initialize)");
						
						// Sent back to back: the second request is on its way before the first load ends.
						await server.SendAsync(new { jsonrpc = "2.0", id = server.NextId(), method = "tools/call",
							@params = new { name = "roslyn_get_diagnostics", arguments = new { projectPath = fx.PathOf("ParallelLoad.slnx"), take = 0 } } });
						await server.SendAsync(new { jsonrpc = "2.0", id = server.NextId(), method = "tools/call",
							@params = new { name = "roslyn_list_types", arguments = new { projectPath = fx.Csproj } } });
						
						var answers = new[] { await server.ReceiveAsync(120_000), await server.ReceiveAsync(120_000) };
						
						var answered = answers.All(answer => answer?["result"] is not null && answer["error"] is null);
						
						if(!ServerLog.TryRead(logDir, logStem + "*", 1, out var lines, out var failure))
							
							return (false, failure);
						
						var label    = Path.GetFileName(fx.Dir);
						var loads    = lines.Count(line => line.Contains("Loading solution") && line.Contains(label));
						var released = lines.Count(line => line.Contains("Released") && line.Contains(label));
						
						return answered && loads == 1 && released == 0
							? (true,  "PASS")
							: (false, $"FAIL  (both calls answered: {answered}; solution loads logged: {loads}; loads released: {released})");
					}
					finally {
						await DefaultWorkspaceTests.StopServerAsync(process, server);
					}
				}),
		};
		
		return new TestGroup($"Parallel Load ({tests.Count} tests)", tests, Teardown: () => {
			
			fx.Dispose();
			
			return Task.CompletedTask;
		});
	}
}
