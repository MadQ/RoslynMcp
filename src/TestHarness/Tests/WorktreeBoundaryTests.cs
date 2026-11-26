using System.Text.Json.Nodes;

/// <summary>
///     #511: a linked git worktree inside the checkout it belongs to is a second copy of every
///     file under the workspace root. The server walked it as part of the workspace: a file named
///     by the end of its path was "not found" because two files matched, and listings and
///     searches answered for both trees. It now reads git's own records of its worktrees and
///     stays out of them, and says "ambiguous" with the files when a path does end several.
/// </summary>
/// <remarks>
///     The fixtures lay the records out by hand, in the two files git writes for a worktree:
///     <c>.git/worktrees/&lt;name&gt;/gitdir</c> with the path of the worktree's <c>.git</c>
///     file, and that file with <c>gitdir: …</c> pointing back. No <c>git</c> is run, so the
///     group needs none on the machine and skips nowhere. The project fixture keeps its worktree
///     under a directory whose name starts with a dot, which the SDK's default items leave out,
///     and puts no C# in it: what a project compiles is not what is tested here.
/// </remarks>
static class WorktreeBoundaryTests
{
	// Makes relative a linked worktree of the repository at the fixture's root, as git records one.
	static void LinkWorktree(FixtureProject fx, string name, string relative)
	{
		var record = fx.PathOf($".git/worktrees/{name}");
		
		fx.Write($".git/worktrees/{name}/gitdir", fx.PathOf($"{relative}/.git").Replace('\\', '/') + "\n");
		fx.Write($".git/worktrees/{name}/commondir", "../..\n");
		fx.Write($"{relative}/.git", $"gitdir: {record.Replace('\\', '/')}\n");
	}
	
