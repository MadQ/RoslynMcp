using System.Text.Json.Nodes;

/// <summary>
///     The DotnetRunner gate (#300/#306): the server runs one dotnet build/clean/restore at a time, and a
///     request cancelled while it waits never starts a process. Both checks read the harness server's log for
///     the entries their own requests wrote. They used to live in Workspace Hygiene for its log access, but
///     they need nothing from the rest of the run, so a filtered run (#367) can include or skip them.
/// </summary>
static class DotnetRunnerTests
{
	// How long the fixture's clean holds its dotnet process — long enough that a second request is always
	// dispatched (and, for the cancellation check, cancelled) while the first is still running.
	const int HoldSeconds = 3;
	
	/// <param name="ctx">Shared harness context.</param>
	/// <param name="serverLogDir">Directory the harness pointed ROSLYNMCP_LOG_PATH at.</param>
	/// <param name="serverLogGlob">Glob for the harness server's log (the server inserts its PID).</param>
	internal static TestGroup Build(TestContext ctx, string serverLogDir, string serverLogGlob)
	{
		// One fixture for both checks: each loads it (only the first load costs anything) and counts only
		// the log entries written after it started, so neither sees the other's cleans.
		var fx = NewHeldCleanFixture("DotnetGate");
		
		var tests = new List<TestCase> {
			
			new("dotnet runner: concurrent dotnet runs are serialized",
				() => AssertDotnetRunsSerializedAsync(ctx, fx, serverLogDir, serverLogGlob)),
			
			new("dotnet runner: a run cancelled while queued never starts dotnet",
				() => AssertQueuedCancellationAsync(ctx, fx, serverLogDir, serverLogGlob)),
		};
		
		return new TestGroup($"Dotnet Runner ({tests.Count} tests)", tests, Teardown: () => {
			
			fx.Dispose();
			
			return Task.CompletedTask;
		});
	}
	
	/// <summary>
	///     Guards #306: the server runs one dotnet build/clean/restore at a time, because concurrent runs over
	///     one solution race on shared obj\ state (the contention behind #300). Two roslyn_clean_solution
	///     requests are written to the server before either response is read, so they are in flight together;
	///     clean is used because it is quick and needs no restore. Both TOOL entries are then found in the
	///     server log by the fixture directory, which DotnetRunner records as <c>cwd=…</c> in each entry's
	///     detail, and exactly one must carry the <c>queued … behind another dotnet command</c> note. Without the
	///     gate neither call waits, so no entry is marked. The overlap is deterministic, not a timing accident:
	///     the fixture's clean holds for <see cref="HoldSeconds"/> (see <see cref="NewHeldCleanFixture"/>), far
	///     longer than dispatching the second request takes. The exit codes are not asserted: the fixture is
	///     never restored, and whether clean then succeeds is beside the point — what matters is that both
	///     processes ran, and not at the same time.
	/// </summary>
	static async Task<(bool pass, string message)> AssertDotnetRunsSerializedAsync(TestContext ctx, FixtureProject fx, string logDir, string logGlob)
	{
		// Loaded up front so the two requests overlap only in DotnetRunner. Cold, both would enter
		// the workspace load together (it runs outside the cache lock) — an unrelated concurrent
		// design-time build that could blur or destabilize what this check observes.
		await fx.WarmAsync(ctx);
		
		if(!TryCountCleans(fx, logDir, logGlob, out var before, out var failure))
			
			return (false, failure);
		
		// Both requests go out before either response is read — sequential RunTestAsync calls would
		// never overlap — and the fixture's clean holds for HoldSeconds, so the second request is
		// always dispatched while the first dotnet process is still running. Responses may arrive in
		// either order; only their count matters here.
		foreach(var _ in Enumerable.Range(0, 2))
			await SendClean(ctx, fx);
		
		for(var i = 0; i < 2; i++)
			if(await ctx.ReceiveAsync() is null)
				
				return (false, "FAIL  (a clean request timed out)");
		
		if(!ServerLog.TryRead(logDir, logGlob, 1, out var lines, out failure))
			
			return (false, failure);
		
		var details = CleanDetails(lines, fx)[before..];
		var queued  = details.Count(d => d.Contains("behind another dotnet command", StringComparison.Ordinal));
		
		// pid= is recorded only once Process.Start has succeeded, so two of them prove both runs
		// really executed — a queued note alone would also fit a run that waited and then failed
		// before launching anything.
		var started = details.Count(d => d.Contains("pid=", StringComparison.Ordinal));
		
		return details.Length == 2 && queued == 1 && started == 2
			? (true,  "PASS  (two concurrent cleans; both ran, the second queued behind the first)")
			: (false, $"FAIL  (expected 2 clean entries, both started, exactly 1 queued; found {details.Length} entries, {started} started, {queued} queued)")
		;
	}
	
