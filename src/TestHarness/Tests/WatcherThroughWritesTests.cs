using System.Diagnostics;
using System.Text.Json.Nodes;

/// <summary>
///     Covers the file watchers staying live while the server writes a file itself (#358).
///     <para>
///         Every edit the server applied to a project used to switch every watcher off around the
///         write, and on again afterwards. A change anyone else made in between, to any file,
///         raised no event and was never caught up on: the workspace kept the old text. On macOS
///         switching a watcher on starts a new event stream, which made the window long enough
///         for the <c>Linked Files</c> tests to fall into it now and then.
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Changes made during the server's own edits are seen.</b> The server edits a
///             large tracked file forty times; during each edit a marker is appended on disk to
///             another tracked file, at a random moment within the time one edit takes. Each of
///             those files must serve its marker within fifteen seconds. On Windows the old code
///             lost one to three of the forty in every one of four runs, all of them writes that
///             fell into the pause around the edit's write. The edited file is large so that
///             this write takes long enough to be hit, and the edit before the timed one pays
///             for the compilation, which would otherwise spread the outside writes far past the
///             edits. Losing one is a matter of timing, so a run of broken code can still pass.
///         </item>
///         <item>
///             <b>The server's own writes are still recognised.</b> They are reported now, and the
///             flush must recognise them rather than apply them a second time or flag a reload: a
///             burst of edits, through text and through the syntax tree, must leave no reload
///             pending and the document equal to the file once the reports have been handled.
///         </item>
///     </list>
///     <para>
///         Both tests first wait for the watchers to be live (<c>SettleAsync</c>), as the
///         <c>Linked Files</c> tests do: they start on a background thread, and a change made
///         before that is reported by nobody, which would fail the first test for the wrong
///         reason.
///     </para>
/// </summary>
static class WatcherThroughWritesTests
{
	// Enough files that, with the old code, at least one write fell into an apply in every run
	// measured on Windows.
	const int OutsideFiles = 40;
	
