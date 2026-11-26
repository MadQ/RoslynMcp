using System.Text.Json.Nodes;

/// <summary>
///     Covers the directories the server stays out of (#309). Before the fix every recursive walk
///     and the file watcher covered the whole workspace root, so a <c>node_modules</c> or
///     <c>.git</c> tree was enumerated and watched in full — slow enough on a network share that an
///     MCP client gave up waiting. These tests pin the observable contract of the fix, not its
///     mechanism: what <c>roslyn_list_files</c> returns, what a relative <c>filePath</c> resolves
///     to, and which external changes the watcher turns into a pending reload.
///     <para>
///         The fixture is a throwaway MSBuild project under %TEMP% (#274), laid out before the
///         first tool call so the initial load and the watch plan both see it:
///     </para>
///     <list type="bullet">
///         <item><c>node_modules/pkg/</c> — a built-in ignored directory, holding a stray <c>.cs</c>.</item>
///         <item><c>dist/</c> — ignored by a plain-name <c>.gitignore</c> entry.</item>
///         <item>
///             <c>anchored/</c> and <c>sub/anchored/</c> — the <c>.gitignore</c> entry is
///             <c>/anchored</c>, which names only the first.
///         </item>
///         <item><c>custom_skip/</c> — ignored by the <c>ignore</c> key of <c>.madq_roslynmcp.json</c>.</item>
///         <item><c>nested/deep/Only.txt</c> — an ordinary file reachable only by suffix match.</item>
///     </list>
///     The <c>.gitignore</c> also carries a glob (<c>*.log</c>), which the conservative reader must
///     skip without rejecting the file, and two negations: <c>!keep</c>, which names nothing that
///     was ignored, and <c>!reincluded/</c>, which undoes the <c>reincluded/</c> line before it.
/// </summary>
static class IgnoredDirectoryTests
{
	internal static async Task<TestGroup> BuildAsync(TestContext ctx, string serverLogDir, string serverLogGlob)
	{
		
		var fx     = TestFixtures.NewMsBuildProject("IgnoredDirs");
		var csproj = fx.Csproj!;
		
		fx.Write("Probe.cs",                     "class Probe { }\n");
		fx.Write("node_modules/pkg/index.js",    "module.exports = 1;\n");
		fx.Write("node_modules/pkg/Stray.cs",    "class Stray { }\n");
		fx.Write("node_modules/pkg/Hidden.txt",  "hidden\n");
		fx.Write("dist/out.txt",                 "built\n");
		fx.Write("anchored/a.txt",               "a\n");
		fx.Write("sub/anchored/b.txt",           "b\n");
		fx.Write("custom_skip/c.txt",            "c\n");
		fx.Write("nested/deep/Only.txt",         "only\n");
		fx.Write("reincluded/r.txt",             "r\n");
		fx.Write(".gitignore",                   "# build output\nreincluded/\n!reincluded/\ndist/\n*.log\n!keep\n/anchored\n");
		fx.Write(".madq_roslynmcp.json",         "{ \"ignore\": [\"custom_skip\"] }\n");
		
		// Calls a tool and returns (no-protocol-error, parsed JSON data).
		async Task<(bool ok, JsonNode? data)> Call(string tool, object args)
		{
			
			await ctx.SendAsync(new { jsonrpc = "2.0", id = ctx.NextId(), method = "tools/call",
				@params = new { name = tool, arguments = args } });
			
			var resp = await ctx.ReceiveAsync();
			var text = resp?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
			
			JsonNode? data = null;
			
			try { data = JsonNode.Parse(text); }
			catch { }
			
			return (resp?["error"] is null && text.Length > 0, data);
		}
		
		// Every path roslyn_list_files returns for a pattern, forward-slashed. take: 500 is the
		// tool's maximum and far above what the fixture holds.
		async Task<string[]?> ListAsync(string pattern)
		{
			
			var (ok, data) = await Call("roslyn_list_files", new { projectPath = csproj, pattern, take = 500 });
			
			if(!ok || data?["files"] is not JsonArray files)
				
				return null;
			
			return [..files.Select(file => file!.GetValue<string>().Replace('\\', '/'))];
		}
		
		// Null means the probe itself failed. reload_pending is omitted from the JSON when false.
		async Task<bool?> ReloadPendingAsync()
		{
			
			var (ok, data) = await Call("roslyn_check_drift", new { projectPath = csproj });
			
			if(!ok || data?["drifted"] is null)
				
				return null;
			
			return data["reload_pending"]?.GetValue<bool>() ?? false;
		}
		
		// Services a pending reload and waits for the flag to clear, as in ReloadFlaggingTests.
		async Task<bool> SettleAsync()
		{
			
			var deadline = DateTime.UtcNow.AddSeconds(15);
			
			while(DateTime.UtcNow < deadline) {
				
				await Call("roslyn_get_diagnostics", new { projectPath = csproj, take = 0, severity = "errors" });
				
				if(await ReloadPendingAsync() == false)
					
					return true;
				
				await Task.Delay(250);
			}
			
			return false;
		}
		
		// Polls for reload_pending to become true. The watchers start on a background thread after
		// the load and events are debounced for 300 ms, so the flag is never immediate.
		async Task<bool> BecomesPendingAsync(TimeSpan within)
		{
			
			var deadline = DateTime.UtcNow + within;
			
			while(DateTime.UtcNow < deadline) {
				
				if(await ReloadPendingAsync() == true)
					
					return true;
				
				await Task.Delay(200);
			}
			
			return false;
		}
		
		// What the server's log says about watching this fixture, for a failure's message (#339).
		// The adoption test failed on macOS now and then, and the log of the run that failed was
		// gone: whether the new directory was never seen, or seen before its file was there, or
		// the watchers were not live yet, is in these lines and nowhere else.
		string WatchLog()
		{
			if(!ServerLog.TryRead(serverLogDir, serverLogGlob, 1, out var lines, out var failure))
				
				return $"the server's log could not be read: {failure}";
			
			// The fixture by its directory's name: a path in a log line is JSON, its separators escaped.
			var name = Path.GetFileName(fx.Dir);
			
			string[] said = [..
				lines.Where(line => line.Contains(name, StringComparison.Ordinal) && (line.Contains("Watch", StringComparison.Ordinal) || line.Contains("Reload", StringComparison.Ordinal)))
					.Select(line => {
						
						try {
							
							var entry = JsonNode.Parse(line);
							
							return $"{entry?["timestamp"]?.GetValue<string>()} {entry?["message"]?.GetValue<string>()}";
						}
						catch(System.Text.Json.JsonException) {
							
							return line;
						}
					})
					.TakeLast(15)
			];
			
			return said.Length == 0 ? "the server's log has no line about watching this fixture" : string.Join(" | ", said);
		}
		
		await fx.WarmAsync(ctx);
		
		var tests = new List<TestCase> {
			
			// The headline listing case. Each excluded path is ignored by a different rule —
			// built-in name, .gitignore plain name, .gitignore anchored path, configured name,
			// build output — so one missing rule fails with the path that names it.
			// sub/anchored/b.txt is the control for the anchored entry: "/anchored" names the
			// root-level directory only, and matching it at any depth would hide this file.
			// reincluded/r.txt is the control for negation: the .gitignore ignores "reincluded/"
			// and then re-includes it, so git tracks the file and hiding it would be wrong.
			new("ignored dirs: list_files skips ignored directories and keeps the rest",
				async () => {
					
					var files = await ListAsync("**/*");
					
					if(files is null)
						
						return (false, "FAIL  (roslyn_list_files returned no files array)");
					
					string[] mustBeAbsent  = ["node_modules/pkg/index.js", "dist/out.txt", "anchored/a.txt", "custom_skip/c.txt"];
					string[] mustBePresent = ["Probe.cs", "sub/anchored/b.txt", "nested/deep/Only.txt", ".gitignore", "reincluded/r.txt"];
					
					var leaked  = mustBeAbsent.Where(files.Contains).ToArray();
					var missing = mustBePresent.Where(path => !files.Contains(path)).ToArray();
					var objLeak = files.FirstOrDefault(path => path.StartsWith("obj/", StringComparison.OrdinalIgnoreCase));
					
					if(leaked.Length > 0)
						
						return (false, $"FAIL  (listed from an ignored directory: {string.Join(", ", leaked)})");
					
					if(objLeak is not null)
						
						return (false, $"FAIL  (listed build output: {objLeak})");
					
					if(missing.Length > 0)
						
						return (false, $"FAIL  (missing from the listing: {string.Join(", ", missing)})");
					
					return (true, $"PASS  ({files.Length} files, none from an ignored directory)");
				}),
			
			// The escape hatch: a pattern that spells an ignored directory out is a request for it.
			// Without this an agent could no longer reach bin/ or obj/ through the tool at all.
			new("ignored dirs: list_files enters an ignored directory the pattern names",
				async () => {
					
					var files = await ListAsync("node_modules/**/*");
					
					if(files is null)
						
						return (false, "FAIL  (roslyn_list_files returned no files array)");
					
					return files.Contains("node_modules/pkg/index.js")
						? (true,  $"PASS  ({files.Length} files under node_modules)")
						: (false, $"FAIL  (expected node_modules/pkg/index.js, got: {string.Join(", ", files)})");
				}),
			
			// The suffix-match fallback of ResolveFilePath walks the tree. "deep/Only.txt" is not a
			// root-relative path, so only that walk can find it — proving the pruned walk still
			// resolves ordinary files. "Hidden.txt" exists only under node_modules, which the walk
			// no longer enters, so it must now fail rather than resolve to a package file.
			new("ignored dirs: a relative filePath resolves by suffix but not into an ignored directory",
				async () => {
					
					var (foundOk, found) = await Call("roslyn_read_file", new { projectPath = csproj, filePath = "deep/Only.txt" });
					
					if(!foundOk || found?["error"] is not null || found?["total_lines"] is null)
						
						return (false, $"FAIL  (deep/Only.txt did not resolve: {found?["error"]?.GetValue<string>() ?? "no result"})");
					
					var (_, hidden) = await Call("roslyn_read_file", new { projectPath = csproj, filePath = "Hidden.txt" });
					
					return hidden?["error"] is not null
						? (true,  "PASS  (suffix match resolved; node_modules was not searched)")
						: (false, "FAIL  (Hidden.txt resolved into node_modules)");
				}),
			
			// The fixture root has node_modules below it, so it is watched non-recursively with one
			// recursive watcher per remaining child. A directory created afterwards has no watcher;
			// the root's watcher must adopt it and report the file already inside, or new source
			// folders would silently stop being picked up. The file is written straight after the
			// directory is created — the realistic sequence, and the one a late watcher misses.
			// Written by the harness, not a tool: tool writes sync the workspace without the watcher.
			// It also establishes, for the test after it, that the watchers are live.
			new("ignored dirs: a .cs in a directory created after load flags a reload",
				async () => {
					
					if(!await SettleAsync())
						
						return (false, "FAIL  (precondition: reload_pending never cleared)");
					
					var written = DateTime.UtcNow;
					
					fx.Write("fresh/Inside.cs", "class InsideFresh { }\n");
					
					return await BecomesPendingAsync(TimeSpan.FromSeconds(10))
						? (true,  "PASS  (reload_pending=True after fresh/Inside.cs appeared)")
						: (false, $"FAIL  (reload_pending stayed False for 10 s after fresh/Inside.cs was written at {written:O}. The server's log: {WatchLog()})");
				}),
			
			// An adopted directory is listed again half a second and three seconds after its
			// adoption (#339), for a file that landed before its watcher delivered. That look
			// back must report nothing the first listing or the reload already took: here the
			// reload it caused is serviced, and no second one may be flagged while the look
			// back runs. What it is for, a file no watcher saw, cannot be staged from outside
			// the server; that it costs nothing when there is no such file can.
			new("ignored dirs: the look back at an adopted directory flags no second reload",
				async () => {
					
					if(!await SettleAsync())
						
						return (false, "FAIL  (precondition: reload_pending never cleared)");
					
					fx.Write("later/Inside.cs", "class InsideLater { }\n");
					
					if(!await BecomesPendingAsync(TimeSpan.FromSeconds(10)))
						
						return (false, $"FAIL  (reload_pending stayed False for 10 s after later/Inside.cs was written. The server's log: {WatchLog()})");
					
					if(!await SettleAsync())
						
						return (false, "FAIL  (the reload for later/Inside.cs was not serviced)");
					
					// Past the second look back, counted generously from the write.
					await Task.Delay(4000);
					
					var pending = await ReloadPendingAsync();
					
					return pending == false
						? (true,  "PASS  (no reload flagged by the look back)")
						: (false, $"FAIL  (expected reload_pending=False after the look back, got {pending?.ToString() ?? "no result"}. The server's log: {WatchLog()})");
				}),
			
			// The watcher must not see ignored trees. A new .cs anywhere it does watch flags a
			// reload (see the next test), so "no flag" after a .cs lands in node_modules and in the
			// configured directory is the observable for "not watched, or dropped". 1.5 s covers
			// the 300 ms debounce several times over. This runs after the adoption test on
			// purpose: the watchers start in the background, and only an event already seen
			// from them proves they are live — without that, a pass here could be a write that
			// landed before any watcher existed.
			new("ignored dirs: a new .cs under an ignored directory does not flag a reload",
				async () => {
					
					if(!await SettleAsync())
						
						return (false, "FAIL  (precondition: reload_pending never cleared)");
					
					fx.Write("node_modules/pkg/Added.cs", "class AddedInNodeModules { }\n");
					fx.Write("custom_skip/Added.cs",      "class AddedInCustomSkip { }\n");
					
					await Task.Delay(1500);
					
					var pending = await ReloadPendingAsync();
					
					return pending == false
						? (true,  "PASS  (reload_pending=False after writes under node_modules and custom_skip)")
						: (false, $"FAIL  (expected reload_pending=False, got {pending?.ToString() ?? "no result"})");
				}),
		};
		
		return new TestGroup($"Ignored Directories ({tests.Count} tests)", tests, Teardown: async () =>
		{
			
			// Leave the server settled for the next group, then drop the temp project.
			await SettleAsync();
			
			fx.Dispose();
		});
	}
}
