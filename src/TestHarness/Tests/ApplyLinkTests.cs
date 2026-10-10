using System.Text.Json.Nodes;

/// <summary>
///     Covers what the shared apply step does with a source file that is a symbolic link (#334).
///     <c>roslyn_apply_rename</c>, <c>roslyn_apply_signature_change</c> and
///     <c>roslyn_apply_code_fix</c> all write through <c>PhysicalSolutionApplier</c>, which used
///     to swap every file at the document's own path. A document that was a link was therefore
///     replaced by a regular file and silently cut off from the file it pointed at, while every
///     other write tool wrote through the link (#322).
///     <para>
///         Four rules are pinned, one test each, all through <c>roslyn_apply_rename</c> because
///         the three tools share the one apply step:
///     </para>
///     <list type="bullet">
///         <item>
///             A link that stays <b>inside</b> the workspace is written through: the link is
///             still a link afterwards and the file behind it has the new content.
///             <c>LinkInside.cs</c> points at <c>store/Inside.source</c>, a name the project does
///             not compile, so the link is the only document for that file.
///         </item>
///         <item>
///             A link that leads <b>out</b> of the workspace is not written through — that is the
///             escape the workspace boundary exists to stop. <c>LinkOutside.cs</c> points at a
///             file in a second fixture directory. The link is replaced where it sits by a
///             regular file with the new content, the file outside keeps its old content, and
///             the result says so in <c>_caution</c>, because otherwise nothing would.
///         </item>
///         <item>
///             A link and the file it points at, <b>both</b> in one change, apply cleanly.
///             <c>Shared.cs</c> and the link <c>SharedLink.cs</c> are both compiled, so the one
///             file is two documents, each a declaration of the same empty partial class — the
///             only shape in which one file can be compiled twice without errors. A rename
///             changes both documents. Before the fix the second entry's stale check saw the
///             first entry's write and reported a partial apply that had not happened.
///         </item>
///         <item>
///             A link whose target passes <b>through a linked directory</b> is written through as
///             well. <c>LinkVia.cs</c> points at <c>alias/Via.source</c>, and <c>alias</c> is a
///             directory link to <c>store</c>. The apply step writes to the place the workspace
///             boundary resolved — <c>store/Via.source</c>, with no link left in the path — not
///             to the link's target as text, which a repointed <c>alias</c> would send somewhere
///             else. The repointing itself cannot be staged through a tool call; what the test
///             pins is that the resolved path is the right file and both links survive.
///         </item>
///     </list>
///     <para>
///         The class names differ from the file names on purpose: a rename of a type whose name
///         matches its file also renames the file, which is a separate step and not what is
///         under test. Every fixture file is written before the first tool call, and the checks
///         look at the file system directly; nothing is mocked.
///     </para>
///     <para>
///         Creating a link needs a privilege Windows grants only to administrators or in developer
///         mode. Where it is refused the fixture cannot be built: the tests are reported as
///         skipped on a developer's machine and as failed on a CI runner (see <see cref="Skip"/>),
///         never as passed.
///     </para>
/// </summary>
static class ApplyLinkTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		
		var fx      = TestFixtures.NewMsBuildProject("ApplyLinks");
		var outside = TestFixtures.NewAdhocDir("ApplyLinksOutside");
		
		var insideReal  = fx.Write("store/Inside.source", "class InsideTargetOld { }\n");
		var insideLink  = fx.PathOf("LinkInside.cs");
		var outsideReal = outside.Write("Outside.source", "class OutsideTargetOld { }\n");
		var outsideLink = fx.PathOf("LinkOutside.cs");
		var sharedReal  = fx.Write("Shared.cs", "partial class SharedPairOld { }\n");
		var sharedLink  = fx.PathOf("SharedLink.cs");
		var viaReal     = fx.Write("store/Via.source", "class ViaTargetOld { }\n");
		var alias       = fx.PathOf("alias");
		var viaLink     = fx.PathOf("LinkVia.cs");
		var linked      = false;
		
		try {
			
			Directory.CreateSymbolicLink(alias,  fx.PathOf("store"));
			File.CreateSymbolicLink(viaLink,     Path.Combine(alias, "Via.source"));
			File.CreateSymbolicLink(insideLink,  insideReal);
			File.CreateSymbolicLink(outsideLink, outsideReal);
			File.CreateSymbolicLink(sharedLink,  sharedReal);
			
			linked = true;
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
		
		// Calls a tool and returns its parsed result, or null when the call failed outright.
		async Task<JsonNode?> Call(string tool, object args)
		{
			
			await ctx.SendAsync(new { jsonrpc = "2.0", id = ctx.NextId(), method = "tools/call",
				@params = new { name = tool, arguments = args } });
			
			var resp = await ctx.ReceiveAsync();
			var text = resp?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
			
			if(text is null)
				
				return null;
			
			try { return JsonNode.Parse(text); }
			catch { return null; }
		}
		
		// Previews and applies one rename; returns the apply result, or the preview when it gave no token.
		async Task<JsonNode?> Rename(string symbolName, string newName)
		{
			
			var preview = await Call("roslyn_preview_rename", new { symbolName, newName, projectPath = fx.Csproj });
			
			if(preview?["token"]?.GetValue<string>() is not { } token)
				
				return preview;
			
			return await Call("roslyn_apply_rename", new { token, approval = "y", projectPath = fx.Csproj });
		}
		
		static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;
		
		static string Show(JsonNode? result) => result?.ToJsonString() ?? "none";
		
		// A skip on a developer's machine, a failure on a CI runner — see Skip.
		const string noLinks = "this machine may not create symbolic links";
		
		var tests = new List<TestCase> {
			
			// The link must survive, and the proof that it was written through is the file
			// behind it: that file has no document of its own, so nothing else changes it.
			new("apply through links: a rename keeps a linked source file a link and changes the file behind it",
				async () => {
					
					if(!linked)
						
						return Skip.SetupUnavailable(noLinks);
					
					var result = await Rename("InsideTargetOld", "InsideTargetNew");
					
					var stillLink = IsLink(insideLink);
					var content   = File.ReadAllText(insideReal);
					
					return result?["error"] is null && stillLink && content.Contains("InsideTargetNew")
						? (true,  "PASS")
						: (false, $"FAIL  (still a link: {stillLink}; file behind it: {content.Trim()}; result: {Show(result)})");
				}),
			
			// The part that must not give: the file outside keeps its old content. The rename
			// itself still has to land, in the workspace, and the result has to say that the
			// link is gone.
			new("apply through links: a link that leads out of the workspace is replaced, not written through",
				async () => {
					
					if(!linked)
						
						return Skip.SetupUnavailable(noLinks);
					
					var result = await Rename("OutsideTargetOld", "OutsideTargetNew");
					
					var outsideContent = File.ReadAllText(outsideReal);
					var untouched      = outsideContent.Contains("OutsideTargetOld") && !outsideContent.Contains("OutsideTargetNew");
					var replaced       = !IsLink(outsideLink) && File.ReadAllText(outsideLink).Contains("OutsideTargetNew");
					var caution        = result?["_caution"]?.GetValue<string>();
					
					return result?["error"] is null && untouched && replaced && caution?.Contains("LinkOutside.cs") == true
						? (true,  "PASS")
						: (false, $"FAIL  (file outside untouched: {untouched}; link replaced by the new content: {replaced}; caution: {caution ?? "none"}; result: {Show(result)})");
				}),
			
			// Both documents are in the change, and they are one file. The apply must report
			// success for both, the link must stay, and the file must have the new name once.
			new("apply through links: a link and its target in one change apply cleanly",
				async () => {
					
					if(!linked)
						
						return Skip.SetupUnavailable(noLinks);
					
					var result = await Rename("SharedPairOld", "SharedPairNew");
					
					var states = result?["files"]?.AsArray()
						.Select(file => file?["state"]?.GetValue<string>())
						.ToArray()
						?? []
					;
					var bothWritten = states.Length == 2 && states.All(state => state == "written");
					var stillLink   = IsLink(sharedLink);
					var content     = File.ReadAllText(sharedReal);
					
					return result?["error"] is null && bothWritten && stillLink && content.Trim() == "partial class SharedPairNew { }"
						? (true,  "PASS")
						: (false, $"FAIL  (both documents written: {bothWritten}; still a link: {stillLink}; file: {content.Trim()}; result: {Show(result)})");
				}),
			
			// The file is reached through two links: the file link, and the directory link in
			// its target. Both must still be links, and the content must have arrived in the
			// one real file.
			new("apply through links: a link whose target passes through a linked directory is written through",
				async () => {
					
					if(!linked)
						
						return Skip.SetupUnavailable(noLinks);
					
					var result = await Rename("ViaTargetOld", "ViaTargetNew");
					
					var fileLinkKept = IsLink(viaLink);
					var dirLinkKept  = new DirectoryInfo(alias).LinkTarget is not null;
					var content      = File.ReadAllText(viaReal);
					
					return result?["error"] is null && fileLinkKept && dirLinkKept && content.Contains("ViaTargetNew")
						? (true,  "PASS")
						: (false, $"FAIL  (file link kept: {fileLinkKept}; directory link kept: {dirLinkKept}; file behind them: {content.Trim()}; result: {Show(result)})");
				}),
		};
		
		return new TestGroup($"Apply Through Links ({tests.Count} tests)", tests, Teardown: () => {
			
			try {
				if(new DirectoryInfo(alias).LinkTarget is not null)
					Directory.Delete(alias);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			
			// The links first, and only the links: the file one of them points at is in the
			// other fixture.
			foreach(var link in new[] { insideLink, outsideLink, sharedLink, viaLink })
				try {
					if(IsLink(link))
						File.Delete(link);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			
			fx.Dispose();
			outside.Dispose();
			
			return Task.CompletedTask;
		});
	}
}
