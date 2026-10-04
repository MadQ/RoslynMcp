using System.Diagnostics;
using System.Reflection;

/// <summary>
///     Comprehensive test harness for RoslynMcp. Tests all tools against RoslynMcp itself (dogfooding).
///     Usage: dotnet run --project TestHarness/TestHarness.csproj [-- --only-build-diag] [-- --quiet]
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
		
		var repoRoot   = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
		var serverProj = Path.Combine(repoRoot, "src", "RoslynMcp", "RoslynMcp.csproj");
		var targetPath = Path.Combine(repoRoot, "src", "RoslynMcp"); // Dogfood: analyze ourselves
		
		var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
		
		// Quiet mode (#325): only failures and the final summary line reach the console.
		var quiet = args.Contains("--quiet");
		
		void Info(string line)
		{
			if(!quiet)
				Console.WriteLine(line);
		}
		
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
		
		// Build the server first -- dotnet run's build output goes to stdout and breaks the MCP stdio protocol.
		if(!quiet)
			Console.Write("Building server... ");
		
		// Quiet mode captures the build output and shows it only when the build fails. Otherwise
		// it goes straight to the console, as it always has.
		var buildProc = Process.Start(new ProcessStartInfo("dotnet")
		{
			
			Arguments              = $"build \"{serverProj}\" -f net10.0 --nologo -v q",
			UseShellExecute        = false,
			RedirectStandardOutput = quiet,
			RedirectStandardError  = quiet,
		})!;
		
		// Both pipes are drained while the build runs — a full pipe would stall it.
		var buildOutput = quiet ? buildProc.StandardOutput.ReadToEndAsync() : Task.FromResult("");
		var buildErrors = quiet ? buildProc.StandardError.ReadToEndAsync()  : Task.FromResult("");
		
		await buildProc.WaitForExitAsync();
		
		if(buildProc.ExitCode != 0) {
			
			Console.Error.Write(await buildOutput);
			Console.Error.Write(await buildErrors);
			Console.Error.WriteLine($"Server build failed (exit code {buildProc.ExitCode}).");
			
			return 1;
		}
		
		Info("done.");
		
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
		
		using var proc = Process.Start(psi)!;
		
		// The server's stderr. Quiet mode holds it back and prints it only if the run fails —
		// a passing run logs expected errors there too (a cancelled request, for one).
		var serverErrors       = new System.Collections.Concurrent.ConcurrentQueue<string>();
		var serverErrorsClosed = new TaskCompletionSource();
		
		proc.ErrorDataReceived += (_, e) =>
		{
			
			// A null line is the end of the stream: every line the server wrote has been delivered.
			if(e.Data is null) {
				
				serverErrorsClosed.TrySetResult();
				
				return;
			}
			
			if(quiet)
				serverErrors.Enqueue(e.Data);
			else
				Console.Error.WriteLine($"[stderr] {e.Data}");
		};
		
		// Written to stdout, like the failure lines and the summary: one stream keeps their order,
		// so the summary is reliably the last line even when a caller merges the two streams.
		void FlushServerErrors()
		{
			while(serverErrors.TryDequeue(out var line))
				Console.WriteLine($"[stderr] {line}");
		}
		proc.BeginErrorReadLine();
		
		var ctx = new TestContext(proc.StandardInput, proc.StandardOutput, targetPath, repoRoot, serverProj);
		
		// -- MCP Session Initialization --------------------------------------------------------
		
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
			FlushServerErrors();
			Console.Error.WriteLine("\n[FATAL] Server did not respond to initialize -- check stderr above for crash details.")
			;
			proc.Kill(entireProcessTree: true);
			
			return 1;
		}
		
		await ctx.SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
		
		Info("OK MCP session initialized\n");
		
		// -- Test Suite -----------------------------------------------------------------------
		
		var groups        = new List<TestGroup>();
		var onlyBuildDiag = args.Contains("--only-build-diag");
		var total         = 0;
		var done          = 0;
		var results       = new List<(string name, bool pass, string msg)>();
		
		// What the run was doing when an exception escaped, and the exception. Tests report their
		// own failures; something escaping means a test, a teardown or a group's setup crashed —
		// often because the server died. Quiet mode turns that into a FAIL summary line below
		// instead of ending in a bare stack trace with the server's stderr still unprinted.
		var        running = "test group setup";
		Exception? aborted = null;
		
		try {
			
			if(!onlyBuildDiag) {
				
				groups.Add(DiscoveryTests.Build(ctx));
				groups.Add(VsVersionPinTests.Build(ctx));
				groups.Add(ProjectStyleDetectionTests.Build(ctx));
				groups.Add(FindStringLiteralTests.Build(ctx));
				groups.Add(FindUnusedTests.Build(ctx));
				groups.Add(MemberBodyTests.Build(ctx));
				groups.Add(TypeTests.Build(ctx));
				groups.Add(NavigationTests.Build(ctx));
				groups.Add(CallGraphTests.Build(ctx));
				groups.Add(CodeGenerationTests.Build(ctx));
				groups.Add(FileContentTests.Build(ctx));
				groups.Add(CodeFixTests.Build(ctx));
			}
			
			groups.Add(ValidationTests.Build(ctx));
			
			if(!onlyBuildDiag) {
				
				groups.Add(RefactoringTests.Build(ctx));
				groups.Add(EditingTests.Build(ctx));
				groups.Add(await ReloadFlaggingTests.BuildAsync(ctx));
				groups.Add(PaginationTests.Build(ctx));
				groups.Add(HookHintTests.Build(ctx));
				groups.Add(ClaudeHookSetupTests.Build(ctx));
				groups.Add(await IgnoredDirectoryTests.BuildAsync(ctx));
				groups.Add(await IncrementalAdditionalDocTests.BuildAsync(ctx));
				groups.Add(await LocalHistoryTests.BuildAsync(ctx));
				groups.Add(await DefaultWorkspaceTests.BuildAsync(ctx));
				
				// Last on purpose: it reads the server log written by everything above.
				groups.Add(WorkspaceHygieneTests.Build(ctx, serverLogDir, serverLogGlob));
			}
			
			total = groups.Sum(g => g.Tests.Count);
			
			foreach(var group in groups) {
				
				Info($"\n{group.Header}");
				Info("─────────────────────────────────────────────────────────────");
				
				try {
					
					foreach(var tc in group.Tests) {
						
						running = $"{group.Header} › {tc.Name}";
						
						if(!quiet)
							Console.Write($"  {tc.Name,-55} ");
						
						var (pass, msg) = await tc.Run();
						
						done++;
						
						// Quiet mode names a failure as it happens, with its group — the group headers
						// that would otherwise say where it belongs are not printed.
						if(!quiet)
							Console.WriteLine($"{msg}  [{done}/{total}]");
						else if(!pass)
							Console.WriteLine($"FAIL  {group.Header} › {tc.Name}: {msg}");
						
						results.Add((tc.Name, pass, msg));
					}
					
					// Only once every test has run: when a test throws, the teardown below still
					// runs, and the report must keep naming the test, not the teardown.
					running = $"{group.Header} › teardown";
				}
				finally {
					
					if(group.Teardown is not null)
						await group.Teardown()
						;
				}
			}
		}
		catch(Exception ex) when(quiet) {
			aborted = ex;
		}
		
		// -- Summary --------------------------------------------------------------------------
		
		var passed   = results.Count(r => r.pass);
		var failed   = results.Count - passed;
		var failures = results.Where(r => !r.pass).ToArray();
		
		if(!quiet) {
			
			Console.WriteLine("\n═══════════════════════════════════════════════════════════════");
			Console.WriteLine("  Test Summary");
			Console.WriteLine("═══════════════════════════════════════════════════════════════\n");
			
			if(failed == 0) {
				
				Console.WriteLine($"  All {total} tests passed! ✓");
			}
			else {
				
				Console.WriteLine($"  {passed} passed, {failed} failed\n");
				Console.WriteLine("  Failures:");
				
				foreach(var (name, _, msg) in failures) {
					
					Console.WriteLine($"    ✗ {name}");
					Console.WriteLine($"      {msg}");
				}
			}
			
			Console.WriteLine();
		}
		
		ctx.CloseInput();
		
		// Give the server up to 5 seconds to exit cleanly; kill it if it does not.
		try {
			await proc.WaitForExitAsync(new CancellationTokenSource(5_000).Token);
		}
		catch(OperationCanceledException) { }
		
		// The whole tree: the server is a child of "dotnet run", and killing only the parent would
		// leave it running and holding the stderr pipe open.
		if(!proc.HasExited)
			proc.Kill(entireProcessTree: true);
		
		if(quiet) {
			
			// Quiet mode reports only now, after the server is gone, so that what it logged while
			// handling the last request and while shutting down is included. The wait is bounded:
			// a process that outlives the kill must not hang the harness.
			await Task.WhenAny(serverErrorsClosed.Task, Task.Delay(TimeSpan.FromSeconds(3)));
			
			// The failures were printed as they happened; the server's stderr explains them.
			if(failed > 0 || aborted is not null)
				FlushServerErrors();
			
			// The one line a caller needs, on every path.
			if(aborted is not null) {
				
				var notRun = total > results.Count ? $", {total - results.Count} not run" : "";
				
				Console.WriteLine($"FAIL  {passed} passed, {failed} failed{notRun} — aborted in {running}: {aborted.GetType().Name}: {aborted.Message}");
			}
			else
				Console.WriteLine(failed == 0 ? $"PASS  {passed} passed" : $"FAIL  {passed} passed, {failed} failed");
		}
		
		return failed == 0 && aborted is null ? 0 : 1;
	}
}
