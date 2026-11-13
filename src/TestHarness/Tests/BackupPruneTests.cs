using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

// The pass that deletes expired backups, and the calls it used to hold up (#445). The backup store
// is built inside the first call of a tool that writes, and its constructor ran that pass before
// returning: one meta file read per backed-up file path, thousands on a machine in use, with
// every other writing call queued behind the same singleton. Two calls waited 76 and 16 seconds
// before their tools ran, and the harness, which had given up after 60, read each later answer
// one call late. The pass now runs on a thread of its own, started with the server.
//
// A server of its own, because the pass runs once per process and only in a process that is a
// pruning run: ROSLYNMCP_PRUNE_MIN_RUNS=1 makes every start one. Its backup directory is seeded
// with expired snapshots.
//
// How long a pass takes depends on the machine, so the test does not race it: it holds the mutex
// the pass needs before the server starts. The pass then cannot begin until the test lets go,
// and whatever the server answers meanwhile, it answers with the pass still ahead of it.
static class BackupPruneTests
{
	const int SeededDirectories = 200;
	
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
		
		// The name BackupStore gives the mutex of one store: a hash of the store's path. Kept in step
		// with it by the test itself, which fails when the server prunes while this one is held.
		var mutexName = $@"Global\RoslynMcp_BackupPruner_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(store.Dir).ToUpperInvariant())))[..16]}";
		
		// The "Backups" lines of the server's log, oldest first.
		string[] PruneLines()
		{
			if(!ServerLog.TryRead(logs, $"{stem}*", 1, out var lines, out _))
				
				return [];
			
			var found = new List<string>();
			
			foreach(var line in lines) {
				
				try {
					
					if(JsonNode.Parse(line)?["message"]?.GetValue<string>() is { } message && message.StartsWith("Backups", StringComparison.Ordinal))
						found.Add(message);
				}
				catch(System.Text.Json.JsonException) { }
			}
			
			return [..found];
		}
		
		int SeedsLeft() => Directory.EnumerateDirectories(store.Dir).Count(dir => Path.GetFileName(dir).StartsWith("seed", StringComparison.Ordinal));
		
		var tests = new List<TestCase> {
			
			// With the pass held back, the server must start and answer a write: the first call of a
			// tool that takes the backup store, the call the pass used to run inside. Nothing may be
			// pruned by then, or the mutex held here is not the one the server waits for and the
			// test has shown nothing. Once the mutex is let go the pass must do its work all the
			// same (every seeded directory gone) and must not cost the write its own snapshots,
			// which were saved before the pass began: local history still lists them.
			new("backup prune: the server answers a write while the prune of expired backups is still ahead", async () => {
				
				using var held    = new ManualResetEventSlim();
				using var release = new ManualResetEventSlim();
				
				// A mutex belongs to the thread that took it, and a test's continuations do not stay
				// on one: a thread of its own takes it, holds it, and lets it go when told.
				var holder = new Thread(() => {
					
					using var mutex = new Mutex(false, mutexName);
					
					try {
						mutex.WaitOne();
					}
					catch(AbandonedMutexException) { }
					
					held.Set();
					release.Wait();
					mutex.ReleaseMutex();
				}) { IsBackground = true };
				
				holder.Start();
				
				if(!held.Wait(TimeSpan.FromSeconds(30))) {
					
					release.Set();
					
					return Skip.SetupUnavailable("the store's prune mutex could not be taken within 30 s");
				}
				
				var (process, server) = await DefaultWorkspaceTests.StartServerLoggingToAsync(ctx, fx.Dir, Path.Combine(logs, $"{stem}.log"), environment);
				
				try {
					
					if(server is null)
						return (false, "FAIL  (server did not answer initialize while the prune was held back)");
					
					var sw      = Stopwatch.StartNew();
					var written = await server.CallToolJsonAsync("roslyn_write_file", new { filePath = "Probe.cs", content = "public class Probe { public int Value => 1; }\n", projectPath = fx.Dir });
					var answered = sw.ElapsedMilliseconds;
					
					if(written?["error"] is not null)
						return (false, $"FAIL  (roslyn_write_file: {written.ToJsonString()})");
					
					if(PruneLines() is { Length: > 0 } early || SeedsLeft() < SeededDirectories)
						return (false, $"FAIL  (the server pruned while the test held '{mutexName}': the store's mutex has another name; {SeedsLeft()} of {SeededDirectories} seeded directories left)");
					
					release.Set();
					
					string[] lines = [];
					
					while(sw.Elapsed < TimeSpan.FromSeconds(90) && lines.Length == 0) {
						
						await Task.Delay(250);
						lines = PruneLines();
					}
					
					if(!lines.Any(line => line.Contains("pruned in", StringComparison.Ordinal)))
						return (false, $"FAIL  (no backup prune was logged within 90 s of the mutex being free: {string.Join(" | ", lines)})");
					
					if(SeedsLeft() is var left and > 0)
						return (false, $"FAIL  ({left} of {SeededDirectories} expired backup directories are still there after the pass: {lines[0]})");
					
					var history = await server.CallToolJsonAsync("roslyn_local_history", new { action = "list", filePath = "Probe.cs", projectPath = fx.Dir });
					var kept    = history?["items"]?.AsArray().Count ?? 0;
					
					return kept > 0
						? (true, $"PASS  (write answered in {answered} ms with the pass held back; then {lines[0]})")
						: (false, $"FAIL  (the write's own snapshots are gone after the pass: {history?.ToJsonString()})");
				}
				finally {
					
					release.Set();
					await DefaultWorkspaceTests.StopServerAsync(process, server);
				}
			}),
		};
		
		return new TestGroup($"Backup Prune ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); store.Dispose(); return Task.CompletedTask; });
	}
}