	/// <summary>
	///     Guards #306's cancellation contract: a request cancelled while queued behind another dotnet run must
	///     never start its own process. With the fixture workspace warm, request A is sent and given a second to
	///     take the gate (concurrent requests reach it in no guaranteed order), then B is sent and, 500 ms later,
	///     cancelled with <c>notifications/cancelled</c> while A's held clean (<see cref="HoldSeconds"/>) still
	///     runs. The delay matters twice over: the server silently ignores a cancellation for a request it has
	///     not registered yet, and B must already be waiting on the gate, which A's hold keeps it doing for
	///     seconds more. B's note must show it waited at least 200 ms, so it cannot pass by merely arriving
	///     with an already-cancelled token. B's log entry is found by the fixture path DotnetRunner records before any wait, and
	///     must say it was cancelled without ever starting dotnet — no <c>pid=</c>, which is recorded only once a
	///     process exists. A must still complete normally. Before the tools passed their request token through,
	///     B ignored the cancellation, queued, and started dotnet once A finished.
	/// </summary>
	static async Task<(bool pass, string message)> AssertQueuedCancellationAsync(TestContext ctx, FixtureProject fx, string logDir, string logGlob)
	{
		// Loaded up front: a cold load takes seconds, and B would otherwise still be resolving its
		// workspace when the cancellation arrives — reaching the gate already cancelled instead of
		// being cancelled while waiting on it, which is the case under test.
		await fx.WarmAsync(ctx);
		
		if(!TryCountCleans(fx, logDir, logGlob, out var before, out var failure))
			
			return (false, failure);
		
		// A must own the gate before B is sent: concurrently dispatched requests reach it in no
		// guaranteed order, and B winning would turn this into a cancel-while-running check (which
		// kills the process instead). With the workspace warm, A takes the gate within milliseconds
		// and holds it for HoldSeconds; one second later it is certainly A's.
		var idA = await SendClean(ctx, fx);
		
		await Task.Delay(1_000);
		
		var idB = await SendClean(ctx, fx);
		
		await Task.Delay(500);
		await ctx.SendAsync(new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = idB, reason = "harness: queued-cancellation check" } });
		
		// Read until A answers. A reply to B may or may not be sent for a cancelled request; any that
		// arrives is consumed here so it can never be mistaken for a later test's response.
		var sawA = false;
		var sawB = false;
		
		while(!sawA) {
			
			var response = await ctx.ReceiveAsync();
			
			if(response is null)
				
				return (false, "FAIL  (request A timed out)");
			
			var id = response["id"]?.GetValue<int>();
			
			sawA |= id == idA;
			sawB |= id == idB;
		}
		
		if(!sawB)
			await ctx.ReceiveAsync(1_500);
		
		if(!ServerLog.TryRead(logDir, logGlob, 1, out var lines, out failure))
			
			return (false, failure);
		
		var details   = CleanDetails(lines, fx)[before..];
		var cancelled = details.Where(d => d.Contains("dotnet never started", StringComparison.Ordinal)).ToArray();
		var completed = details.Where(d => d.Contains("pid=", StringComparison.Ordinal)).ToArray();
		
		// B must have been cancelled while actually waiting on the gate — the 500 ms before the
		// cancellation, minus dispatch — not merely have arrived with an already-cancelled token.
		var waited = cancelled.Length == 1
			&& System.Text.RegularExpressions.Regex.Match(cancelled[0], @"cancelled after (\d+) ms queued") is { Success: true } m
			&& int.Parse(m.Groups[1].Value) >= 200;
		
		return cancelled.Length == 1 && waited && completed.Length == 1 && !cancelled[0].Contains("pid=", StringComparison.Ordinal)
			? (true,  "PASS  (B cancelled while queued never started dotnet; A completed)")
			: (false, $"FAIL  (expected 1 clean cancelled after ≥200 ms queued and 1 completed; found {cancelled.Length} cancelled (waited: {waited}), {completed.Length} completed: {string.Join(" || ", details)})")
		;
	}
	
	/// <summary>
	///     An MSBuild fixture whose clean takes <see cref="HoldSeconds"/>: a target that runs before
	///     <c>Clean</c> and sleeps, so concurrency checks do not depend on how fast the machine is. The sleep
	///     is <c>ping</c> on Windows and <c>sleep</c> elsewhere (CI builds on Ubuntu); both are available
	///     without extra tooling.
	/// </summary>
	static FixtureProject NewHeldCleanFixture(string label)
	{
		var fx = TestFixtures.NewMsBuildProject(label, extraProjectXml: $"""
			<Target Name="HoldClean" BeforeTargets="Clean">
			    <Exec Command="ping -n {HoldSeconds + 1} 127.0.0.1 &gt; NUL" Condition="'$(OS)' == 'Windows_NT'" />
			    <Exec Command="sleep {HoldSeconds}" Condition="'$(OS)' != 'Windows_NT'" />
			  </Target>
			""");
		
		fx.Write("Probe.cs", "class Probe { }\n");
		
		return fx;
	}
	
	// The number of clean entries this fixture already has in the log: the baseline a check counts from,
	// since the fixture is shared and the other check's cleans are in the same log.
	static bool TryCountCleans(FixtureProject fx, string logDir, string logGlob, out int count, out string failure)
	{
		count = 0;
		
		if(!ServerLog.TryRead(logDir, logGlob, 1, out var lines, out failure))
			
			return false;
		
		count = CleanDetails(lines, fx).Length;
		
		return true;
	}
	
	// Sends one roslyn_clean_solution request without waiting for its response; returns its request id.
	static async Task<int> SendClean(TestContext ctx, FixtureProject fx)
	{
		var id = ctx.NextId();
		
		await ctx.SendAsync(new { jsonrpc = "2.0", id, method = "tools/call",
			@params = new { name = "roslyn_clean_solution", arguments = new { projectPath = fx.Csproj } } });
		
		return id;
	}
	
	// The details of every clean_solution TOOL entry for this fixture, in log order. DotnetRunner records
	// the working directory before any wait, so even a run cancelled while queued is found.
	static string[] CleanDetails(string[] lines, FixtureProject fx)
	{
		var details = new List<string>();
		
		foreach(var line in lines) {
			
			JsonNode? entry;
			
			try {
				entry = JsonNode.Parse(line);
			}
			catch(System.Text.Json.JsonException) {
				continue;
			}
			
			var detail = entry?["detail"]?.GetValue<string>() ?? "";
			
			if(entry?["tool_name"]?.GetValue<string>() == "clean_solution"
				&& detail.Contains(fx.Dir, StringComparison.OrdinalIgnoreCase))
				details.Add(detail);
		}
		
		return [.. details];
	}
}
