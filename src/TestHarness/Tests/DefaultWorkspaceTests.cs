using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;

/// <summary>
///     Covers the session default workspace (#295): tools called without <c>projectPath</c>.
///     <para>
///         The end-to-end half starts its own servers rather than using the harness server, because the
///         default depends on startup inputs (<c>--root</c>, CWD) the shared server cannot vary, and a
///         default resolved from the harness CWD would be the dogfood repo. One server is rooted at a
///         two-project solution fixture; a second is rooted at an empty directory to observe the
///         no-default error. Both run with their CWD under <see cref="TestFixtures.TempRoot"/>, so an
///         ignored <c>--root</c> could never fall back to the repo.
///     </para>
///     <para>
///         The fixture solution lists <c>App</c> before <c>Lib</c>, and <c>App</c> references
///         <c>Lib</c>. That order is load-bearing: the solution's default project is <c>App</c>, whose
///         compilation holds none of <c>Lib</c>'s syntax trees, so a file-anchored tool reaching a
///         <c>Lib</c> file without <c>projectPath</c> proves the owning-project inference ran — a tool
///         that simply used the default project would report "not found in the compilation".
///     </para>
///     <para>
///         The reflection half exercises the pure resolution rules (flag precedence, solution pinning,
///         ambiguity) in-process, the way <see cref="VsVersionPinTests"/> does, because each case would
///         otherwise need its own server process.
///     </para>
/// </summary>
static class DefaultWorkspaceTests
{
	static Assembly? serverAssembly;
	