	/// <param name="ctx">The harness's connection to the server.</param>
	/// <param name="serverLogDir">Directory the harness pointed ROSLYNMCP_LOG_PATH at.</param>
	/// <param name="serverLogGlob">File pattern of the server's log in that directory.</param>
	internal static TestGroup Build(TestContext ctx, string serverLogDir, string serverLogGlob)
	{
		
		var fx = TestFixtures.NewMsBuildProject("WatcherThroughWrites");
		
		// Large on purpose: every edit rewrites the whole file, and the longer that write takes,
		// the more of the outside changes the old code's pause around it swallowed.
		var filler  = string.Concat(Enumerable.Repeat("\t// Filler that makes each write of this file take a while.\n", 10_000));
		var edited  = fx.Write("Edited.cs", $"class Edited\n{{\n\tint kept;\n{filler}}}\n");
		var beacon  = fx.Write("Beacon.cs", "class Beacon { }\n");
		var small   = fx.Write("Small.cs",  "class Small\n{\n\tint a;\n}\n");
		var outside = Enumerable.Range(1, OutsideFiles)
			.Select(n => fx.Write($"Outside{n:00}.cs", $"class Outside{n:00} {{ }}\n"))
			.ToArray()
		;
		
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
		Task<JsonNode?> Call(string tool, object args) => ctx.CallToolJsonAsync(tool, args);
		
		// The text the workspace serves for a file, or null when it serves none from a document.
		async Task<string?> WorkspaceTextAsync(string path)
		{
			
			var blocks = await CallBlocks("roslyn_read_file", new { filePath = Path.GetFileName(path), projectPath = fx.Csproj });
			
			return blocks.Length > 1 && blocks[0].Contains("\"roslyn\"") ? blocks[1] : null;
		}
		
		// "pending", "none", or "unknown" when the probe itself failed. reload_pending is
		// omitted from the JSON when false.
		async Task<string> ReloadStateAsync()
		{
			
			var data = await Call("roslyn_check_drift", new { projectPath = fx.Csproj });
			
			if(data?["drifted"] is null)
				
				return "unknown";
			
			return data["reload_pending"]?.GetValue<bool>() == true ? "pending" : "none";
		}
		
		// Loads the workspace, waits until no reload is pending, then until the watchers are
		// live: a marker appended to the beacon on disk has to reach its document. As in
		// LinkedFileTests.SettleAsync.
		async Task<bool> SettleAsync()
		{
			
			var deadline = DateTime.UtcNow.AddSeconds(30);
			var settled  = false;
			
			while(DateTime.UtcNow < deadline && !settled) {
				
				await Call("roslyn_get_diagnostics", new { projectPath = fx.Csproj, take = 0, severity = "errors" });
				
				settled = await ReloadStateAsync() == "none";
				
				if(!settled)
					await Task.Delay(250);
			}
			
			if(!settled)
				
				return false;
			
			var marker = $"// live {Guid.NewGuid():N}";
			
			File.AppendAllText(beacon, marker + "\n");
			
			while(DateTime.UtcNow < deadline) {
				
				await Task.Delay(250);
				
				if((await WorkspaceTextAsync(beacon))?.Contains(marker) == true)
					
					return true;
			}
			
			return false;
		}
		
		// One edit of Edited.cs through the server: a line inserted at the top, which in a
		// project is applied through TryApplyChanges.
		async Task<bool> EditAsync(int n)
		{
			
			var data = await Call("roslyn_insert_lines", new { filePath = "Edited.cs", text = $"// edit {n}", atLine = 1, projectPath = fx.Csproj });
			
			return data is not null && data["error"] is null;
		}
		
		var tests = new List<TestCase> {
			
			new("watcher through writes: changes made on disk during the server's own edits are all seen",
				async () => {
					
					if(!await SettleAsync())
						
						return (false, "FAIL  (precondition: the workspace never settled with live watchers)");
					
					var marker = $"// outside {Guid.NewGuid():N}";
					
					// One edit is timed first, so each outside write can be aimed at a random
					// moment inside an edit of the same length; the one before it pays for the
					// compilation, and would make the edits look several times longer than they
					// are. Seeded, so that runs differ only in timing.
					if(!await EditAsync(-1))
						
						return (false, "FAIL  (roslyn_insert_lines failed on the warm-up edit)");
					
					var timer = Stopwatch.StartNew();
					
					if(!await EditAsync(0))
						
						return (false, "FAIL  (roslyn_insert_lines failed on the timing edit)");
					
					var span   = Math.Max(1, (int) timer.ElapsedMilliseconds);
					var random = new Random(358);
					var edits  = 0;
					
					foreach(var path in outside) {
						
						var delay = random.Next(span);
						var edit  = EditAsync(++edits);
						var write = Task.Run(async () => {
							
							await Task.Delay(delay);
							File.AppendAllText(path, marker + "\n");
						});
						
						if(!await edit)
							
							return (false, $"FAIL  (roslyn_insert_lines failed on edit {edits})");
						
						await write;
					}
					
					// Polled as a whole: a file once seen stays seen, so only the rest is asked again.
					var unseen   = outside.ToList();
					var deadline = DateTime.UtcNow.AddSeconds(15);
					
					while(unseen.Count > 0 && DateTime.UtcNow < deadline) {
						
						await Task.Delay(250);
						
						foreach(var path in unseen.ToArray())
							if((await WorkspaceTextAsync(path))?.Contains(marker) == true)
								unseen.Remove(path);
					}
					
					return unseen.Count == 0
						? (true,  $"PASS  ({OutsideFiles} outside changes seen across {edits} edits of about {span} ms)")
						: (false, $"FAIL  ({unseen.Count} of {OutsideFiles} files changed on disk during {edits} edits never reached the workspace: {string.Join(", ", unseen.Select(Path.GetFileName))})");
				}),
			
			new("watcher through writes: the server's own edits flag no reload and leave the document equal to the file",
				async () => {
					
					if(!await SettleAsync())
						
						return (false, "FAIL  (precondition: the workspace never settled with live watchers)");
					
					for(var n = 1; n <= 5; n++)
						if(!await EditAsync(1000 + n))
							
							return (false, $"FAIL  (roslyn_insert_lines failed on edit {n})");
					
					// Through the syntax tree as well: a whole-solution apply rather than one text.
					var renamed = await Call("roslyn_replace_in_code", new { filePath = "Edited.cs", nodeKind = "field", textPattern = "kept", replacement = "int keptToo;", projectPath = fx.Csproj });
					
					if(renamed is null || renamed["error"] is not null)
						
						return (false, $"FAIL  (roslyn_replace_in_code failed: {renamed?.ToJsonString() ?? "no response"})");
					
					// Past the 300 ms debounce, so the reports of those writes have been handled.
					await Task.Delay(1500);
					
					var state  = await ReloadStateAsync();
					var served = await WorkspaceTextAsync(edited);
					var onDisk = File.ReadAllText(edited);
					
					// The source block may end with a newline the file does not have; compared
					// line by line, which is what matters here.
					static string[] Lines(string? text) => text?.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n') ?? [];
					
					var equal = Lines(served).SequenceEqual(Lines(onDisk));
					
					// reload_pending only shows a flag that is still waiting. The next edit's
					// compilation would have serviced an earlier one, so the log is asked too: no
					// reload may have been flagged for a file of this fixture, for any reason.
					if(!ServerLog.TryRead(serverLogDir, serverLogGlob, 1, out var log, out var logFailure))
						
						return (false, logFailure);
					
					var flagged = log.Count(line => line.Contains("Flagged") && line.Contains("WatcherThroughWrites"));
					var edits   = onDisk.Contains("keptToo") && onDisk.Contains("// edit 1005");
					
					return state == "none" && flagged == 0 && equal && edits
						? (true,  "PASS")
						: (false, $"FAIL  (reload state: {state}; reloads flagged for this fixture in the log: {flagged}; document equals file: {equal}; file has every edit: {edits})");
				}),
			
			// An outside edit that leaves the file the length the server's own last write left
			// it. The flush used to skip a report whose file had the size recorded at the
			// server's write, without looking at the text, and that record was only cleared by
			// a report of the server's own write — which, arriving during the write, never got
			// that far. So the record stayed and swallowed the next same-length change.
			new("watcher through writes: an outside edit of the same length after the server's own edit is seen",
				async () => {
					
					if(!await SettleAsync())
						
						return (false, "FAIL  (precondition: the workspace never settled with live watchers)");
					
					var own = await Call("roslyn_replace_in_file", new { filePath = "Small.cs", pattern = "int a;", replacement = "int b;", projectPath = fx.Csproj });
					
					if(own is null || own["error"] is not null)
						
						return (false, $"FAIL  (roslyn_replace_in_file failed: {own?.ToJsonString() ?? "no response"})");
					
					// Past the debounce, so whatever the server's own write produced has been handled.
					await Task.Delay(1500);
					
					File.WriteAllText(small, File.ReadAllText(small).Replace("int b;", "int c;"));
					
					var deadline = DateTime.UtcNow.AddSeconds(10);
					var seen     = false;
					
					while(DateTime.UtcNow < deadline && !seen) {
						
						await Task.Delay(250);
						
						seen = (await WorkspaceTextAsync(small))?.Contains("int c;") == true;
					}
					
					return seen
						? (true,  "PASS")
						: (false, "FAIL  (Small.cs was changed on disk to a text of the same length ten seconds ago, and the workspace still serves the server's own edit)");
				}),
		};
		
		return new TestGroup($"Watcher Through Writes ({tests.Count} tests)", tests, Teardown: () => {
			
			fx.Dispose();
			
			return Task.CompletedTask;
		});
	}
}
