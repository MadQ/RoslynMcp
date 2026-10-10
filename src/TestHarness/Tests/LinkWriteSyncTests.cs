using System.Text.Json.Nodes;

/// <summary>
///     Covers what the workspace knows after a file was written <b>through a symbolic link</b>
///     (#338). A write tool tells the workspace which path it wrote, and the workspace does two
///     things for that path: it ignores the file watcher's report of the write, and it refreshes
///     the document at that path. When the path is a link, the file system reports the change at
///     the file the link leads to — a path the workspace was never told about. Two things went
///     wrong there. The first is tested for both ways a tool writes — replacing the file, and
///     editing it in place through the workspace — and the second once:
///     <list type="bullet">
///         <item>
///             <b>A stale document.</b> When the file behind the link is a document of its own,
///             its text in the workspace stayed as it was until the watcher caught up, a third
///             of a second later. A tool called straight after the write answered from the old
///             text. <c>Shared.cs</c> and the link <c>SharedLink.cs</c> are both compiled, each
///             a declaration of the same partial class — the one shape in which a file can be
///             compiled twice. The test writes through the link and reads <c>Shared.cs</c> with
///             no wait in between: the new member must be there, and it must come from the
///             workspace, not from disk.
///         </item>
///         <item>
///             <b>A needless reload.</b> When the file behind the link is a <c>.cs</c> file no
///             project compiles — a source kept apart and linked into the project — the
///             watcher's report looked like a new source file appearing, and a new source file
///             costs a full reload of the workspace. <c>store/Kept.cs</c> is excluded from the
///             project with <c>Compile Remove</c>, and <c>Stored.cs</c> links to it. The test
///             writes through the link, waits longer than the watcher's debounce, and asks
///             <c>roslyn_check_drift</c> whether a reload is pending: it must not be.
///         </item>
///     </list>
///     <para>
///         <c>reload_pending</c> is the observable for the second test because it shows the flag
///         without triggering the reload it stands for. The wait is one second against a
///         debounce of 300 ms; a run on a slow machine can only make the test pass late, never
///         fail without cause, since nothing else in the fixture changes.
///     </para>
///     <para>
///         Creating a link needs a privilege Windows grants only to administrators or in developer
///         mode. Where it is refused the fixture cannot be built: the tests are reported as
///         skipped on a developer's machine and as failed on a CI runner (see <see cref="Skip"/>),
///         never as passed.
///     </para>
/// </summary>
static class LinkWriteSyncTests
{
	// Unknown: the probe itself failed, which is neither of the other two.
	enum Reload { Unknown, None, Pending }
	
