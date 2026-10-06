using System.Text.Json.Nodes;

/// <summary>
///     Covers which symbolic links the workspace boundary denies (#330). The boundary used to deny
///     a file when <i>any</i> component of its full path was a link — the directories above the
///     workspace included. A workspace that merely lives under a link therefore had every file
///     denied: reads reported "file not found", writes "the specified path is not accessible".
///     On macOS that is every workspace under the temp directory, because <c>/var</c> is a link to
///     <c>/private/var</c>; the first harness run there failed 64 tests for this one reason. It is
///     the same on Windows or Linux for a checkout under a junction or a symlinked home directory.
///     <para>
///         The rule being pinned has three parts, one test each. A link <b>above or at</b> the
///         workspace root is where the user put the workspace and must not matter. A link
///         <b>inside</b> the workspace is followed when it leads to somewhere inside the workspace
///         (#322) — a folder shared between two places in a repository is ordinary. A link inside
///         the workspace that leads <b>out</b> of it must still be refused: that is the escape the
///         boundary exists to stop, and neither of the other two may open it. A fourth test
///         covers the same escape for a write, through a link whose target does not exist yet:
///         <c>dangling.txt</c> points at a file under <c>outside</c> that nobody created. A fifth,
///         on Linux only, links to a directory whose name differs from the root's in case alone.
///     </para>
///     <para>
///         The fixture builds all three on any operating system instead of relying on the
///         platform's temp directory: <c>linked</c> is a directory link to <c>real</c>, and the
///         workspace is opened as <c>linked/proj</c>, so that link sits above the root. Inside the
///         workspace, <c>alias</c> is a directory link to the workspace's own <c>lib</c> folder,
///         and <c>escape</c> is a directory link to <c>outside</c>, which holds a file the
///         workspace must not reach. Opening the workspace through <c>linked</c> also makes the
///         <c>alias</c> case the hard one: the link's target is written with the real path while
///         the root is known by its linked path, so the two only compare equal once both are
///         resolved. Nothing is mocked: the tests call <c>roslyn_read_file</c> on the real server
///         and look at what comes back.
///     </para>
///     <para>
///         Creating a link needs a privilege Windows grants only to administrators or in developer
///         mode. Where it is refused the fixture cannot be built: the tests are reported as
///         skipped on a developer's machine and as failed on a CI runner (see <see cref="Skip"/>),
///         never as passed.
///     </para>
/// </summary>
static class LinkedWorkspaceTests
{
	const string Secret = "outside-the-workspace";
	
