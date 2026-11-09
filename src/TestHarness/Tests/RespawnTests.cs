using System.Diagnostics;

/// <summary>
///     <c>roslyn_respawn</c> ships in every build as of #406, because the hints of the diagnostics, drift,
///     build and reload paths name it as the way to reload a workspace that loaded badly. Until then it
///     was DEBUG-only and no test had ever called it. The harness is the one MCP client that can check the
///     whole contract: the tool answers (the response must be flushed before the exit, or the client sees a
///     dropped request), the process then exits by itself with exit code 0 — the cue a client takes to start
///     it again; a non-zero code or a hang makes some clients give up — the shutdown is the ordinary one (the
///     log has its STOP entry, which the same hook writes as removes the heartbeat the pre-tool hook reads),
///     and a server started again at the same root answers with another pid. Nothing is mocked: a real
///     server of the group's own is started with a loaded workspace, respawned, and started again. The
///     harness's own server is never respawned — every other group depends on it.
/// </summary>
static class RespawnTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		var fx      = TestFixtures.NewAdhocDir("Respawn");
		var logDir  = Path.Combine(TestFixtures.TempRoot, "logs");
		var logStem = $"respawn-{Guid.NewGuid():N}";
		
		fx.Write("Probe.cs", "class Probe { }\n");
		
		async Task<(bool pass, string msg)> RunRespawn()
		{
			var sw = Stopwatch.StartNew();
			var (process, server) = await DefaultWorkspaceTests.StartServerLoggingToAsync(ctx, fx.Dir, Path.Combine(logDir, $"{logStem}.log"));
			
			try {
				
				if(server is null)
					
					return (false, "FAIL  (server did not answer initialize)");
				
				var before   = await server.CallToolJsonAsync("roslyn_info", new { });
				var firstPid = before?["pid"]?.GetValue<int>();
				
				// A loaded workspace, so the shutdown has state to dispose.
				await server.CallToolJsonAsync("roslyn_list_files", new { projectPath = fx.Dir, pattern = "*.cs" });
				
				var respawn = await server.CallToolJsonAsync("roslyn_respawn", new { });
				
				if(firstPid is null || respawn?["pid"]?.GetValue<int>() != firstPid)
					
					return (false, $"FAIL  (respawn answered: {respawn?.ToJsonString() ?? "nothing"}; first pid {firstPid?.ToString() ?? "unknown"}) [{sw.ElapsedMilliseconds}ms]");
				
				// The exit is the server's own: stdin stays open, as a client keeps it.
				try {
					await process.WaitForExitAsync(new CancellationTokenSource(30_000).Token);
				}
				catch(OperationCanceledException) {
					return (false, $"FAIL  (the server did not exit within 30 s of answering) [{sw.ElapsedMilliseconds}ms]");
				}
				
				if(process.ExitCode != 0)
					
					return (false, $"FAIL  (exit code {process.ExitCode}, expected 0) [{sw.ElapsedMilliseconds}ms]");
				
				var stopped = ServerLog.TryRead(logDir, $"{logStem}.*.log*", 1, out var lines, out var logFailure)
					&& lines.Any(line => line.Contains("\"level\":\"STOP\"", StringComparison.Ordinal));
				
				// The client's part, stood in for: the same server started again at the same root.
				var (again, second) = await DefaultWorkspaceTests.StartServerLoggingToAsync(ctx, fx.Dir, Path.Combine(logDir, $"{logStem}-again.log"));
				
				try {
					
					var after     = second is null ? null : await second.CallToolJsonAsync("roslyn_info", new { });
					var secondPid = after?["pid"]?.GetValue<int>();
					var pass      = stopped && secondPid is not null && secondPid != firstPid;
					
					return (pass, pass
						? $"PASS  [{sw.ElapsedMilliseconds}ms]"
						: $"FAIL  (STOP logged: {stopped}{(stopped ? "" : $" — {logFailure}")}; first pid {firstPid}, second pid {secondPid?.ToString() ?? "no answer"}) [{sw.ElapsedMilliseconds}ms]");
				}
				finally {
					await DefaultWorkspaceTests.StopServerAsync(again, second);
				}
			}
			finally {
				
				// The server exited on its own; closing the pipe it held is a courtesy that may fail.
				try {
					server?.CloseInput();
				}
				catch(IOException) { }
				
				if(!process.HasExited)
					process.Kill(entireProcessTree: true);
				
				process.Dispose();
			}
		}
		
		List<TestCase> tests = [
			new("roslyn_respawn answers, the server exits with code 0 and a STOP entry, and a restarted server has another pid", RunRespawn),
		];
		
		return new TestGroup($"Respawn ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); return Task.CompletedTask; });
	}
}
