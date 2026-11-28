using System.Diagnostics;
using System.Text.Json.Nodes;

/// <summary>
///     Paths, one-level wildcards and exceptions in a config file's <c>ignore</c> list (#543). The
///     list took bare directory names, which mean a name at any depth, so it could not say "this
///     one directory", nor "everything in here but one". And a directory named <c>packages</c> is
///     ignored by a built-in rule (the legacy NuGet folder), so a repository called
///     <c>Packages</c> under a root of several solutions was invisible whole, with no way to get
///     one of its solutions back.
///     <para>
///         The fixture is a root of several solutions (#501) with all three cases, and a server
///         of its own started with that root. <c>Packages/</c> is hidden by the built-in name and
///         <c>!Packages/Wanted</c> takes one of its two solutions back; <c>Hidden/*</c> hides the
///         directories of <c>Hidden</c> and <c>!Hidden/Kept</c> keeps one; <c>Plain/Gone</c>
///         hides one directory and leaves <c>Plain/Stay</c> beside it. Each directory holds a
///         solution with one project and one type named after it, so what is seen and what is
///         not can be told by name. Nothing is mocked.
///     </para>
/// </summary>
static class IgnorePathTests
{
	const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n";
	
	internal static async Task<TestGroup> BuildAsync(TestContext ctx)
	{
		var fx = TestFixtures.NewAdhocDir("IgnorePaths");
		
		// The second entry is refused and dropped (a path may not leave the file's directory),
		// and must not take the others with it.
		fx.Write(".madq_roslynmcp.json", "{ \"ignore\": [\"!Packages/Wanted\", \"../Outside\", \"Hidden/*\", \"!Hidden/Kept\", \"Plain/Gone\"] }\n");
		
		foreach(var directory in (string[]) ["Packages/Wanted", "Packages/Other", "Hidden/A", "Hidden/Kept", "Plain/Gone", "Plain/Stay"]) {
			
			var name = Path.GetFileName(directory);
			
			fx.Write($"{directory}/{name}.slnx", "<Solution>\n  <Project Path=\"P/P.csproj\" />\n</Solution>\n");
			fx.Write($"{directory}/P/P.csproj", Project);
			fx.Write($"{directory}/P/{name}.cs", $"namespace {name};\n\npublic class {name}Type {{ }}\n");
		}
		
		var (process, server) = await DefaultWorkspaceTests.StartServerLoggingToAsync(ctx, fx.Dir, Path.Combine(TestFixtures.TempRoot, "logs", $"ignorepaths-{Guid.NewGuid():N}.log"));
		
		Task<JsonNode?> Call(string tool, object args) => server!.CallToolJsonAsync(tool, args);
		
		static (bool pass, string msg) Verdict(bool pass, string detail, Stopwatch sw)
			=> (pass, pass ? $"PASS  [{sw.ElapsedMilliseconds}ms]" : $"FAIL  ({detail}) [{sw.ElapsedMilliseconds}ms]");
		
		static string Slashed(string? path) => (path ?? "").Replace('\\', '/');
		
		// The three directories that must be seen, and the three that must not.
		string[] seen   = ["Packages/Wanted", "Hidden/Kept", "Plain/Stay"];
		string[] unseen = ["Packages/Other", "Hidden/A", "Plain/Gone"];
		
		bool SeesExactly(IEnumerable<string> paths, out string problem)
		{
			string[] all = [..paths];
			
			var missing = seen.Where(directory => !all.Any(path => path.StartsWith(directory + "/"))).ToArray();
			var leaked  = unseen.Where(directory => all.Any(path => path.StartsWith(directory + "/"))).ToArray();
			
			problem = $"missing {string.Join(", ", missing)}; leaked {string.Join(", ", leaked)}";
			
			return missing.Length == 0 && leaked.Length == 0;
		}
		
		List<TestCase> tests = [
			
			// The solutions of the set are the ones in directories that are not ignored. The one
			// under Packages is there though a built-in name hides its parent, because an
			// exception names it; its sibling is not, because the parent is still hidden. The
			// wildcard hides the directories of Hidden but for the one excepted, and the plain
			// path hides one directory and not the one beside it.
			new("ignore paths: the solutions below the root are the ones the paths leave", async () => {
				
				var sw   = Stopwatch.StartNew();
				var info = (await Call("roslyn_info", new { }))?["default_workspace"];
				
				string[] members = [..(info?["solutions"]?.AsArray() ?? []).Select(member => Slashed(member?["path"]?.GetValue<string>()))];
				
				return Verdict(SeesExactly(members, out var problem) && members.Length == 3,
					$"expected the solutions of {string.Join(", ", seen)} and no other; {problem}; got {info?.ToJsonString() ?? "null"}", sw);
			}),
			
			// An ignored directory is as if it were not there: a listing and a search of the root
			// show nothing of it, by name or by content.
			new("ignore paths: listing and search leave the ignored directories out", async () => {
				
				var sw     = Stopwatch.StartNew();
				var listed = await Call("roslyn_list_files", new { pattern = "**/*.cs" });
				var found  = await Call("roslyn_search_files", new { pattern = "Type" });
				
				string[] files = [..(listed?["files"]?.AsArray() ?? []).Select(file => Slashed(file?.GetValue<string>()))];
				string[] hits  = [..(found?["files"]?.AsArray() ?? []).Select(group => Slashed(group?["file"]?.GetValue<string>()))];
				
				var listedRight = SeesExactly(files, out var listProblem);
				var foundRight  = SeesExactly(hits, out var searchProblem);
				
				return Verdict(listedRight && foundRight,
					$"listing: {listProblem}; search: {searchProblem}; got {listed?.ToJsonString() ?? "null"} / {found?.ToJsonString() ?? "null"}", sw);
			}),
			
			// A call that names a file lands in its solution where that is a member, and in none
			// where the solution is ignored: the way up from a file passes an ignored directory
			// as the way down from the root never reaches it. Before, only the way down knew the
			// ignore list, and a file in an ignored directory still loaded that directory's
			// solution. A symbol of an ignored solution is not offered as one it "could be in".
			new("ignore paths: a file lands in a solution that is seen, and in none that is ignored", async () => {
				
				var sw      = Stopwatch.StartNew();
				var wanted  = await Call("roslyn_get_file_outline", new { filePath = "Packages/Wanted/P/Wanted.cs" });
				var other   = await Call("roslyn_get_file_outline", new { filePath = "Packages/Other/P/Other.cs" });
				var symbol  = await Call("roslyn_find_references", new { symbolName = "OtherType" });
				var info    = (await Call("roslyn_info", new { }))?["default_workspace"];
				
				string[] loaded = [..(info?["solutions"]?.AsArray() ?? [])
					.Where(member => member?["loaded"]?.GetValue<bool>() == true)
					.Select(member => Slashed(member?["path"]?.GetValue<string>()))];
				
				return Verdict(wanted?["error"] is null && Slashed(wanted?["file"]?.GetValue<string>()) == "Packages/Wanted/P/Wanted.cs"
					&& other?["error"]?.GetValue<string>() == "root_needs_solution"
					&& symbol?["error"]?.GetValue<string>() == "symbol_not_in_loaded_solutions" && symbol?["found_solutions"] is null
					&& loaded.SequenceEqual(["Packages/Wanted/Wanted.slnx"]),
					$"expected Wanted.cs outlined, Other.cs with no solution, OtherType offered nowhere, and only Wanted loaded; got {wanted?.ToJsonString() ?? "null"} / {other?.ToJsonString() ?? "null"} / {symbol?.ToJsonString() ?? "null"} / loaded {string.Join(", ", loaded)}", sw);
			}),
			
			// Ignoring hides, it does not lock: a solution named outright is still loaded and
			// answered, as bin and obj are listed when a pattern names them.
			new("ignore paths: a solution named outright is still answered", async () => {
				
				var sw    = Stopwatch.StartNew();
				var types = await Call("roslyn_list_types", new { projectPath = "Plain/Gone/Gone.slnx" });
				
				return Verdict(types?["error"] is null && (types?.ToJsonString() ?? "").Contains("GoneType"),
					$"expected GoneType from the solution named outright; got {types?.ToJsonString() ?? "null"}", sw);
			}),
		];
		
		if(server is null)
			tests = [new("ignore paths: the server starts", () => Task.FromResult((false, "FAIL  (the server did not answer initialize)")))];
		
		return new TestGroup($"Ignore Paths ({tests.Count} tests)", tests, Teardown: async () => {
			
			await DefaultWorkspaceTests.StopServerAsync(process, server);
			
			fx.Dispose();
		});
	}
}