	internal static TestGroup Build(TestContext ctx)
	{
		
		var fx = TestFixtures.NewAdhocDir("LinkedWorkspace");
		
		fx.Write("real/proj/Probe.cs",   "class Probe { }\n");
		fx.Write("real/proj/Notes.txt",  "inside-the-workspace\n");
		fx.Write("outside/secret.txt",   Secret + "\n");
		fx.Write("real/proj/lib/Inner.txt", "reached-through-the-alias\n");
		
		var linkAbove  = fx.PathOf("linked");
		var linkInside = fx.PathOf("real/proj/escape");
		var linkAlias  = fx.PathOf("real/proj/alias");
		
		// A file link whose target does not exist. Nothing can be read through it, but a write
		// would create the target — outside the workspace.
		var linkDangling   = fx.PathOf("real/proj/dangling.txt");
		var danglingTarget = fx.PathOf("outside/created-through-the-link.txt");
		
		// A directory beside the workspace root whose name differs from the root's only in case.
		// Only Linux can have one: elsewhere real/PROJ and real/proj are the same directory.
		var linkCased = fx.PathOf("real/proj/cased");
		
		if(OperatingSystem.IsLinux())
			fx.Write("real/PROJ/secret.txt", Secret + "\n");
		var linksMade  = false;
		
		try {
			
			Directory.CreateSymbolicLink(linkAbove,  fx.PathOf("real"));
			Directory.CreateSymbolicLink(linkInside, fx.PathOf("outside"));
			Directory.CreateSymbolicLink(linkAlias,  fx.PathOf("real/proj/lib"));
			File.CreateSymbolicLink(linkDangling,    danglingTarget);
			
			if(OperatingSystem.IsLinux())
				Directory.CreateSymbolicLink(linkCased, fx.PathOf("real/PROJ"));
			
			linksMade = true;
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
		
		// The workspace as the server is asked to open it: through the link.
		var projectPath = Path.Combine(linkAbove, "proj");
		
		// The text of a file as roslyn_read_file returns it, or null with the error when it fails.
		async Task<(string? text, string? error)> ReadAsync(string filePath)
		{
			
			await ctx.SendAsync(new { jsonrpc = "2.0", id = ctx.NextId(), method = "tools/call",
				@params = new { name = "roslyn_read_file", arguments = new { filePath, projectPath } } });
			
			var resp    = await ctx.ReceiveAsync();
			var content = resp?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
			
			if(content is null)
				
				return (null, resp?["error"]?["message"]?.GetValue<string>() ?? "no response");
			
			JsonNode? data;
			
			try { data = JsonNode.Parse(content); }
			catch { return (null, "unparseable response"); }
			
			// A successful read is a JSON header block followed by the file's text as a raw block (#328).
			if(data?["error"] is null && resp?["result"]?["content"]?[1]?["text"]?.GetValue<string>() is { } source)
				
				return (source, null);
			
			return (null, data?["error"]?.GetValue<string>() ?? content);
		}
		
		// A skip on a developer's machine, a failure on a CI runner — see Skip.
		const string noLinks = "this machine may not create symbolic links";
		
		var tests = new List<TestCase> {
			
			// Both a tracked .cs document and a plain file: they are resolved along different
			// routes, and both ran into the boundary.
			new("linked workspace: files are readable when the workspace sits under a link",
				async () => {
					
					if(!linksMade)
						
						return Skip.SetupUnavailable(noLinks);
					
					var (notes, notesError) = await ReadAsync("Notes.txt");
					var (probe, probeError) = await ReadAsync("Probe.cs");
					
					return notes?.Contains("inside-the-workspace") == true && probe?.Contains("class Probe") == true
						? (true,  "PASS")
						: (false, $"FAIL  (Notes.txt: {notesError ?? "wrong content"}; Probe.cs: {probeError ?? "wrong content"})");
				}),
			
			// A link that stays inside the workspace is followed. Before #322 every link inside a
			// workspace was refused, whatever it pointed at.
			new("linked workspace: a link that stays inside the workspace is followed",
				async () => {
					
					if(!linksMade)
						
						return Skip.SetupUnavailable(noLinks);
					
					var (text, error) = await ReadAsync("alias/Inner.txt");
					
					return text?.Contains("reached-through-the-alias") == true
						? (true,  "PASS")
						: (false, $"FAIL  (alias/Inner.txt: {error ?? "wrong content"})");
				}),
			
			// The part that must not give. The read must fail, and the file's content must not
			// appear anywhere in what comes back.
			new("linked workspace: a link that leads out of the workspace is not followed",
				async () => {
					
					if(!linksMade)
						
						return Skip.SetupUnavailable(noLinks);
					
					var (text, error) = await ReadAsync("escape/secret.txt");
					
					return text is null && error?.Contains(Secret) != true
						? (true,  "PASS")
						: (false, $"FAIL  (the file outside the workspace was served: {text ?? error})");
				}),
			
			// The same escape for a write. The link's target does not exist, so there is nothing
			// to read and the link is easy to mistake for a file that is about to be created;
			// writing through it would create a file outside the workspace. The write must be
			// refused, and the proof is on disk: the target must still not exist.
			new("linked workspace: a write through a dangling link that leads out is refused",
				async () => {
					
					if(!linksMade)
						
						return Skip.SetupUnavailable(noLinks);
					
					await ctx.SendAsync(new { jsonrpc = "2.0", id = ctx.NextId(), method = "tools/call",
						@params = new { name = "roslyn_write_file", arguments = new {
							filePath = "dangling.txt", content = "written-through-the-link\n", createNew = true, projectPath } } });
					
					var resp = await ctx.ReceiveAsync();
					var body = resp?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
					
					bool refused;
					
					try { refused = JsonNode.Parse(body)?["error"] is not null; }
					catch { refused = false; }
					
					var created = File.Exists(danglingTarget);
					
					return refused && !created
						? (true,  "PASS")
						: (false, $"FAIL  (refused: {refused}; file created outside the workspace: {created}; response: {body})");
				}),
			
			// "Inside the workspace" must mean the same directory, not a name that looks the
			// same with case ignored. The link points at real/PROJ, a different directory from
			// the workspace's real/proj on Linux; a comparison that ignores case took it for the
			// workspace itself and served the file. The other platforms cannot build the
			// fixture, and their file systems make the two names one directory anyway.
			new("linked workspace: a link to a directory that differs from the root only in case is not followed",
				async () => {
					
					if(!OperatingSystem.IsLinux())
						
						return Skip.NotApplicable("names that differ only in case are one directory on this platform");
					
					if(!linksMade)
						
						return Skip.SetupUnavailable(noLinks);
					
					var (text, error) = await ReadAsync("cased/secret.txt");
					
					return text is null && error?.Contains(Secret) != true
						? (true,  "PASS")
						: (false, $"FAIL  (the file outside the workspace was served: {text ?? error})");
				}),
		};
		
		return new TestGroup($"Linked Workspace ({tests.Count} tests)", tests, Teardown: () => {
			
			// The links first, and only the links: deleting the tree through them would reach
			// into their targets.
			foreach(var link in new[] { linkCased, linkAlias, linkInside, linkAbove })
				try {
					if(Directory.Exists(link))
						Directory.Delete(link);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			
			try {
				File.Delete(linkDangling);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			
			fx.Dispose();
			
			return Task.CompletedTask;
		});
	}
}
