using System.Diagnostics;
using System.Text.Json.Nodes;

// The pass that deletes expired backups, and the call it used to hold up (#445). The backup store
// is built inside the first call of a tool that writes, and its constructor ran that pass before
// returning: one meta file read per backed-up file path, thousands on a machine in use, with
// every other writing call queued behind the same singleton. Two calls waited 76 and 16 seconds
// before their tools ran, and the harness, which had given up after 60, read each later answer
// one call late. The pass now runs on a thread of its own.
//
// A server of its own, because the pass runs once per process and only in a process that is a
// pruning run: ROSLYNMCP_PRUNE_MIN_RUNS=1 makes every start one. Its backup directory is seeded
// with expired snapshots, enough of them that deleting them takes far longer than one write.
static class BackupPruneTests
{
	const int SeededDirectories = 4_000;
	
	internal static TestGroup Build(TestContext ctx)
	{
		var fx    = TestFixtures.NewAdhocDir("BackupPrune");
		var store = TestFixtures.NewAdhocDir("BackupPruneStore");
		var stem  = $"backupprune-{Guid.NewGuid():N}";
		var logs  = Path.Combine(TestFixtures.TempRoot, "logs");
		
		fx.Write("Probe.cs", "public class Probe { }\n");
		
		// One directory per backed-up path, as the store lays them out: a meta file naming one
		// snapshot from the year 2000 and the snapshot beside it.
		for(var index = 0; index < SeededDirectories; index++) {
			
			var hash = $"seed{index:D6}";
			
			store.Write($"{hash}/meta.json",
				$"{{ \"{hash}_946684800000_ab12_pre\": {{ \"absolutePath\": \"/gone/Old{index}.cs\", \"projectPath\": \"/gone\", \"operation\": \"seed\", \"timestamp\": \"2000-01-01T00:00:00.000Z\", \"fileSizeBytes\": 3, \"phase\": \"pre\" }} }}\n");
			store.Write($"{hash}/Old{index}.cs_946684800000_ab12.pre.bak", "old");
		}
		
		var environment = new Dictionary<string, string> {
			["ROSLYNMCP_BACKUP_PATH"]    = store.Dir,
			["ROSLYNMCP_PRUNE_MIN_RUNS"] = "1",
		};
		
		// The messages of the server's log entries, oldest first: a TOOL entry as "TOOL <name>".
		string[] Entries()
		{
			if(!ServerLog.TryRead(logs, $"{stem}*", 1, out var lines, out _))
				
				return [];
			
			var entries = new List<string>();
			
			foreach(var line in lines) {
				
				try {
					
					var entry = JsonNode.Parse(line);
					
					entries.Add(entry?["level"]?.GetValue<string>() == "TOOL"
						? $"TOOL {entry["tool_name"]?.GetValue<string>()}"
						: entry?["message"]?.GetValue<string>() ?? "");
				}
				catch(System.Text.Json.JsonException) { }
			}
			
			return [..entries];
		}
		
		var tests = new List<TestCase> {
			
			// The write is the process's first call of a tool that takes the backup store, so it is
			// the call the pass used to run inside. Its log entry must come before the pass's own
			// line: with the pass in the constructor it always came after. Then the pass must have
			// done its work all the same (every seeded directory gone, none left for a second
			// look), and must not have cost the write its own snapshots, which were saved while
			// the pass was deleting around them: local history still lists them.
			new("backup prune: the first write is answered while the expired backups are still being deleted", async () => {
				
				var (process, server) = await DefaultWorkspaceTests.StartServerLoggingToAsync(ctx, fx.Dir, Path.Combine(logs, $"{stem}.log"), environment);
				
				try {
					
					if(server is null)
						return (false, "FAIL  (server did not answer initialize)");
					
					// The workspace is loaded by a call that does not take the backup store, so the write
					// below costs a file write and no more: milliseconds against the seconds the pass
					// needs for thousands of directories, on any machine. Without it the write would
					// include the load, and a fast disk could finish the pass first.
					await server.CallToolJsonAsync("roslyn_read_file", new { filePath = "Probe.cs", projectPath = fx.Dir });
					
					var sw      = Stopwatch.StartNew();
					var written = await server.CallToolJsonAsync("roslyn_write_file", new { filePath = "Probe.cs", content = "public class Probe { public int Value => 1; }\n", projectPath = fx.Dir });
					var answered = sw.ElapsedMilliseconds;
					
					if(written?["error"] is not null)
						return (false, $"FAIL  (roslyn_write_file: {written.ToJsonString()})");
					
					string[] entries = [];
					
					while(sw.Elapsed < TimeSpan.FromSeconds(90)) {
						
						entries = Entries();
						
						if(entries.Any(entry => entry.StartsWith("Backups", StringComparison.Ordinal)))
							break;
						
						await Task.Delay(250);
					}
					
					if(entries.Any(entry => entry.Contains("prune skipped", StringComparison.Ordinal)))
						return Skip.SetupUnavailable("another pruner held the prune mutex for minutes, so this server did not prune");
					
					var pruned = Array.FindIndex(entries, entry => entry.StartsWith("Backups", StringComparison.Ordinal) && entry.Contains("pruned in", StringComparison.Ordinal));
					var wrote  = Array.IndexOf(entries, "TOOL write_file");
					
					if(pruned < 0)
						return (false, "FAIL  (the server logged no backup prune within 90 s)");
					
					if(wrote < 0 || wrote > pruned)
						return (false, $"FAIL  (the write's entry is at {wrote}, the prune's at {pruned}: the call waited for the pass)");
					
					var left = Directory.EnumerateDirectories(store.Dir).Count(dir => Path.GetFileName(dir).StartsWith("seed", StringComparison.Ordinal));
					
					if(left > 0)
						return (false, $"FAIL  ({left} of {SeededDirectories} expired backup directories are still there after the pass: {entries[pruned]})");
					
					var history = await server.CallToolJsonAsync("roslyn_local_history", new { action = "list", filePath = "Probe.cs", projectPath = fx.Dir });
					var kept    = history?["items"]?.AsArray().Count ?? 0;
					
					return kept > 0
						? (true, $"PASS  (answered in {answered} ms; {entries[pruned]})")
						: (false, $"FAIL  (the write's own snapshots are gone after the pass: {history?.ToJsonString()})");
				}
				finally {
					await DefaultWorkspaceTests.StopServerAsync(process, server);
				}
			}),
		};
		
		return new TestGroup($"Backup Prune ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); store.Dispose(); return Task.CompletedTask; });
	}
}
