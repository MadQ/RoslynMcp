using System.Reflection;

// The default itself: a required projectPath still rejects "", a root with nothing to load names
// the fix, and — in-process, through reflection — the rules that pick the default (#295).
static partial class DefaultWorkspaceTests
{
	static List<TestCase> DefaultTests(Fixture f)
	{
		var rootedCtx = f.Rooted;
		var bareCtx   = f.Bare;
		var ctx       = f.Ctx;
		
		return new List<TestCase> {
			
			// Only tools whose projectPath is optional may use the default. A tool that still requires it
			// must reject "" even when a default exists — otherwise it would silently search the default
			// solution's first project (App here), not fail as the schema promises.
			new("DefaultWorkspace: a required projectPath still rejects the empty string", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_member_body", new { symbolName = "Greet", projectPath = "" });
				
				return Verdict(ok && data?["error"]?.GetValue<string>() == "missing_project_path",
					$"expected missing_project_path, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// A root with nothing to load: the structured error must name the fix.
			new("DefaultWorkspace: no default yields missing_project_path with a --root hint", async () => {
				
				var (ok, data) = await Call(bareCtx, "roslyn_list_files", new { });
				
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
	}
	
	static Assembly? serverAssembly;
	
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
