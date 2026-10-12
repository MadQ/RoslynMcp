using System.Text.Json.Nodes;

/// <summary>
///     Covers what happens to the documents that are symbolic links when the workspace is
///     loaded, and when the file behind a link goes away (#343). <c>LinkWriteSyncTests</c>
///     covers the other half: a file that <em>changes</em>.
///     <list type="bullet">
///         <item>
///             <b>One document per file at load.</b> A workspace without a project takes every
///             <c>.cs</c> it finds for a document. A link to a source file is the same file
///             again, so loading both compiled it twice and every type in it was declared
///             twice (CS0101). The fixture has both shapes: <c>Alias.cs</c> links to
///             <c>Real.cs</c> in the same tree, and <c>OutA.cs</c> and <c>OutB.cs</c> both link
///             to one file outside the tree. The test asks for the errors right after the
///             load and expects no duplicate type; it also checks that the file is still
///             there under one name, so that "no error" cannot be had by loading nothing.
///         </item>
///         <item>
///             <b>A deleted file takes its links' documents with it.</b> The watcher reports
///             the delete under the file's own name. When that file is not a document itself
///             but a link to it is, nothing used to happen: the document kept the deleted
///             content for good. In a project that is now what deleting any tracked source
///             is — a pending reload; in a workspace without a project the document is
///             removed. Both tests delete the file on disk and poll, because the report is
///             debounced, and fail only when nothing has happened after ten seconds.
///         </item>
///     </list>
///     <para>
///         The file behind the link has to be one that is <em>not</em> a document, or the
///         existing handling of a deleted document would pass the test on its own. In the
///         project it is excluded with <c>Compile Remove</c>; in the workspace without a
///         project it lies under <c>obj</c>, which the loading walk stays out of but the
///         watcher covers.
///     </para>
///     <para>
///         Creating a link needs a privilege Windows grants only to administrators or in
///         developer mode. Where it is refused the fixture cannot be built: the tests are
///         reported as skipped on a developer's machine and as failed on a CI runner (see
///         <see cref="Skip"/>), never as passed.
///     </para>
/// </summary>
static class LinkedFileTests
{
	/// <param name="ctx">The harness's connection to the server.</param>
	/// <param name="serverLogDir">Directory the harness pointed ROSLYNMCP_LOG_PATH at.</param>
	/// <param name="serverLogGlob">File pattern of the server's log in that directory.</param>
	internal static TestGroup Build(TestContext ctx, string serverLogDir, string serverLogGlob)
	{
		
		var loose   = TestFixtures.NewAdhocDir("LinkedFilesLoad");
		var outside = TestFixtures.NewAdhocDir("LinkedFilesOutside");
		var fading  = TestFixtures.NewAdhocDir("LinkedFilesFading");
		var project = TestFixtures.NewMsBuildProject("LinkedFilesDelete", extraProjectXml: """
			<ItemGroup>
			  <Compile Remove="store/**" />
			</ItemGroup>
			""");
		
		var real      = loose.Write("Real.cs",         "class LoadedOnce { }\n");
		var apart     = outside.Write("Apart.cs",      "class KeptOutside { }\n");
		var generated = fading.Write("obj/Made.cs",    "class MadeElsewhere { }\n");
		var stored    = project.Write("store/Gone.cs", "class StoredApart { }\n");
		
		// For the links that change while the server runs. Both targets are kept out of the
		// compilation, so that only the link is a document.
		var moving = TestFixtures.NewMsBuildProject("LinkedFilesRepoint", extraProjectXml: """
			<ItemGroup>
			  <Compile Remove="store/**" />
			</ItemGroup>
			""");
		
		var first   = moving.Write("store/One.cs", "class FirstTarget { }\n");
		var second  = moving.Write("store/Two.cs", "class SecondTarget { }\n");
		var plain   = moving.Write("Plain.cs",     "class PlainFile { }\n");
		var pointer = moving.PathOf("Pointer.cs");
		
		// One ordinary source file per project, for SettleAsync to probe the watcher with.
		var projectBeacon = project.Write("Beacon.cs", "class BeaconOfDelete { }\n");
		var movingBeacon  = moving.Write("Beacon.cs",  "class BeaconOfRepoint { }\n");
		
		var links = new[] {
			(Link: loose.PathOf("Alias.cs"),  Target: real),
			(Link: loose.PathOf("OutA.cs"),   Target: apart),
			(Link: loose.PathOf("OutB.cs"),   Target: apart),
			(Link: fading.PathOf("Made.cs"),  Target: generated),
			(Link: project.PathOf("Here.cs"), Target: stored),
			(Link: pointer,                   Target: first),
		};
		
		var linked = false;
		
		try {
			
			foreach(var (link, target) in links)
				File.CreateSymbolicLink(link, target);
			
			linked = true;
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
		
		// Calls a tool and returns its content blocks' text, or an empty array when the call failed outright.
		async Task<string[]> CallBlocks(string tool, object args)
		{
			
			await ctx.SendAsync(new { jsonrpc = "2.0", id = ctx.NextId(), method = "tools/call",
				@params = new { name = tool, arguments = args } });
			
			var resp = await ctx.ReceiveAsync();
			
			return resp?["result"]?["content"]?.AsArray()
				.Select(block => block?["text"]?.GetValue<string>() ?? "")
				.ToArray()
				?? []
			;
		}
		
		// The first block parsed as JSON: a tool's result, or the header of a source response.
		async Task<JsonNode?> Call(string tool, object args)
		{
			
			var blocks = await CallBlocks(tool, args);
			
			if(blocks.Length == 0)
				
				return null;
			
			try { return JsonNode.Parse(blocks[0]); }
			catch { return null; }
		}
		
		// Whether the workspace serves this file from a document of its own.
		async Task<bool> IsDocumentAsync(string? projectPath, string filePath)
			=> (await Call("roslyn_read_file", new { filePath, projectPath }))?["source"]?.GetValue<string>() == "roslyn";
		
		// "pending", "none", or "unknown" when the probe itself failed. reload_pending is
		// omitted from the JSON when false.
		async Task<string> ReloadStateAsync(string? projectPath)
		{
			
			var data = await Call("roslyn_check_drift", new { projectPath });
			
			if(data?["drifted"] is null)
				
				return "unknown";
			
			return data["reload_pending"]?.GetValue<bool>() == true ? "pending" : "none";
		}
		
		// Loads the workspace and waits until no reload is pending, so that a flag seen later
		// was raised by the test's own change. Then makes sure the watchers are live: they
		// start on a background thread, and a change made before that is reported by nobody
		// (macOS takes long enough for a test to fall into that). The probe is an edit of an
		// ordinary tracked file, which is applied in place and reloads nothing — once the
		// workspace serves the edited text, the edit was reported, or picked up by the
		// catch-up pass that runs the moment the watchers go live. Either way they are live.
		async Task<bool> SettleAsync(string? projectPath, string beaconPath)
		{
			
			var deadline = DateTime.UtcNow.AddSeconds(30);
			var settled  = false;
			
			while(DateTime.UtcNow < deadline && !settled) {
				
				await Call("roslyn_get_diagnostics", new { projectPath, take = 0, severity = "errors" });
				
				settled = await ReloadStateAsync(projectPath) == "none";
				
				if(!settled)
					await Task.Delay(250);
			}
			
			if(!settled)
				
				return false;
			
			var marker = $"// live {Guid.NewGuid():N}";
			
			File.AppendAllText(beaconPath, marker + "\n");
			
			while(DateTime.UtcNow < deadline) {
				
				await Task.Delay(250);
				
				var blocks = await CallBlocks("roslyn_read_file", new { filePath = Path.GetFileName(beaconPath), projectPath });
				
				if(blocks.Length > 1 && blocks[0].Contains("\"roslyn\"") && blocks[1].Contains(marker))
					
					return true;
			}
			
			return false;
		}
		
		// The same for a workspace without a project, where a new file is a new document and
		// reloads nothing: a probe file is created every half second until one of them has
		// become a document. A single probe is not enough — created right after the load, it
		// can itself come before the watchers are live, and the catch-up pass does not look
		// for new files.
		async Task<bool> AwaitLiveAsync(FixtureProject fixture)
		{
			
			var deadline = DateTime.UtcNow.AddSeconds(20);
			
			for(var n = 1; DateTime.UtcNow < deadline; n++) {
				
				File.WriteAllText(fixture.PathOf($"Probe{n}.cs"), $"class WatcherIsLive{n} {{ }}\n");
				
				await Task.Delay(500);
				
				if(await IsDocumentAsync(fixture.Dir, $"Probe{n}.cs"))
					
					return true;
			}
			
			return false;
		}
		
		// Whether the server's log says a reload was flagged because the link at this file
		// changed. reload_pending alone would be satisfied by any reload reason — a document
		// reported as deleted, say — and the test is about this one.
		bool FlaggedAsLinkChange(string fixtureLabel, string fileName)
			=> WorkspaceHygieneTests.TryReadLog(serverLogDir, serverLogGlob, out var lines, out _)
				&& lines.Any(line => line.Contains("Flagged (link changed)") && line.Contains(fixtureLabel) && line.Contains(fileName));
		
		// Polls until a reload is pending, for at most ten seconds.
		async Task<string> AwaitReloadAsync(string? projectPath)
		{
			
			var deadline = DateTime.UtcNow.AddSeconds(10);
			var state    = "none";
			
			while(DateTime.UtcNow < deadline && state != "pending") {
				
				await Task.Delay(250);
				
				state = await ReloadStateAsync(projectPath);
			}
			
			return state;
		}
		
		// Puts a link to target at path in one step, whatever is there now: the link is made
		// under another name and moved over it. Deleting first would leave a moment in which
		// the path does not exist, and a flush that falls into it sees a deleted document —
		// a reload for a reason that is not the one under test. The other name does not end
		// in .cs, so that it is not a new source file either.
		static void PointAt(string path, string target)
		{
			
			var staged = path + ".staged";
			
			File.CreateSymbolicLink(staged, target);
			File.Move(staged, path, overwrite: true);
		}
		
		// A skip on a developer's machine, a failure on a CI runner — see Skip.
		const string noLinks = "this machine may not create symbolic links";
		
		var tests = new List<TestCase> {
			
			new("linked files: a workspace without a project loads a linked source file once",
				async () => {
					
					if(!linked)
						
						return Skip.SetupUnavailable(noLinks);
					
					// A workspace without a project has no references and never compiles
					// cleanly. What is looked for is the one error a second document causes.
					var data = await Call("roslyn_get_diagnostics", new { projectPath = loose.Dir, take = 200, severity = "errors" });
					
					if(data?["items"] is not JsonArray items)
						
						return (false, $"FAIL  (no diagnostics: {data?.ToJsonString() ?? "none"})");
					
					var duplicates = items.Count(item => item?["code"]?.GetValue<string>() == "CS0101");
					var realLoaded = await IsDocumentAsync(loose.Dir, "Real.cs");
					var aliasToo   = await IsDocumentAsync(loose.Dir, "Alias.cs");
					var outA       = await IsDocumentAsync(loose.Dir, "OutA.cs");
					var outB       = await IsDocumentAsync(loose.Dir, "OutB.cs");
					
					// The file in the tree under its own name and not under the link; the file
					// outside under exactly one of its two links.
					return duplicates == 0 && realLoaded && !aliasToo && (outA ^ outB)
						? (true,  "PASS")
						: (false, $"FAIL  (duplicate-type errors: {duplicates}; Real.cs a document: {realLoaded}; Alias.cs a document: {aliasToo}; OutA.cs: {outA}; OutB.cs: {outB})");
				}),
			
			new("linked files: deleting the file behind a link flags a reload in a project",
				async () => {
					
					if(!linked)
						
						return Skip.SetupUnavailable(noLinks);
					
					if(!await SettleAsync(project.Csproj, projectBeacon) || !await IsDocumentAsync(project.Csproj, "Here.cs"))
						
						return (false, "FAIL  (precondition: the workspace never settled with live watchers, or Here.cs is not a document)");
					
					File.Delete(stored);
					
					var after = await AwaitReloadAsync(project.Csproj);
					
					return after == "pending"
						? (true,  "PASS")
						: (false, $"FAIL  (reload state ten seconds after the delete: {after})");
				}),
			
			// The file behind the link is edited where it lies. It is reported under its own
			// path, obj/Made.cs — a path that is no document, because the loading walk stays
			// out of obj. The document it has is the link. The edit must reach that document,
			// and the path must not become a second one: that would be the file compiled
			// twice again, by the back door.
			new("linked files: an edit of the file behind a link adds no second document in a workspace without a project",
				async () => {
					
					if(!linked)
						
						return Skip.SetupUnavailable(noLinks);
					
					if(!await IsDocumentAsync(fading.Dir, "Made.cs") || !await AwaitLiveAsync(fading))
						
						return (false, "FAIL  (precondition: Made.cs is not a document, or the watcher never went live)");
					
					File.WriteAllText(generated, "class MadeElsewhere { public int EditedInPlace; }\n");
					
					var deadline = DateTime.UtcNow.AddSeconds(10);
					var current  = false;
					
					while(DateTime.UtcNow < deadline && !current) {
						
						await Task.Delay(250);
						
						var blocks = await CallBlocks("roslyn_read_file", new { filePath = "Made.cs", projectPath = fading.Dir });
						
						current = blocks.Length > 1 && blocks[0].Contains("\"roslyn\"") && blocks[1].Contains("EditedInPlace");
					}
					
					var data       = await Call("roslyn_get_diagnostics", new { projectPath = fading.Dir, take = 200, severity = "errors" });
					var duplicates = data?["items"] is JsonArray items ? items.Count(item => item?["code"]?.GetValue<string>() == "CS0101") : -1;
					var second     = await IsDocumentAsync(fading.Dir, "obj/Made.cs");
					
					return current && duplicates == 0 && !second
						? (true,  "PASS")
						: (false, $"FAIL  (the link's document has the edit: {current}; duplicate-type errors: {duplicates}; obj/Made.cs a document of its own: {second})");
				}),
			
			new("linked files: deleting the file behind a link removes the document in a workspace without a project",
				async () => {
					
					if(!linked)
						
						return Skip.SetupUnavailable(noLinks);
					
					if(!await IsDocumentAsync(fading.Dir, "Made.cs") || !await AwaitLiveAsync(fading))
						
						return (false, "FAIL  (precondition: Made.cs, a link to a file under obj, is not a document, or the watcher never went live)");
					
					File.Delete(generated);
					
					var deadline = DateTime.UtcNow.AddSeconds(10);
					var tracked  = true;
					
					while(DateTime.UtcNow < deadline && tracked) {
						
						await Task.Delay(250);
						
						tracked = await IsDocumentAsync(fading.Dir, "Made.cs");
					}
					
					return !tracked
						? (true,  "PASS")
						: (false, "FAIL  (Made.cs is still a document ten seconds after the file behind it was deleted)");
				}),
			
			// A link that someone else changes while the server runs (#343). The index says
			// where each linked document led when the workspace loaded; after this it is
			// wrong for Pointer.cs, and a change to the file it leads to now would not reach
			// the document. The workspace has to notice and reload. Only the link is a
			// document here — both files it points at are kept out of the compilation — so
			// nothing but the link itself can raise the flag: its text changes too, but a
			// changed text is applied in place and reloads nothing.
			new("linked files: repointing a link while the server runs flags a reload",
				async () => {
					
					if(!linked)
						
						return Skip.SetupUnavailable(noLinks);
					
					if(!await SettleAsync(moving.Csproj, movingBeacon) || !await IsDocumentAsync(moving.Csproj, "Pointer.cs"))
						
						return (false, "FAIL  (precondition: the workspace never settled with live watchers, or Pointer.cs is not a document)");
					
					PointAt(pointer, second);
					
					var state  = await AwaitReloadAsync(moving.Csproj);
					var reason = FlaggedAsLinkChange("LinkedFilesRepoint", "Pointer.cs");
					
					return state == "pending" && reason
						? (true,  "PASS")
						: (false, $"FAIL  (reload state ten seconds after the link was repointed: {state}; logged as a link change: {reason})");
				}),
			
			// The other way a path comes to lead elsewhere: a source file of its own is
			// replaced by a link, as a branch switch can do. Before, the file was not in
			// the index at all, so there is no entry to compare with — the check has to
			// look at a tracked document that the index knows nothing about.
			new("linked files: a source file that becomes a link while the server runs flags a reload",
				async () => {
					
					if(!linked)
						
						return Skip.SetupUnavailable(noLinks);
					
					if(!await SettleAsync(moving.Csproj, movingBeacon) || !await IsDocumentAsync(moving.Csproj, "Plain.cs"))
						
						return (false, "FAIL  (precondition: the workspace never settled with live watchers, or Plain.cs is not a document)");
					
					PointAt(plain, second);
					
					var state  = await AwaitReloadAsync(moving.Csproj);
					var reason = FlaggedAsLinkChange("LinkedFilesRepoint", "Plain.cs");
					
					return state == "pending" && reason
						? (true,  "PASS")
						: (false, $"FAIL  (reload state ten seconds after the file became a link: {state}; logged as a link change: {reason})");
				}),
		};
		
		return new TestGroup($"Linked Files ({tests.Count} tests)", tests, Teardown: () => {
			
			foreach(var (link, _) in links)
				try {
					if(new FileInfo(link).LinkTarget is not null)
						File.Delete(link);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			
			// Plain.cs is a link once its test has run, and so may a staged link be if a test
			// stopped half way.
			foreach(var path in new[] { plain, plain + ".staged", pointer + ".staged" })
				try {
					if(new FileInfo(path).LinkTarget is not null)
						File.Delete(path);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			
			moving.Dispose();
			loose.Dispose();
			outside.Dispose();
			fading.Dispose();
			project.Dispose();
			
			return Task.CompletedTask;
		});
	}
}
