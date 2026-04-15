using System.Diagnostics;
using System.Reflection;

/// <summary>
///     Comprehensive test harness for RoslynMcp. Tests all tools against RoslynMcp itself (dogfooding).
///     Usage: dotnet run --project TestHarness/TestHarness.csproj [-- --only-build-diag]
/// </summary>
class Program
{
	static async Task<int> Main(string[] args)
	{
		
		var repoRoot   = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
		var serverProj = Path.Combine(repoRoot, "src", "RoslynMcp", "RoslynMcp.csproj");
		var targetPath = Path.Combine(repoRoot, "src", "RoslynMcp"); // Dogfood: analyze ourselves
		
		var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
		
		Console.WriteLine("═══════════════════════════════════════════════════════════════");
		Console.WriteLine($"  RoslynMcp Test Harness v{version}");
		Console.WriteLine("═══════════════════════════════════════════════════════════════");
		Console.WriteLine($"Server:  {serverProj}");
		Console.WriteLine($"Target:  {targetPath}");
		Console.WriteLine();
		
		// Clean up any scratch files left over from a previous aborted run before attempting build.
		// ValidationTests creates _BuildDiagnosticsTest_.cs and deletes it in teardown, but if the
		// harness is killed before teardown the file persists and breaks the next build.
		var diagnosticsScratch = Path.Combine(repoRoot, "src", "RoslynMcp", "_BuildDiagnosticsTest_.cs")
		;
		if(File.Exists(diagnosticsScratch)) File.Delete(diagnosticsScratch);
		
		// Build the server first -- dotnet run's build output goes to stdout and breaks the MCP stdio protocol.
		Console.Write("Building server... ")
		;
		var buildProc = Process.Start(new ProcessStartInfo("dotnet")
		{
			
			Arguments       = $"build \"{serverProj}\" -f net10.0 --nologo -v q",
			UseShellExecute = false,
		})!;
		buildProc.WaitForExit();
		
		if(buildProc.ExitCode != 0) {
			
			Console.Error.WriteLine($"Server build failed (exit code {buildProc.ExitCode}).");
			
			return 1;
		}
		
		Console.WriteLine("done.");
		
		var psi = new ProcessStartInfo("dotnet")
		{
			
			Arguments              = $"run --no-build --project \"{serverProj}\" -f net10.0",
			RedirectStandardInput  = true,
			RedirectStandardOutput = true,
			RedirectStandardError  = true,
			UseShellExecute        = false,
		};
		
		using var proc = Process.Start(psi)!;
		
		proc.ErrorDataReceived += (_, e) =>
		{
			
			if(e.Data is not null)
				Console.Error.WriteLine($"[stderr] {e.Data}");
		};
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
			Console.Error.WriteLine("\n[FATAL] Server did not respond to initialize -- check stderr above for crash details.")
			;
			proc.Kill(entireProcessTree: true);
			
			return 1;
		}
		
		await ctx.SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
		
		Console.WriteLine("OK MCP session initialized\n");
		
		// -- Test Suite -----------------------------------------------------------------------
		
		var groups        = new List<TestGroup>();
		var onlyBuildDiag = args.Contains("--only-build-diag");
		
		if(!onlyBuildDiag) {
			
			groups.Add(DiscoveryTests.Build(ctx));
			groups.Add(FindStringLiteralTests.Build(ctx));
			groups.Add(MemberBodyTests.Build(ctx));
			groups.Add(TypeTests.Build(ctx));
			groups.Add(NavigationTests.Build(ctx));
			groups.Add(CallGraphTests.Build(ctx));
			groups.Add(CodeGenerationTests.Build(ctx));
			groups.Add(FileContentTests.Build(ctx));
		}
		
		groups.Add(ValidationTests.Build(ctx));
		
		if(!onlyBuildDiag) {
			
			groups.Add(RefactoringTests.Build(ctx));
			groups.Add(EditingTests.Build(ctx));
			groups.Add(await LocalHistoryTests.BuildAsync(ctx));
		}
		
		var total   = groups.Sum(g => g.Tests.Count);
		var done    = 0;
		var results = new List<(string name, bool pass, string msg)>();
		
		foreach(var group in groups) {
			
			Console.WriteLine($"\n{group.Header}");
			Console.WriteLine("─────────────────────────────────────────────────────────────")
			;
			
			try {
				
				foreach(var tc in group.Tests) {
					
					Console.Write($"  {tc.Name,-55} ");
					var (pass, msg) = await tc.Run();
					Console.WriteLine($"{msg}  [{++done}/{total}]");
					results.Add((tc.Name, pass, msg));
				}
			}
			finally {
				
				if(group.Teardown is not null)
					await group.Teardown()
					;
			}
		}
		
		// -- Summary --------------------------------------------------------------------------
		
		var passed   = results.Count(r => r.pass);
		var failed   = results.Count - passed;
		var failures = results.Where(r => !r.pass).ToArray();
		
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
		
		ctx.CloseInput();
		
		// Give the server up to 5 seconds to exit cleanly; kill it if it does not.
		try {
			await proc.WaitForExitAsync(new CancellationTokenSource(5_000).Token);
		}
		catch(OperationCanceledException) { }
		
		if(!proc.HasExited)
			proc.Kill();
		
		return failed == 0 ? 0 : 1;
	}
}
