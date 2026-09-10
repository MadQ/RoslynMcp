using System.Reflection;
using System.Runtime.CompilerServices;

/// <summary>
///     Covers <c>MSBuildBootstrap.DetectLoadStyle</c> — the auto-mode decision between SDK and
///     Visual Studio MSBuild (#266). The bug it guards against: a solution was judged by whatever
///     .csproj the file system enumerated first, which on a real repo was an SDK-style backup copy
///     that the solution did not even reference, so a legacy solution auto-detected as Sdk and
///     skipped the VS BuildHost pin. Every test builds a throwaway tree under %TEMP% with the
///     deciding file deliberately placed where the old heuristic would have picked wrong.
/// </summary>
static class ProjectStyleDetectionTests
{
	static Assembly? serverAssembly;
	
	const string SdkCsproj = """
		<Project Sdk="Microsoft.NET.Sdk">
		  <PropertyGroup>
		    <TargetFramework>net10.0</TargetFramework>
		  </PropertyGroup>
		</Project>
		""";
	
	const string LegacyCsproj = """
		<?xml version="1.0" encoding="utf-8"?>
		<Project ToolsVersion="15.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
		  <PropertyGroup>
		    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
		  </PropertyGroup>
		</Project>
		""";
	
	internal static TestGroup Build(TestContext ctx)
	{
		
		var tests = new List<TestCase> {
			
			// The exact #266 shape: "BackupQ" sorts before "Src", is SDK-style, and is not in the
			// .sln. The old FindFirstCsproj peeked at it and said Sdk; the solution's only real
			// project is legacy, so the answer must be Vs.
			new("ProjectStyle: .sln is judged by referenced projects, not the first csproj on disk",
				() => Task.FromResult(AssertLegacySolutionIgnoresUnreferencedBackup(ctx))),
			
			// A referenced-but-missing project (a sibling repo not checked out) must be skipped
			// silently, and the count in the detail must reflect only projects that exist.
			new("ProjectStyle: missing solution projects are skipped",
				() => Task.FromResult(AssertMissingProjectsSkipped(ctx))),
			
			// .slnx is XML, not the Project("{GUID}") line format — separate parser path.
			new("ProjectStyle: .slnx projects are read",
				() => Task.FromResult(AssertSlnxDetected(ctx))),
			
			// A solution that lists no readable projects falls back to a directory scan, and the
			// scan must not be fooled by a legacy csproj sitting under bin/.
			new("ProjectStyle: directory fallback skips bin/obj",
				() => Task.FromResult(AssertDirectoryFallbackSkipsBuildOutput(ctx))),
			
			// A direct .csproj path bypasses solution parsing entirely.
			new("ProjectStyle: direct legacy csproj detects Vs",
				() => Task.FromResult(AssertDirectCsproj(ctx))),
		};
		
		return new TestGroup($"ProjectStyleDetection ({tests.Count} tests)", tests);
	}
	
	static (bool pass, string message) AssertLegacySolutionIgnoresUnreferencedBackup(TestContext ctx)
	{
		
		return WithScratchTree(ctx, "legacy-sln", root => {
			
			WriteFile(root, "BackupQ/App.csproj", SdkCsproj);
			WriteFile(root, "Src/App.csproj",     LegacyCsproj);
			WriteFile(root, "App.sln", Sln(("App", @"Src\App.csproj")));
			
			var (mode, detail) = Detect(ctx, Path.Combine(root, "App.sln"));
			
			if(mode != "Vs")
				return (false, $"FAIL  (expected Vs, got {mode}: {detail})");
			
			if(!detail.Contains("App.sln") || !detail.Contains("1 projects"))
				return (false, $"FAIL  (detail should name the solution and count 1 project: {detail})");
			
			return (true, $"PASS  ({detail})");
		});
	}
	
	static (bool pass, string message) AssertMissingProjectsSkipped(TestContext ctx)
	{
		
		return WithScratchTree(ctx, "missing-proj", root => {
			
			WriteFile(root, "Lib/Lib.csproj", SdkCsproj);
			WriteFile(root, "App.sln", Sln(("Gone", @"..\Gone\Gone.csproj"), ("Lib", @"Lib\Lib.csproj")));
			
			var (mode, detail) = Detect(ctx, Path.Combine(root, "App.sln"));
			
			if(mode != "Sdk" || !detail.Contains("all 1 projects"))
				return (false, $"FAIL  (expected Sdk with 1 existing project, got {mode}: {detail})");
			
			return (true, $"PASS  ({detail})");
		});
	}
	