	internal static TestGroup Build(TestContext ctx)
	{
		
		// One project per test. The second test reads a flag that belongs to a whole workspace,
		// and a write in the first one can raise it late: on Windows the watcher sometimes
		// reports a replaced file as deleted, and a tracked document reported as deleted flags
		// a reload. That is a separate matter from what is tested here, and in a shared
		// workspace it would decide the second test.
		var fx    = TestFixtures.NewMsBuildProject("LinkWriteSync", extraProjectXml: """
			<ItemGroup>
			  <AdditionalFiles Include="notes/*.txt" />
			</ItemGroup>
			""");
		var apart = TestFixtures.NewMsBuildProject("LinkWriteSyncApart", extraProjectXml: """
			<ItemGroup>
			  <Compile Remove="store/**" />
			</ItemGroup>
			""");
		
		var sharedReal = fx.Write("Shared.cs",        "partial class SyncPair { }\n");
		var sharedLink = fx.PathOf("SharedLink.cs");
		var secondLink = fx.PathOf("SharedLinkToo.cs");
		var notesReal  = fx.Write("notes/Real.txt",   "first\n");
		var notesLink  = fx.PathOf("notes/Linked.txt");
		var keptReal   = apart.Write("store/Kept.cs", "class KeptApart { }\n");
		var keptLink   = apart.PathOf("Stored.cs");
		var dirReal    = fx.Write("lib/Dir.cs",       "partial class DirPair { }\n");
		var dirAlias   = fx.PathOf("alias");
		var linked     = false;
		
		try {
			
			Directory.CreateSymbolicLink(dirAlias, fx.PathOf("lib"));
			File.CreateSymbolicLink(sharedLink, sharedReal);
			File.CreateSymbolicLink(secondLink, sharedReal);
			File.CreateSymbolicLink(notesLink,  notesReal);
			File.CreateSymbolicLink(keptLink,   keptReal);
			
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
		
		Task<JsonNode?> Write(string? projectPath, string filePath, string content)
			=> Call("roslyn_write_file", new { filePath, content, projectPath });
		
		// What roslyn_check_drift says about a pending reload. Three answers, so an enum: a probe
		// that failed must not read as "nothing pending". reload_pending is omitted from the
		// JSON when false.
		async Task<Reload> ReloadStateAsync(string? projectPath)
		{
			
			var data = await Call("roslyn_check_drift", new { projectPath });
			
			if(data?["drifted"] is null)
				
				return Reload.Unknown;
			
			return data["reload_pending"]?.GetValue<bool>() == true ? Reload.Pending : Reload.None;
		}
		
		// Loads the workspace and waits until no reload is pending, so that a flag seen later
		// was raised by the test's own write.
		async Task<bool> SettleAsync(string? projectPath)
		{
			
			var deadline = DateTime.UtcNow.AddSeconds(15);
			
			while(DateTime.UtcNow < deadline) {
				
				await Call("roslyn_get_diagnostics", new { projectPath, take = 0, severity = "errors" });
				
				if(await ReloadStateAsync(projectPath) == Reload.None)
					
					return true;
				
				await Task.Delay(250);
			}
			
			return false;
		}
		
		static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;
		
		// A skip on a developer's machine, a failure on a CI runner — see Skip.
		const string noLinks = "this machine may not create symbolic links";
		
		var tests = new List<TestCase> {
			
			// No wait between the write and the read: that is the point. The header says where
			// the text came from, and it has to be the workspace's copy that is current.
			new("link write sync: the document behind a link is current straight after a write through the link",
				async () => {
					
					if(!linked)
						
						return Skip.SetupUnavailable(noLinks);
					
					if(!await SettleAsync(fx.Csproj))
						
						return (false, "FAIL  (precondition: the workspace never settled)");
					
					var written = await Write(fx.Csproj, "SharedLink.cs", "partial class SyncPair { public int AddedThroughLink; }\n");
					var blocks  = await CallBlocks("roslyn_read_file", new { filePath = "Shared.cs", projectPath = fx.Csproj });
					
					JsonNode? header = null;
					
					try { header = blocks.Length > 0 ? JsonNode.Parse(blocks[0]) : null; }
					catch { }
					
					var source  = header?["source"]?.GetValue<string>();
					var text    = blocks.Length > 1 ? blocks[1] : "";
					var current = text.Contains("AddedThroughLink");
					
					return written?["written"]?.GetValue<bool>() == true && source == "roslyn" && current && IsLink(sharedLink)
						? (true,  "PASS")
						: (false, $"FAIL  (served from: {source ?? "nothing"}; has the new member: {current}; still a link: {IsLink(sharedLink)}; write: {written?.ToJsonString() ?? "none"})");
				}),
			
			// The same through the other way a tool writes. roslyn_write_file replaces the file;
			// roslyn_replace_in_file hands the new text to the workspace, which writes the
			// document at its own path — through the link — with the watcher switched off for
			// the duration. Nothing would ever tell the workspace about the file behind the
			// link: without the sync it stayed stale until something else touched it. Runs
			// after the test above and edits the member that one added.
			new("link write sync: the document behind a link is current straight after an in-place edit through the link",
				async () => {
					
					if(!linked)
						
						return Skip.SetupUnavailable(noLinks);
					
					var edited = await Call("roslyn_replace_in_file", new {
						filePath = "SharedLink.cs", pattern = "AddedThroughLink", replacement = "EditedThroughLink", projectPath = fx.Csproj });
					var blocks = await CallBlocks("roslyn_read_file", new { filePath = "Shared.cs", projectPath = fx.Csproj });
					
					JsonNode? header = null;
					
					try { header = blocks.Length > 0 ? JsonNode.Parse(blocks[0]) : null; }
					catch { }
					
					var source  = header?["source"]?.GetValue<string>();
					var text    = blocks.Length > 1 ? blocks[1] : "";
					var current = text.Contains("EditedThroughLink");
					var onDisk  = File.ReadAllText(sharedReal).Contains("EditedThroughLink");
					
					return edited?["applied"]?.GetValue<bool>() == true && source == "roslyn" && current && onDisk && IsLink(sharedLink)
						? (true,  "PASS")
						: (false, $"FAIL  (served from: {source ?? "nothing"}; workspace has the edit: {current}; file behind the link has it: {onDisk}; still a link: {IsLink(sharedLink)}; edit: {edited?.ToJsonString() ?? "none"})");
				}),
			
			// One file, three documents: Shared.cs and two links to it. A write through the one
			// link is reported under that link's name and, at best, seen by the watcher under
			// the file's own; the document at the other link hears of it from nobody. It must
			// be current all the same.
			new("link write sync: a second link to the same file is current after a write through the first",
				async () => {
					
					if(!linked)
						
						return Skip.SetupUnavailable(noLinks);
					
					var written = await Write(fx.Csproj, "SharedLink.cs", "partial class SyncPair { public int SeenThroughEveryLink; }\n");
					var blocks  = await CallBlocks("roslyn_read_file", new { filePath = "SharedLinkToo.cs", projectPath = fx.Csproj });
					
					JsonNode? header = null;
					
					try { header = blocks.Length > 0 ? JsonNode.Parse(blocks[0]) : null; }
					catch { }
					
					var source  = header?["source"]?.GetValue<string>();
					var text    = blocks.Length > 1 ? blocks[1] : "";
					var current = text.Contains("SeenThroughEveryLink");
					
					return written?["written"]?.GetValue<bool>() == true && source == "roslyn" && current && IsLink(secondLink)
						? (true,  "PASS")
						: (false, $"FAIL  (served from: {source ?? "nothing"}; has the new member: {current}; still a link: {IsLink(secondLink)}; write: {written?.ToJsonString() ?? "none"})");
				}),
			
			// The same for the other kind of document the workspace writes itself: an
			// AdditionalFiles item. notes/Real.txt and the link notes/Linked.txt are both
			// declared, so both are documents. Nothing watches a .txt, which makes this the
			// case with no second chance: what the edit does not sync stays stale for good.
			new("link write sync: an AdditionalFiles item behind a link is current after an edit through the link",
				async () => {
					
					if(!linked)
						
						return Skip.SetupUnavailable(noLinks);
					
					var probe = await Call("roslyn_read_file", new { filePath = "notes/Real.txt", projectPath = fx.Csproj });
					
					if(probe?["source"]?.GetValue<string>() != "roslyn")
						
						return (false, $"FAIL  (precondition: notes/Real.txt is not a tracked document; read header: {probe?.ToJsonString() ?? "none"})");
					
					var edited = await Call("roslyn_replace_in_file", new {
						filePath = "notes/Linked.txt", pattern = "first", replacement = "second", projectPath = fx.Csproj });
					var blocks = await CallBlocks("roslyn_read_file", new { filePath = "notes/Real.txt", projectPath = fx.Csproj });
					
					JsonNode? header = null;
					
					try { header = blocks.Length > 0 ? JsonNode.Parse(blocks[0]) : null; }
					catch { }
					
					var source  = header?["source"]?.GetValue<string>();
					var text    = blocks.Length > 1 ? blocks[1] : "";
					var current = text.Contains("second");
					var onDisk  = File.ReadAllText(notesReal).Contains("second");
					
					return edited?["applied"]?.GetValue<bool>() == true && source == "roslyn" && current && onDisk && IsLink(notesLink)
						? (true,  "PASS")
						: (false, $"FAIL  (served from: {source ?? "nothing"}; workspace has the edit: {current}; file behind the link has it: {onDisk}; still a link: {IsLink(notesLink)}; edit: {edited?.ToJsonString() ?? "none"})");
				}),
			
			// The link can also be a directory above the file. alias is a directory link to lib,
			// so alias/Dir.cs and lib/Dir.cs are one file under two names, and the project
			// compiles both. A write to the one name must leave the document at the other
			// current. The test only says something when alias/Dir.cs is a document of its
			// own — otherwise writing it is adding a new file, which reloads the workspace and
			// makes everything current whatever the code under test does. That is checked
			// first, and where the project system does not compile through a linked directory
			// the test is a skip, not a pass.
			new("link write sync: a write to a file under a linked directory leaves the real file's document current",
				async () => {
					
					if(!linked)
						
						return Skip.SetupUnavailable(noLinks);
					
					var probe = await Call("roslyn_read_file", new { filePath = "alias/Dir.cs", projectPath = fx.Csproj });
					
					if(probe?["source"]?.GetValue<string>() != "roslyn")
						
						return Skip.NotApplicable("the project system does not compile files under a linked directory here");
					
					var written = await Write(fx.Csproj, "alias/Dir.cs", "partial class DirPair { public int AddedThroughDirectory; }\n");
					var blocks  = await CallBlocks("roslyn_read_file", new { filePath = "lib/Dir.cs", projectPath = fx.Csproj });
					
					JsonNode? header = null;
					
					try { header = blocks.Length > 0 ? JsonNode.Parse(blocks[0]) : null; }
					catch { }
					
					var source   = header?["source"]?.GetValue<string>();
					var text     = blocks.Length > 1 ? blocks[1] : "";
					var current  = text.Contains("AddedThroughDirectory");
					var linkKept = new DirectoryInfo(dirAlias).LinkTarget is not null;
					
					return written?["written"]?.GetValue<bool>() == true && source == "roslyn" && current && linkKept
						? (true,  "PASS")
						: (false, $"FAIL  (served from: {source ?? "nothing"}; has the new member: {current}; directory link kept: {linkKept}; file: {File.ReadAllText(dirReal).Trim()}; write: {written?.ToJsonString() ?? "none"})");
				}),
			
			// The file behind the link was there all along and is not a compilation input.
			// Writing it must not look like a new source file.
			new("link write sync: a write through a link to a source no project compiles flags no reload",
				async () => {
					
					if(!linked)
						
						return Skip.SetupUnavailable(noLinks);
					
					if(!await SettleAsync(apart.Csproj))
						
						return (false, "FAIL  (precondition: reload_pending never cleared before the write)");
					
					var written = await Write(apart.Csproj, "Stored.cs", "class KeptApart { public int Changed; }\n");
					
					// Longer than the watcher's debounce, so a report of the write has been handled.
					await Task.Delay(1000);
					
					var reload  = await ReloadStateAsync(apart.Csproj);
					var arrived = File.ReadAllText(keptReal).Contains("Changed");
					
					return written?["written"]?.GetValue<bool>() == true && reload == Reload.None && arrived && IsLink(keptLink)
						? (true,  "PASS")
						: (false, $"FAIL  (reload: {reload}; content arrived behind the link: {arrived}; still a link: {IsLink(keptLink)}; write: {written?.ToJsonString() ?? "none"})");
				}),
		};
		
		return new TestGroup($"Link Write Sync ({tests.Count} tests)", tests, Teardown: () => {
			
			// The directory link first, and only the link: deleting the tree through it would
			// reach into lib.
			try {
				if(new DirectoryInfo(dirAlias).LinkTarget is not null)
					Directory.Delete(dirAlias);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			
			foreach(var link in new[] { sharedLink, secondLink, notesLink, keptLink })
				try {
					if(IsLink(link))
						File.Delete(link);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			
			fx.Dispose();
			apart.Dispose();
			
			return Task.CompletedTask;
		});
	}
}
