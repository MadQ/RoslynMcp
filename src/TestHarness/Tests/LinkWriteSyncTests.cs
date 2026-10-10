using System.Text.Json.Nodes;

/// <summary>
///     Covers what the workspace knows after a file was written <b>through a symbolic link</b>
///     (#338). A write tool tells the workspace which path it wrote, and the workspace does two
///     things for that path: it ignores the file watcher's report of the write, and it refreshes
///     the document at that path. When the path is a link, the file system reports the change at
///     the file the link leads to — a path the workspace was never told about. Two things went
///     wrong there, one test each:
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
	internal static TestGroup Build(TestContext ctx)
	{
		
		var fx = TestFixtures.NewMsBuildProject("LinkWriteSync", extraProjectXml: """
			<ItemGroup>
			  <Compile Remove="store/**" />
			</ItemGroup>
			""");
		
		var sharedReal = fx.Write("Shared.cs",     "partial class SyncPair { }\n");
		var sharedLink = fx.PathOf("SharedLink.cs");
		var keptReal   = fx.Write("store/Kept.cs", "class KeptApart { }\n");
		var keptLink   = fx.PathOf("Stored.cs");
		var linked     = false;
		
		try {
			
			File.CreateSymbolicLink(sharedLink, sharedReal);
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
		
		Task<JsonNode?> Write(string filePath, string content)
			=> Call("roslyn_write_file", new { filePath, content, projectPath = fx.Csproj });
		
		// Null means the probe itself failed. reload_pending is omitted from the JSON when false.
		async Task<bool?> ReloadPendingAsync()
		{
			
			var data = await Call("roslyn_check_drift", new { projectPath = fx.Csproj });
			
			if(data?["drifted"] is null)
				
				return null;
			
			return data["reload_pending"]?.GetValue<bool>() ?? false;
		}
		
		// Loads the workspace and waits until no reload is pending, so that a flag seen later
		// was raised by the test's own write.
		async Task<bool> SettleAsync()
		{
			
			var deadline = DateTime.UtcNow.AddSeconds(15);
			
			while(DateTime.UtcNow < deadline) {
				
				await Call("roslyn_get_diagnostics", new { projectPath = fx.Csproj, take = 0, severity = "errors" });
				
				if(await ReloadPendingAsync() == false)
					
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
					
					if(!await SettleAsync())
						
						return (false, "FAIL  (precondition: the workspace never settled)");
					
					var written = await Write("SharedLink.cs", "partial class SyncPair { public int AddedThroughLink; }\n");
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
			
			// The file behind the link was there all along and is not a compilation input.
			// Writing it must not look like a new source file.
			new("link write sync: a write through a link to a source no project compiles flags no reload",
				async () => {
					
					if(!linked)
						
						return Skip.SetupUnavailable(noLinks);
					
					if(!await SettleAsync())
						
						return (false, "FAIL  (precondition: reload_pending never cleared before the write)");
					
					var written = await Write("Stored.cs", "class KeptApart { public int Changed; }\n");
					
					// Longer than the watcher's debounce, so a report of the write has been handled.
					await Task.Delay(1000);
					
					var pending = await ReloadPendingAsync();
					var arrived = File.ReadAllText(keptReal).Contains("Changed");
					
					return written?["written"]?.GetValue<bool>() == true && pending == false && arrived && IsLink(keptLink)
						? (true,  "PASS")
						: (false, $"FAIL  (reload_pending: {pending?.ToString() ?? "no result"}; content arrived behind the link: {arrived}; still a link: {IsLink(keptLink)}; write: {written?.ToJsonString() ?? "none"})");
				}),
		};
		
		return new TestGroup($"Link Write Sync ({tests.Count} tests)", tests, Teardown: () => {
			
			foreach(var link in new[] { sharedLink, keptLink })
				try {
					if(IsLink(link))
						File.Delete(link);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			
			fx.Dispose();
			
			return Task.CompletedTask;
		});
	}
}
