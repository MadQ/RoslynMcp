using System.Text.Json.Nodes;

/// <summary>
///     Runs last and asserts the run did not disturb the dogfood workspace (#274). The harness used to
///     write ~16 scratch .cs files into src/RoslynMcp; each one forced a full reload of the solution in
///     the harness's own server and in the developer's live one. Fixtures now live in temp projects, so
///     any reload flagged for a path inside the repo is a regression — and since #273 the server log
///     names the file that caused it, so a failure here says which test wrote where.
/// </summary>
static class WorkspaceHygieneTests
{
	static readonly string[] ScratchPatterns = ["_*_.cs", ".test_*", "0_FindUnused*"];
	
	// Floor for "this really is the log the run wrote". Without it, a log that was truncated,
	// redirected elsewhere, or never written passes the reload assertion on missing evidence. This
	// group only runs in a full run, which logs one TOOL entry per tool call and makes hundreds.
	const int MinExpectedLogEntries = 50;
	
	/// <param name="ctx">Shared harness context (repo root).</param>
	/// <param name="serverLogDir">Directory the harness pointed ROSLYNMCP_LOG_PATH at.</param>
	/// <param name="serverLogGlob">
	///     Glob for the harness server's NDJSON log: the harness sets ROSLYNMCP_LOG_PATH to a per-run
	///     stem and the server inserts its own PID before the extension.
	/// </param>
	internal static TestGroup Build(TestContext ctx, string serverLogDir, string serverLogGlob)
	{
		var tests = new List<TestCase> {
			
			// Every "Reload — Flagged (<reason>): <path>" line is the decision point that costs a reload
			// (#273). One inside the repo means a test wrote a compilation input into the dogfood tree.
			// "Reloading workspace (Solution: …RoslynMcp.slnx)" is the reload itself — counted too, so a
			// flag raised by something the log did not attribute still fails the run.
			new("workspace hygiene: no reload flagged for a path inside the repo",
				() => Task.FromResult(AssertNoDogfoodReload(ctx, serverLogDir, serverLogGlob))),
			
			// The same property from the file system's side: nothing may be left in src/RoslynMcp.
			new("workspace hygiene: no scratch files left in src/RoslynMcp",
				() => Task.FromResult(AssertNoScratchFiles(ctx))),
			
			// #303: MSBuild entries name the MSBuild flavor (SDK/VS), not just "MSB". Lives here because
			// this group already reads the harness server's log, and only a full run produces one.
			new("workspace hygiene: MSBuild entries are labelled SDK once resolved",
				() => Task.FromResult(AssertSdkWorkspaceLabel(serverLogDir, serverLogGlob))),
			
			// #303: tools that resolve only a root or a solution must still record Adhoc.
			new("workspace hygiene: root/solution-only tools label an adhoc workspace ADH",
				() => AssertAdhocLabelAsync(ctx, serverLogDir, serverLogGlob)),
			
			// #306: concurrent dotnet runs are queued, not raced. Lives here for log access.
			new("workspace hygiene: concurrent dotnet runs are serialized",
				() => AssertDotnetRunsSerializedAsync(ctx, serverLogDir, serverLogGlob)),
			
			new("workspace hygiene: a run cancelled while queued never starts dotnet",
				() => AssertQueuedCancellationAsync(ctx, serverLogDir, serverLogGlob)),
		};
		
		return new TestGroup($"Workspace Hygiene ({tests.Count} tests)", tests);
	}
	
