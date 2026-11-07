using System.Text.Json.Nodes;

/// <summary>
///     Covers a populated directory that is moved while the watchers are live (#348, PR #350).
///     Moving a directory raises one event, for the directory, and none for the files in it, and
///     a recursive watcher used to ask for no directory events at all. So a directory moved in
///     brought nothing into the workspace, and one moved out left its documents behind.
///     <para>
///         Each test first waits for the watchers (<c>SettleAsync</c>), because these are about
///         the live path. The moment before the watchers are live is
///         <see cref="WatcherStartupTests"/>' subject. Each has a fixture of its own: a
///         reload pending in one must not answer for another. The expected outcomes are the
///         ones the two workspace kinds have for any new or deleted source file: a workspace
///         without a project changes its documents in place, and a project flags a reload.
///     </para>
///     <para>
///         The rename test exists because reporting only the arrival would be worse than
///         nothing there: the old documents would stay beside the new ones, and every type in
///         the directory would be declared twice.
///     </para>
/// </summary>
static class MovedDirectoryTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		
		var adhocIn     = TestFixtures.NewAdhocDir("MoveAdhocIn");
		var adhocOut    = TestFixtures.NewAdhocDir("MoveAdhocOut");
		var adhocRename = TestFixtures.NewAdhocDir("MoveAdhocRename");
		var adhocCase   = TestFixtures.NewAdhocDir("MoveAdhocCase");
		var adhocLink   = TestFixtures.NewAdhocDir("MoveAdhocLink");
		var projIn      = TestFixtures.NewMsBuildProject("MoveProjectIn");
		var projOut     = TestFixtures.NewMsBuildProject("MoveProjectOut");
		var elsewhere   = TestFixtures.NewAdhocDir("MoveElsewhere");
		
		foreach(var fixture in new[] { adhocIn, adhocOut, adhocRename, adhocCase, adhocLink, projIn, projOut })
			fixture.Write("Beacon.cs", "class Beacon { }\n");
		
		adhocOut.Write("Moved/Inside.cs",    "class Leaves { }\n");
		projOut.Write("Moved/Inside.cs",     "class Leaves { }\n");
		adhocRename.Write("Old/Inside.cs",   "class Renamed { }\n");
		adhocCase.Write("Cased/Inside.cs",   "class CaseRenamed { }\n");
		elsewhere.Write("ForAdhoc/Inside.cs", "class Arrives { }\n");
		elsewhere.Write("ForProject/Inside.cs", "class Arrives { }\n");
		
		// The file lies under obj, which the load does not walk but the watcher covers, so it
		// is a document only under its link.
		var stored = adhocLink.Write("obj/store/Real.cs", "class OnlyLinked { }\n");
		var linked = false;
		
		try {
			
			File.CreateSymbolicLink(adhocLink.PathOf("Alias.cs"), stored);
			
			linked = true;
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
		
		// Calls a tool and returns the first content block parsed as JSON.
		Task<JsonNode?> Call(string tool, object args) => ctx.CallToolJsonAsync(tool, args);
		
		// Polls until the condition holds, for at most fifteen seconds.
		static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
		{
			
			var deadline = DateTime.UtcNow.AddSeconds(15);
			
			while(DateTime.UtcNow < deadline) {
				
				if(await condition())
					
					return true;
				
				await Task.Delay(250);
			}
			
			return false;
		}
		
		// Whether the workspace serves this file from a document of its own.
		async Task<bool> IsDocumentAsync(string? projectPath, string filePath)
			=> (await Call("roslyn_read_file", new { filePath, projectPath }))?["source"]?.GetValue<string>() == "roslyn";
		
		// Whether the workspace answers that it has no such file. Only that answer counts: a call
		// that failed for any other reason says nothing about the document.
		async Task<bool> IsNotADocumentAsync(string? projectPath, string filePath)
			=> (await Call("roslyn_read_file", new { filePath, projectPath }))?["error"]?.GetValue<string>().Contains("not found") == true;
		
		// reload_pending is omitted from the JSON when false.
		async Task<bool> ReloadPendingAsync(string? projectPath)
			=> (await Call("roslyn_check_drift", new { projectPath }))?["reload_pending"]?.GetValue<bool>() == true;
		
		// How many lines of the workspace's documents hold this text. Null when the call failed.
		async Task<int?> MatchesAsync(string projectPath, string pattern)
			=> (await Call("roslyn_search_files", new { pattern, projectPath }))?["total_matches"]?.GetValue<int>();
		
		// Edits the tracked beacon and waits for the workspace to show the edit.
		async Task<bool> AwaitEditSeenAsync(FixtureProject fixture)
		{
			
			var marker = $"Beacon{Guid.NewGuid():N}";
			
			fixture.Write("Beacon.cs", $"class {marker} {{ }}\n");
			
			return await EventuallyAsync(async () => await MatchesAsync(fixture.ProjectPath, marker) > 0);
		}
		
		// Returns once the watchers are live. An edit to a tracked file reaches the workspace
		// either through a watcher or through the catch-up pass, and that pass runs once the
		// watchers are live, so seeing the edit means they are, whichever brought it.
		// The old text is searched for first: a project reads a document's text when it is first
		// asked for, so a first search after the edit would read it from disk and prove nothing.
		async Task<bool> SettleAsync(FixtureProject fixture)
			=> await MatchesAsync(fixture.ProjectPath, "class Beacon ") > 0 && await AwaitEditSeenAsync(fixture);
		
		var tests = new List<TestCase> {
			
			new("moved directory: a directory moved in brings its source files along in a workspace without a project",
				async () => {
					
					if(!await SettleAsync(adhocIn))
						
						return (false, "FAIL  (precondition: the watchers did not go live)");
					
					Directory.Move(elsewhere.PathOf("ForAdhoc"), adhocIn.PathOf("Moved"));
					
					return await EventuallyAsync(() => IsDocumentAsync(adhocIn.Dir, "Moved/Inside.cs"))
						? (true,  "PASS")
						: (false, "FAIL  (Moved/Inside.cs is not a document fifteen seconds after its directory was moved in)");
				}),
			
			new("moved directory: a directory moved out takes its documents along in a workspace without a project",
				async () => {
					
					if(!await SettleAsync(adhocOut) || !await IsDocumentAsync(adhocOut.Dir, "Moved/Inside.cs"))
						
						return (false, "FAIL  (precondition: the watchers did not go live, or Moved/Inside.cs was not loaded)");
					
					Directory.Move(adhocOut.PathOf("Moved"), elsewhere.PathOf("FromAdhoc"));
					
					return await EventuallyAsync(() => IsNotADocumentAsync(adhocOut.Dir, "Moved/Inside.cs"))
						? (true,  "PASS")
						: (false, "FAIL  (Moved/Inside.cs is still a document fifteen seconds after its directory was moved out)");
				}),
			
			new("moved directory: a renamed directory leaves one document for each file in a workspace without a project",
				async () => {
					
					if(!await SettleAsync(adhocRename) || !await IsDocumentAsync(adhocRename.Dir, "Old/Inside.cs"))
						
						return (false, "FAIL  (precondition: the watchers did not go live, or Old/Inside.cs was not loaded)");
					
					Directory.Move(adhocRename.PathOf("Old"), adhocRename.PathOf("New"));
					
					// Searched, not read: a read of Old/Inside.cs falls back to the only file of
					// that name, which is the renamed one, and would never report it gone.
					return await EventuallyAsync(async () => {
							
							var found = await Call("roslyn_search_files", new { pattern = "class Renamed", projectPath = adhocRename.Dir });
							
							return found?["total_matches"]?.GetValue<int>() == 1
								&& found["matches"]?[0]?["file"]?.GetValue<string>().Contains("New") == true;
						})
						? (true,  "PASS")
						: (false, "FAIL  (fifteen seconds after the rename, the type is not declared exactly once, under New)");
				}),
			
			// On Windows and macOS the file still opens under the old spelling, so its existence
			// says nothing. On Linux these are two names and the test is an ordinary rename.
			new("moved directory: a directory renamed in case only leaves one document for each file in a workspace without a project",
				async () => {
					
					if(!await SettleAsync(adhocCase))
						
						return (false, "FAIL  (precondition: the watchers did not go live)");
					
					Directory.Move(adhocCase.PathOf("Cased"), adhocCase.PathOf("cased"));
					
					// An edit made after the rename: once it is seen, the rename has been handled.
					// Before that, every check below would pass on the old state.
					if(!await AwaitEditSeenAsync(adhocCase))
						
						return (false, "FAIL  (an edit made after the rename never reached the workspace)");
					
					// The compiler, not a search: a search counts paths ignoring case, so it shows
					// two documents that differ in case as one. A workspace without a project has
					// no references and never compiles cleanly. What is looked for is the one
					// error a second document causes.
					var diagnostics = await Call("roslyn_get_diagnostics", new { projectPath = adhocCase.Dir, take = 200, severity = "errors" });
					
					if(diagnostics?["items"] is not JsonArray items)
						
						return (false, $"FAIL  (no diagnostics: {diagnostics?.ToJsonString() ?? "none"})");
					
					var duplicates = items.Count(item => item?["code"]?.GetValue<string>() == "CS0101");
					
					if(duplicates > 0)
						
						return (false, $"FAIL  ({duplicates} CS0101 after the rename: the type is declared twice)");
					
					var found = await Call("roslyn_search_files", new { pattern = "class CaseRenamed", projectPath = adhocCase.Dir });
					var file  = found?["matches"]?[0]?["file"]?.GetValue<string>();
					
					// macOS keeps the document under the old spelling, which still opens: see
					// WORKSPACE_SYNC.md. The same search says nothing about how many there are.
					return OperatingSystem.IsMacOS() || file?.EndsWith($"cased{Path.DirectorySeparatorChar}Inside.cs", StringComparison.Ordinal) == true
						? (true,  "PASS")
						: (false, $"FAIL  (the document does not have the new spelling: {file ?? "no result"})");
				}),
			
			// Nothing reports Real.cs, and Alias.cs is still there: it is a link that leads
			// nowhere now. Searched, because a read of such a link is an error of its own.
			new("moved directory: a directory moved out takes the documents at links to its files along",
				async () => {
					
					if(!linked)
						
						return Skip.SetupUnavailable("cannot create symbolic links on this machine");
					
					if(!await SettleAsync(adhocLink) || await MatchesAsync(adhocLink.Dir, "class OnlyLinked") != 1)
						
						return (false, "FAIL  (precondition: the watchers did not go live, or the link was not loaded once)");
					
					Directory.Move(adhocLink.PathOf("obj/store"), elsewhere.PathOf("FromLink"));
					
					return await EventuallyAsync(async () => await MatchesAsync(adhocLink.Dir, "class OnlyLinked") == 0)
						? (true,  "PASS")
						: (false, "FAIL  (the link's document is still there fifteen seconds after the directory of its file was moved out)");
				}),
			
			new("moved directory: a directory moved in flags a reload in a project",
				async () => {
					
					if(!await SettleAsync(projIn) || await ReloadPendingAsync(projIn.Csproj))
						
						return (false, "FAIL  (precondition: the watchers did not go live, or a reload was pending already)");
					
					Directory.Move(elsewhere.PathOf("ForProject"), projIn.PathOf("Moved"));
					
					return await EventuallyAsync(() => ReloadPendingAsync(projIn.Csproj))
						? (true,  "PASS")
						: (false, "FAIL  (no reload pending fifteen seconds after the directory was moved in)");
				}),
			
			new("moved directory: a directory moved out flags a reload in a project",
				async () => {
					
					if(!await SettleAsync(projOut) || await ReloadPendingAsync(projOut.Csproj))
						
						return (false, "FAIL  (precondition: the watchers did not go live, or a reload was pending already)");
					
					Directory.Move(projOut.PathOf("Moved"), elsewhere.PathOf("FromProject"));
					
					return await EventuallyAsync(() => ReloadPendingAsync(projOut.Csproj))
						? (true,  "PASS")
						: (false, "FAIL  (no reload pending fifteen seconds after the directory was moved out)");
				}),
		};
		
		return new TestGroup($"Moved Directory ({tests.Count} tests)", tests, Teardown: () => {
			
			adhocIn.Dispose();
			adhocOut.Dispose();
			adhocRename.Dispose();
			adhocCase.Dispose();
			adhocLink.Dispose();
			projIn.Dispose();
			projOut.Dispose();
			elsewhere.Dispose();
			
			return Task.CompletedTask;
		});
	}
}
