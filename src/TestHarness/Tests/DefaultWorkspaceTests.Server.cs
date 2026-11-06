using System.Diagnostics;

// Servers of the group's own, rooted where a test needs them; also used by other groups.
static partial class DefaultWorkspaceTests
{
	/// <summary>
	///     Starts a server with <c>--root &lt;root&gt;</c> and completes the MCP handshake. The server was
	///     already built by the harness, hence <c>--no-build</c>. Returns a null context when the server
	///     does not answer <c>initialize</c>; the group's guard test reports it.
	/// </summary>
	static Task<(Process process, TestContext? server)> StartServerAsync(TestContext ctx, string label, string root)
		=> StartServerLoggingToAsync(ctx, root, Path.Combine(TestFixtures.TempRoot, "logs", $"defaultws-{label}-{Guid.NewGuid():N}.log"));
	
	/// <summary>
	///     The same, logging to <paramref name="logPath"/> — for a test that reads what a fresh server
	///     logged. The server injects its process id into the file name, so the file is found by the
	///     name's stem followed by <c>*</c>.
	/// </summary>
	internal static Task<(Process process, TestContext? server)> StartServerLoggingToAsync(TestContext ctx, string root, string logPath)
		=> StartServerLoggingToAsync(ctx, root, logPath, new Dictionary<string, string>())
	;
	
	/// <summary>
	///     The same, with <paramref name="environment"/> set for the server as well — for a test that needs a
	///     setting the harness's own server must not have (a short load timeout, say).
	/// </summary>
	internal static async Task<(Process process, TestContext? server)> StartServerLoggingToAsync(TestContext ctx, string root, string logPath, IReadOnlyDictionary<string, string> environment)
	{
		var psi = new ProcessStartInfo("dotnet")
		{
			
			Arguments              = $"run --no-build --no-launch-profile --project \"{ctx.ServerProj}\" -f net10.0 -- --root \"{root}\"",
			WorkingDirectory       = TestFixtures.TempRoot,
			RedirectStandardInput  = true,
			RedirectStandardOutput = true,
			RedirectStandardError  = true,
			UseShellExecute        = false,
		};
		psi.Environment["ROSLYNMCP_LOG_PATH"] = logPath;
		
		// As for the harness's own server: the debug-only fix provider for the marker types, which
		// the code-fix round trip through a solution uses.
		psi.Environment["ROSLYNMCP_TEST_CODE_FIXES"] = "1";
		
		foreach(var (name, value) in environment)
			psi.Environment[name] = value;
		
		var process = Process.Start(psi)!;
		
		// Drained so a chatty stderr can never fill the pipe and stall the server.
		process.ErrorDataReceived += (_, _) => { };
		process.BeginErrorReadLine();
		
		var server = new TestContext(process.StandardInput, process.StandardOutput, root, ctx.RepoRoot, ctx.ServerProj);
		
		await server.SendAsync(new { jsonrpc = "2.0", id = server.NextId(), method = "initialize",
			@params = new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "TestHarness", version = "1.0" } } });
		
		if(await server.ReceiveAsync() is null)
			
			return (process, null);
		
		await server.SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
		
		return (process, server);
	}
	
	internal static async Task StopServerAsync(Process process, TestContext? server)
	{
		server?.CloseInput();
		
		try {
			await process.WaitForExitAsync(new CancellationTokenSource(5_000).Token);
		}
		catch(OperationCanceledException) { }
		
		if(!process.HasExited)
			process.Kill(entireProcessTree: true);
		
		process.Dispose();
	}
}
