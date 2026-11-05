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
static partial class DefaultWorkspaceTests
{
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
		
		// #376: a warning (CS0168) in Lib, the solution's second project, for the solution-wide
		// diagnostics test; and a project under the solution's directory that the solution does not
		// list, so that a .csproj loads the solution and still names none of its projects.
		// #384: members of Lib, the solution's second project, renamed and re-signed through the solution.
		fx.Write("Lib/ApplyTarget.cs", "namespace Lib;\n\npublic class ApplyTarget\n{\n\tpublic int Before(int a) => a;\n\tpublic int Shape(int a) => a;\n\tpublic int Kept(int a) => a;\n}\n");
		// #384 review, round 3: one file per apply through Linked/, so neither depends on the other's write
		// having reached the workspace the next preview runs in.
		fx.Write("Lib/ViaSolution.cs", "namespace Lib;\n\npublic class ViaSolutionTarget\n{\n\tpublic int Early(int a) => a;\n}\n");
		fx.Write("Lib/ViaProject.cs", "namespace Lib;\n\npublic class ViaProjectTarget\n{\n\tpublic int Early(int a) => a;\n}\n");
		fx.Write("Lib/Warn.cs", "namespace Lib;\n\ninternal class WarnHolder\n{\n\tvoid M()\n\t{\n\t\tint unused;\n\t}\n}\n");
		fx.Write("Stray/Stray.csproj", """
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup>
			    <TargetFramework>net10.0</TargetFramework>
			  </PropertyGroup>
			</Project>
			""");
		
		// #384 review: Twin/Lib.csproj shares only its name with Lib/Lib.csproj, a listed project, so it
		// is not that project. Alias/Lib.csproj is a symbolic link to Lib/Lib.csproj in another directory:
		// the same file, but MSBuild evaluates it from Alias/, so it is not that project either (where
		// links can be created).
		fx.Write("Twin/Lib.csproj", """
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup>
			    <TargetFramework>net10.0</TargetFramework>
			  </PropertyGroup>
			</Project>
			""");
		
		var aliasLinked = false;
		
