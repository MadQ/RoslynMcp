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
		
		// Outlines filePath and checks it named the expected file: the type it declares, or ambiguous_file
		// with both candidates when expectType is null. projectPath null exercises the default workspace.
		async Task<(bool pass, string message)> AssertOutline(string filePath, string? projectPath, string? expectType)
		{
			
			var (ok, data) = projectPath is null
				? await Call(rootedCtx, "roslyn_get_file_outline", new { filePath })
				: await Call(rootedCtx, "roslyn_get_file_outline", new { filePath, projectPath });
			
			if(expectType is null)
				
				return Verdict(ok && data?["error"]?.GetValue<string>() == "ambiguous_file" && data?["found_in"]?.AsArray().Count == 2,
					$"expected ambiguous_file naming both files, got {data?.ToJsonString() ?? "null"}");
			
			string?[] types = [.. data?["types"]?.AsArray().Select(t => t?["name"]?.GetValue<string>()) ?? []];
			
			return Verdict(ok && types is [var only] && only == expectType, $"expected type {expectType}, got {data?.ToJsonString() ?? "null"}");
		}
		
		var appProject = fx.PathOf("App/App.csproj");
		var libProject = fx.PathOf("Lib/Lib.csproj");
		
		var tests = new List<TestCase> {
			
			// Guard: without both servers the rest of the group can only produce noise.
			new("DefaultWorkspace: rooted and bare servers initialize",
				() => Task.FromResult(Verdict(rootedCtx is not null && bareCtx is not null, "a server did not answer initialize"))),
			
			// Solution-level: text search is solution-wide, so both projects' files must appear.
			new("DefaultWorkspace: search_files without projectPath spans the solution", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_search_files", new { pattern = "class (Greeter|Consumer)" });
				var files      = data?["matches"]?.AsArray().Select(m => m?["file"]?.GetValue<string>() ?? "").ToArray() ?? [];
				
				return Verdict(ok
					&& files.Any(f => f.Contains("Greeter.cs"))
					&& files.Any(f => f.Contains("Consumer.cs")),
					$"expected hits in both projects, got [{string.Join(", ", files)}]");
			}),
			
			// File-anchored: Lib is not the solution's default project (see the class remarks).
			new("DefaultWorkspace: get_file_outline infers the owning project", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_file_outline", new { filePath = "Lib/Greeter.cs" });
				var types      = data?["types"]?.AsArray().Select(t => t?["name"]?.GetValue<string>()).ToArray() ?? [];
				
				return Verdict(ok && types.Contains("Greeter"), $"expected type Greeter, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// A bare file name matching a different file in each project must not silently pick the first
			// project: that would outline App's Shared.cs for a caller that meant Lib's, or the reverse.
			new("DefaultWorkspace: an ambiguous file name is rejected with candidates", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_file_outline", new { filePath = "Shared.cs" });
				var found      = data?["found_in"]?.AsArray().Count ?? 0;
				
				return Verdict(ok && data?["error"]?.GetValue<string>() == "ambiguous_file" && found == 2,
					$"expected ambiguous_file naming both projects, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #362: the name of a file at the project root is exact, so it must not resolve to a nested
			// file with the same trailing path. With projectPath omitted, the default workspace narrows to
			// App first and the lookup then runs against App's root — both routes must agree.
			new("DefaultWorkspace: a root file name beats a nested file with the same name (default)",
				() => AssertOutline("Program.cs", null, "RootProgram")),
			
			new("DefaultWorkspace: a root file name beats a nested file with the same name (explicit)",
				() => AssertOutline("Program.cs", appProject, "RootProgram")),
			
			new("DefaultWorkspace: a nested path still selects the nested file",
				() => AssertOutline("Tools/Program.cs", appProject, "ToolsProgram")),
			
			// Two nested files with nothing at the root: no exact match, so a bare name is ambiguous
			// within one project — the cross-project check cannot catch it.
			new("DefaultWorkspace: a name matching two nested files is ambiguous (default)",
				() => AssertOutline("Dup.cs", null, null)),
			
			new("DefaultWorkspace: a name matching two nested files is ambiguous (explicit)",
				() => AssertOutline("Dup.cs", libProject, null)),
			
			new("DefaultWorkspace: a longer path picks one of two nested files (default)",
				() => AssertOutline("A/Dup.cs", null, "DupA")),
			
			new("DefaultWorkspace: a longer path picks one of two nested files (explicit)",
				() => AssertOutline("B/Dup.cs", libProject, "DupB")),
			
			// The project-relative exact path is canonical, so spellings that are no raw suffix of any file
			// still name the project's root file.
			new("DefaultWorkspace: './Program.cs' and 'Tools/../Program.cs' name the project's root file", async () => {
				
				var dot  = await AssertOutline("./Program.cs",        appProject, "RootProgram");
				var back = await AssertOutline("Tools/../Program.cs", appProject, "RootProgram");
				
				return dot.pass ? back : dot;
			}),
			
			// get_line_count takes a list that can span projects, so with projectPath omitted each path not
			// on disk finds its own project: Dup.cs lives in Lib, not in the solution's default project App.
			new("DefaultWorkspace: get_line_count resolves each entry's project when projectPath is omitted", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_line_count", new { filePaths = "Dup.cs,A/Dup.cs" });
				var files      = data?["files"]?.AsArray();
				var dup        = files?[0]?["error"]?.GetValue<string>() ?? "";
				var counted    = files?[1]?["line_count"] is not null;
				
				return Verdict(ok && dup.Contains("ambiguous") && counted,
					$"expected Dup.cs ambiguous and A/Dup.cs counted, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// A positional rename reports the ambiguous file with the standard structured error, not as text
			// inside a message — a client must be able to read error and found_in.
			new("DefaultWorkspace: preview_rename at a position in an ambiguous file returns ambiguous_file", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_preview_rename", new {
					symbolName  = "DupA",
					newName     = "DupRenamed",
					projectPath = libProject,
					filePath    = "Dup.cs",
					line        = 3,
					column      = 16
				});
				
				return Verdict(ok && data?["error"]?.GetValue<string>() == "ambiguous_file" && data?["found_in"]?.AsArray().Count == 2,
					$"expected a structured ambiguous_file, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// An exact name follows the file system's case rules: where Name.cs and name.cs are two files,
			// each name selects its own — a case-insensitive comparison would take whichever came first.
			new("DefaultWorkspace: an exact name is case-sensitive where the file system is", async () => {
				
				if(!caseSensitive)
					
					return Skip.NotApplicable("names that differ only in case are one file on this file system");
				
				var upper = await AssertOutline("Case/Name.cs", caseFx.Dir, "UpperName");
				var lower = await AssertOutline("Case/name.cs", caseFx.Dir, "LowerName");
				
				if(upper.pass && lower.pass)
					
					return upper;
				
				// Says whether the compilation holds both files at all, which the outline cannot tell apart
				// from a lookup that picked the wrong one.
				var (_, types) = await Call(rootedCtx, "roslyn_list_types", new { projectPath = caseFx.Dir, namespaceFilter = "Probe" });
				
				return (false, $"{(upper.pass ? lower : upper).message}; types in Probe: {types?.ToJsonString() ?? "null"}");
			}),
			
			// MSBuildWorkspace on Linux loads one of two files whose names differ only in case. The other
			// name must then be "not found", never an answer about the loaded file — which one is loaded is
			// Roslyn's choice, so each name may resolve or be missing, but never to the other class.
			new("DefaultWorkspace: a case variant missing from the compilation is not found, not swapped", async () => {
				
				if(!caseSensitive)
					
					return Skip.NotApplicable("names that differ only in case are one file on this file system");
				
				var found = 0;
				
				foreach(var (name, type) in new[] { ("Case/Name.cs", "UpperName"), ("Case/name.cs", "LowerName") }) {
					
					var (ok, data) = await Call(rootedCtx, "roslyn_get_file_outline", new { filePath = name, projectPath = appProject });
					string?[] types = [.. data?["types"]?.AsArray().Select(t => t?["name"]?.GetValue<string>()) ?? []];
					var notFound    = (data?["error"]?.GetValue<string>() ?? "").Contains("not found");
					
					if(!ok || !(notFound || types is [var only] && only == type))
						
						return (false, $"FAIL  ({name}: expected {type} or not found, got {data?.ToJsonString() ?? "null"})");
					
					if(!notFound)
						found++;
				}
				
				return found > 0 ? (true, "PASS") : (false, "FAIL  (neither name resolved; expected the loaded one to)");
			}),
			
			// A position-based semantic tool needs the owning project's semantic model, not just its text.
			new("DefaultWorkspace: get_symbol_info resolves in a non-default project", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_symbol_info", new { filePath = "Lib/Greeter.cs", line = 5, column = 28 });
				var json       = data?.ToJsonString() ?? "";
				
				return Verdict(ok && json.Contains("Text") && data?["error"] is null, $"expected Text, got {json}");
			}),
			
			// Editing a C# file with only filePath: the write lands and the workspace sees it.
			new("DefaultWorkspace: replace_in_code edits with only filePath", async () => {
				
				var (ok, _) = await Call(rootedCtx, "roslyn_replace_in_code", new {
					filePath    = "Lib/Greeter.cs",
					nodeKind    = "method",
					textPattern = "Greet",
					replacement = "public string Greet() => \"hello\";"
				});
				
				// The workspace's view of the file, through a content search: roslyn_read_file returns its
				// source as a raw text block, which this group's JSON-only Call helper does not see.
				var (readOk, read) = await Call(rootedCtx, "roslyn_search_files", new { pattern = "\"hello\"", mode = "literal", filePattern = "Greeter.cs" });
				var lines          = read?["matches"]?.AsArray().Select(m => m?["text"]?.GetValue<string>() ?? "") ?? [];
				
				return Verdict(ok && readOk
					&& lines.Any(l => l.Contains("\"hello\""))
					&& File.ReadAllText(fx.PathOf("Lib/Greeter.cs")).Contains("\"hello\""),
					"edit not visible in the workspace or on disk");
			}),
			
			// A file no project tracks falls back to the default solution rather than failing.
			new("DefaultWorkspace: insert_lines on an untracked file", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_insert_lines", new { filePath = "Notes.md", text = "- added", atLine = 2 });
				
				return Verdict(ok && data?["error"] is null && File.ReadAllText(fx.PathOf("Notes.md")).Contains("- added"),
					$"expected the line on disk, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// check_drift resolves the workspace before peeking, so an omitted path is a normal call.
			new("DefaultWorkspace: check_drift without projectPath", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_check_drift", new { });
				
				return Verdict(ok && data?["drifted"] is not null && data?["error"] is null,
					$"expected a drift report, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #376: a tool that works on one project, given the solution, used to answer for the
			// solution's first project (App) — and Greet, which is in Lib, was "not found". It must
			// say that it needs one project, and name them, so the agent can pass one back. Passed back
			// exactly as returned: this server's working directory is not the workspace root, so a
			// path relative to the root would resolve elsewhere (review of #378).
			new("DefaultWorkspace: a solution passed to a one-project tool is an error that lists the projects", async () => {
				
				var (ok, data)   = await Call(rootedCtx, "roslyn_get_member_body", new { symbolName = "Greet", containingType = "Greeter", projectPath = fx.PathOf("DefaultWs.slnx") });
				var found        = data?["found_projects"]?.AsArray().Select(p => p?.GetValue<string>() ?? "").ToArray() ?? [];
				var choice       = found.FirstOrDefault(p => p.EndsWith("Lib.csproj")) ?? "";
				var (libOk, lib) = await Call(rootedCtx, "roslyn_get_member_body", new { symbolName = "Greet", containingType = "Greeter", projectPath = choice });
				
				return Verdict(ok
					&& data?["error"]?.GetValue<string>() == "solution_needs_project"
					&& found.Any(p => p.EndsWith("App.csproj")) && found.Any(p => p.EndsWith("Lib.csproj"))
					&& libOk && lib?["error"] is null,
					$"expected solution_needs_project naming App and Lib, and a body through Lib.csproj; got {data?.ToJsonString() ?? "null"} / {lib?.ToJsonString() ?? "null"}");
			}),
			
			// #376: Stray.csproj lies under the solution's directory, so it loads the solution — which
			// does not contain it. It used to answer for the solution's first project.
			new("DefaultWorkspace: a .csproj the loaded solution does not contain is an error", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_member_body", new { symbolName = "Greet", projectPath = fx.PathOf("Stray/Stray.csproj") });
				
				return Verdict(ok && data?["error"]?.GetValue<string>() == "project_not_in_workspace",
					$"expected project_not_in_workspace, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #376: diagnostics for a solution cover every project — the warning is in Lib, not in the
			// first project — and a file-scoped check finds the file in whichever project holds it.
			new("DefaultWorkspace: get_diagnostics for a solution checks every project", async () => {
				
				var solution         = fx.PathOf("DefaultWs.slnx");
				var (ok, data)       = await Call(rootedCtx, "roslyn_get_diagnostics", new { projectPath = solution, severity = "warnings", take = 200 });
				var (fileOk, onFile) = await Call(rootedCtx, "roslyn_get_diagnostics", new { projectPath = solution, filePath = "Lib/Warn.cs", severity = "warnings" });
				
				static bool HasUnused(JsonNode? result)
					=> result?["items"]?.AsArray().Any(item => item?["code"]?.GetValue<string>() == "CS0168" && (item?["file"]?.GetValue<string>() ?? "").Contains("Warn.cs")) == true;
				
				return Verdict(ok && fileOk && HasUnused(data) && HasUnused(onFile),
					$"expected CS0168 in Lib/Warn.cs both ways; got {data?.ToJsonString() ?? "null"} / {onFile?.ToJsonString() ?? "null"}");
			}),
			
			// #376 review W1: a file no project has, with projectPath omitted. The default solution
			// stays, and the tool that needs a compilation must say the file is not found — not that a
			// solution was given where one project was needed.
			new("DefaultWorkspace: a file in no project is 'not found', not a solution error", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_file_outline", new { filePath = "Lib/Gretter.cs" });
				var error      = data?["error"]?.GetValue<string>() ?? "";
				
				return Verdict(ok && error.Contains("not found in any project"),
					$"expected 'not found in any project', got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #376 review W2: one entry the workspace cannot find fails that entry, not the batch.
			new("DefaultWorkspace: get_line_count reports a file in no project per entry", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_line_count", new { filePaths = "App/Consumer.cs,Lib/Nowhere.cs" });
				var files      = data?["files"]?.AsArray() ?? [];
				var counted    = files.Any(f => (f?["file"]?.GetValue<string>() ?? "").Contains("Consumer.cs") && f?["line_count"] is not null);
				var missing    = files.Any(f => (f?["file"]?.GetValue<string>() ?? "").Contains("Nowhere.cs") && f?["error"] is not null);
				
				return Verdict(ok && data?["error"] is null && counted && missing,
					$"expected Consumer.cs counted and Nowhere.cs with an error; got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #376 review W3: an explicit solution with filePath is narrowed to the file's project,
			// as the default solution is.
			new("DefaultWorkspace: an explicit solution with filePath uses the file's project", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_file_outline", new { filePath = "Lib/Greeter.cs", projectPath = fx.PathOf("DefaultWs.slnx") });
				var types      = data?["types"]?.AsArray().Select(t => t?["name"]?.GetValue<string>()).ToArray() ?? [];
				
				return Verdict(ok && types.Contains("Greeter"), $"expected the outline of Greeter, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #376 review W4: Shared.cs is App/Shared.cs and Lib/Shared.cs. Diagnosing it across the
			// solution must be ambiguous_file, not the two files' diagnostics merged.
			new("DefaultWorkspace: get_diagnostics with a solution and an ambiguous file name is ambiguous_file", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_diagnostics", new { projectPath = fx.PathOf("DefaultWs.slnx"), filePath = "Shared.cs" });
				
				return Verdict(ok && data?["error"]?.GetValue<string>() == "ambiguous_file",
					$"expected ambiguous_file, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #376 review W5: Beside.csproj lies under a solution of one project that does not list it.
			// The solution's one project used to answer for it; the solution itself still names it.
			new("DefaultWorkspace: a solution of one project still rejects a .csproj it does not list", async () => {
				
				var (ok, data)   = await Call(rootedCtx, "roslyn_get_member_body", new { symbolName = "Value", projectPath = single.PathOf("Beside/Beside.csproj") });
				var (slnOk, sln) = await Call(rootedCtx, "roslyn_get_member_body", new { symbolName = "Value", projectPath = single.PathOf("Single.slnx") });
				
				return Verdict(ok && data?["error"]?.GetValue<string>() == "project_not_in_workspace" && slnOk && sln?["error"] is null,
					$"expected project_not_in_workspace for Beside.csproj and a body through Single.slnx; got {data?.ToJsonString() ?? "null"} / {sln?.ToJsonString() ?? "null"}");
			}),
			
			// Review of #378: a project outside the solution's directory is still checked, and a project
			// with two target frameworks is checked under both — one warning only exists under each.
			new("DefaultWorkspace: get_diagnostics for a solution covers projects outside its directory and every target framework", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_diagnostics", new { projectPath = single.PathOf("Sln/Apart.slnx"), severity = "warnings", take = 200 });
				var messages   = data?["items"]?.AsArray().Select(item => item?["message"]?.GetValue<string>() ?? "").ToArray() ?? [];
				
				bool Has(string name) => messages.Any(message => message.Contains(name));
				
				return Verdict(ok && Has("outsideTheSolutionDirectory") && Has("onlyOnWindows") && Has("onlyElsewhere"),
					$"expected the warnings of Apart and of both Multi frameworks; got {data?.ToJsonString() ?? "null"}");
			}),
			
			// Review of #378: in an adhoc workspace the solution's projects have no files; the one
			// compilation is checked rather than none — "0 errors" for broken code was the result.
			new("DefaultWorkspace: get_diagnostics for a solution in an adhoc workspace still checks the code", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_diagnostics", new { projectPath = adhocSln.PathOf("Pinned.slnx"), severity = "errors", take = 200 });
				
				return Verdict(ok && data?["items"]?.AsArray().Any(item => item?["code"]?.GetValue<string>() == "CS0103") == true,
					$"expected CS0103 from Broken.cs; got {data?.ToJsonString() ?? "null"}");
			}),
			
			// Review of #378, round 2: the shared file is diagnosed in every project that compiles it —
			// P1 lacks OnlyInP2 and P2 lacks OnlyInP1 — not only in the first project found to have it.
			new("DefaultWorkspace: get_diagnostics for a file shared by two projects reports both projects' errors", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_diagnostics", new { projectPath = single.PathOf("Shared/Shared.slnx"), filePath = "Common/SharedSource.cs", severity = "errors" });
				var messages   = data?["items"]?.AsArray().Select(item => item?["message"]?.GetValue<string>() ?? "").ToArray() ?? [];
				
				return Verdict(ok && messages.Any(m => m.Contains("OnlyInP1")) && messages.Any(m => m.Contains("OnlyInP2")),
					$"expected the missing OnlyInP1 and OnlyInP2 both; got {data?.ToJsonString() ?? "null"}");
			}),
			
			// Review of #378, round 2: a preview given the solution binds its token to the project of
			// the file (P2, not the first project); applying it through the same solution must be
			// accepted, not refused as another workspace.
			new("DefaultWorkspace: a code fix previewed and applied through a solution round-trips", async () => {
				
				var solution       = single.PathOf("Shared/Shared.slnx");
				var (ok, preview)  = await Call(rootedCtx, "roslyn_preview_code_fix", new { projectPath = solution, filePath = "P2/FixMe.cs", line = 1, column = 24, diagnosticId = "CS0246", actionIndex = 0 });
				var token          = preview?["token"]?.GetValue<string>();
				
				if(!ok || token is null)
					
					return (false, $"FAIL  (preview gave no token: {preview?.ToJsonString() ?? "null"})");
				
				var (applyOk, applied) = await Call(rootedCtx, "roslyn_apply_code_fix", new { token, approval = "y", projectPath = solution });
				var text               = applied?.ToJsonString() ?? "null";
				
				return Verdict(applyOk && !text.Contains("mismatch") && applied?["error"] is null,
					$"expected the fix applied through the solution, got {text}");
			}),
			
			// #384: a rename previewed in Lib.csproj and applied through the solution. The solution used to
			// answer for its first project (App), so the token's own workspace was refused as a mismatch.
			new("DefaultWorkspace: a rename previewed in one project applies through its solution", async () => {
				
				var (ok, preview) = await Call(rootedCtx, "roslyn_preview_rename", new { symbolName = "Before", newName = "After", containingType = "ApplyTarget", projectPath = libProject });
				var token         = preview?["token"]?.GetValue<string>();
				
				if(!ok || token is null)
					
					return (false, $"FAIL  (preview gave no token: {preview?.ToJsonString() ?? "null"})");
				
				var (applyOk, applied) = await Call(rootedCtx, "roslyn_apply_rename", new { token, approval = "y", projectPath = fx.PathOf("DefaultWs.slnx") });
				var renamed            = File.ReadAllText(fx.PathOf("Lib/ApplyTarget.cs")).Contains("After(");
				
				return Verdict(applyOk && applied?["error"] is null && renamed,
					$"expected Before renamed through the solution, got {applied?.ToJsonString() ?? "null"}");
			}),
			
			// #384: the same for a signature change.
			new("DefaultWorkspace: a signature change previewed in one project applies through its solution", async () => {
				
				var (ok, preview) = await Call(rootedCtx, "roslyn_change_signature", new {
					methodName     = "Shape",
					containingType = "ApplyTarget",
					addParameters  = """[{"name":"b","type":"int","defaultValue":"0"}]""",
					projectPath    = libProject
				});
				var token = preview?["token"]?.GetValue<string>();
				
				if(!ok || token is null)
					
					return (false, $"FAIL  (preview gave no token: {preview?.ToJsonString() ?? "null"})");
				
				var (applyOk, applied) = await Call(rootedCtx, "roslyn_apply_signature_change", new { token, approval = "y", projectPath = fx.PathOf("DefaultWs.slnx") });
				var changed            = File.ReadAllText(fx.PathOf("Lib/ApplyTarget.cs")).Contains("int b");
				
				return Verdict(applyOk && applied?["error"] is null && changed,
					$"expected Shape given a parameter through the solution, got {applied?.ToJsonString() ?? "null"}");
			}),
			
			// #384: membership, not any solution. Single.slnx does not list Lib.csproj, so a Lib token
			// applied through it is still refused, and the file is left alone.
			new("DefaultWorkspace: a token applied through a solution without its project is refused", async () => {
				
				var (ok, preview) = await Call(rootedCtx, "roslyn_preview_rename", new { symbolName = "Kept", newName = "Lost", containingType = "ApplyTarget", projectPath = libProject });
				var token         = preview?["token"]?.GetValue<string>();
				
				if(!ok || token is null)
					
					return (false, $"FAIL  (preview gave no token: {preview?.ToJsonString() ?? "null"})");
				
				var (applyOk, applied) = await Call(rootedCtx, "roslyn_apply_rename", new { token, approval = "y", projectPath = single.PathOf("Single.slnx") });
				var untouched          = !File.ReadAllText(fx.PathOf("Lib/ApplyTarget.cs")).Contains("Lost(");
				
				await Call(rootedCtx, "roslyn_apply_rename", new { token, approval = "n", projectPath = libProject });
				
				return Verdict(applyOk && applied?["error"]?.GetValue<string>() == "workspace mismatch" && untouched,
					$"expected workspace mismatch, got {applied?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review: a solution of one project names that project, for the preview as for every
			// lookup; the token must bind to Only.csproj, not to the root, or no apply can match it.
			new("DefaultWorkspace: a rename previewed and applied through a solution of one project round-trips", async () => {
				
				var solution      = single.PathOf("Single.slnx");
				var (ok, preview) = await Call(rootedCtx, "roslyn_preview_rename", new { symbolName = "Old", newName = "Renewed", containingType = "SoleTarget", projectPath = solution });
				var token         = preview?["token"]?.GetValue<string>();
				
				if(!ok || token is null)
					
					return (false, $"FAIL  (preview gave no token: {preview?.ToJsonString() ?? "null"})");
				
				var (applyOk, applied) = await Call(rootedCtx, "roslyn_apply_rename", new { token, approval = "y", projectPath = solution });
				var renamed            = File.ReadAllText(single.PathOf("Only/SoleTarget.cs")).Contains("Renewed(");
				
				return Verdict(applyOk && applied?["error"] is null && renamed,
					$"expected Old renamed through Single.slnx, got {applied?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review: a workspace pinned to adhoc has no project to build, whatever the path names.
			// A solution path is still a solution in adhoc mode, so it must not reach dotnet.
			new("DefaultWorkspace: build_project refuses a solution in an adhoc workspace", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_build_project", new { projectPath = adhocSln.PathOf("Pinned.slnx"), forceBuild = true });
				var error      = data?["error"]?.GetValue<string>() ?? "";
				
				return Verdict(ok && error.Contains("MSBuildWorkspace mode"),
					$"expected the MSBuild-only refusal, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review: Stray.csproj loads the solution above it, which does not list it. Restore must
			// target Stray.csproj — the project named — not the solution's first project (App) or nothing.
			new("DefaultWorkspace: restore_packages targets a .csproj its solution does not list", async () => {
				
				var assets     = fx.PathOf("Stray/obj/project.assets.json");
				var (ok, data) = await Call(rootedCtx, "roslyn_restore_packages", new { projectPath = fx.PathOf("Stray/Stray.csproj") });
				
				return Verdict(ok && data?["error"] is null && File.Exists(assets),
					$"expected Stray restored, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review, round 2: without forceBuild too. Stray has no compilation in the loaded
			// workspace for Roslyn to check first, so the check is skipped and dotnet builds it — it used
			// to stop at project_not_in_workspace. Needs the restore above.
			new("DefaultWorkspace: build_project builds a .csproj its solution does not list", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_build_project", new { projectPath = fx.PathOf("Stray/Stray.csproj") });
				
				return Verdict(ok && data?["succeeded"]?.GetValue<bool>() == true && data?["source"]?.GetValue<string>() != "roslyn",
					$"expected a dotnet build of Stray, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review, round 2: a .csproj that only shares its name with a listed project is not that
			// project — Greet is Lib's, and must not be found through Twin/Lib.csproj.
			new("DefaultWorkspace: a .csproj that only shares a listed project's name is not that project", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_member_body", new { symbolName = "Greet", projectPath = fx.PathOf("Twin/Lib.csproj") });
				
				return Verdict(ok && data?["error"]?.GetValue<string>() == "project_not_in_workspace",
					$"expected project_not_in_workspace, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review, round 4: a link to a listed project's file in another directory is the same file,
			// but MSBuild evaluates it from Alias/, with Alias/'s globs, so it is not that project. Greet is
			// Lib's and must not be found through it.
			new("DefaultWorkspace: a link to a listed project's .csproj in another directory is not that project", async () => {
				
				if(!aliasLinked)
					
					return Skip.SetupUnavailable("cannot create symbolic links here");
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_member_body", new { symbolName = "Greet", projectPath = fx.PathOf("Alias/Lib.csproj") });
				
				return Verdict(ok && data?["error"]?.GetValue<string>() == "project_not_in_workspace",
					$"expected project_not_in_workspace, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review, round 4: being a project of its own, the link is what dotnet is given — restore
			// evaluates it from Alias/ and writes Alias/obj, as for any project the solution does not list.
			new("DefaultWorkspace: restore_packages through a link to a .csproj in another directory restores that project", async () => {
				
				if(!aliasLinked)
					
					return Skip.SetupUnavailable("cannot create symbolic links here");
				
				var (ok, data) = await Call(rootedCtx, "roslyn_restore_packages", new { projectPath = fx.PathOf("Alias/Lib.csproj") });
				
				return Verdict(ok && data?["error"] is null && File.Exists(fx.PathOf("Alias/obj/project.assets.json")),
					$"expected Alias/Lib.csproj restored in Alias/, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review, round 4: Other.slnx lists Other/Lib.csproj, a link to Lib's file that MSBuild
			// evaluates from Other/ — another project. A Lib token applied through it is refused, and Lib's
			// file is left alone; matching by file alone accepted it.
			new("DefaultWorkspace: a token is refused through a solution listing a link to its project's file elsewhere", async () => {
				
				if(!aliasLinked)
					
					return Skip.SetupUnavailable("cannot create symbolic links here");
				
				var (ok, preview) = await Call(rootedCtx, "roslyn_preview_rename", new { symbolName = "Kept", newName = "Gone", containingType = "ApplyTarget", projectPath = libProject });
				var token         = preview?["token"]?.GetValue<string>();
				
				if(!ok || token is null)
					
					return (false, $"FAIL  (preview gave no token: {preview?.ToJsonString() ?? "null"})");
				
				var (applyOk, applied) = await Call(rootedCtx, "roslyn_apply_rename", new { token, approval = "y", projectPath = fx.PathOf("Other/Other.slnx") });
				var untouched          = !File.ReadAllText(fx.PathOf("Lib/ApplyTarget.cs")).Contains("Gone(");
				
				await Call(rootedCtx, "roslyn_apply_rename", new { token, approval = "n", projectPath = libProject });
				
				return Verdict(applyOk && applied?["error"]?.GetValue<string>() == "workspace mismatch" && untouched,
					$"expected workspace mismatch, got {applied?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review, round 3: a solution that lists the token's project through a directory link
			// holds that project. Membership compared path text, so the apply was refused as a mismatch.
			new("DefaultWorkspace: a token applies through a solution that lists its project through a link", async () => {
				
				if(!dirLinked)
					
					return Skip.SetupUnavailable("cannot create symbolic links here");
				
				var (ok, preview) = await Call(rootedCtx, "roslyn_preview_rename", new { symbolName = "Early", newName = "Late", containingType = "ViaSolutionTarget", projectPath = libProject });
				var token         = preview?["token"]?.GetValue<string>();
				
				if(!ok || token is null)
					
					return (false, $"FAIL  (preview gave no token: {preview?.ToJsonString() ?? "null"})");
				
				var (applyOk, applied) = await Call(rootedCtx, "roslyn_apply_rename", new { token, approval = "y", projectPath = fx.PathOf("Linked/Linked.slnx") });
				var renamed            = File.ReadAllText(fx.PathOf("Lib/ViaSolution.cs")).Contains("Late(");
				
				return Verdict(applyOk && applied?["error"] is null && renamed,
					$"expected Early renamed through Linked.slnx, got {applied?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review, round 3: the same through the project's linked path itself — the binding
			// comparison, not the solution membership.
			new("DefaultWorkspace: a token applies through a link to its project's directory", async () => {
				
				if(!dirLinked)
					
					return Skip.SetupUnavailable("cannot create symbolic links here");
				
				var (ok, preview) = await Call(rootedCtx, "roslyn_preview_rename", new { symbolName = "Early", newName = "Late", containingType = "ViaProjectTarget", projectPath = libProject });
				var token         = preview?["token"]?.GetValue<string>();
				
				if(!ok || token is null)
					
					return (false, $"FAIL  (preview gave no token: {preview?.ToJsonString() ?? "null"})");
				
				var (applyOk, applied) = await Call(rootedCtx, "roslyn_apply_rename", new { token, approval = "y", projectPath = fx.PathOf("Linked/LibLink/Lib.csproj") });
				var renamed            = File.ReadAllText(fx.PathOf("Lib/ViaProject.cs")).Contains("Late(");
				
				return Verdict(applyOk && applied?["error"] is null && renamed,
					$"expected Early renamed through Linked/LibLink, got {applied?.ToJsonString() ?? "null"}");
			}),
			
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
	
	// ── Server lifecycle ─────────────────────────────────────────────────────
	
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
	internal static async Task<(Process process, TestContext? server)> StartServerLoggingToAsync(TestContext ctx, string root, string logPath)
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