	static (bool pass, string message) AssertNoDogfoodReload(TestContext ctx, string logDir, string logGlob)
	{
		if(!TryReadLog(logDir, logGlob, out var lines, out var failure))
			
			return (false, failure);
		
		var repoRoot  = ctx.RepoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var offenders = new List<string>();
		var reloads   = 0;
		
		foreach(var line in lines) {
			
			string? message;
			
			try {
				message = JsonNode.Parse(line)?["message"]?.GetValue<string>();
			}
			catch(System.Text.Json.JsonException) {
				continue;
			}
			
			if(message is null)
				continue;
			
			if(message.StartsWith("Reload — Flagged", StringComparison.Ordinal)
				&& message.Contains(repoRoot, StringComparison.OrdinalIgnoreCase))
				
				offenders.Add(message);
			
			if(message.StartsWith("Reload — Reloading workspace (Solution:", StringComparison.Ordinal)
				&& message.Contains(repoRoot, StringComparison.OrdinalIgnoreCase))
				
				reloads++;
		}
		
		if(offenders.Count == 0 && reloads == 0)
			
			return (true, $"PASS  ({lines.Length} log entries, no dogfood reload)");
		
		var sample = string.Join(" | ", offenders.Take(3));
		
		return (false, $"FAIL  ({offenders.Count} reload flag(s) inside the repo, {reloads} dogfood reload(s): {sample})");
	}
	
	/// <summary>
	///     Guards #303: TOOL entries used to log a bare <c>MSB</c> for every MSBuildWorkspace call, so an SDK
	///     session and a VS session were indistinguishable in the log. The harness server runs in auto mode
	///     against SDK-style projects (the dogfood solution and every <see cref="TestFixtures"/> project), so
	///     auto-detection resolves Sdk on the first MSBuild load. From that entry on, every MSBuild entry must
	///     read <c>SDK</c>; before it, an undecided auto session may still log the generic <c>MSB</c>.
	///     <c>ADH</c> entries (the adhoc fixtures) are allowed anywhere. The check is order-sensitive on
	///     purpose: a label computed from stale state would show up as an <c>MSB</c> after the first <c>SDK</c>.
	///     Against the pre-fix server every MSBuild entry reads <c>MSB</c>, so the "at least one SDK" half fails.
	/// </summary>
	static (bool pass, string message) AssertSdkWorkspaceLabel(string logDir, string logGlob)
	{
		if(!TryReadLog(logDir, logGlob, out var lines, out var failure))
			
			return (false, failure);
		
		var sdkEntries  = 0;
		var msbAfterSdk = 0;
		var otherLabels = new HashSet<string>(StringComparer.Ordinal);
		
		foreach(var line in lines) {
			
			string? mode;
			
			try {
				mode = JsonNode.Parse(line)?["workspace_mode"]?.GetValue<string>();
			}
			catch(System.Text.Json.JsonException) {
				continue;
			}
			
			switch(mode) {
				
				case null or "ADH":
					break;
				
				case "SDK":
					sdkEntries++;
					break;
				
				case "MSB":
					if(sdkEntries > 0)
						msbAfterSdk++;
					
					break;
				
				default:
					otherLabels.Add(mode);
					break;
			}
		}
		
		if(sdkEntries == 0)
			
			return (false, "FAIL  (no TOOL entry is labelled SDK — MSBuild calls in an SDK session are not distinguished)");
		
		if(msbAfterSdk > 0 || otherLabels.Count > 0)
			
			return (false, $"FAIL  ({msbAfterSdk} MSB entr(ies) after the mode resolved to SDK; unexpected labels: [{string.Join(", ", otherLabels)}])");
		
		return (true, $"PASS  ({sdkEntries} SDK entries, no MSB after resolution)");
	}
	
