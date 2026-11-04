using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

/// <summary>
///     Comprehensive test harness for RoslynMcp. Tests all tools against RoslynMcp itself (dogfooding).
///     Usage: dotnet run --project TestHarness/TestHarness.csproj [-- [--quiet] [--only &lt;groups&gt;] [--test &lt;text&gt;] [--list]]
///     <para>
///         <c>--only</c> takes comma-separated parts of group names (case-insensitive) and builds and runs
///         only those groups; <c>--test</c> keeps the tests whose name contains the text; <c>--list</c>
///         prints the group names. <c>--only-build-diag</c> is <c>--only validation</c>. See
///         <see cref="HarnessGroups"/>.
///     </para>
///     <para>
///         <c>--quiet</c> is for runs whose output is read by an agent or a script: a passing test
///         prints nothing, a failing one prints one line, and the run ends with exactly one summary
///         line. The exit code is the same either way — 0 when everything passed, 1 otherwise.
///     </para>
/// </summary>
class Program
{
	static async Task<int> Main(string[] args)
	{
		// Before anything is built: listing the groups must be instant.
		if(args.Contains("--list")) {
			
			foreach(var group in HarnessGroups.All)
				Console.WriteLine(group.FullRunOnly ? $"{group.Name}  (full run only, or when named in --only)" : group.Name);
			
			return 0;
		}
		
		// A selection that cannot be honoured is a usage error, not a test failure: exit code 2.
		if(!HarnessGroups.TrySelect(args, out var selection, out var error)) {
			
			Console.WriteLine($"FAIL  {error}");
			
			return 2;
		}
		
		var run = new HarnessRun(args, selection);
		
		// Quiet mode has one exit, and this is it. Whatever ends the run — a failing test, a failed
		// server build, a server that dies during startup, an exception from anywhere — arrives
		// here, and the report below prints the diagnostics and the summary line in one place.
		// Reporting from each failure site instead left a path uncovered every time one was added
		// (#325). Default mode keeps its long-standing behaviour: an exception propagates.
		try {
			await run.ExecuteAsync();
		}
		catch(Exception ex) when(run.Quiet) {
			run.Stop($"aborted in {run.Running}: {ex.GetType().Name}: {ex.Message}");
		}
		
		await run.ShutdownServerAsync();
		
		return await run.ReportAsync();
	}
}

/// <summary>
///     One run of the harness: builds and starts the server, runs the test groups, shuts the
///     server down, and reports. The three steps are separate methods so that <c>Main</c> can
///     route every outcome of the first through the other two.
/// </summary>
sealed class HarnessRun(string[] args, HarnessSelection selection)
{
	// The server's stderr, held back in quiet mode: a passing run logs expected errors there too
	// (a cancelled request, for one), so it is shown only when the run fails.
	readonly ConcurrentQueue<string> serverErrors       = new();
	readonly TaskCompletionSource    serverErrorsClosed = new();
	
	// Messages about a failure outside any test (build output, a server that does not answer).
	readonly List<string> diagnostics = [];
	
	readonly List<(string name, bool pass, string msg)> results = [];
	
	Process? server;
	string?  stopReason;
	int      total;
	
	/// <summary>Quiet mode (#325): only failures and the final summary line reach the console.</summary>
	public bool Quiet { get; } = args.Contains("--quiet");
	
	/// <summary>
	///     What the run is doing right now, for the report of an exception that escapes: a startup
	///     step, or a group and test. Tests report their own failures; something escaping means a
	///     step crashed — often because the server died.
	/// </summary>
	public string Running { get; private set; } = "startup";
	
	/// <summary>
	///     Records why the run ended before or outside a test. The first reason wins: what follows
	///     a failure is usually a consequence of it. The reason becomes part of the summary line,
	///     which must stay one line and the last one.
	/// </summary>
	public void Stop(string reason) => stopReason ??= OneLine(reason);
	
	// Folds line breaks into spaces. Quiet mode promises one line per failing test and one summary
	// line, and the text it is given does not: exception messages, the server's error messages
	// and file contents quoted in a failure all span lines.
	static string OneLine(string text)
		=> string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
	;
	
	void Info(string line)
	{
		if(!Quiet)
			Console.WriteLine(line);
	}
	