	static (bool pass, string message) AssertSlnxDetected(TestContext ctx)
	{
		
		return WithScratchTree(ctx, "slnx", root => {
			
			WriteFile(root, "Old/Old.csproj", LegacyCsproj);
			WriteFile(root, "App.slnx", """
				<Solution>
				  <Folder Name="/Solution Items/">
				    <File Path="README.md" />
				  </Folder>
				  <Project Path="Old/Old.csproj" />
				</Solution>
				""");
			
			var (mode, detail) = Detect(ctx, Path.Combine(root, "App.slnx"));
			
			if(mode != "Vs" || !detail.Contains("Old.csproj"))
				return (false, $"FAIL  (expected Vs via Old.csproj, got {mode}: {detail})");
			
			return (true, $"PASS  ({detail})");
		});
	}
	
	static (bool pass, string message) AssertDirectoryFallbackSkipsBuildOutput(TestContext ctx)
	{
		
		return WithScratchTree(ctx, "dir-fallback", root => {
			
			WriteFile(root, "bin/Stale.csproj", LegacyCsproj);
			WriteFile(root, "Lib/Lib.csproj",   SdkCsproj);
			WriteFile(root, "Empty.sln", Sln());
			
			var (mode, detail) = Detect(ctx, Path.Combine(root, "Empty.sln"));
			
			if(mode != "Sdk" || !detail.Contains("directory scan"))
				return (false, $"FAIL  (expected Sdk via directory scan ignoring bin/, got {mode}: {detail})");
			
			return (true, $"PASS  ({detail})");
		});
	}
	
	static (bool pass, string message) AssertDirectCsproj(TestContext ctx)
	{
		
		return WithScratchTree(ctx, "direct", root => {
			
			WriteFile(root, "Old.csproj", LegacyCsproj);
			
			var (mode, detail) = Detect(ctx, Path.Combine(root, "Old.csproj"));
			
			if(mode != "Vs")
				return (false, $"FAIL  (expected Vs, got {mode}: {detail})");
			
			return (true, $"PASS  ({detail})");
		});
	}
	
	// ── helpers ──────────────────────────────────────────────────────────────
	
	// Builds a minimal but well-formed .sln: one Project(...) line per (name, relativePath).
	// The GUID is the C# project type; the per-project GUID is irrelevant to the parser.
	static string Sln(params (string Name, string RelativePath)[] projects)
	{
		
		var lines = new List<string> {
			"Microsoft Visual Studio Solution File, Format Version 12.00",
			"# Visual Studio Version 17",
		};
		
		foreach(var (name, relativePath) in projects)
			lines.Add($"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{name}\", \"{relativePath}\", \"{{{Guid.NewGuid()}}}\"");
		
		lines.Add("Global");
		lines.Add("EndGlobal");
		
		return string.Join(Environment.NewLine, lines);
	}
	
	static void WriteFile(string root, string relativePath, string content)
	{
		
		var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
		
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, content);
	}
	
	// Invokes MSBuildBootstrap.DetectLoadStyle via reflection (internal type) and unpacks the
	// (WorkspaceMode, string) tuple as strings so this file need not reference the server's enum.
	static (string mode, string detail) Detect(TestContext ctx, string path)
	{
		
		var assembly  = LoadServerAssembly(ctx);
		var bootstrap = assembly.GetType("RoslynMcp.MSBuildBootstrap", throwOnError: true)!;
		var method    = bootstrap.GetMethod("DetectLoadStyle", BindingFlags.Public | BindingFlags.Static)!;
		var result    = (ITuple) method.Invoke(null, [path])!;
		
		return (result[0]!.ToString()!, (string) result[1]!);
	}
	
	static (bool pass, string message) WithScratchTree(TestContext ctx, string label, Func<string, (bool pass, string message)> body)
	{
		
		var root = Path.Combine(Path.GetTempPath(), "RoslynMcp.TestHarness", $"ProjectStyle.{label}.{Guid.NewGuid():N}");
		
		try {
			
			Directory.CreateDirectory(root);
			
			return body(root);
		}
		catch(Exception ex) {
			return (false, $"FAIL  ({ex.GetType().Name}: {ex.Message})");
		}
		finally {
			
			try {
				
				if(Directory.Exists(root))
					Directory.Delete(root, recursive: true);
			}
			catch(Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				// Best-effort cleanup of a temp tree; a leftover folder must not fail the test.
			}
		}
	}
	
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
