using System.Diagnostics;
using System.Text.Json.Nodes;

/// <summary>
///     The development host (#426, <c>src/RoslynMcp.DevHost</c>): the process an MCP client launches in
///     place of the server, which starts the server build a pointer file names and puts another in its
///     place when the pointer is written again. What it promises is checked here against a real host over
///     the server the harness built, with the harness as the client: a session through it is an ordinary
///     session; a swap gives the session another server process and tells the client its tool list
///     changed; a call that is waiting when the pointer changes is answered first; a build that does not
///     run is refused and the session keeps its server; the server's own exit ends the host with the same
///     code, so a client restarts it as it would the server; and a build that dies at start-up is replaced
///     by the last one that answered. Every "build" is the harness's one server build under another
///     pointer value: writing the pointer is the restart request, whatever it names, so a second build
///     would add minutes and prove nothing more. Each test has a host home of its own
///     (<c>ROSLYNMCP_DEV_HOME</c>), so the developer's own is never read or written.
/// </summary>
static class DevHostTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		var fx        = TestFixtures.NewAdhocDir("DevHost");
		var homes     = TestFixtures.NewAdhocDir("DevHostHomes");
		var slow      = TestFixtures.NewMsBuildProject("DevHostSlow", targetFrameworks: "net10.0");
		var hostDll   = Path.Combine(ctx.RepoRoot, "src", "RoslynMcp.DevHost", "bin", "Debug", "net10.0", "RoslynMcp.DevHost.dll");
		var serverDir = Path.Combine(ctx.RepoRoot, "src", "RoslynMcp", "bin", "Debug", "net10.0");
		
		fx.Write("Probe.cs", "class Probe { }\n");
		slow.Write("Probe.cs", "class Probe { }\n");
		
		// A directory the host takes for a build, whose server exits at once: dotnet cannot run the file.
		var broken = Path.Combine(homes.Dir, "broken-build");
		Directory.CreateDirectory(broken);
		File.WriteAllText(Path.Combine(broken, "RoslynMcp.dll"), "not an assembly");
		
		var empty = Path.Combine(homes.Dir, "empty-build");
		Directory.CreateDirectory(empty);
		
		string NewHome(string label, string build)
		{
			var home = Path.Combine(homes.Dir, label);
			
			Directory.CreateDirectory(home);
			Point(home, build);
			
			return home;
		}
		
		async Task<(Process process, TestContext? client)> StartHostAsync(string home)
		{
			var psi = new ProcessStartInfo("dotnet")
			{
				
				WorkingDirectory       = TestFixtures.TempRoot,
				RedirectStandardInput  = true,
				RedirectStandardOutput = true,
				RedirectStandardError  = true,
				UseShellExecute        = false,
			};
			
			foreach(var argument in new[] { hostDll, "--root", fx.Dir })
				psi.ArgumentList.Add(argument);
			
			psi.Environment["ROSLYNMCP_DEV_HOME"]    = home;
			psi.Environment["ROSLYNMCP_LOG_PATH"]    = Path.Combine(TestFixtures.TempRoot, "logs", $"devhost-{Guid.NewGuid():N}.log");
			psi.Environment["ROSLYNMCP_USER_CONFIG"] = Path.Combine(TestFixtures.TempRoot, "no-user-config.json");
			
			var process = Process.Start(psi)!;
			
			// Drained so a chatty stderr can never fill the pipe and stall the host or its server.
			process.ErrorDataReceived += (_, _) => { };
			process.BeginErrorReadLine();
			
			var client = new TestContext(process.StandardInput, process.StandardOutput, fx.Dir, ctx.RepoRoot, ctx.ServerProj);
			
			await client.SendAsync(new { jsonrpc = "2.0", id = client.NextId(), method = "initialize",
				@params = new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "TestHarness", version = "1.0" } } });
			
			if(await client.ReceiveAsync() is null)
				
				return (process, null);
			
			await client.SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
			
			return (process, client);
		}
		
		async Task<(bool pass, string msg)> WithHostAsync(string home, Func<Process, TestContext, Task<string?>> body)
		{
			if(!File.Exists(hostDll))
				
				return (false, $"FAIL  (the host was not built: {hostDll})");
			
			var sw = Stopwatch.StartNew();
			var (process, client) = await StartHostAsync(home);
			
			try {
				
				if(client is null)
					
					return (false, $"FAIL  (the host did not answer initialize; its log: {HostLog(home)}) [{sw.ElapsedMilliseconds}ms]");
				
				var failure = await body(process, client);
				
				return failure is null
					? (true,  $"PASS  [{sw.ElapsedMilliseconds}ms]")
					: (false, $"FAIL  ({failure}; host log: {HostLog(home)}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				
				// The host may have exited on its own; closing the pipe it held is a courtesy that may fail.
				try {
					await DefaultWorkspaceTests.StopServerAsync(process, client);
				}
				catch(IOException) {
					
					if(!process.HasExited)
						process.Kill(entireProcessTree: true);
				}
			}
		}
		
		Task<(bool pass, string msg)> RunPassThrough() => WithHostAsync(NewHome("pass-through", serverDir), async (process, client) =>
		{
			var notifications = new List<string>();
			var pid           = Pid(await CallAsync(client, "roslyn_info", new { }, notifications));
			var files         = Content(await CallAsync(client, "roslyn_list_files", new { projectPath = fx.Dir, pattern = "*.cs" }, notifications));
			
			if(pid is null || pid == process.Id)
				
				return $"roslyn_info answered pid {pid?.ToString() ?? "nothing"}, the host is {process.Id}: no server of its own answered";
			
			if(files?["files"]?.AsArray().Any(file => file?.GetValue<string>().EndsWith("Probe.cs", StringComparison.Ordinal) == true) != true)
				
				return $"roslyn_list_files through the host answered {files?.ToJsonString() ?? "nothing"}";
			
			return notifications.Count == 0 ? null : $"the host sent {string.Join(", ", notifications)} with no swap";
		});
		
		Task<(bool pass, string msg)> RunSwap()
		{
			var home = NewHome("swap", serverDir);
			
			return WithHostAsync(home, async (_, client) =>
			{
				var notifications = new List<string>();
				var first         = Pid(await CallAsync(client, "roslyn_info", new { }, notifications));
				
				if(first is null)
					
					return "roslyn_info did not answer before the swap";
				
				Point(home, serverDir);
				
				var second = await PidAfterSwapAsync(client, first.Value, notifications);
				
				if(second is null)
					
					return $"the server was still pid {first} 30 s after the pointer was written";
				
				if(!notifications.Contains("notifications/tools/list_changed"))
					
					return $"the client was not told its tool list changed (got: {string.Join(", ", notifications)})";
				
				var files = Content(await CallAsync(client, "roslyn_list_files", new { projectPath = fx.Dir, pattern = "*.cs" }, notifications));
				
				if(files?["count"]?.GetValue<int>() != 1)
					
					return $"the new server answered roslyn_list_files with {files?.ToJsonString() ?? "nothing"}";
				
				if(!await ExitedAsync(first.Value))
					
					return $"the old server (pid {first}) still runs 15 s after the swap";
				
				var lastGood = File.Exists(Path.Combine(home, "last-good")) ? File.ReadAllText(Path.Combine(home, "last-good")).Trim() : "";
				
				return lastGood == serverDir ? null : $"last-good holds '{lastGood}', expected the build that answered";
			});
		}
		
		Task<(bool pass, string msg)> RunCallInFlight()
		{
			var home = NewHome("in-flight", serverDir);
			
			return WithHostAsync(home, async (_, client) =>
			{
				var notifications = new List<string>();
				var first         = Pid(await CallAsync(client, "roslyn_info", new { }, notifications));
				
				if(first is null)
					
					return "roslyn_info did not answer before the swap";
				
				// The first call on an MSBuild project loads it, which takes seconds: long enough for the
				// host to see the pointer while the call waits.
				var id = client.NextId();
				
				await client.SendAsync(new { jsonrpc = "2.0", id, method = "tools/call",
					@params = new { name = "roslyn_get_diagnostics", arguments = new { projectPath = slow.Csproj, take = 0 } } });
				
				Point(home, serverDir);
				
				var answer = await AnswerAsync(client, id, notifications);
				
				if(Content(answer)?["summary"] is null)
					
					return $"the call that was waiting was answered with {answer?.ToJsonString() ?? "nothing"}";
				
				if(await PidAfterSwapAsync(client, first.Value, notifications) is null)
					
					return $"the server was still pid {first} 30 s after the waiting call was answered";
				
				return HostLog(home).Contains("waiting for 1 request(s) to be answered", StringComparison.Ordinal)
					? null
					: "the host never waited for the call: the swap was not asked for while it was in flight, so the test proved nothing";
			});
		}
		
		Task<(bool pass, string msg)> RunRefusedBuilds()
		{
			var home = NewHome("refused", serverDir);
			
			return WithHostAsync(home, async (_, client) =>
			{
				var notifications = new List<string>();
				var first         = Pid(await CallAsync(client, "roslyn_info", new { }, notifications));
				
				if(first is null)
					
					return "roslyn_info did not answer before the swap";
				
				foreach(var (build, reason) in new[] { (empty, "no RoslynMcp executable"), (broken, "exited before answering initialize") }) {
					
					Point(home, build);
					
					if(!await LoggedAsync(home, $"Build {build} was not swapped in: ", reason))
						
						return $"the host did not refuse {Path.GetFileName(build)} with '{reason}' within 30 s";
					
					var still = Pid(await CallAsync(client, "roslyn_info", new { }, notifications));
					
					if(still != first)
						
						return $"after refusing {Path.GetFileName(build)} roslyn_info answered pid {still?.ToString() ?? "nothing"}, expected the first server {first}";
				}
				
				if(notifications.Count > 0)
					
					return $"the host sent {string.Join(", ", notifications)} for a build it refused";
				
				// A refusal is of that pointer value, not of swapping.
				Point(home, serverDir);
				
				return await PidAfterSwapAsync(client, first.Value, notifications) is null
					? "a build that runs was not swapped in after two refusals"
					: null;
			});
		}
		
		Task<(bool pass, string msg)> RunServerExit() => WithHostAsync(NewHome("server-exit", serverDir), async (process, client) =>
		{
			var respawn = Content(await CallAsync(client, "roslyn_respawn", new { }, []));
			
			if(respawn?["pid"] is null)
				
				return $"roslyn_respawn through the host answered {respawn?.ToJsonString() ?? "nothing"}";
			
			try {
				await process.WaitForExitAsync(new CancellationTokenSource(30_000).Token);
			}
			catch(OperationCanceledException) {
				return "the host still runs 30 s after its server exited";
			}
			
			return process.ExitCode == 0 ? null : $"the host exited with code {process.ExitCode}, its server with 0";
		});
		
		Task<(bool pass, string msg)> RunLastGood()
		{
			var home = NewHome("last-good", broken);
			
			File.WriteAllText(Path.Combine(home, "last-good"), serverDir + "\n");
			
			return WithHostAsync(home, async (_, client) =>
			{
				var pid = Pid(await CallAsync(client, "roslyn_info", new { }, []));
				
				if(pid is null)
					
					return "the session got no server";
				
				return HostLog(home).Contains("Running the last good build", StringComparison.Ordinal)
					? null
					: "the session was answered, but the host does not say it fell back to the last good build";
			});
		}
		
		List<TestCase> tests = [
			new("a session through the host is answered by a server of the host's own, with nothing added", RunPassThrough),
			new("writing the pointer swaps the server: another pid, tools/list_changed, the old process gone, last-good recorded", RunSwap),
			new("a call waiting when the pointer changes is answered, and the swap follows it", RunCallInFlight),
			new("a pointer to no build, and to one that exits at start-up, is refused and the session keeps its server", RunRefusedBuilds),
			new("the server's own exit (roslyn_respawn) ends the host with the same exit code", RunServerExit),
			new("a build that exits before answering initialize is replaced by the last good build", RunLastGood),
		];
		
		return new TestGroup($"Dev Host ({tests.Count} tests)", tests, Teardown: () =>
		{
			fx.Dispose();
			homes.Dispose();
			slow.Dispose();
			
			return Task.CompletedTask;
		});
	}
	
	// Replaced whole, as the publish script does: the host must never read half a path.
	static void Point(string home, string build)
	{
		var temp = Path.Combine(home, $"current.{Guid.NewGuid():N}.tmp");
		
		File.WriteAllText(temp, build + "\n");
		File.Move(temp, Path.Combine(home, "current"), overwrite: true);
	}
	
	static Task<JsonNode?> CallAsync(TestContext client, string tool, object arguments, List<string> notifications)
	{
		var id = client.NextId();
		
		return SendAsync();
		
		async Task<JsonNode?> SendAsync()
		{
			await client.SendAsync(new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name = tool, arguments } });
			
			return await AnswerAsync(client, id, notifications);
		}
	}
	
	// The answer to one request. A host may send a notification of its own at any moment, so whatever
	// else arrives first is collected by its method, not taken for the answer.
	static async Task<JsonNode?> AnswerAsync(TestContext client, int id, List<string> notifications)
	{
		while(await client.ReceiveAsync() is { } message) {
			
			if(message["id"] is JsonValue value && value.TryGetValue<int>(out var answered) && answered == id)
				
				return message;
			
			if(message["method"]?.GetValue<string>() is { } method)
				notifications.Add(method);
		}
		
		return null;
	}
	
	static JsonNode? Content(JsonNode? answer)
	{
		var text = answer?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
		
		if(string.IsNullOrEmpty(text))
			
			return null;
		
		try {
			return JsonNode.Parse(text);
		}
		catch(System.Text.Json.JsonException) {
			return null;
		}
	}
	
	static int? Pid(JsonNode? answer) => Content(answer)?["pid"]?.GetValue<int>()
	;
	
	// The pid of the server that answers once it is no longer the first one; null after 30 s of the same.
	static async Task<int?> PidAfterSwapAsync(TestContext client, int first, List<string> notifications)
	{
		var sw = Stopwatch.StartNew();
		
		while(sw.ElapsedMilliseconds < 30_000) {
			
			if(Pid(await CallAsync(client, "roslyn_info", new { }, notifications)) is { } pid && pid != first)
				
				return pid;
			
			await Task.Delay(200);
		}
		
		return null;
	}
	
	static async Task<bool> ExitedAsync(int pid)
	{
		var sw = Stopwatch.StartNew();
		
		while(sw.ElapsedMilliseconds < 15_000) {
			
			try {
				
				using var process = Process.GetProcessById(pid);
				
				if(process.HasExited)
					
					return true;
			}
			catch(ArgumentException) {
				return true; // No such process.
			}
			
			await Task.Delay(200);
		}
		
		return false;
	}
	
	static string HostLog(string home)
	{
		var logs = Path.Combine(home, "logs");
		
		try {
			return Directory.Exists(logs) ? string.Concat(Directory.EnumerateFiles(logs, "devhost.*.log").Select(ReadShared)) : "";
		}
		catch(IOException failure) {
			return $"(unreadable: {failure.Message})";
		}
	}
	
	// The host appends while the test reads.
	static string ReadShared(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using var reader = new StreamReader(stream);
		
		return reader.ReadToEnd();
	}
	
	static async Task<bool> LoggedAsync(string home, string start, string reason)
	{
		var sw = Stopwatch.StartNew();
		
		while(sw.ElapsedMilliseconds < 30_000) {
			
			if(HostLog(home).Split('\n').Any(line => line.Contains(start, StringComparison.Ordinal) && line.Contains(reason, StringComparison.Ordinal)))
				
				return true;
			
			await Task.Delay(200);
		}
		
		return false;
	}
}