		try {
			
			Directory.CreateDirectory(fx.PathOf("Alias"));
			File.CreateSymbolicLink(fx.PathOf("Alias/Lib.csproj"), fx.PathOf("Lib/Lib.csproj"));
			
			// Round 4: the same kind of link, listed by a solution of its own.
			Directory.CreateDirectory(fx.PathOf("Other"));
			File.CreateSymbolicLink(fx.PathOf("Other/Lib.csproj"), fx.PathOf("Lib/Lib.csproj"));
			fx.Write("Other/Other.slnx", """
				<Solution>
				  <Project Path="Lib.csproj" />
				</Solution>
				""");
			
			aliasLinked = true;
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
		
		// #384 review, round 3: Linked/Linked.slnx lists Lib through a directory link, Linked/LibLink →
		// Lib, so its project is Lib under another name. In a directory of its own: a second solution
		// beside DefaultWs.slnx would make the default workspace ambiguous.
		var dirLinked = false;
		
		try {
			
			Directory.CreateDirectory(fx.PathOf("Linked"));
			Directory.CreateSymbolicLink(fx.PathOf("Linked/LibLink"), fx.PathOf("Lib"));
			fx.Write("Linked/Linked.slnx", """
				<Solution>
				  <Project Path="LibLink/Lib.csproj" />
				</Solution>
				""");
			
			dirLinked = true;
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
		
		// Same file name, different files, different projects — the Program.cs-in-every-project case.
		fx.Write("App/Shared.cs", "namespace App;\n\ninternal class AppShared { }\n");
		fx.Write("Lib/Shared.cs", "namespace Lib;\n\ninternal class LibShared { }\n");
		
		// Same trailing path twice in one project (#362): a root file and a nested one, and two nested
		// ones with nothing at the root. The project-level check above cannot see either case.
		fx.Write("App/Program.cs",       "namespace App;\n\ninternal class RootProgram { }\n");
		fx.Write("App/Tools/Program.cs", "namespace App.Tools;\n\ninternal class ToolsProgram { }\n");
		fx.Write("Lib/A/Dup.cs",         "namespace Lib.A;\n\ninternal class DupA { }\n");
		fx.Write("Lib/B/Dup.cs",         "namespace Lib.B;\n\ninternal class DupB { }\n");
		
		// #376: a solution of one project with another, unlisted project beside it. One project is no
		// reason to take any path for it.
		var single = TestFixtures.NewAdhocDir("DefaultWorkspaceSingle");
		
		single.Write("Single.slnx", "<Solution>\n  <Project Path=\"Only/Only.csproj\" />\n</Solution>\n");
		single.Write("Only/Only.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
		single.Write("Only/OnlyType.cs", "namespace Only;\n\npublic class OnlyType\n{\n\tpublic int Value() => 1;\n}\n");
		// #384 review: a member renamed through the solution of one project, which names that project.
		single.Write("Only/SoleTarget.cs", "namespace Only;\n\npublic class SoleTarget\n{\n\tpublic int Old() => 0;\n}\n");
		single.Write("Beside/Beside.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
		
		// Review of #378: a solution in a directory of its own that lists a project outside it, and
		// a project with two target frameworks whose warnings differ by framework — net10.0-windows
		// defines WINDOWS. EnableWindowsTargeting lets it load on Linux too; it needs no other pack.
		single.Write("Sln/Apart.slnx", "<Solution>\n  <Project Path=\"../Apart/Apart.csproj\" />\n  <Project Path=\"../Multi/Multi.csproj\" />\n</Solution>\n");
		single.Write("Apart/Apart.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
		single.Write("Apart/Warned.cs", "namespace Apart;\n\ninternal class Warned\n{\n\tvoid M()\n\t{\n\t\tint outsideTheSolutionDirectory;\n\t}\n}\n");
		single.Write("Multi/Multi.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFrameworks>net10.0;net10.0-windows</TargetFrameworks>\n    <EnableWindowsTargeting>true</EnableWindowsTargeting>\n  </PropertyGroup>\n</Project>\n");
		single.Write("Multi/Both.cs", "namespace Multi;\n\ninternal class Both\n{\n\tvoid M()\n\t{\n#if WINDOWS\n\t\tint onlyOnWindows;\n#else\n\t\tint onlyElsewhere;\n#endif\n\t}\n}\n");
		
		// Review of #378, round 2: two projects that both compile Common/SharedSource.cs, which uses
		// a type each project declares and the other lacks — the file is an error in both, each
		// time for the other project's reason. And a file of the second project with a code fix,
		// for a preview/apply round trip through the solution. In a directory of its own, so that
		// Beside.csproj still finds only Single.slnx above it.
		single.Write("Shared/Shared.slnx", "<Solution>\n  <Project Path=\"P1/P1.csproj\" />\n  <Project Path=\"P2/P2.csproj\" />\n</Solution>\n");
		
		foreach(var name in new[] { "P1", "P2" }) {
			
			single.Write($"Shared/{name}/{name}.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n  <ItemGroup>\n    <Compile Include=\"../Common/SharedSource.cs\" Link=\"SharedSource.cs\" />\n  </ItemGroup>\n</Project>\n");
			single.Write($"Shared/{name}/OnlyIn{name}.cs", $"namespace Shared;\n\ninternal class OnlyIn{name} {{ }}\n");
		}
		
		single.Write("Shared/Common/SharedSource.cs", "namespace Shared;\n\ninternal class SharedSource\n{\n\tOnlyInP1? first;\n\tOnlyInP2? second;\n}\n");
		single.Write("Shared/P2/FixMe.cs", "class CodeFixFixture { TestMultipleActions value; }\n");
		
		// Review of #378: a solution in a directory whose project file pins the adhoc workspace. The
		// adhoc project has no file, so a solution's projects come out as none; the one compilation
		// must still be checked.
		var adhocSln = TestFixtures.NewAdhocDir("DefaultWorkspaceAdhocSln");
		
		adhocSln.Write(".madq_roslynmcp.json", "{ \"workspace\": \"adhoc\" }\n");
		adhocSln.Write("Pinned.slnx", "<Solution>\n  <Project Path=\"Pinned/Pinned.csproj\" />\n</Solution>\n");
		adhocSln.Write("Pinned/Pinned.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
		adhocSln.Write("Pinned/Broken.cs", "namespace Pinned;\n\ninternal class Broken\n{\n\tint M() => undefinedName;\n}\n");
		
		// Two names that differ only in case are two files only where the file system says so (Linux,
		// not Windows or a default macOS volume) — probed rather than assumed from the platform. The
		// pair lives in an Adhoc directory: MSBuildWorkspace on Linux loads only one of two Compile items
		// whose paths differ only in case (MSBuild itself lists both), so a project could not hold them.
		var caseFx = TestFixtures.NewAdhocDir("DefaultWorkspaceCase");
		
		caseFx.Write("Case/Name.cs", "namespace Probe;\n\ninternal class UpperName { }\n");
		
		var caseSensitive = !File.Exists(caseFx.PathOf("Case/name.cs"));
		
		if(caseSensitive) {
			
			caseFx.Write("Case/name.cs", "namespace Probe;\n\ninternal class LowerName { }\n");
			
			// The same pair in an MSBuild project, where only one of the two is loaded.
			fx.Write("App/Case/Name.cs", "namespace App.Case;\n\ninternal class UpperName { }\n");
			fx.Write("App/Case/name.cs", "namespace App.Case;\n\ninternal class LowerName { }\n");
		}
		
		var (rooted, rootedCtx)   = await StartServerAsync(ctx, "rooted", fx.Dir);
		var (bare,   bareCtx)     = await StartServerAsync(ctx, "bare",   empty.Dir);
		
		var f = new Fixture(ctx, rootedCtx, bareCtx, fx, single, adhocSln, caseFx, caseSensitive, aliasLinked, dirLinked);
		
		// In the order the tests have always run: a few depend on an earlier one — the Stray build on the
		// restore before it, apply tests on files that earlier tests renamed.
		List<TestCase> tests = [.. FileTests(f), .. SolutionTests(f), .. ApplyTests(f), .. DefaultTests(f)];
		
		return new TestGroup($"DefaultWorkspace ({tests.Count} tests)", tests, Teardown: async () => {
			
			await StopServerAsync(rooted, rootedCtx);
			await StopServerAsync(bare,   bareCtx);
			fx.Dispose();
			empty.Dispose();
			caseFx.Dispose();
			single.Dispose();
			adhocSln.Dispose();
		});
	}
	
	/// <summary>
	///     What every part of the group works against, built once by <see cref="BuildAsync"/>: the
	///     context of the harness, the rooted and bare servers (null when one did not start), the fixture
	///     directories, and what the machine allowed to be built (case-distinct names, symbolic links).
	///     Each part copies the members it uses into locals named as the tests have always named them,
	///     so the tests read as they did when they were one list (#387).
	/// </summary>
	sealed record Fixture(
		TestContext    Ctx,
		TestContext?   Rooted,
		TestContext?   Bare,
		FixtureProject Fx,
		FixtureProject Single,
		FixtureProject AdhocSln,
		FixtureProject CaseFx,
		bool           CaseSensitive,
		bool           AliasLinked,
		bool           DirLinked)
	{
		public string AppProject => Fx.PathOf("App/App.csproj");
		public string LibProject => Fx.PathOf("Lib/Lib.csproj");
	}
	
	// Calls a tool on the given server and returns (no-protocol-error, parsed JSON data). A server
	// that failed to start yields a plain failure: the harness has no per-test exception guard, so
	// a null dereference here would abort the whole run instead of failing one test.
	static async Task<(bool ok, JsonNode? data)> Call(TestContext? server, string tool, object args)
	{
		
		if(server is null)
			
			return (false, null);
		
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
}
