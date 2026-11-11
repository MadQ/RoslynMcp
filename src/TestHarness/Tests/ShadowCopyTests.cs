using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
///     The analyzer shadow copies under contention (#412). Every server on the machine shares one
///     shadow directory, and a client restart starts several at once, so a server can find another's
///     copy of an assembly unfinished or held open. Before the fix that cost the session the
///     assembly's analyzers and fix providers: the copy was written straight into its final name,
///     any file there was taken for complete, and the failed load was cached for the process.
///     <para>
///         The harness plays the other server. It puts a file where the server's shadow copy of a
///         fixture assembly belongs — cut short, or whole and held open without sharing — and checks
///         what the server then reports for the assembly as a source of providers and how many
///         project analyzers it runs. RoslynMcp.Analyzers.dll is the assembly, as in the Code Action
///         Config group: it holds real analyzers and fix providers, and the harness has it built.
///         Each test copies it to a path of its own, which gives it a shadow location no earlier run
///         has filled. The shadow location is derived here as the server derives it; a test that
///         plants a file also checks that the server acted on that very file, so the two
///         derivations drifting apart fails the test instead of passing it on nothing.
///     </para>
/// </summary>
internal static partial class ShadowCopyTests
{
	const string Probe = "public static class Probe\n{\n\tpublic static string Describe(bool big)\n\t{\n\t\tif(big)\n\t\t\treturn \"big\";\n\t\telse\n\t\t\treturn \"small\";\n\t}\n}\n";
	
