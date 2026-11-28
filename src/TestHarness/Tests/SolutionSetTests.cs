using System.Diagnostics;
using System.Text.Json.Nodes;

/// <summary>
///     A root that holds several solutions, each below it in a directory of its own (#501): the
///     directory a session is opened in when a product is many repositories side by side. Before,
///     such a root had no default workspace, so every call without a projectPath failed, and the
///     root named outright became an AdhocWorkspace over every source file of every solution.
///     <para>
///         The fixture is such a root: <c>Alpha/Alpha.slnx</c> one level down,
///         <c>Beta/src/Beta.slnx</c> two levels down, <c>Alpha/tools/Nested/Nested.slnx</c> below
///         another solution's directory, and <c>Gamma/Gamma.slnx</c>, which no test names a file
///         of. Beta lists Alpha's project as well, as a consumer lists a contracts project it
///         references as a project, so one project file is part of two solutions. A server of
///         the group's own is started with the root as <c>--root</c>; nothing is mocked. The
///         tests run in order and each builds on what the ones before it loaded, so the group is
///         run whole.
///     </para>
/// </summary>
static class SolutionSetTests
{
	const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n";
	
	internal static async Task<TestGroup> BuildAsync(TestContext ctx)
	{
		var fx   = TestFixtures.NewAdhocDir("SolutionSet");
		var lone = TestFixtures.NewAdhocDir("SolutionSetLone");
		
		// Written before the server starts. Greet is declared on line 5, its name at column 16.
		fx.Write("README.md", "# root\n\nGreet is mentioned here too.\n");
		fx.Write("Alpha/Alpha.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
		fx.Write("Alpha/App/App.csproj", Project);
		fx.Write("Alpha/App/Greeter.cs", "namespace App;\n\npublic class Greeter\n{\n\tpublic string Greet() => \"alpha\";\n}\n");
		fx.Write("Alpha/tools/Nested/Nested.slnx", "<Solution>\n  <Project Path=\"N/N.csproj\" />\n</Solution>\n");
		fx.Write("Alpha/tools/Nested/N/N.csproj", Project);
		fx.Write("Alpha/tools/Nested/N/Inner.cs", "namespace N;\n\npublic class Inner { }\n");
		fx.Write("Beta/src/Beta.slnx", "<Solution>\n  <Project Path=\"Lib/Lib.csproj\" />\n  <Project Path=\"../../Alpha/App/App.csproj\" />\n</Solution>\n");
		fx.Write("Beta/src/Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n  <ItemGroup>\n    <ProjectReference Include=\"../../../Alpha/App/App.csproj\" />\n  </ItemGroup>\n</Project>\n");
		fx.Write("Beta/src/Lib/Worker.cs", "namespace Lib;\n\npublic class Worker\n{\n\tpublic string Work() => new App.Greeter().Greet();\n}\n");
		fx.Write("Gamma/Gamma.slnx", "<Solution>\n  <Project Path=\"Tool/Tool.csproj\" />\n</Solution>\n");
		fx.Write("Gamma/Tool/Tool.csproj", Project);
		fx.Write("Gamma/Tool/Tool.cs", "namespace Tool;\n\npublic class Hammer { }\n");
		
		// The root as a repository of its own that ignores the repositories in it by name, which
		// must not hide them from a listing or a search of the root; and a directory no solution
		// is above, for a source file written through the root alone.
		fx.Write(".gitignore", "Alpha/\nBeta/\nGamma/\n");
		fx.Write("Loose/keep.txt", "kept\n");
		
		// Five levels down, one past where solutions are looked for: no member of the set.
		fx.Write("Deep/a/b/c/d/Deep.slnx", "<Solution />\n");
		
		// The other shape: one solution below the root, which is then the default itself.
		lone.Write("Only/Only.slnx", "<Solution>\n  <Project Path=\"P/P.csproj\" />\n</Solution>\n");
		lone.Write("Only/P/P.csproj", Project);
		lone.Write("Only/P/One.cs", "namespace P;\n\npublic class One { }\n");
		
		var logDir = Path.Combine(TestFixtures.TempRoot, "logs");
		
		var (process, server)         = await DefaultWorkspaceTests.StartServerLoggingToAsync(ctx, fx.Dir, Path.Combine(logDir, $"solutionset-{Guid.NewGuid():N}.log"));
		var (loneProcess, loneServer) = await DefaultWorkspaceTests.StartServerLoggingToAsync(ctx, lone.Dir, Path.Combine(logDir, $"solutionset-lone-{Guid.NewGuid():N}.log"));
		
		Task<JsonNode?> Call(string tool, object args) => server!.CallToolJsonAsync(tool, args);
		
		static (bool pass, string msg) Verdict(bool pass, string detail, Stopwatch sw)
			=> (pass, pass ? $"PASS  [{sw.ElapsedMilliseconds}ms]" : $"FAIL  ({detail}) [{sw.ElapsedMilliseconds}ms]");
		
		static string Slashed(string? path) => (path ?? "").Replace('\\', '/');
		
		// Which solutions of the set the server holds now, as roslyn_info says it.
		async Task<string[]> LoadedAsync()
		{
			var info = await Call("roslyn_info", new { });
			
			return [..(info?["default_workspace"]?["solutions"]?.AsArray() ?? [])
				.Where(member => member?["loaded"]?.GetValue<bool>() == true)
				.Select(member => Slashed(member?["path"]?.GetValue<string>()))];
		}
		
		// Every file a search or a reference answer names, with forward slashes.
		static string[] FilesOf(JsonNode? answer, string list)
			=> [..(answer?[list]?.AsArray() ?? []).Select(group => Slashed(group?["file"]?.GetValue<string>()))];
		
		List<TestCase> tests = [
			
			// The root has no solution of its own, so there is no path an omitted projectPath
			// stands for. roslyn_info must say what there is instead: the four solutions, relative
			// to the root, none of them loaded by the question. The one five levels down is not
			// among them: the search has a depth, or a root above a large tree would walk all of it.
			new("solution set: roslyn_info lists the solutions below the root, none loaded", async () => {
				
				var sw   = Stopwatch.StartNew();
				var info = (await Call("roslyn_info", new { }))?["default_workspace"];
				
				string[] members = [..(info?["solutions"]?.AsArray() ?? []).Select(member => Slashed(member?["path"]?.GetValue<string>()))];
				
				return Verdict(info?["path"] is null && info?["unavailable"] is null
					&& members.Order().SequenceEqual(new[] { "Alpha/Alpha.slnx", "Alpha/tools/Nested/Nested.slnx", "Beta/src/Beta.slnx", "Gamma/Gamma.slnx" }.Order())
					&& (await LoadedAsync()).Length == 0,
					$"expected four solutions and none loaded; got {info?.ToJsonString() ?? "null"}", sw);
			}),
			
			// Listing, searching and reading need files and no workspace. One call must cover the
			// whole root, with paths relative to it, a hit in source under its declaration though
			// no project was loaded to parse it, and a hit in a file no solution is above. And
			// none of the three may load a solution: a search across twenty repositories that
			// loaded what it touched would take minutes and empty the cache of what is in use.
			new("solution set: list, search and read cover the whole root and load nothing", async () => {
				
				var sw     = Stopwatch.StartNew();
				var listed = await Call("roslyn_list_files", new { pattern = "**/*.cs" });
				var found  = await Call("roslyn_search_files", new { pattern = "Greet", filePattern = "*" });
				var source = await Call("roslyn_search_files", new { pattern = "Greet\\(" });
				var read   = await Call("roslyn_read_file", new { filePath = "Alpha/App/Greeter.cs" });
				
				string[] files  = [..(listed?["files"]?.AsArray() ?? []).Select(file => Slashed(file?.GetValue<string>()))];
				var      hits   = FilesOf(found, "files");
				var      inCode = source?["files"]?.AsArray().FirstOrDefault(group => Slashed(group?["file"]?.GetValue<string>()) == "Alpha/App/Greeter.cs");
				
				return Verdict(
					files.Contains("Alpha/App/Greeter.cs") && files.Contains("Beta/src/Lib/Worker.cs") && files.Contains("Gamma/Tool/Tool.cs")
					&& hits.Contains("README.md") && hits.Contains("Alpha/App/Greeter.cs") && hits.Contains("Beta/src/Lib/Worker.cs")
					&& inCode?["in"]?[0]?["declaration"]?.GetValue<string>() == "method Greet"
					&& Slashed(read?["file"]?.GetValue<string>()) == "Alpha/App/Greeter.cs" && read?["source"]?.GetValue<string>() == "disk"
					&& (await LoadedAsync()).Length == 0,
					$"expected root-relative files from every solution, 'method Greet' for the hit in source, a disk read and nothing loaded; " +
					$"listed {listed?.ToJsonString() ?? "null"} / found {found?.ToJsonString() ?? "null"} / source {source?.ToJsonString() ?? "null"} / read {read?.ToJsonString() ?? "null"} / loaded {string.Join(", ", await LoadedAsync())}", sw);
			}),
			
			// A call that needs code and names neither a file nor a solution cannot be answered,
			// and must not pick: the answer is the solutions to choose from, in the form a
			// projectPath is given in. Before, it was missing_project_path with no way forward.
			new("solution set: a call that names nothing is refused with the solutions", async () => {
				
				var sw     = Stopwatch.StartNew();
				var answer = await Call("roslyn_get_diagnostics", new { take = 0 });
				
				string[] solutions = [..(answer?["found_solutions"]?.AsArray() ?? []).Select(solution => Slashed(solution?.GetValue<string>()))];
				
				return Verdict(answer?["error"]?.GetValue<string>() == "root_needs_solution" && solutions.Length == 4 && solutions.Contains("Beta/src/Beta.slnx"),
					$"expected root_needs_solution with four found_solutions; got {answer?.ToJsonString() ?? "null"}", sw);
			}),
			
			// The same for every tool that covers a whole solution or runs dotnet on one. Each has
			// its own way to its workspace, and one that slipped past the refusal would build,
			// clean or restore the root directory, or answer for an AdhocWorkspace over all of it.
			new("solution set: every tool that covers a solution is refused the same way", async () => {
				
				var sw       = Stopwatch.StartNew();
				var failures = new List<string>();
				
				(string Tool, object Args)[] calls = [
					("roslyn_check_drift",         new { }),
					("roslyn_build_project",       new { }),
					("roslyn_restore_packages",    new { }),
					("roslyn_clean_solution",      new { }),
					("roslyn_list_types",          new { }),
					("roslyn_find_unused",         new { }),
					("roslyn_get_project_info",    new { }),
					("roslyn_semantic_search",     new { pattern = "Greet" }),
					("roslyn_find_string_literal", new { pattern = "alpha" }),
					("roslyn_find_references",     new { symbolName = "Greet" }),
				];
				
				foreach(var (tool, args) in calls) {
					
					var answer = await Call(tool, args);
					
					if(answer?["error"]?.GetValue<string>() != "root_needs_solution")
						failures.Add($"{tool}: {answer?.ToJsonString() ?? "null"}");
				}
				
				return Verdict(failures.Count == 0 && (await LoadedAsync()).Length == 0 && !Directory.Exists(fx.PathOf("obj")),
					$"expected root_needs_solution from each and nothing loaded; got {string.Join(" | ", failures)}", sw);
			}),
			
			// The mode itself: a call that names a file lands in the solution above the file
			// without naming it, and that solution alone is loaded. The path is taken relative to
			// the root and answered relative to the root, so it can go into the next call as it is.
			new("solution set: a call that names a file lands in the solution above it", async () => {
				
				var sw      = Stopwatch.StartNew();
				var outline = await Call("roslyn_get_file_outline", new { filePath = "Alpha/App/Greeter.cs" });
				var loaded  = await LoadedAsync();
				
				return Verdict(outline?["error"] is null && Slashed(outline?["file"]?.GetValue<string>()) == "Alpha/App/Greeter.cs"
					&& loaded.SequenceEqual(["Alpha/Alpha.slnx"]),
					$"expected the outline of Alpha/App/Greeter.cs and only Alpha/Alpha.slnx loaded; got {outline?.ToJsonString() ?? "null"} / loaded {string.Join(", ", loaded)}", sw);
			}),
			
			// A solution below another solution's directory: the nearest one above the file owns
			// it, which is Nested.slnx and not Alpha.slnx, three levels above.
			new("solution set: the nearest solution above the file wins over one further up", async () => {
				
				var sw      = Stopwatch.StartNew();
				var outline = await Call("roslyn_get_file_outline", new { filePath = "Alpha/tools/Nested/N/Inner.cs" });
				var loaded  = await LoadedAsync();
				
				return Verdict(outline?["error"] is null && loaded.Contains("Alpha/tools/Nested/Nested.slnx"),
					$"expected Inner.cs through Nested.slnx; got {outline?.ToJsonString() ?? "null"} / loaded {string.Join(", ", loaded)}", sw);
			}),
			
			// One project file in two loaded solutions. Beta is loaded after Alpha and lists
			// Alpha's project, so the server's index of project files now leads to Beta. A call
			// that lands in Alpha by a file of that project must still be answered from Alpha:
			// there Greet has no caller, in Beta it has Worker. Asked through Beta outright, the
			// caller is there, which shows the two answers do differ. And a solution named
			// outright holds its call the same way, with the file relative to that solution.
			new("solution set: a project that two loaded solutions share is answered from the solution the call landed in", async () => {
				
				var sw = Stopwatch.StartNew();
				
				await Call("roslyn_get_file_outline", new { filePath = "Beta/src/Lib/Worker.cs" });
				
				var landed   = await Call("roslyn_find_references", new { symbolName = "Greet", filePath = "Alpha/App/Greeter.cs", line = 5, column = 16 });
				var named    = await Call("roslyn_find_references", new { symbolName = "Greet", projectPath = "Alpha/Alpha.slnx", filePath = "App/Greeter.cs", line = 5, column = 16 });
				var consumer = await Call("roslyn_find_references", new { symbolName = "Greet", projectPath = "Beta/src/Beta.slnx", filePath = "Lib/Worker.cs", line = 5, column = 43 });
				
				return Verdict(
					landed?["error"] is null && !FilesOf(landed, "references").Any(file => file.EndsWith("Worker.cs"))
					&& named?["error"] is null && !FilesOf(named, "references").Any(file => file.EndsWith("Worker.cs"))
					&& FilesOf(consumer, "references").Any(file => file.EndsWith("Worker.cs")),
					$"expected no reference in Worker.cs from Alpha, landed or named, and one from Beta; got {landed?.ToJsonString() ?? "null"} / {named?.ToJsonString() ?? "null"} / {consumer?.ToJsonString() ?? "null"}", sw);
			}),
			
			// Edits land the same way: a source file in its solution, and a file no solution is
			// above through the root alone. Both are read back by their root-relative paths.
			new("solution set: an edit lands in the file's solution, and a file above every solution is edited through the root", async () => {
				
				var sw     = Stopwatch.StartNew();
				var code   = await Call("roslyn_replace_in_file", new { filePath = "Alpha/App/Greeter.cs", pattern = "\"alpha\"", replacement = "\"ALPHA\"" });
				var text   = await Call("roslyn_replace_in_file", new { filePath = "README.md", pattern = "# root", replacement = "# the root" });
				var onDisk = File.ReadAllText(fx.PathOf("Alpha/App/Greeter.cs")).Contains("\"ALPHA\"") && File.ReadAllText(fx.PathOf("README.md")).Contains("# the root");
				
				// Asked of Alpha's workspace, whose documents the search reads: the edit reached it.
				var inWorkspace = await Call("roslyn_search_files", new { pattern = "ALPHA", projectPath = "Alpha/Alpha.slnx" });
				
				
				// A new source file no solution is above concerns no loaded workspace. Passed to all
				// of them, each took the unknown file for its own and flagged a full reload.
				var loose = await Call("roslyn_write_file", new { filePath = "Loose/Gen.cs", content = "class Gen { }\n", createNew = true });
				var drift = await Call("roslyn_check_drift", new { projectPath = "Alpha/Alpha.slnx" });
				
				return Verdict(code?["error"] is null && text?["error"] is null && onDisk && inWorkspace?["total_matches"]?.GetValue<int>() == 1
					&& loose?["error"] is null && File.Exists(fx.PathOf("Loose/Gen.cs")) && drift?["reload_pending"]?.GetValue<bool>() != true,
					$"expected both edits on disk, the new text in Alpha's workspace, and a loose file written with no reload flagged; got {code?.ToJsonString() ?? "null"} / {text?.ToJsonString() ?? "null"} / on disk {onDisk} / workspace {inWorkspace?.ToJsonString() ?? "null"} / loose {loose?.ToJsonString() ?? "null"} / drift {drift?.ToJsonString() ?? "null"}", sw);
			}),
			
			// Loading stays lazy to the end: Gamma was listed, searched and counted among the
			// solutions, and no call named a file of it.
			new("solution set: a solution no call named a file of was never loaded", async () => {
				
				var sw     = Stopwatch.StartNew();
				var loaded = await LoadedAsync();
				
				return Verdict(loaded.Length == 3 && !loaded.Contains("Gamma/Gamma.slnx"),
					$"expected Alpha, Nested and Beta loaded and Gamma not; got {string.Join(", ", loaded)}", sw);
			}),
			
			// One solution below the root is no set: it is the default as if it stood in the
			// root, which used to be missing_project_path as well.
			new("solution set: a single solution below the root is the default workspace", async () => {
				
				var sw   = Stopwatch.StartNew();
				var info = (await loneServer!.CallToolJsonAsync("roslyn_info", new { }))?["default_workspace"];
				var list = await loneServer.CallToolJsonAsync("roslyn_list_types", new { });
				
				return Verdict(Slashed(info?["path"]?.GetValue<string>()).EndsWith("Only/Only.slnx") && info?["solutions"] is null
					&& (list?.ToJsonString() ?? "").Contains("One"),
					$"expected Only/Only.slnx as the default and its type listed; got {info?.ToJsonString() ?? "null"} / {list?.ToJsonString() ?? "null"}", sw);
			}),
		];
		
		if(server is null || loneServer is null)
			tests = [new("solution set: the servers start", () => Task.FromResult((false, "FAIL  (a server did not answer initialize)")))];
		
		return new TestGroup($"Solution Set ({tests.Count} tests)", tests, Teardown: async () => {
			
			await DefaultWorkspaceTests.StopServerAsync(process, server);
			await DefaultWorkspaceTests.StopServerAsync(loneProcess, loneServer);
			
			fx.Dispose();
			lone.Dispose();
		});
	}
}
