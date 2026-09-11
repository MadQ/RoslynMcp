using System.Reflection;

static class VsVersionPinTests
{
	static Assembly? serverAssembly;
	
	internal static TestGroup Build(TestContext ctx)
	{
		
		var tests = new List<TestCase> {
			
			new("VsVersionPin: major normalizes to next-major range",
				() => Task.FromResult(AssertNormalization(ctx, "17", true, "[17.0,18.0)"))),
			
			new("VsVersionPin: major.minor normalizes to next-minor range",
				() => Task.FromResult(AssertNormalization(ctx, "17.14", true, "[17.14,17.15)"))),
			
			new("VsVersionPin: exact and range inputs are accepted canonically",
				() => Task.FromResult(AssertExactAndRangeNormalization(ctx))),
			
			new("VsVersionPin: invalid input is rejected",
				() => Task.FromResult(AssertNormalization(ctx, "foo", false, ""))),

			new("VsVersionPin: int.MaxValue boundaries are rejected",
				() => Task.FromResult(AssertOverflowBoundariesRejected(ctx))),
			
			new("VsVersionPin: CLI valid beats env and project",
				() => Task.FromResult(AssertEffectiveVsVersion(ctx,
					args: ["--vs-version", "17"],
					envVsVersion: "18",
					projectVsVersion: "19",
					expected: "[17.0,18.0)"))),
			
			new("VsVersionPin: invalid CLI falls back to env before project",
				() => Task.FromResult(AssertEffectiveVsVersion(ctx,
					args: ["--vs-version", "foo"],
					envVsVersion: "18",
					projectVsVersion: "19",
					expected: "[18.0,19.0)",
					expectedRejected: "foo"))),
			
			new("VsVersionPin: project config supplies the pin when CLI and env do not",
				() => Task.FromResult(AssertEffectiveVsVersion(ctx,
					args: [],
					envVsVersion: null,
					projectVsVersion: "19",
					expected: "[19.0,20.0)"))),
		};
		
		return new TestGroup($"VsVersionPin ({tests.Count} tests)", tests);
	}
	
	static (bool pass, string message) AssertNormalization(TestContext ctx, string value, bool expectedValid, string expectedRange)
	{
		
		try {
			
			var assembly    = LoadServerAssembly(ctx);
			var pinType     = assembly.GetType("RoslynMcp.VsVersionPin", throwOnError: true)!;
			var tryNormalize = pinType.GetMethod("TryNormalize", BindingFlags.Public | BindingFlags.Static)!;
			object?[] args  = [value, ""];
			var valid       = (bool) tryNormalize.Invoke(null, args)!;
			var actualRange = (string) args[1]!;
			
			if(valid != expectedValid || actualRange != expectedRange)
				return (false, $"FAIL  (expected valid={expectedValid}, range='{expectedRange}'; got valid={valid}, range='{actualRange}')");
			
			return (true, "PASS  (normalized as expected)");
		}
		catch(Exception ex) {
			return (false, $"FAIL  ({ex.GetType().Name}: {ex.Message})");
		}
	}
	
	static (bool pass, string message) AssertExactAndRangeNormalization(TestContext ctx)
	{
		
		var exact = AssertNormalization(ctx, "17.14.37516.0", true, "[17.14.37516.0,17.14.37516.0]");
		
		if(!exact.pass)
			return exact;
		
		return AssertNormalization(ctx, " [17.0, 18.0) ", true, "[17.0,18.0)");
	}

	static (bool pass, string message) AssertOverflowBoundariesRejected(TestContext ctx)
	{
		
		var major = AssertNormalization(ctx, int.MaxValue.ToString(), false, "");
		
		if(!major.pass)
			return major;
		
		return AssertNormalization(ctx, $"17.{int.MaxValue}", false, "");
	}
	
