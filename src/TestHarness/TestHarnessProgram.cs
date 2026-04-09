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

var version = typeof(Program).Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine($"  RoslynMcp Test Harness v{version}");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine($"Server:  {serverProj}");
Console.WriteLine($"Target:  {targetPath}");
Console.WriteLine();

// Build the server first -- dotnet run's build output goes to stdout and breaks the MCP stdio protocol.
Console.Write("Building server... ");
var buildProc = Process.Start(new ProcessStartInfo("dotnet") {

Arguments       = $"build \"{serverProj}\" -f net10.0 --nologo -v q",
UseShellExecute = false,
})!;
buildProc.WaitForExit();

if(buildProc.ExitCode != 0) {

Console.Error.WriteLine($"Server build failed (exit code {buildProc.ExitCode}).");

return 1;
}

Console.WriteLine("done.");

var psi = new ProcessStartInfo("dotnet") {

Arguments              = $"run --no-build --project \"{serverProj}\" -f net10.0",
RedirectStandardInput  = true,
RedirectStandardOutput = true,
RedirectStandardError  = true,
UseShellExecute        = false,
};

using var proc = Process.Start(psi)!;

proc.ErrorDataReceived += (_, e) => {

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

var tests = new List<(bool pass, string message)>();
var onlyBuildDiag = args.Contains("--only-build-diag");

if(!onlyBuildDiag) {

await DiscoveryTests.RunAsync(ctx, tests);
await MemberBodyTests.RunAsync(ctx, tests);
await TypeTests.RunAsync(ctx, tests);
await NavigationTests.RunAsync(ctx, tests);
await CallGraphTests.RunAsync(ctx, tests);
await CodeGenerationTests.RunAsync(ctx, tests);
await FileContentTests.RunAsync(ctx, tests);
}

await ValidationTests.RunAsync(ctx, tests);

if(!onlyBuildDiag) {

await RefactoringTests.RunAsync(ctx, tests);
await EditingTests.RunAsync(ctx, tests);
await LocalHistoryTests.RunAsync(ctx, tests);
}

// -- Summary --------------------------------------------------------------------------

Console.WriteLine("\n===============================================================");
Console.WriteLine("  Test Summary");
Console.WriteLine("===============================================================\n");

var passed = tests.Count(t => t.pass);
var failed = tests.Count - passed;

foreach(var test in tests)
Console.WriteLine(test.message);

Console.WriteLine();
Console.WriteLine($"Passed: {passed}/{tests.Count}");
Console.WriteLine($"Failed: {failed}/{tests.Count}");
Console.WriteLine();

if(failed == 0)
Console.WriteLine("All tests passed!");
else
Console.WriteLine($"{failed} test(s) failed.");

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