	/// <summary>
	///     Guards the second half of #303. The tool scope defaults to the MSBuild label, and only the guards
	///     that record the mode override it. <c>roslyn_list_files</c> resolves nothing but its root
	///     (<c>TryResolveRoot</c>) and <c>roslyn_search_files</c> nothing but the solution and root
	///     (<c>TryResolveSolution</c>) — neither recorded the mode, so both logged an AdhocWorkspace call as
	///     <c>SDK</c> (before the SDK/VS split: <c>MSB</c>). The check makes one call to each against a bare
	///     temp directory (no .csproj, so AdhocWorkspace), using a pattern no other test uses as the log
	///     subject, then finds exactly those two TOOL entries in the server's log and requires <c>ADH</c>.
	///     The scope writes its entry as the tool method returns, before the response is sent, so the
	///     entries exist once the calls complete. Against the pre-fix server both entries read <c>SDK</c>.
	/// </summary>
	static async Task<(bool pass, string message)> AssertAdhocLabelAsync(TestContext ctx, string logDir, string logGlob)
	{
		var fx = TestFixtures.NewAdhocDir("AdhocLabel");
		
		try {
			
			fx.Write("AdhocLabelProbe.cs", "class AdhocLabelProbe { }\n");
			
			// Both patterns double as the TOOL entry's subject; neither is used by any other test, and
			// the GUID makes the search entry unique even across repeated runs sharing a log.
			var listPattern   = "AdhocLabelProbe*.cs";
			var searchPattern = $"AdhocLabelProbe_{Guid.NewGuid():N}|class AdhocLabelProbe";
			
			var list   = await ctx.RunTestAsync("roslyn_list_files",   new { pattern = listPattern,   projectPath = fx.Dir }, data => data?["count"] is not null);
			var search = await ctx.RunTestAsync("roslyn_search_files", new { pattern = searchPattern, projectPath = fx.Dir }, data => data?["matches"] is not null);
			
			if(!list.pass || !search.pass)
				
				return (false, $"FAIL  (probe calls failed: list_files {list.message}; search_files {search.message})");
			
			if(!TryReadLog(logDir, logGlob, out var lines, out var failure))
				
				return (false, failure);
			
			string? listMode   = null;
			string? searchMode = null;
			
			foreach(var line in lines) {
				
				JsonNode? entry;
				
				try {
					entry = JsonNode.Parse(line);
				}
				catch(System.Text.Json.JsonException) {
					continue;
				}
				
				var subject = entry?["subject"]?.GetValue<string>();
				var mode    = entry?["workspace_mode"]?.GetValue<string>();
				
				if(subject == listPattern)
					listMode = mode;
				else if(subject == searchPattern)
					searchMode = mode;
			}
			
			return listMode == "ADH" && searchMode == "ADH"
				? (true,  "PASS  (list_files and search_files on an adhoc workspace both logged ADH)")
				: (false, $"FAIL  (expected ADH; list_files logged '{listMode ?? "no entry"}', search_files logged '{searchMode ?? "no entry"}')")
			;
		}
		finally {
			fx.Dispose();
		}
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
	static async Task<(bool pass, string message)> AssertDotnetRunsSerializedAsync(TestContext ctx, string logDir, string logGlob)
	{
		var fx = NewHeldCleanFixture("DotnetGate");
		
		try {
			
			// Both requests go out before either response is read — sequential RunTestAsync calls would
			// never overlap — and the fixture's clean holds for HoldSeconds, so the second request is
			// always dispatched while the first dotnet process is still running. Responses may arrive in
			// either order; only their count matters here.
			foreach(var _ in Enumerable.Range(0, 2))
				await SendClean(ctx, fx);
			
			for(var i = 0; i < 2; i++)
				if(await ctx.ReceiveAsync() is null)
					
					return (false, "FAIL  (a clean request timed out)");
			
			if(!TryReadLog(logDir, logGlob, out var lines, out var failure))
				
				return (false, failure);
			
			var details = CleanDetails(lines, fx);
			var queued  = details.Count(d => d.Contains("behind another dotnet command", StringComparison.Ordinal));
			
			return details.Length == 2 && queued == 1
				? (true,  "PASS  (two concurrent cleans; the second queued behind the first)")
				: (false, $"FAIL  (expected 2 clean entries with exactly 1 queued; found {details.Length} entries, {queued} queued)")
			;
		}
		finally {
			fx.Dispose();
		}
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
	static async Task<(bool pass, string message)> AssertQueuedCancellationAsync(TestContext ctx, string logDir, string logGlob)
	{
		var fx = NewHeldCleanFixture("DotnetGateCancel");
		
		try {
			
			// Loaded up front: a cold load takes seconds, and B would otherwise still be resolving its
			// workspace when the cancellation arrives — reaching the gate already cancelled instead of
			// being cancelled while waiting on it, which is the case under test.
			await fx.WarmAsync(ctx);
			
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
			
			if(!TryReadLog(logDir, logGlob, out var lines, out var failure))
				
				return (false, failure);
			
			var details   = CleanDetails(lines, fx);
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
		finally {
			fx.Dispose();
		}
	}
	
	// How long the fixture's clean holds its dotnet process — long enough that a second request is always
	// dispatched (and, for the cancellation check, cancelled) while the first is still running.
	const int HoldSeconds = 3;
	
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
	
	/// <summary>
	///     Reads every line of the harness server's log, rotation siblings included, oldest file first so
	///     order-sensitive checks see entries in the order they were written. Fails when the log is missing,
	///     unreadable, or implausibly short — the last meaning it is not the log this run wrote, in which case
	///     an assertion over it would pass on missing evidence rather than on a clean run.
	/// </summary>
	static bool TryReadLog(string logDir, string logGlob, out string[] lines, out string failure)
	{
		lines   = [];
		failure = "";
		
		try {
			
			if(!Directory.Exists(logDir)) {
				
				failure = $"FAIL  (server log directory not found: {logDir})";
				
				return false;
			}
			
			// Every rotation sibling, not just the newest file: FileLogger rotates to "<log>.1",
			// "<log>.2" at maxFileSizeBytes, and reading only the current file would silently drop
			// the early entries — the ones a dogfood reload would appear in — turning a real
			// regression into a PASS. The glob ends in '*' for that reason. Rotated files are older
			// than the live one, so write time (not name) gives the chronological order.
			var logFiles = Directory.EnumerateFiles(logDir, logGlob)
				.OrderBy(File.GetLastWriteTimeUtc)
				.ToArray()
			;
			
			if(logFiles.Length == 0) {
				
				failure = $"FAIL  (server log not found: {Path.Combine(logDir, logGlob)})";
				
				return false;
			}
			
			lines = [.. logFiles.SelectMany(ReadSharedLines)];
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			failure = $"FAIL  (server log unreadable: {ex.Message})";
			
			return false;
		}
		
		// Every run logs one TOOL entry per tool call, and the harness makes well over a hundred.
		if(lines.Length < MinExpectedLogEntries) {
			
			failure = $"FAIL  (only {lines.Length} log entries — expected at least {MinExpectedLogEntries}; the log is not the one this run wrote)";
			
			return false;
		}
		
		return true;
	}
	

	static (bool pass, string message) AssertNoScratchFiles(TestContext ctx)
	{
		var dogfood  = Path.Combine(ctx.RepoRoot, "src", "RoslynMcp");
		var leftover = new List<string>();
		
		foreach(var pattern in ScratchPatterns) {
			
			try {
				leftover.AddRange(Directory.EnumerateFiles(dogfood, pattern, SearchOption.TopDirectoryOnly).Select(Path.GetFileName)!);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				
				return (false, $"FAIL  (cannot enumerate {dogfood}: {ex.Message})");
			}
		}
		
		return leftover.Count == 0
			? (true,  "PASS  (src/RoslynMcp clean)")
			: (false, $"FAIL  (left behind: {string.Join(", ", leftover)})");
	}
	
	// The server still has the file open for appending; read through a shared handle.
	static string[] ReadSharedLines(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using var reader = new StreamReader(stream);
		
		var lines = new List<string>();
		
		while(reader.ReadLine() is { } line)
			lines.Add(line);
		
		return [.. lines];
	}
}