	// Default mode writes a diagnostic to stderr at once, as it always has. Quiet mode holds it
	// for the report, which prints it to stdout ahead of the summary line: one stream, so a
	// caller that reads only stdout sees it, and a caller that merges the streams sees it in order.
	void Diagnostic(string text)
	{
		if(Quiet)
			diagnostics.Add(text.Trim());
		else
			Console.Error.WriteLine(text);
	}
	
	public async Task ExecuteAsync()
	{
		var repoRoot   = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
		var serverProj = Path.Combine(repoRoot, "src", "RoslynMcp", "RoslynMcp.csproj");
		var targetPath = Path.Combine(repoRoot, "src", "RoslynMcp"); // Dogfood: analyze ourselves
		
		var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
		
		Info("═══════════════════════════════════════════════════════════════");
		Info($"  RoslynMcp Test Harness v{version}");
		Info("═══════════════════════════════════════════════════════════════");
		Info($"Server:  {serverProj}");
		Info($"Target:  {targetPath}");
		Info("");
		
		// Sweep what a killed run left behind before building: stale fixture trees under %TEMP%, and
		// the scratch .cs files older harness binaries wrote into src/RoslynMcp, which would break the
		// server build (#274). Fixtures now live in per-group temp projects — see TestFixtures.
		TestFixtures.SweepStale(repoRoot, TimeSpan.FromHours(1));
		
		// -- Server build -----------------------------------------------------------------------
		
		Running = "server build";
		
		// Build the server first -- dotnet run's build output goes to stdout and breaks the MCP stdio protocol.
		if(!Quiet)
			Console.Write("Building server... ");
		
		// Quiet mode captures the build output and shows it only when the build fails. Otherwise
		// it goes straight to the console, as it always has.
		using var buildProc = Process.Start(new ProcessStartInfo("dotnet")
		{
			
			Arguments              = $"build \"{serverProj}\" -f net10.0 --nologo -v q",
			UseShellExecute        = false,
			RedirectStandardOutput = Quiet,
			RedirectStandardError  = Quiet,
		})!;
		
		// Both pipes are drained while the build runs — a full pipe would stall it.
		var buildOutput = Quiet ? buildProc.StandardOutput.ReadToEndAsync() : Task.FromResult("");
		var buildErrors = Quiet ? buildProc.StandardError.ReadToEndAsync()  : Task.FromResult("");
		
		await buildProc.WaitForExitAsync();
		
		if(buildProc.ExitCode != 0) {
			
			foreach(var captured in new[] { await buildOutput, await buildErrors })
				if(!string.IsNullOrWhiteSpace(captured))
					Diagnostic(captured);
			
			Diagnostic($"Server build failed (exit code {buildProc.ExitCode}).");
			Stop("the server build failed");
			
			return;
		}
		
		Info("done.");
		
		// -- Server start -----------------------------------------------------------------------
		
		Running = "server start";
		
		// The harness server logs to its own per-run file under the fixture root, not the shared
		// %LOCALAPPDATA% log. The server inserts its PID before the extension, hence the glob;
		// WorkspaceHygieneTests reads the file at the end of the run (#274).
		var serverLogDir  = Path.Combine(TestFixtures.TempRoot, "logs");
		var serverLogStem = $"harness-{Guid.NewGuid():N}";
		// Trailing '*' so the glob also matches FileLogger's rotation siblings ("<stem>.<pid>.log.1").
		var serverLogGlob = $"{serverLogStem}.*.log*";
		
		var psi = new ProcessStartInfo("dotnet")
		{
			
			// MCP uses stdout exclusively for JSON-RPC. Do not let a local launch profile
			// inject non-MCP command-line arguments before the initialize response.
			Arguments              = $"run --no-build --no-launch-profile --project \"{serverProj}\" -f net10.0",
			RedirectStandardInput  = true,
			RedirectStandardOutput = true,
			RedirectStandardError  = true,
			UseShellExecute        = false,
		};
		psi.Environment["ROSLYNMCP_TEST_CODE_FIXES"] = "1";
		psi.Environment["ROSLYNMCP_LOG_PATH"]        = Path.Combine(serverLogDir, $"{serverLogStem}.log");
		
		server = Process.Start(psi)!;
		
		server.ErrorDataReceived += (_, e) =>
		{
			
			// A null line is the end of the stream: every line the server wrote has been delivered.
			if(e.Data is null) {
				
				serverErrorsClosed.TrySetResult();
				
				return;
			}
			
			if(Quiet)
				serverErrors.Enqueue(e.Data);
			else
				Console.Error.WriteLine($"[stderr] {e.Data}");
		};
		server.BeginErrorReadLine();
		
		var ctx = new TestContext(server.StandardInput, server.StandardOutput, targetPath, repoRoot, serverProj) { Diagnostic = Diagnostic };
		
		// -- MCP Session Initialization --------------------------------------------------------
		
		Running = "MCP handshake";
		
		await ctx.SendAsync(new {
			
			jsonrpc = "2.0",
			id      = ctx.NextId(),
			method  = "initialize",
			@params = new {
				
				protocolVersion = "2024-11-05",
				capabilities    = new { },
				clientInfo      = new { name = "TestHarness", version = "1.0" }
			}
		});
		
		var initResponse = await ctx.ReceiveAsync();
		
		if(initResponse is null) {
			
			await Task.Delay(200); // Allow stderr to flush.
			Diagnostic("\n[FATAL] Server did not respond to initialize -- check stderr above for crash details.");
			
			server.Kill(entireProcessTree: true);
			Stop("the server did not answer initialize");
			
			return;
		}
		
		await ctx.SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
		
		Info("OK MCP session initialized\n");
		
		// -- Test Suite -----------------------------------------------------------------------
		
		Running = "test group setup";
		
		// Only the selected groups are built: building one can start servers or create projects (#367).
		var groups = new List<TestGroup>();
		var inputs = new GroupInputs(ctx, serverLogDir, serverLogGlob, FullRun: selection.FilterNote is null);
		var done   = 0;
		
		foreach(var entry in selection.Groups) {
			
			Running = $"{entry.Name} › setup";
			
			var group = await entry.Build(inputs);
			
			if(selection.TestFilter is { } filter) {
				
				group = group with { Tests = [.. group.Tests.Where(t => t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))] };
				
				// Built, so its fixtures exist: a group left with no tests is torn down at once.
				if(group.Tests.Count == 0) {
					
					if(group.Teardown is not null)
						await group.Teardown();
					
					continue;
				}
			}
			
			groups.Add(group);
		}
		
