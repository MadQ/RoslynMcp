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
		// So is the exception for an obj directory: build output and tooling directories cannot be
		// brought back by a committed file. And an exception inside an exception's directory
		// opens one directory of a 'packages' folder there, not all of it.
		fx.Write(".madq_roslynmcp.json", "{ \"ignore\": [\"!Packages/Wanted\", \"../Outside\", \"Hidden/*\", \"!Hidden/Kept\", \"!Hidden/Kept/packages/Mine\", \"!Plain/Stay/obj\", \"Plain/Gone\", \"Plain/Stay/docs\", \"!Plain/Stay/docs/keep\"] }\n");
		
		// The root is a repository that ignores Plain by name. That has no say over a set's top
		// level, and must have none over the files directly in Plain either, though an exception
		// lies below it: only docs, which an entry names, is walked through with nothing of its own.
		fx.Write(".gitignore", "Plain/\n");
		fx.Write("Plain/Top.cs", "class TopType { }\n");
		fx.Write("Plain/Stay/docs/Doc.cs", "class DocType { }\n");
		fx.Write("Plain/Stay/docs/keep/Keep.cs", "class KeepType { }\n");
		
		// Directly in Packages, which is ignored and only walked through to reach Wanted: a
		// solution, a source file, and a config file of its own, which must not make the
		// directories below it forget the root's entries.
		fx.Write("Packages/All.slnx", "<Solution />\n");
		fx.Write("Packages/Loose.cs", "class LooseType { }\n");
		fx.Write("Packages/.madq_roslynmcp.json", "{ }\n");
		fx.Write("Hidden/Kept/packages/Mine/Mine.cs", "class MineType { }\n");
		fx.Write("Hidden/Kept/packages/Theirs/Theirs.cs", "class TheirsType { }\n");
		fx.Write("Plain/Stay/obj/Gen.cs", "class GenType { }\n");
		
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
				
				// The finer cases, in both answers: the one directory an inner exception opens and
				// not its sibling; nothing that lies directly in a directory that is only walked
				// through; and nothing of an obj directory, whose exception was refused.
				foreach(var (answer, paths) in new[] { ("listing", files), ("search", hits) }) {
					
					string[] wrong = [
						..new[] { "Hidden/Kept/packages/Theirs/Theirs.cs", "Packages/Loose.cs", "Plain/Stay/obj/Gen.cs", "Plain/Stay/docs/Doc.cs" }.Where(paths.Contains).Select(path => $"{path} is shown"),
						..new[] { "Hidden/Kept/packages/Mine/Mine.cs", "Plain/Top.cs", "Plain/Stay/docs/keep/Keep.cs" }.Where(path => !paths.Contains(path)).Select(path => $"{path} is missing"),
					];
					
					if(wrong.Length > 0)
						
						return Verdict(false, $"{answer}: {string.Join(", ", wrong)}; got {listed?.ToJsonString() ?? "null"} / {found?.ToJsonString() ?? "null"}", sw);
				}
				
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
			new("ignore paths: a solution or a directory named outright is still answered", async () => {
				
				var sw    = Stopwatch.StartNew();
				var types = await Call("roslyn_list_types", new { projectPath = "Plain/Gone/Gone.slnx" });
				
				// The same for the files directly in a directory that is only walked through: a
				// pattern that names the directory lists them, as it lists a hidden one's.
				var loose = await Call("roslyn_list_files", new { pattern = "Packages/*.cs" });
				var other = await Call("roslyn_list_files", new { pattern = "Packages/Other/**/*.cs" });
				
				string[] looseFiles = [..(loose?["files"]?.AsArray() ?? []).Select(file => Slashed(file?.GetValue<string>()))];
				string[] otherFiles = [..(other?["files"]?.AsArray() ?? []).Select(file => Slashed(file?.GetValue<string>()))];
				
				// Only what was hidden and named is opened. A pattern that names a directory which
				// was visible anyway must leave the entries below it standing: 'Hidden/**' still
				// hides Hidden/A, and 'Plain/**' still hides Plain/Gone and the files of docs.
				var underVisible = await Call("roslyn_list_files", new { pattern = "{Hidden,Plain}/**/*.cs" });
				
				string[] visibleFiles = [..(underVisible?["files"]?.AsArray() ?? []).Select(file => Slashed(file?.GetValue<string>()))];
				
				string[] leaked = [..visibleFiles.Where(file => file.StartsWith("Hidden/A/") || file.StartsWith("Plain/Gone/") || file == "Plain/Stay/docs/Doc.cs" || file.StartsWith("Hidden/Kept/packages/Theirs/"))];
				
				if(leaked.Length > 0 || !visibleFiles.Contains("Hidden/Kept/P/Kept.cs") || !visibleFiles.Contains("Plain/Stay/P/Stay.cs"))
					
					return Verdict(false, $"expected a pattern naming a visible directory to keep the entries below it; leaked {string.Join(", ", leaked)}; got {underVisible?.ToJsonString() ?? "null"}", sw);
				
				// Nor does a directory that is only walked through open its hidden children by being
				// named on the way to something else. Packages/Wanted/Other.cs does not exist; the
				// lookup of a file by its name must not find Other.cs in the hidden sibling instead.
				var stray = await Call("roslyn_read_file", new { filePath = "Packages/Wanted/Other.cs" });
				
				if(stray?["error"] is null)
					
					return Verdict(false, $"expected Packages/Wanted/Other.cs not to be found; got {stray?.ToJsonString() ?? "null"}", sw);
				
				return Verdict(types?["error"] is null && (types?.ToJsonString() ?? "").Contains("GoneType")
					&& looseFiles.SequenceEqual(["Packages/Loose.cs"]) && otherFiles.SequenceEqual(["Packages/Other/P/Other.cs"]),
					$"expected GoneType from the solution named outright, and the named directories' files; got {types?.ToJsonString() ?? "null"} / {loose?.ToJsonString() ?? "null"} / {other?.ToJsonString() ?? "null"}", sw);
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