	internal static async Task<TestGroup> BuildAsync(TestContext ctx)
	{
		
		var fx    = TestFixtures.NewAdhocDir("DefaultWorkspace");
		var empty = TestFixtures.NewAdhocDir("DefaultWorkspaceEmpty");
		
		// All fixture files are written before either server starts, so the initial load sees them.
		fx.Write("DefaultWs.slnx", """
			<Solution>
			  <Project Path="App/App.csproj" />
			  <Project Path="Lib/Lib.csproj" />
			</Solution>
			""");
		fx.Write("Lib/Lib.csproj", """
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup>
			    <TargetFramework>net10.0</TargetFramework>
			  </PropertyGroup>
			</Project>
			""");
		// Line 5 holds a reference (Text, columns 27-30) for get_symbol_info — the tool resolves
		// references, not declaration names.
		fx.Write("Lib/Greeter.cs", "namespace Lib;\n\npublic class Greeter\n{\n\tpublic string Greet() => Text;\n\tconst string Text = \"hi\";\n}\n");
		fx.Write("App/App.csproj", """
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup>
			    <TargetFramework>net10.0</TargetFramework>
			  </PropertyGroup>
			  <ItemGroup>
			    <ProjectReference Include="../Lib/Lib.csproj" />
			  </ItemGroup>
			</Project>
			""");
		fx.Write("App/Consumer.cs", "namespace App;\n\npublic class Consumer\n{\n\tpublic string Use() => new Lib.Greeter().Greet();\n}\n");
		fx.Write("Notes.md", "# notes\n");
		
		var (rooted, rootedCtx)   = await StartServerAsync(ctx, "rooted", fx.Dir);
		var (bare,   bareCtx)     = await StartServerAsync(ctx, "bare",   empty.Dir);
		
		// Calls a tool on the given server and returns (no-protocol-error, parsed JSON data).
		static async Task<(bool ok, JsonNode? data)> Call(TestContext server, string tool, object args)
		{
			
			await server.SendAsync(new { jsonrpc = "2.0", id = server.NextId(), method = "tools/call",
				@params = new { name = tool, arguments = args } });
			
			var resp = await server.ReceiveAsync();
			var text = resp?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
			
			JsonNode? data = null;
			
			try { data = JsonNode.Parse(text); }
			catch { }
			
			return (resp?["error"] is null && text.Length > 0, data);
		}
		
		static (bool pass, string message) Verdict(bool pass, string failure)
			=> pass ? (true, "PASS") : (false, $"FAIL  ({failure})");
		
		var tests = new List<TestCase> {
			
			// Guard: without both servers the rest of the group can only produce noise.
			new("DefaultWorkspace: rooted and bare servers initialize",
				() => Task.FromResult(Verdict(rootedCtx is not null && bareCtx is not null, "a server did not answer initialize"))),
			
			// Solution-level: text search is solution-wide, so both projects' files must appear.
			new("DefaultWorkspace: search_files without projectPath spans the solution", async () => {
				
				var (ok, data) = await Call(rootedCtx!, "roslyn_search_files", new { pattern = "class (Greeter|Consumer)" });
				var files      = data?["matches"]?.AsArray().Select(m => m?["file"]?.GetValue<string>() ?? "").ToArray() ?? [];
				
				return Verdict(ok
					&& files.Any(f => f.Contains("Greeter.cs"))
					&& files.Any(f => f.Contains("Consumer.cs")),
					$"expected hits in both projects, got [{string.Join(", ", files)}]");
			}),
			
			// File-anchored: Lib is not the solution's default project (see the class remarks).
			new("DefaultWorkspace: get_file_outline infers the owning project", async () => {
				
				var (ok, data) = await Call(rootedCtx!, "roslyn_get_file_outline", new { filePath = "Lib/Greeter.cs" });
				var types      = data?["types"]?.AsArray().Select(t => t?["name"]?.GetValue<string>()).ToArray() ?? [];
				
				return Verdict(ok && types.Contains("Greeter"), $"expected type Greeter, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// A position-based semantic tool needs the owning project's semantic model, not just its text.
			new("DefaultWorkspace: get_symbol_info resolves in a non-default project", async () => {
				
				var (ok, data) = await Call(rootedCtx!, "roslyn_get_symbol_info", new { filePath = "Lib/Greeter.cs", line = 5, column = 28 });
				var json       = data?.ToJsonString() ?? "";
				
				return Verdict(ok && json.Contains("Text") && data?["error"] is null, $"expected Text, got {json}");
			}),
			
			// Editing a C# file with only filePath: the write lands and the workspace sees it.
			new("DefaultWorkspace: replace_in_code edits with only filePath", async () => {
				
				var (ok, _) = await Call(rootedCtx!, "roslyn_replace_in_code", new {
					filePath    = "Lib/Greeter.cs",
					nodeKind    = "method",
					textPattern = "Greet",
					replacement = "public string Greet() => \"hello\";"
				});
				
				var (readOk, read) = await Call(rootedCtx!, "roslyn_read_file", new { filePath = "Lib/Greeter.cs" });
				var lines          = read?["lines"]?.AsArray().Select(l => l?.GetValue<string>() ?? "") ?? [];
				
				return Verdict(ok && readOk
					&& lines.Any(l => l.Contains("\"hello\""))
					&& File.ReadAllText(fx.PathOf("Lib/Greeter.cs")).Contains("\"hello\""),
					"edit not visible in the workspace or on disk");
			}),
			
			// A file no project tracks falls back to the default solution rather than failing.
			new("DefaultWorkspace: insert_lines on an untracked file", async () => {
				
				var (ok, data) = await Call(rootedCtx!, "roslyn_insert_lines", new { filePath = "Notes.md", text = "- added", atLine = 2 });
				
				return Verdict(ok && data?["error"] is null && File.ReadAllText(fx.PathOf("Notes.md")).Contains("- added"),
					$"expected the line on disk, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// check_drift resolves the workspace before peeking, so an omitted path is a normal call.
			new("DefaultWorkspace: check_drift without projectPath", async () => {
				
				var (ok, data) = await Call(rootedCtx!, "roslyn_check_drift", new { });
				
				return Verdict(ok && data?["drifted"] is not null && data?["error"] is null,
					$"expected a drift report, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// A root with nothing to load: the structured error must name the fix.
			new("DefaultWorkspace: no default yields missing_project_path with a --root hint", async () => {
				
				var (ok, data) = await Call(bareCtx!, "roslyn_list_files", new { });
				
				return Verdict(ok
					&& data?["error"]?.GetValue<string>() == "missing_project_path"
					&& (data?["hint"]?.GetValue<string>() ?? "").Contains("--root"),
					$"expected missing_project_path, got {data?.ToJsonString() ?? "null"}");
			}),
			
			new("DefaultWorkspace: --root beats positional beats ROSLYNMCP_ROOT",
				() => Task.FromResult(AssertRootPrecedence(ctx))),
			
			new("DefaultWorkspace: two solutions without a pin are rejected",
				() => Task.FromResult(AssertDefaultResolution(ctx, twoSolutions: true, pin: null, expectFile: null))),
			
			new("DefaultWorkspace: a solution pin picks one of several",
				() => Task.FromResult(AssertDefaultResolution(ctx, twoSolutions: true, pin: "B.slnx", expectFile: "B.slnx"))),
			
			// A pin is a bare file name; a path component would let a committed file point outside its directory.
			new("DefaultWorkspace: a pin with a path component is ignored",
				() => Task.FromResult(AssertDefaultResolution(ctx, twoSolutions: true, pin: "../B.slnx", expectFile: null))),
			
			new("DefaultWorkspace: a solution above the root is found",
				() => Task.FromResult(AssertSolutionAbove(ctx))),
		};
		
		return new TestGroup($"DefaultWorkspace ({tests.Count} tests)", tests, Teardown: async () => {
			
			await StopServerAsync(rooted, rootedCtx);
			await StopServerAsync(bare,   bareCtx);
			fx.Dispose();
			empty.Dispose();
		});
	}
	
	// ── Server lifecycle ─────────────────────────────────────────────────────
	
	/// <summary>
	///     Starts a server with <c>--root &lt;root&gt;</c> and completes the MCP handshake. The server was
	///     already built by the harness, hence <c>--no-build</c>. Returns a null context when the server
	///     does not answer <c>initialize</c>; the group's guard test reports it.
	/// </summary>
	static async Task<(Process process, TestContext? server)> StartServerAsync(TestContext ctx, string label, string root)
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
		psi.Environment["ROSLYNMCP_LOG_PATH"] = Path.Combine(TestFixtures.TempRoot, "logs", $"defaultws-{label}-{Guid.NewGuid():N}.log");
		
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
	
	static async Task StopServerAsync(Process process, TestContext? server)
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
	
	// ── Reflection: resolution rules ─────────────────────────────────────────
	
	/// <summary>
	///     Parses three argument/environment combinations through the real <c>ServerArgs</c> constructor
	///     and checks <c>Root</c>: the explicit flag wins over a positional path, and either wins over
	///     <c>ROSLYNMCP_ROOT</c>, which applies only when neither is given. The positional path comes
	///     first in each case — the order the docs show — because a bare on/off flag such as
	///     <c>--elicit</c> consumes a following non-flag token as its value.
	/// </summary>
	static (bool pass, string message) AssertRootPrecedence(TestContext ctx)
	{
		var assembly     = LoadServerAssembly(ctx);
		var argsType     = assembly.GetType("RoslynMcp.ServerArgs", throwOnError: true)!;
		var ctor         = argsType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(string[])], null)!;
		var rootProperty = argsType.GetProperty("Root", BindingFlags.Public | BindingFlags.Instance)!;
		var priorEnv     = Environment.GetEnvironmentVariable("ROSLYNMCP_ROOT");
		
		try {
			
			Environment.SetEnvironmentVariable("ROSLYNMCP_ROOT", "env-root");
			
			(string[] args, string expected)[] cases = [
				(["positional-root", "--root", "flag-root"], "flag-root"),
				(["positional-root", "--workspace", "sdk"],  "positional-root"),
				(["--workspace", "sdk"],                      "env-root"),
			];
			
			foreach(var (args, expected) in cases) {
				
				var actual = (string?) rootProperty.GetValue(ctor.Invoke([args]));
				
				if(actual != expected)
					return (false, $"FAIL  (args [{string.Join(" ", args)}]: expected '{expected}', got '{actual ?? "null"}')");
			}
			
			return (true, "PASS  (precedence resolved as expected)");
		}
		catch(Exception ex) {
			return (false, $"FAIL  ({ex.GetType().Name}: {ex.Message})");
		}
		finally {
			Environment.SetEnvironmentVariable("ROSLYNMCP_ROOT", priorEnv);
		}
	}
	
	/// <summary>
	///     Runs <c>WorkspaceManager.FindDefaultWorkspace</c> on a directory holding <c>A.slnx</c> (and
	///     <c>B.slnx</c> when <paramref name="twoSolutions"/>), optionally with a <c>.madq_roslynmcp.json</c>
	///     pin. <paramref name="expectFile"/> null means the call must be rejected with
	///     <c>NoDefaultWorkspaceException</c>; otherwise it must return that file.
	/// </summary>
	static (bool pass, string message) AssertDefaultResolution(TestContext ctx, bool twoSolutions, string? pin, string? expectFile)
	{
		var dir = Path.Combine(TestFixtures.TempRoot, $"DefaultWorkspaceRules.{Guid.NewGuid():N}");
		
		try {
			
			Directory.CreateDirectory(dir);
			File.WriteAllText(Path.Combine(dir, "A.slnx"), "<Solution />");
			
			if(twoSolutions)
				File.WriteAllText(Path.Combine(dir, "B.slnx"), "<Solution />");
			
			if(pin is not null)
				File.WriteAllText(Path.Combine(dir, ".madq_roslynmcp.json"), $$"""{ "solution": "{{pin}}" }""");
			
			return InvokeFindDefault(ctx, dir, expectFile is null ? null : Path.Combine(dir, expectFile));
		}
		finally {
			TestFixtures.DeleteTree(dir);
		}
	}
	
	/// <summary>
	///     A root with no solution of its own resolves to the nearest solution above it — the same walk a
	///     .csproj path already takes to find its solution.
	/// </summary>
	static (bool pass, string message) AssertSolutionAbove(TestContext ctx)
	{
		var dir = Path.Combine(TestFixtures.TempRoot, $"DefaultWorkspaceAbove.{Guid.NewGuid():N}");
		var sub = Path.Combine(dir, "src", "Proj");
		
		try {
			
			Directory.CreateDirectory(sub);
			File.WriteAllText(Path.Combine(dir, "Top.slnx"), "<Solution />");
			
			return InvokeFindDefault(ctx, sub, Path.Combine(dir, "Top.slnx"));
		}
		finally {
			TestFixtures.DeleteTree(dir);
		}
	}
	
	// Invokes the private static resolver with a throwaway logger; null expectation means "must throw
	// NoDefaultWorkspaceException". ServerArgs.Current is swapped for a fresh instance because the
	// FileLogger constructor reads it, and restored afterwards so no other group observes the change.
	static (bool pass, string message) InvokeFindDefault(TestContext ctx, string root, string? expected)
	{
		var assembly     = LoadServerAssembly(ctx);
		var managerType  = assembly.GetType("RoslynMcp.WorkspaceManager", throwOnError: true)!;
		var argsType     = assembly.GetType("RoslynMcp.ServerArgs", throwOnError: true)!;
		var loggerType   = assembly.GetType("RoslynMcp.FileLogger", throwOnError: true)!;
		var currentField = argsType.GetField("current", BindingFlags.Static | BindingFlags.NonPublic)!;
		var ctor         = argsType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(string[])], null)!;
		var find         = managerType.GetMethod("FindDefaultWorkspace", BindingFlags.Static | BindingFlags.NonPublic)!;
		var priorCurrent = currentField.GetValue(null);
		var priorLogPath = Environment.GetEnvironmentVariable("ROSLYNMCP_LOG_PATH");
		IDisposable? logger = null;
		
		try {
			
			Environment.SetEnvironmentVariable("ROSLYNMCP_LOG_PATH", "");
			currentField.SetValue(null, ctor.Invoke([Array.Empty<string>()]));
			logger = (IDisposable) Activator.CreateInstance(loggerType)!;
			
			string actual;
			
			try {
				actual = (string) find.Invoke(null, [root, "test root", logger])!;
			}
			catch(TargetInvocationException ex) when(ex.InnerException?.GetType().Name == "NoDefaultWorkspaceException") {
				
				return expected is null
					? (true,  "PASS  (rejected as expected)")
					: (false, $"FAIL  (expected '{expected}', got rejection: {ex.InnerException.Message})");
			}
			
			return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
				? (true,  "PASS  (resolved as expected)")
				: (false, $"FAIL  (expected {(expected is null ? "rejection" : $"'{expected}'")}, got '{actual}')");
		}
		catch(Exception ex) {
			return (false, $"FAIL  ({ex.GetType().Name}: {ex.Message})");
		}
		finally {
			
			logger?.Dispose();
			currentField.SetValue(null, priorCurrent);
			Environment.SetEnvironmentVariable("ROSLYNMCP_LOG_PATH", priorLogPath);
		}
	}
	
	// Same load as VsVersionPinTests: the server assembly the harness just built.
	static Assembly LoadServerAssembly(TestContext ctx)
	{
		if(serverAssembly is not null)
			return serverAssembly;
		
		var projectDir   = Path.GetDirectoryName(ctx.ServerProj)!;
		var assemblyPath = Path.Combine(projectDir, "bin", "Debug", "net10.0", "RoslynMcp.dll");
		
		serverAssembly = Assembly.LoadFrom(assemblyPath);
		
		return serverAssembly;
	}
}
