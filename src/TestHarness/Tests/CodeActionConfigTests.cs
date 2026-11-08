using System.Diagnostics;
using System.Text.Json.Nodes;

/// <summary>
///     The configured provider sources of #389: entries of the user's config.json and of the committed
///     .madq_roslynmcp.json, by NuGet package (resolved from the local cache only, the highest cached
///     version when none is given) or by assembly path (from a committed file only once the user trusts
///     it), the deny list, and the off switch. The user config, the cache and the trust flag are
///     per-process settings, so every test starts a server of its own with the environment it needs.
///     RoslynMcp.Analyzers.dll stands in for a third-party provider package: it carries real
///     [ExportCodeFixProvider] types, and the harness has it built. What is checked is what
///     roslyn_info reports per source — the yield, the kind, and the problem text — and, for the deny
///     list, which refactoring is missing from a list the built-ins would otherwise offer.
/// </summary>
internal static class CodeActionConfigTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		var fx          = TestFixtures.NewAdhocDir("CodeActionConfig");
		var providerDll = FindProviderAssembly(ctx.RepoRoot);
		var nuget       = fx.PathOf("nuget");
		var userConfig  = fx.PathOf("user-config.json");
		var pathEntry   = fx.PathOf(Path.Combine("providers", "RoslynMcp.Analyzers.dll"));
		
		fx.Write("Probe.cs", "public static class Probe\n{\n\tpublic static string Describe(bool big)\n\t{\n\t\tif(big)\n\t\t\treturn \"big\";\n\t\telse\n\t\t\treturn \"small\";\n\t}\n}\n");
		
		if(providerDll is not null) {
			
			// Two cached versions: the entry names none, so the higher one must be picked.
			Copy(providerDll, Path.Combine(nuget, "fakeproviders", "1.2.0", "analyzers", "dotnet", "cs", "RoslynMcp.Analyzers.dll"));
			Copy(providerDll, Path.Combine(nuget, "fakeproviders", "0.9.0", "analyzers", "dotnet", "cs", "RoslynMcp.Analyzers.dll"));
			Copy(providerDll, pathEntry);
		}
		
		// Not an assembly at all: the package loads nothing and says why, and the others still load.
		var badDll = Path.Combine(nuget, "badpkg", "1.0.0", "analyzers", "dotnet", "cs", "Bad.dll");
		Directory.CreateDirectory(Path.GetDirectoryName(badDll)!);
		File.WriteAllBytes(badDll, [0x42, 0x41, 0x44, 0x21]);
		
		File.WriteAllText(userConfig,
			"{ \"codeActions\": { \"providers\": [ { \"package\": \"fakeproviders\" }, { \"package\": \"badpkg\" }, { \"package\": \"missingpkg\", \"version\": \"2.0.0\" } ], \"deny\": [ \"InvertIf\" ] } }\n");
		fx.Write(".madq_roslynmcp.json", "{ \"codeActions\": { \"providers\": [ { \"path\": \"providers/RoslynMcp.Analyzers.dll\" } ] } }\n");
		
		var baseEnvironment = new Dictionary<string, string> {
			["ROSLYNMCP_USER_CONFIG"] = userConfig,
			["NUGET_PACKAGES"]        = nuget,
		};
		
		Dictionary<string, string> With(string name, string value)
			=> new(baseEnvironment) { [name] = value };
		
		async Task<(bool pass, string msg)> WithServer(IReadOnlyDictionary<string, string> environment, Func<TestContext, Task<(bool pass, string msg)>> body)
		{
			var (process, server) = await DefaultWorkspaceTests.StartServerLoggingToAsync(
				ctx, fx.Dir, Path.Combine(TestFixtures.TempRoot, "logs", $"codeactioncfg-{Guid.NewGuid():N}.log"), environment);
			
			try {
				
				if(server is null)
					return (false, "FAIL  (server did not answer initialize)");
				
				return await body(server);
			}
			finally {
				await DefaultWorkspaceTests.StopServerAsync(process, server);
			}
		}
		
		static JsonNode? SourceOfKind(JsonNode? info, string kind, string? sourceStart)
			=> info?["code_actions"]?["sources"]?.AsArray().FirstOrDefault(source =>
				source?["kind"]?.GetValue<string>() == kind
				&& (sourceStart is null || source?["source"]?.GetValue<string>().StartsWith(sourceStart, StringComparison.Ordinal) == true));
		
		static bool HasProblem(JsonNode? source, string text)
			=> source?["problems"]?.AsArray().Any(problem => problem?.GetValue<string>().Contains(text, StringComparison.Ordinal) == true) == true;
		
		static int Fixes(JsonNode? source)
			=> source?["fixes"]?.GetValue<int>() ?? -1;
		
		// Package entries from the user file, a path entry from the committed file without trust, and the deny list.
		Task<(bool pass, string msg)> RunSources() => WithServer(baseEnvironment, async server =>
		{
			if(providerDll is null)
				return Skip.SetupUnavailable("RoslynMcp.Analyzers.dll is not built");
			
			var sw   = Stopwatch.StartNew();
			var list = await server.CallToolJsonAsync("roslyn_list_refactorings", new { projectPath = fx.Dir, filePath = "Probe.cs", line = 5, column = 3 });
			var info = await server.CallToolJsonAsync("roslyn_info", new { });
			
			var fake    = SourceOfKind(info, "global-config", "config: fakeproviders");
			var bad     = SourceOfKind(info, "global-config", "config: badpkg");
			var missing = SourceOfKind(info, "global-config", "config: missingpkg");
			var byPath  = SourceOfKind(info, "project-config", null);
			var denied  = list?["actions"]?.AsArray().All(action => action?["provider"]?.GetValue<string>().EndsWith("InvertIfCodeRefactoringProvider", StringComparison.Ordinal) != true) == true;
			
			// Invert if is the one action the built-ins offer at that caret, so the deny leaves the list empty.
			var pass = list?["error"] is null
				&& denied
				&& Fixes(fake) > 0
				&& fake?["origin"]?.GetValue<string>().Contains("1.2.0", StringComparison.Ordinal) == true
				&& HasProblem(bad, "could not load")
				&& HasProblem(missing, "not in the NuGet cache")
				&& Fixes(byPath) == 0
				&& HasProblem(byPath, "untrusted")
				&& info?["code_actions"]?["user_config"]?.GetValue<string>() == userConfig;
			
			return (pass, pass
				? $"PASS  [{sw.ElapsedMilliseconds}ms]"
				: $"FAIL  (list: {list?.ToJsonString()} | info: {info?["code_actions"]?.ToJsonString()}) [{sw.ElapsedMilliseconds}ms]");
		});
		
		// The same committed path entry loads once the machine's user trusts project providers.
		Task<(bool pass, string msg)> RunTrusted() => WithServer(With("ROSLYNMCP_TRUST_PROJECT_PROVIDERS", "true"), async server =>
		{
			if(providerDll is null)
				return Skip.SetupUnavailable("RoslynMcp.Analyzers.dll is not built");
			
			var sw   = Stopwatch.StartNew();
			var list = await server.CallToolJsonAsync("roslyn_list_refactorings", new { projectPath = fx.Dir, filePath = "Probe.cs", line = 5, column = 3 });
			var info = await server.CallToolJsonAsync("roslyn_info", new { });
			var byPath = SourceOfKind(info, "project-config", null);
			var pass = list?["error"] is null
				&& Fixes(byPath) > 0
				&& !HasProblem(byPath, "untrusted");
			
			return (pass, pass
				? $"PASS  [{sw.ElapsedMilliseconds}ms]"
				: $"FAIL  (info: {info?["code_actions"]?.ToJsonString()}) [{sw.ElapsedMilliseconds}ms]");
		});
		
		// The env var turns the tools off above every file, and nothing but the bundled fixers is loaded.
		Task<(bool pass, string msg)> RunOff() => WithServer(With("ROSLYNMCP_CODE_ACTIONS", "off"), async server =>
		{
			var sw   = Stopwatch.StartNew();
			var list = await server.CallToolJsonAsync("roslyn_list_refactorings", new { projectPath = fx.Dir, filePath = "Probe.cs", line = 5, column = 3 });
			var info = await server.CallToolJsonAsync("roslyn_info", new { });
			var kinds = info?["code_actions"]?["sources"]?.AsArray().Select(source => source?["kind"]?.GetValue<string>()).ToArray() ?? [];
			var pass = list?["error"]?.GetValue<string>() == "providers_disabled"
				&& kinds.Length > 0
				&& kinds.All(kind => kind == "bundled");
			
			return (pass, pass
				? $"PASS  [{sw.ElapsedMilliseconds}ms]"
				: $"FAIL  (list: {list?.ToJsonString()} | info: {info?["code_actions"]?.ToJsonString()}) [{sw.ElapsedMilliseconds}ms]");
		});
		
		List<TestCase> tests = [
			new("package entries load from the cache, a bad one is reported, a committed path waits for trust, deny hides a provider", RunSources),
			new("a committed path entry loads once project providers are trusted", RunTrusted),
			new("ROSLYNMCP_CODE_ACTIONS=off turns the tools off and loads nothing", RunOff),
		];
		
		return new TestGroup($"Code Action Config ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); return Task.CompletedTask; });
	}
	
	// The analyzers project is built with the server (a project reference); the newest output wins
	// when more than one configuration has been built.
	static string? FindProviderAssembly(string repoRoot)
	{
		var bin = Path.Combine(repoRoot, "src", "RoslynMcp.Analyzers", "bin");
		
		if(!Directory.Exists(bin))
			return null;
		
		return Directory.EnumerateFiles(bin, "RoslynMcp.Analyzers.dll", SearchOption.AllDirectories)
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault();
	}
	
	static void Copy(string source, string destination)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
		File.Copy(source, destination, overwrite: true);
	}
}