	internal static TestGroup Build(TestContext ctx)
	{
		var fx    = TestFixtures.NewMsBuildProject("Worktrees");
		var adhoc = TestFixtures.NewAdhocDir("WorktreesAdhoc");
		
		fx.Write(".git/HEAD", "ref: refs/heads/main\n");
		fx.Write("Code.cs", "class Code { }\n");
		fx.Write("conf/Settings.json", "{ \"where\": \"checkout\" }\n");
		fx.Write("notes.txt", "worktree-marker in the checkout\n");
		
		// The worktree: the same files a second time.
		LinkWorktree(fx, "wt", ".trees/wt");
		fx.Write(".trees/wt/conf/Settings.json", "{ \"where\": \"worktree\" }\n");
		fx.Write(".trees/wt/notes.txt", "worktree-marker in the worktree\n");
		
		// A record whose worktree is gone, and an ordinary directory where it was: git keeps the
		// record until it is pruned.
		fx.Write(".git/worktrees/gone/gitdir", fx.PathOf("plain/.git").Replace('\\', '/') + "\n");
		fx.Write("plain/kept.txt", "worktree-marker in a plain directory\n");
		
		// A nested clone (a .git directory) and a submodule (a .git file that leads to
		// .git/modules): neither is a worktree, and both are often inputs of the tree above.
		fx.Write("clone/.git/HEAD", "ref: refs/heads/main\n");
		fx.Write("clone/kept.txt", "worktree-marker in a nested clone\n");
		fx.Write("sub/.git", "gitdir: ../.git/modules/sub\n");
		fx.Write("sub/kept.txt", "worktree-marker in a submodule\n");
		
		// Two files that one path ends, both in the checkout.
		fx.Write("left/Dup.json", "{ \"side\": \"left\" }\n");
		fx.Write("right/Dup.json", "{ \"side\": \"right\" }\n");
		
		// An AdhocWorkspace finds its sources itself: the worktree's copy of a type would
		// declare it twice.
		adhoc.Write(".git/HEAD", "ref: refs/heads/main\n");
		adhoc.Write("Thing.cs", "class Thing { }\n");
		LinkWorktree(adhoc, "wt", "trees/wt");
		adhoc.Write("trees/wt/Thing.cs", "class Thing { }\n");
		
		Task<JsonNode?> Call(string tool, object args) => ctx.CallToolJsonAsync(tool, args);
		
		static (bool, string) Verdict(bool pass, JsonNode? answer) => pass
			? (true, "PASS")
			: (false, $"FAIL  ({answer?.ToJsonString() ?? "null"})");
		
		// The files of a listing, forward-slashed.
		async Task<(string[] Files, JsonNode? Answer)> ListAsync(string pattern)
		{
			var answer = await Call("roslyn_list_files", new { pattern, take = 200, projectPath = fx.Csproj });
			
			return ([..(answer?["files"]?.AsArray() ?? []).Select(file => file!.GetValue<string>().Replace('\\', '/'))], answer);
		}
		
		var tests = new List<TestCase> {
			
			// The case of the issue. The path ends two files, one in the checkout and one in the
			// worktree; the worktree is not looked in, so it names one.
			new("worktrees: a file named by the end of its path is the checkout's, not ambiguous with the worktree's copy", async () => {
				
				// The first block of the answer: the header, which names the file that was read.
				var header = await Call("roslyn_read_file", new { filePath = "Settings.json", projectPath = fx.Csproj });
				var file   = (header?["file"]?.GetValue<string>() ?? "").Replace('\\', '/');
				
				return Verdict(header?["error"] is null && file.EndsWith("conf/Settings.json") && !file.Contains(".trees"), header);
			}),
			
			// A listing is of the checkout. The directory of a stale record, the nested clone
			// and the submodule are part of it.
			new("worktrees: a listing leaves the worktree out, and keeps a stale record's directory, a clone and a submodule", async () => {
				
				var (files, answer) = await ListAsync("**/*.txt");
				
				return Verdict(files.Order(StringComparer.Ordinal).SequenceEqual(["clone/kept.txt", "notes.txt", "plain/kept.txt", "sub/kept.txt"]), answer);
			}),
			
			// As with every directory the server stays out of: a pattern that names it enters it.
			new("worktrees: a pattern that names the worktree's directory lists it", async () => {
				
				var (files, answer) = await ListAsync(".trees/wt/**/*.txt");
				
				return Verdict(files is [".trees/wt/notes.txt"], answer);
			}),
			
			// The search of files on disk enters what a listing enters.
			new("worktrees: a search of disk files does not find the worktree's copy", async () => {
				
				var answer = await Call("roslyn_search_files", new { pattern = "worktree-marker", filePattern = "notes.txt", projectPath = fx.Csproj });
				
				return Verdict(SearchHits.Of(answer) is [{ Text: "worktree-marker in the checkout" }], answer);
			}),
			
			// The records are looked at again on every call, so a worktree made while the server
			// runs is seen without a restart: the second listing is of the same session.
			new("worktrees: a worktree added during the session is left out from then on", async () => {
				
				fx.Write("later/notes.txt", "worktree-marker in a later worktree\n");
				
				var (before, _) = await ListAsync("**/notes.txt");
				
				LinkWorktree(fx, "later", "later");
				
				var (after, answer) = await ListAsync("**/notes.txt");
				
				return Verdict(before.Contains("later/notes.txt") && after is ["notes.txt"], answer);
			}),
			
			// Two files of the checkout that one path ends. "File not found" was the answer, the
			// same as for a path that is misspelled; it is now the two files.
			new("worktrees: a path that ends two files is ambiguous, with both, not 'not found'", async () => {
				
				var read = await Call("roslyn_read_file", new { filePath = "Dup.json", projectPath = fx.Csproj });
				var edit = await Call("roslyn_replace_in_file", new { filePath = "Dup.json", pattern = "side", replacement = "edge", projectPath = fx.Csproj });
				
				static bool Both(JsonNode? answer) => answer?["error"]?.GetValue<string>() == "ambiguous_file"
					&& answer["found_in"]?.AsArray().Select(file => file!.GetValue<string>().Replace('\\', '/')).SequenceEqual(["left/Dup.json", "right/Dup.json"]) == true;
				
				return Verdict(Both(read) && Both(edit)
					&& File.ReadAllText(fx.PathOf("left/Dup.json")).Contains("side")
					&& File.ReadAllText(fx.PathOf("right/Dup.json")).Contains("side"), new JsonArray(read?.DeepClone(), edit?.DeepClone()));
			}),
			
			// A path with a directory that leads to neither file ends none of them: files that
			// share only their name are not candidates for it, and a new file may take that name.
			new("worktrees: a path that ends no file is not found, whatever shares its name", async () => {
				
				var read    = await Call("roslyn_read_file", new { filePath = "middle/Dup.json", projectPath = fx.Csproj });
				var created = await Call("roslyn_write_file", new { filePath = "middle/Dup.json", content = "{ }\n", createNew = true, projectPath = fx.Csproj });
				
				return Verdict(read?["error"]?.GetValue<string>()?.StartsWith("File not found") == true
					&& created?["error"] is null
					&& File.Exists(fx.PathOf("middle/Dup.json")), new JsonArray(read?.DeepClone(), created?.DeepClone()));
			}),
			
			// Another session saves a C# file in its worktree. Here that was an unknown source
			// file under the root, and so a full reload, at every save. No project of this
			// workspace has anything loaded from the worktree, so the change is not its business.
			// The control is a new file in the checkout: it must flag a reload, or the first half
			// would pass with a watcher that saw nothing. Last of the project's tests, since it
			// leaves that reload pending.
			new("worktrees: a file saved in a worktree flags no reload, one saved in the checkout does", async () => {
				
				// The answer leaves reload_pending out when nothing is pending; null is a call that failed.
				async Task<bool?> PendingAsync()
				{
					var drift = await Call("roslyn_check_drift", new { projectPath = fx.Csproj });
					
					return drift is null || drift["error"] is not null ? null : drift["reload_pending"]?.GetValue<bool>() ?? false;
				}
				
				// The workspace is loaded and watched before the files appear.
				if(await PendingAsync() != false)
					
					return (false, $"FAIL  (a reload was pending before the test wrote anything: {(await Call("roslyn_check_drift", new { projectPath = fx.Csproj }))?.ToJsonString()})");
				
				fx.Write(".trees/wt/Saved.cs", "class Saved { }\n");
				
				// Well past the watcher's debounce of 300 ms.
				await Task.Delay(1500);
				
				var afterWorktree = await PendingAsync();
				
				fx.Write("Fresh.cs", "class Fresh { }\n");
				
				var afterCheckout = false;
				
				for(var attempt = 0; attempt < 40 && !afterCheckout; attempt++) {
					
					await Task.Delay(250);
					
					afterCheckout = await PendingAsync() == true;
				}
				
				return afterWorktree == false && afterCheckout
					? (true, "PASS")
					: (false, $"FAIL  (reload pending after the worktree's file: {afterWorktree}; after the checkout's: {afterCheckout})");
			}),
			

			// The AdhocWorkspace compiles what its own walk finds. With the worktree's copy it
			// had every type twice (CS0101). It has no references, so it has errors of its own
			// about predefined types; the duplicate is the one asked about.
			new("worktrees: an AdhocWorkspace does not load the sources of a worktree below its root", async () => {
				
				var diagnostics = await Call("roslyn_get_diagnostics", new { projectPath = adhoc.Dir });
				var types       = await Call("roslyn_list_types", new { projectPath = adhoc.Dir });
				
				return Verdict(diagnostics?["files"]?.AsArray() is [var only]
					&& only?["file"]?.GetValue<string>() == "Thing.cs"
					&& !diagnostics.ToJsonString().Contains("CS0101")
					&& types?["types"]?.AsArray().Count == 1, new JsonArray(diagnostics?.DeepClone(), types?.DeepClone()));
			}),
		};
		
		return new TestGroup($"Worktree Boundaries ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); adhoc.Dispose(); return Task.CompletedTask; });
	}
}