	internal static TestGroup Build(TestContext ctx)
	{
		var providerDll = FindProviderAssembly(ctx.RepoRoot);
		var fixtures    = new List<FixtureProject>();
		
		// The shadow directories the tests' assemblies get. They are in the user's own shadow root,
		// which nothing would clear of them for months, so the teardown does.
		var shadows = new List<string>();
		
		// A project whose one analyzer reference of its own is a private copy of the assembly.
		(FixtureProject Fixture, string Dll) NewReferencingProject(string label, string? sharedDll)
		{
			var dll = sharedDll;
			
			if(dll is null) {
				
				var holder = TestFixtures.NewAdhocDir($"{label}Dll");
				
				dll = holder.PathOf("RoslynMcp.Analyzers.dll");
				
				fixtures.Add(holder);
				File.Copy(providerDll!, dll);
				shadows.Add(Path.GetDirectoryName(ShadowPathOf(dll))!);
			}
			
			var fixture = TestFixtures.NewMsBuildProject(label, "net10.0", $"<ItemGroup><Analyzer Include=\"{dll}\" /></ItemGroup>");
			
			fixture.Write("Probe.cs", Probe);
			fixtures.Add(fixture);
			
			return (fixture, dll);
		}
		
		async Task<(Process Process, TestContext? Server)> Start(FixtureProject fixture, IReadOnlyDictionary<string, string> environment)
			=> await DefaultWorkspaceTests.StartServerLoggingToAsync(
				ctx, fixture.Dir, Path.Combine(TestFixtures.TempRoot, "logs", $"shadowcopy-{Guid.NewGuid():N}.log"), environment);
		
		// The fixture assembly as a provider source, after a call that used it. A source with a
		// problem is named in that call's own answer — and only there when its load failed for the
		// moment, since such a set is not kept and roslyn_info lists what is kept. A source that
		// loaded is only counted in the answer (#414), so it is read from roslyn_info.
		static Task<JsonNode?> SourceAsync(TestContext server, FixtureProject fixture, string kind)
			=> NamedSourceAsync(server, fixture, kind, "RoslynMcp.Analyzers.dll");
		
		// The same for an assembly of another name. A source ends in its file name, after a space
		// ("project: <file>") or a path separator (a configured path), and the whole name is
		// matched: Microsoft.CodeAnalysis.NetAnalyzers.dll is not the C# one.
		static async Task<JsonNode?> NamedSourceAsync(TestContext server, FixtureProject fixture, string kind, string assemblyFile)
		{
			bool EndsInFile(string source)
				=> source.EndsWith(assemblyFile, StringComparison.Ordinal)
					&& (source.Length == assemblyFile.Length || source[^(assemblyFile.Length + 1)] is ' ' or '\\' or '/');
			
			bool IsFixtureSource(JsonNode? source)
				=> source?["kind"]?.GetValue<string>() == kind
					&& source?["source"]?.GetValue<string>() is { } name
					&& EndsInFile(name);
			
			var list = await server.CallToolJsonAsync("roslyn_list_refactorings", new { projectPath = fixture.Csproj, filePath = "Probe.cs", line = 5, column = 3 });
			
			if(list?["providers"]?["problems"]?.AsArray().FirstOrDefault(IsFixtureSource) is { } failed)
				return failed;
			
			var info = await server.CallToolJsonAsync("roslyn_info", new { });
			
			return info?["code_actions"]?["sources"]?.AsArray().FirstOrDefault(IsFixtureSource);
		}
		
		// How many analyzers the project's references gave, which the diagnostics note states.
		static async Task<int> ProjectAnalyzersAsync(TestContext server, FixtureProject fixture)
		{
			var diagnostics = await server.CallToolJsonAsync("roslyn_get_diagnostics", new { projectPath = fixture.Csproj, includeAnalyzers = true, take = 0 });
			var match       = ProjectAnalyzerCount().Match(diagnostics?["hint"]?.GetValue<string>() ?? string.Empty);
			
			return match.Success ? int.Parse(match.Groups[1].Value) : -1;
		}
		
		// A source without a problem comes from roslyn_info, which leaves an empty problem list out.
		static bool Loaded(JsonNode? source)
			=> (source?["fixes"]?.GetValue<int>() ?? 0) > 0
				&& source?["problem"] is null
				&& (source?["problems"]?.AsArray().Count ?? 0) == 0;
		
		// A source that failed comes from the list's answer, which names its first problem.
		static bool Failed(JsonNode? source)
			=> source?["problem"]?.GetValue<string>().Contains("could not load", StringComparison.Ordinal) == true;
		
		// A shadow file cut short — by a server that died while copying, or one from before the copy
		// was atomic — is not an assembly. It must be replaced, not loaded: a thousand bytes of the
		// real file stand in for it, and afterwards that same path must hold the whole assembly.
		async Task<(bool pass, string msg)> RunTruncated()
		{
			if(providerDll is null)
				return Skip.SetupUnavailable("RoslynMcp.Analyzers.dll is not built");
			
			var sw             = Stopwatch.StartNew();
			var (fixture, dll) = NewReferencingProject("ShadowCut", null);
			var shadow         = ShadowPathOf(dll);
			var whole          = new FileInfo(dll).Length;
			
			Directory.CreateDirectory(Path.GetDirectoryName(shadow)!);
			File.WriteAllBytes(shadow, File.ReadAllBytes(dll)[..1000]);
			
			var (process, server) = await Start(fixture, new Dictionary<string, string>());
			
			try {
				
				if(server is null)
					return (false, "FAIL  (server did not answer initialize)");
				
				var source = await SourceAsync(server, fixture, "project");
				var healed = new FileInfo(shadow).Length;
				var pass   = Loaded(source) && healed == whole;
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (source: {source?.ToJsonString() ?? "not reported"} | shadow file: {healed} of {whole} bytes at {shadow}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				await DefaultWorkspaceTests.StopServerAsync(process, server);
			}
		}
		
		// A shadow file of the right length that is no assembly passes the length check, so only the
		// failed load shows it — a copy cut off under a writer that had sized the file first looks
		// like this. Zeros of the assembly's length stand in for it. The server must copy again and
		// load, and afterwards that same path must hold the assembly's own bytes.
		async Task<(bool pass, string msg)> RunCorrupt()
		{
			if(providerDll is null)
				return Skip.SetupUnavailable("RoslynMcp.Analyzers.dll is not built");
			
			var sw             = Stopwatch.StartNew();
			var (fixture, dll) = NewReferencingProject("ShadowBad", null);
			var shadow         = ShadowPathOf(dll);
			var real           = File.ReadAllBytes(dll);
			
			Directory.CreateDirectory(Path.GetDirectoryName(shadow)!);
			File.WriteAllBytes(shadow, new byte[real.Length]);
			
			var (process, server) = await Start(fixture, new Dictionary<string, string>());
			
			try {
				
				if(server is null)
					return (false, "FAIL  (server did not answer initialize)");
				
				var source = await SourceAsync(server, fixture, "project");
				var healed = File.ReadAllBytes(shadow).AsSpan().SequenceEqual(real);
				var pass   = Loaded(source) && healed;
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (source: {source?.ToJsonString() ?? "not reported"} | shadow file at {shadow} {(healed ? "holds" : "does not hold")} the assembly) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				await DefaultWorkspaceTests.StopServerAsync(process, server);
			}
		}
		
		// A shadow file another server holds open cannot be loaded now and can a moment later. The
		// harness holds two without sharing — one behind the project's analyzer reference, one behind
		// a configured path entry — so the first calls must report both as not loaded, and that makes
		// the test's point checkable: the calls after the files are released must find the providers
		// and more project analyzers than before. With the failure cached, as it was, they find
		// the same nothing for the rest of the process. Windows only: elsewhere an open file does
		// not stop another process from reading it, so there is no such failure to recover from.
		async Task<(bool pass, string msg)> RunHeld()
		{
			if(!OperatingSystem.IsWindows())
				return Skip.NotApplicable("an open file blocks another process's load only on Windows");
			
			if(providerDll is null)
				return Skip.SetupUnavailable("RoslynMcp.Analyzers.dll is not built");
			
			var sw             = Stopwatch.StartNew();
			var (fixture, dll) = NewReferencingProject("ShadowHeld", null);
			var configuredDll  = fixture.PathOf(Path.Combine("configured", "RoslynMcp.Analyzers.dll"));
			var userConfig     = fixture.PathOf("user-config.json");
			
			Directory.CreateDirectory(Path.GetDirectoryName(configuredDll)!);
			File.Copy(providerDll, configuredDll);
			shadows.Add(Path.GetDirectoryName(ShadowPathOf(configuredDll))!);
			File.WriteAllText(userConfig, $"{{ \"codeActions\": {{ \"providers\": [ {{ \"path\": \"{configuredDll.Replace('\\', '/')}\" }} ] }} }}\n");
			
			var holds = new List<FileStream>();
			
			foreach(var original in new[] { dll, configuredDll }) {
				
				var shadow = ShadowPathOf(original);
				
				Directory.CreateDirectory(Path.GetDirectoryName(shadow)!);
				File.Copy(original, shadow);
				holds.Add(new FileStream(shadow, FileMode.Open, FileAccess.Read, FileShare.None));
			}
			
			var (process, server) = await Start(fixture, new Dictionary<string, string> { ["ROSLYNMCP_USER_CONFIG"] = userConfig });
			
			try {
				
				if(server is null)
					return (false, "FAIL  (server did not answer initialize)");
				
				var heldProject    = await SourceAsync(server, fixture, "project");
				var heldConfigured = await SourceAsync(server, fixture, "global-config");
				var heldAnalyzers  = await ProjectAnalyzersAsync(server, fixture);
				
				foreach(var hold in holds)
					hold.Dispose();
				
				holds.Clear();
				
				var freeProject    = await SourceAsync(server, fixture, "project");
				var freeConfigured = await SourceAsync(server, fixture, "global-config");
				var freeAnalyzers  = await ProjectAnalyzersAsync(server, fixture);
				
				var pass = Failed(heldProject)
					&& Failed(heldConfigured)
					&& Loaded(freeProject)
					&& Loaded(freeConfigured)
					&& heldAnalyzers >= 0
					&& freeAnalyzers > heldAnalyzers;
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (held: project {heldProject?.ToJsonString() ?? "not reported"}, configured {heldConfigured?.ToJsonString() ?? "not reported"}, {heldAnalyzers} project analyzers | released: project {freeProject?.ToJsonString() ?? "not reported"}, configured {freeConfigured?.ToJsonString() ?? "not reported"}, {freeAnalyzers} project analyzers) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				
				foreach(var hold in holds)
					hold.Dispose();
				
				await DefaultWorkspaceTests.StopServerAsync(process, server);
			}
		}
		
		// Four servers reach for the same assembly, not yet shadow-copied, at the same moment: each has
		// its workspace loaded first, then all four are asked at once. Every one must report the
		// source loaded — whichever of them made the copy, and whichever found the rename refused
		// because a faster one had the file loaded already. A project each, since servers loading
		// one project at the same time would race on its obj directory, which is not the subject.
		// A guard, not a proof: the old copy lost this race only now and then.
		async Task<(bool pass, string msg)> RunTogether()
		{
			if(providerDll is null)
				return Skip.SetupUnavailable("RoslynMcp.Analyzers.dll is not built");
			
			var sw           = Stopwatch.StartNew();
			var (first, dll) = NewReferencingProject("ShadowRace0", null);
			var projects     = new List<FixtureProject> { first };
			
			for(var index = 1; index < 4; index++)
				projects.Add(NewReferencingProject($"ShadowRace{index}", dll).Fixture);
			
			var servers = new List<(Process Process, TestContext? Server)>();
			
			try {
				
				foreach(var project in projects)
					servers.Add(await Start(project, new Dictionary<string, string>()));
				
				if(servers.Any(started => started.Server is null))
					return (false, "FAIL  (a server did not answer initialize)");
				
				await Task.WhenAll(servers.Select((started, index) =>
					started.Server!.CallToolJsonAsync("roslyn_get_diagnostics", new { projectPath = projects[index].Csproj, take = 0 })));
				
				var sources = await Task.WhenAll(servers.Select((started, index) => SourceAsync(started.Server!, projects[index], "project")));
				var pass    = sources.All(Loaded);
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  ({string.Join(" | ", sources.Select(source => source?.ToJsonString() ?? "not reported"))}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				
				foreach(var (process, server) in servers)
					await DefaultWorkspaceTests.StopServerAsync(process, server);
			}
		}
		
		// Private copies of assemblies in a directory of their own, and a project whose analyzer
		// references are those copies in the order given. The SDK's own analyzers are switched off,
		// or the project would reference a second assembly of each name.
		FixtureProject NewOrderedProject(string label, IReadOnlyList<string> dlls)
		{
			var items   = string.Concat(dlls.Select(dll => $"<Analyzer Include=\"{dll}\" />"));
			var fixture = TestFixtures.NewMsBuildProject(label, "net10.0",
				$"<PropertyGroup><EnableNETAnalyzers>false</EnableNETAnalyzers></PropertyGroup><ItemGroup>{items}</ItemGroup>");
			
			fixture.Write("Probe.cs", Probe);
			fixtures.Add(fixture);
			
			return fixture;
		}
		
		string[] CopyToHolder(string label, IReadOnlyList<string> originals)
		{
			var holder = TestFixtures.NewAdhocDir(label);
			
			fixtures.Add(holder);
			
			return [..originals.Select(original => {
				
				var copy = holder.PathOf(Path.GetFileName(original));
				
				File.Copy(original, copy);
				shadows.Add(Path.GetDirectoryName(ShadowPathOf(copy))!);
				
				return copy;
			})];
		}
		
		// An analyzer assembly may need another analyzer reference of the same project, and the
		// project may list that one later (#421): every SDK project lists the C# NetAnalyzers
		// before Microsoft.CodeAnalysis.NetAnalyzers, which holds their base types. Loaded one
		// reference at a time, the first found no dependency and lost 129 types — analyzers and
		// fix providers — for the life of the process. The SDK's own pair is the fixture, in a
		// directory of its own and in both orders, a server per run. The dependent assembly must
		// report no problem, and neither the order of the references nor which side loads first
		// may make a difference to its fix providers or to the number of analyzers the project runs.
		async Task<(bool pass, string msg)> RunDependencyListedLater()
		{
			if(FindSdkAnalyzerPair(ctx.RepoRoot) is not { } pair)
				return Skip.SetupUnavailable("no SDK with Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll beside Microsoft.CodeAnalysis.NetAnalyzers.dll was found");
			
			var sw       = Stopwatch.StartNew();
			var copies   = CopyToHolder("ShadowPairDll", [pair.Dependent, pair.Dependency]);
			var dependentFirst  = NewOrderedProject("ShadowDependentFirst", copies);
			var dependencyFirst = NewOrderedProject("ShadowDependencyFirst", [copies[1], copies[0]]);
			
			// Whichever side loads first registers every reference it creates, so each side is
			// first once with the dependent assembly listed first.
			(string Label, FixtureProject Project, bool AnalyzersFirst)[] runs = [
				("dependent first, analyzers asked first", dependentFirst, true),
				("dependent first, providers asked first", dependentFirst, false),
				("dependency first", dependencyFirst, true),
			];
			
			var sources   = new List<JsonNode?>();
			var analyzers = new List<int>();
			
			foreach(var (_, project, analyzersFirst) in runs) {
				
				var (process, server) = await Start(project, new Dictionary<string, string>());
				
				try {
					
					if(server is null)
						return (false, "FAIL  (server did not answer initialize)");
					
					if(analyzersFirst)
						analyzers.Add(await ProjectAnalyzersAsync(server, project));
					
					sources.Add(await NamedSourceAsync(server, project, "project", Path.GetFileName(pair.Dependent)));
					
					if(!analyzersFirst)
						analyzers.Add(await ProjectAnalyzersAsync(server, project));
				}
				finally {
					await DefaultWorkspaceTests.StopServerAsync(process, server);
				}
			}
			
			var pass = sources.All(Loaded)
				&& sources.All(source => source?["fixes"]?.GetValue<int>() == sources[^1]?["fixes"]?.GetValue<int>())
				&& analyzers[^1] > 0
				&& analyzers.All(count => count == analyzers[^1]);
			
			return (pass, pass
				? $"PASS  [{sw.ElapsedMilliseconds}ms]"
				: $"FAIL  ({string.Join(" | ", runs.Select((run, index) => $"{run.Label}: {sources[index]?.ToJsonString() ?? "not reported"}, {analyzers[index]} project analyzers"))}) [{sw.ElapsedMilliseconds}ms]");
		}
		
		// Types that cannot load are a problem of a source only when a provider is among them (#421).
		// Microsoft.CodeAnalysis.Analyzers has helper types that need Microsoft.Bcl.AsyncInterfaces,
		// which is not shipped beside it and which no host on .NET has; the compiler never loads
		// them. Reported as "27 type(s) could not be loaded", that was a problem on every call for
		// every project using the package, about nothing an agent could act on. The package's own
		// assembly is the fixture — one from the NuGet cache that does reference the missing
		// assembly, so the test cannot pass on a version without the case. Its fix providers must
		// be found and the source must have no problem.
		async Task<(bool pass, string msg)> RunUnloadableHelpers()
		{
			if(FindAnalyzerWithMissingDependency() is not { } original)
				return Skip.SetupUnavailable("no Microsoft.CodeAnalysis.Analyzers.dll referencing Microsoft.Bcl.AsyncInterfaces is in the NuGet cache");
			
			var sw      = Stopwatch.StartNew();
			var copies  = CopyToHolder("ShadowHelperDll", [original]);
			var project = NewOrderedProject("ShadowHelpers", copies);
			
			var (process, server) = await Start(project, new Dictionary<string, string>());
			
			try {
				
				if(server is null)
					return (false, "FAIL  (server did not answer initialize)");
				
				var source = await NamedSourceAsync(server, project, "project", Path.GetFileName(original));
				var pass   = Loaded(source);
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (source: {source?.ToJsonString() ?? "not reported"}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				await DefaultWorkspaceTests.StopServerAsync(process, server);
			}
		}
		
		// The other half of that rule (#421 review): a type that is lost and that a host does load by
		// name is named, analyzers included. Roslyn drops an analyzer that does not load without a
		// word, so when a dependency is really missing this report is the only sign that a project's
		// analyzers run short. The fixture is the dependent half of the SDK's pair alone, in a
		// directory without the assembly its base types are in: its problems must name types, not
		// count them, and both an analyzer and a fix provider must be among the names.
		async Task<(bool pass, string msg)> RunLostTypesAreNamed()
		{
			if(FindSdkAnalyzerPair(ctx.RepoRoot) is not { } pair)
				return Skip.SetupUnavailable("no SDK with Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll beside Microsoft.CodeAnalysis.NetAnalyzers.dll was found");
			
			var sw      = Stopwatch.StartNew();
			var copies  = CopyToHolder("ShadowAloneDll", [pair.Dependent]);
			var project = NewOrderedProject("ShadowAlone", copies);
			
			var (process, server) = await Start(project, new Dictionary<string, string>());
			
			try {
				
				if(server is null)
					return (false, "FAIL  (server did not answer initialize)");
				
				const string lost = ": could not be loaded: ";
				
				// The list loads the source and names its first problem only; roslyn_info has them all.
				await NamedSourceAsync(server, project, "project", Path.GetFileName(pair.Dependent));
				
				var info     = await server.CallToolJsonAsync("roslyn_info", new { });
				var source   = info?["code_actions"]?["sources"]?.AsArray().FirstOrDefault(entry => entry?["source"]?.GetValue<string>().EndsWith($" {Path.GetFileName(pair.Dependent)}", StringComparison.Ordinal) == true);
				var problems = source?["problems"]?.AsArray().Select(problem => problem?.GetValue<string>() ?? "").ToArray() ?? [];
				var names    = problems.Where(problem => problem.Contains(lost, StringComparison.Ordinal)).Select(problem => problem[..problem.IndexOf(lost, StringComparison.Ordinal)]).ToArray();
				var pass     = problems.Length > 0
					&& names.Length == problems.Length
					&& names.Any(name => name.EndsWith("Analyzer", StringComparison.Ordinal))
					&& names.Any(name => name.EndsWith("Fixer", StringComparison.Ordinal));
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  ({problems.Length} problem(s), {names.Length} naming a type; first: {problems.FirstOrDefault() ?? "none"}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				await DefaultWorkspaceTests.StopServerAsync(process, server);
			}
		}
		
		// One server, two projects (#421 review): the first lists the dependent assembly without the
		// one it depends on, so its types are lost and both caches hold the short result, keyed by
		// the assembly alone. The second project lists the pair. The server now knows where the
		// dependency is, and what it cached before must not stand: the dependent's source must be
		// without a problem, and the first project must run more analyzers than it did.
		async Task<(bool pass, string msg)> RunSiblingNamedLater()
		{
			if(FindSdkAnalyzerPair(ctx.RepoRoot) is not { } pair)
				return Skip.SetupUnavailable("no SDK with Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll beside Microsoft.CodeAnalysis.NetAnalyzers.dll was found");
			
			var sw     = Stopwatch.StartNew();
			var copies = CopyToHolder("ShadowLaterDll", [pair.Dependent, pair.Dependency]);
			var alone  = NewOrderedProject("ShadowLaterAlone", [copies[0]]);
			var both   = NewOrderedProject("ShadowLaterBoth", copies);
			
			var (process, server) = await Start(alone, new Dictionary<string, string>());
			
			try {
				
				if(server is null)
					return (false, "FAIL  (server did not answer initialize)");
				
				var file        = Path.GetFileName(pair.Dependent);
				var shortSource = await NamedSourceAsync(server, alone, "project", file);
				var shortCount  = await ProjectAnalyzersAsync(server, alone);
				
				if(Loaded(shortSource))
					return (false, $"FAIL  (the dependent assembly loaded whole without its dependency, so nothing was cached short: {shortSource?.ToJsonString() ?? "not reported"}) [{sw.ElapsedMilliseconds}ms]");
				
				var fullSource = await NamedSourceAsync(server, both, "project", file);
				
				await ProjectAnalyzersAsync(server, both);
				
				var fullCount = await ProjectAnalyzersAsync(server, alone);
				var pass      = Loaded(fullSource) && fullCount > shortCount;
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (after the pair was named: {fullSource?.ToJsonString() ?? "not reported"}; the first project ran {shortCount} analyzers before and {fullCount} after) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				await DefaultWorkspaceTests.StopServerAsync(process, server);
			}
		}
		
		List<TestCase> tests = [
			new("a shadow copy cut short is replaced, not loaded", RunTruncated),
			new("a shadow copy of the right length that is no assembly is copied again", RunCorrupt),
			new("a shadow copy held open by another process fails that call only, not the session", RunHeld),
			new("servers shadow-copying the same assembly at once all load it", RunTogether),
			new("an analyzer reference listed before the reference it depends on loads all its types", RunDependencyListedLater),
			new("helper types that cannot load are not a problem of the source", RunUnloadableHelpers),
			new("an analyzer or fix provider lost to a missing dependency is named in the source's problems", RunLostTypesAreNamed),
			new("a result cached while a dependency was missing does not outlive a later project that names it", RunSiblingNamedLater),
		];
		
		return new TestGroup($"Shadow Copies ({tests.Count} tests)", tests, Teardown: () => {
			
			foreach(var fixture in fixtures)
				fixture.Dispose();
			
			foreach(var shadow in shadows) {
				
				try {
					Directory.Delete(shadow, recursive: true);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			}
			
			return Task.CompletedTask;
		});
	}
	
	// Where the server keeps its copy of an assembly (ShadowCopyAnalyzerLoader.ShadowCopy): a
	// directory named by a hash of the path and the modification time, under the user's local
	// application data.
	static string ShadowPathOf(string dll)
	{
		var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{dll}|{File.GetLastWriteTimeUtc(dll).Ticks}")))[..16];
		
		return Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"RoslynMcp", "analyzer-shadow", key, Path.GetFileName(dll));
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
	
	// The C# NetAnalyzers and the assembly they depend on, from an SDK beside the runtime the
	// harness runs on: the one the repository pins when it is installed, as the fixtures resolve
	// it, else the newest that has both. A newer SDK's analyzers may need a newer Roslyn than the
	// server has, which would fail the test for a reason that is not its subject.
	static (string Dependent, string Dependency)? FindSdkAnalyzerPair(string repoRoot)
	{
		var pinned = PinnedSdkVersion(repoRoot);
		var dotnetRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
		var sdks       = Path.Combine(dotnetRoot, "sdk");
		
		if(!Directory.Exists(sdks))
			return null;
		
		foreach(var sdk in Directory.EnumerateDirectories(sdks).OrderByDescending(sdk => Path.GetFileName(sdk) == pinned).ThenByDescending(Directory.GetCreationTimeUtc)) {
			
			var analyzers  = Path.Combine(sdk, "Sdks", "Microsoft.NET.Sdk", "analyzers");
			var dependent  = Path.Combine(analyzers, "Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll");
			var dependency = Path.Combine(analyzers, "Microsoft.CodeAnalysis.NetAnalyzers.dll");
			
			if(File.Exists(dependent) && File.Exists(dependency))
				return (dependent, dependency);
		}
		
		return null;
	}
	
	// The SDK version the repository's global.json pins; null when there is none to read.
	static string? PinnedSdkVersion(string repoRoot)
	{
		try {
			return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(repoRoot, "global.json")))?["sdk"]?["version"]?.GetValue<string>();
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) {
			return null;
		}
	}
	
	// A Microsoft.CodeAnalysis.Analyzers.dll in the NuGet cache whose metadata references
	// Microsoft.Bcl.AsyncInterfaces. The package is a dependency of the Roslyn packages the server
	// is built with, so a restore of the repository puts one there.
	static string? FindAnalyzerWithMissingDependency()
	{
		var cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
			? configured
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
		
		var package = Path.Combine(cache, "microsoft.codeanalysis.analyzers");
		
		if(!Directory.Exists(package))
			return null;
		
		return Directory.EnumerateDirectories(package)
			.Select(version => Path.Combine(version, "analyzers", "dotnet", "cs", "Microsoft.CodeAnalysis.Analyzers.dll"))
			.Where(File.Exists)
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault(References("Microsoft.Bcl.AsyncInterfaces"));
	}
	
	// A file in the cache that cannot be read as an assembly is one that does not qualify, not a
	// reason to abort the run.
	static Func<string, bool> References(string assemblyName)
		=> dll => {
			
			try {
				
				using var image = new PEReader(File.OpenRead(dll));
				
				var reader = image.GetMetadataReader();
				
				return reader.AssemblyReferences.Any(handle => reader.StringComparer.Equals(reader.GetAssemblyReference(handle).Name, assemblyName));
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) {
				return false;
			}
		};
	
	[GeneratedRegex(@"beside (\d+) from the project's analyzer references")]
	private static partial Regex ProjectAnalyzerCount();
}