		if(groups.Count == 0) {
			
			Stop($"no test matched the selection ({selection.FilterNote})");
			
			return;
		}
		
		total = groups.Sum(g => g.Tests.Count);
		
		// Per group, tests and teardown: the numbers #367 needed and the output never gave.
		var timings = new List<(string header, TimeSpan elapsed)>(groups.Count);
		
		foreach(var group in groups) {
			
			Info($"\n{group.Header}");
			Info("─────────────────────────────────────────────────────────────");
			
			var clock = Stopwatch.StartNew();
			
			try {
				
				foreach(var tc in group.Tests) {
					
					Running = $"{group.Header} › {tc.Name}";
					
					if(!Quiet)
						Console.Write($"  {tc.Name,-55} ");
					
					var (pass, msg) = await tc.Run();
					
					done++;
					
					// Quiet mode names a failure as it happens, with its group — the group headers
					// that would otherwise say where it belongs are not printed. A skipped test
					// is named too: it passed nothing, and staying silent would hide that.
					if(!Quiet)
						Console.WriteLine($"{msg}  [{done}/{total}]");
					else if(!pass)
						Console.WriteLine($"FAIL  {group.Header} › {tc.Name}: {OneLine(msg)}");
					else if(Skip.IsSkip(pass, msg))
						Console.WriteLine($"SKIP  {group.Header} › {tc.Name}: {OneLine(Skip.Reason(msg))}");
					
					results.Add((tc.Name, pass, msg));
				}
				
				// Only once every test has run: when a test throws, the teardown below still
				// runs, and the report must keep naming the test, not the teardown.
				Running = $"{group.Header} › teardown";
			}
			finally {
				
				if(group.Teardown is not null)
					await group.Teardown()
					;
			}
			
			timings.Add((group.Header, clock.Elapsed));
			Info($"  ({clock.Elapsed.TotalSeconds:0.0} s)");
		}
		
		// -- Summary (default mode) -------------------------------------------------------------
		
		if(Quiet)
			
			return;
		
		var skipped  = results.Count(r => Skip.IsSkip(r.pass, r.msg));
		var passed   = results.Count(r => r.pass) - skipped;
		var failed   = results.Count - passed - skipped;
		var failures = results.Where(r => !r.pass).ToArray();
		var skipNote = skipped > 0 ? $", {skipped} skipped" : "";
		