	static (bool pass, string message) AssertEffectiveVsVersion(
		  TestContext ctx
		, string[] args
		, string? envVsVersion
		, string? projectVsVersion
		, string expected
		, string? expectedRejected = null
	)
	{
		
		var tempRoot          = Path.Combine(TestFixtures.TempRoot, $"VsVersionPin.{Guid.NewGuid():N}");
		var configRoot        = Path.Combine(tempRoot, "project");
		var projectDir        = Path.Combine(configRoot, "src");
		var probePath         = Path.Combine(projectDir, "Probe.cs");
		var projectConfigPath = Path.Combine(configRoot, ".madq_roslynmcp.json");
		
		try {
			
			Directory.CreateDirectory(projectDir);
			File.WriteAllText(probePath, "class Probe { }");
			File.WriteAllText(projectConfigPath,
				projectVsVersion is null
					? "{}"
					: $$"""
						{
						  "vsVersion": "{{projectVsVersion}}"
						}
						""");
			
			var assembly         = LoadServerAssembly(ctx);
			var serverArgsType   = assembly.GetType("RoslynMcp.ServerArgs", throwOnError: true)!;
			var projectConfigType = assembly.GetType("RoslynMcp.ProjectConfig", throwOnError: true)!;
			var loggerType       = assembly.GetType("RoslynMcp.FileLogger", throwOnError: true)!;
			var currentField     = serverArgsType.GetField("current", BindingFlags.Static | BindingFlags.NonPublic)!;
			var ctor             = serverArgsType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(string[])], null)!;
			var effectiveMethod  = projectConfigType.GetMethod("EffectiveVsVersion", BindingFlags.Public | BindingFlags.Static)!;
			var rejectedProperty = serverArgsType.GetProperty("VsVersionRejected", BindingFlags.Public | BindingFlags.Instance)!;
			IDisposable? logger  = null;
			var priorCurrent     = currentField.GetValue(null);
			var priorEnv         = Environment.GetEnvironmentVariable("ROSLYNMCP_VS_VERSION");
			var priorLogPath     = Environment.GetEnvironmentVariable("ROSLYNMCP_LOG_PATH");
			
			try {
				
				Environment.SetEnvironmentVariable("ROSLYNMCP_VS_VERSION", envVsVersion);
				Environment.SetEnvironmentVariable("ROSLYNMCP_LOG_PATH", "");
				var serverArgs = ctor.Invoke([args]);
				currentField.SetValue(null, serverArgs);
				logger = (IDisposable) Activator.CreateInstance(loggerType)!;
				var effective = (string?) effectiveMethod.Invoke(null, [probePath, logger]);
				var rejected  = (string?) rejectedProperty.GetValue(serverArgs);
				
				if(effective != expected)
					return (false, $"FAIL  (expected effective='{expected}', got '{effective ?? "null"}')");
				
				if(rejected != expectedRejected)
					return (false, $"FAIL  (expected rejected='{expectedRejected ?? "null"}', got '{rejected ?? "null"}')");
			}
			finally {
				
				logger?.Dispose();
				currentField.SetValue(null, priorCurrent);
				Environment.SetEnvironmentVariable("ROSLYNMCP_VS_VERSION", priorEnv);
				Environment.SetEnvironmentVariable("ROSLYNMCP_LOG_PATH", priorLogPath);
			}
			
			return (true, "PASS  (precedence resolved as expected)");
		}
		catch(Exception ex) {
			return (false, $"FAIL  ({ex.GetType().Name}: {ex.Message})");
		}
		finally {
			
			if(Directory.Exists(tempRoot))
				Directory.Delete(tempRoot, recursive: true);
		}
	}
	
	static Assembly LoadServerAssembly(TestContext ctx)
	{
		
		if(serverAssembly is not null)
			return serverAssembly;
		
		var projectDir = Path.GetDirectoryName(ctx.ServerProj)!;
		var assemblyPath = Path.Combine(projectDir, "bin", "Debug", "net10.0", "RoslynMcp.dll");
		
		serverAssembly = Assembly.LoadFrom(assemblyPath);
		
		return serverAssembly;
	}
}