		Console.WriteLine("\n═══════════════════════════════════════════════════════════════");
		Console.WriteLine("  Test Summary");
		Console.WriteLine("═══════════════════════════════════════════════════════════════\n");
		
		if(failed == 0) {
			
			// A skipped test did not pass, so "all" would overstate it.
			Console.WriteLine(skipped == 0 ? $"  All {total} tests passed! ✓" : $"  {passed} passed{skipNote} ✓");
		}
		else {
			
			Console.WriteLine($"  {passed} passed, {failed} failed{skipNote}\n");
			Console.WriteLine("  Failures:");
			
			foreach(var (name, _, msg) in failures) {
				
				Console.WriteLine($"    ✗ {name}");
				Console.WriteLine($"      {msg}");
			}
		}
		
		// A partial run must never read like a full one.
		if(selection.FilterNote is { } note)
			Console.WriteLine($"\n  ({note})");
		
		Console.WriteLine("\n  Slowest groups:");
		
		foreach(var (header, elapsed) in timings.OrderByDescending(t => t.elapsed).Take(5))
			Console.WriteLine($"    {elapsed.TotalSeconds,6:0.0} s  {header}");
		
		Console.WriteLine();
	}
	
	/// <summary>
	///     Ends the server, if one was started: closes its input, gives it five seconds to exit,
	///     then kills it. Never throws — it runs between a failure and its report.
	/// </summary>
	public async Task ShutdownServerAsync()
	{
		if(server is null)
			
			return;
		
		try {
			
			// A server that already died leaves a broken pipe behind; closing it must not replace
			// the report with an exception.
			try {
				server.StandardInput.Close();
			}
			catch(IOException) { }
			
			try {
				await server.WaitForExitAsync(new CancellationTokenSource(5_000).Token);
			}
			catch(OperationCanceledException) { }
			
			// The whole tree: the server is a child of "dotnet run", and killing only the parent
			// would leave it running and holding the stderr pipe open.
			if(!server.HasExited)
				server.Kill(entireProcessTree: true);
		}
		catch(Exception ex) when(ex is InvalidOperationException or Win32Exception or AggregateException or NotSupportedException) {
			// The process went away between the check and the call, or part of its tree could
			// not be ended. Neither may stand between a failure and its report.
		}
	}
	
	/// <summary>
	///     Returns the exit code and, in quiet mode, prints the whole report: the server's stderr and
	///     the diagnostics when the run failed, then the one summary line. It runs after the server
	///     has shut down, so that what the server logged during its last request and while exiting
	///     is included. Everything goes to stdout, which keeps the summary the last line even for a
	///     caller that merges the streams.
	/// </summary>
	public async Task<int> ReportAsync()
	{
		var skipped   = results.Count(r => Skip.IsSkip(r.pass, r.msg));
		var passed    = results.Count(r => r.pass) - skipped;
		var failed    = results.Count - passed - skipped;
		var succeeded = failed == 0 && stopReason is null;
		var skipNote  = skipped > 0 ? $", {skipped} skipped" : "";
		
		// A filtered run says so on the one line an agent or a script reads (#367).
		if(selection.FilterNote is { } note)
			skipNote += $" ({note})";
		
		if(Quiet) {
			
			// Bounded: a process that outlives the kill must not hang the harness.
			if(server is not null)
				await Task.WhenAny(serverErrorsClosed.Task, Task.Delay(TimeSpan.FromSeconds(3)));
			
			if(!succeeded) {
				
				while(serverErrors.TryDequeue(out var line))
					Console.WriteLine($"[stderr] {line}");
				
				foreach(var diagnostic in diagnostics)
					Console.WriteLine(diagnostic);
			}
			
			if(stopReason is null)
				Console.WriteLine(failed == 0 ? $"PASS  {passed} passed{skipNote}" : $"FAIL  {passed} passed, {failed} failed{skipNote}");
			else {
				
				var notRun = total > results.Count ? $", {total - results.Count} not run" : "";
				
				Console.WriteLine($"FAIL  {passed} passed, {failed} failed{skipNote}{notRun} — {stopReason}");
			}
		}
		
		server?.Dispose();
		
		return succeeded ? 0 : 1;
	}
}
