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
				// Nothing may leave the thread: an exception that does ends the harness without its
				// summary line. What went wrong is the test's to report.
				Exception? holderFailure = null;
				
				var holder = new Thread(() => {
					
					try {
						
						using var mutex = new Mutex(false, mutexName);
						
						try {
							mutex.WaitOne();
						}
						catch(AbandonedMutexException) { }
						
						held.Set();
						release.Wait();
						mutex.ReleaseMutex();
					}
					catch(Exception ex) {
						
						holderFailure = ex;
						held.Set();
					}
				}) { IsBackground = true };
				
				holder.Start();
				
				// The name is this fixture's alone, so the wait is for the thread to start. Joined on
				// every way out below, before the events it waits on are disposed.
				held.Wait();
				
				if(holderFailure is not null) {
					
					holder.Join();
					
					return (false, $"FAIL  (the store's prune mutex could not be taken: {holderFailure.GetType().Name}: {holderFailure.Message})");
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
					holder.Join();
					await DefaultWorkspaceTests.StopServerAsync(process, server);
				}
			}),
			
			// What the pass keeps and what it takes, with one directory for each case (#474):
			//
			//   recent   a snapshot of today, its file's folder gone   skipped: its name says it is newer than
			//                                                          anything the pass could remove, so
			//                                                          its meta file is not opened
			//   orphan   30 days old, its file's folder gone           removed: nothing to restore it into
			//   deleted  30 days old, the file gone, the folder there  kept: a file deleted from a project
			//                                                          that is still there is what local
			//                                                          history is for
			//   live     30 days old, the file there                   kept
			//   expired  100 days old, the file there                  removed: past the retention of 90
			//
			// On the machine this came from, 7,318 of 7,646 backup directories were of the second
			// kind (snapshots of test fixtures deleted minutes later) and waited ninety days, and
			// the pass opened a meta file in every one of them, also in those of the first kind.
			new("backup prune: orphans go after a week, a deleted file in a live folder stays, and a recent directory is not opened", async () => {
				
				var orphans = TestFixtures.NewAdhocDir("BackupPruneOrphans");
				var name    = $"backuporphans-{Guid.NewGuid():N}";
				var gone    = Path.Combine(TestFixtures.TempRoot, $"gone-{Guid.NewGuid():N}");
				
				void Seed(string hash, int ageDays, string absolutePath)
				{
					var made   = DateTimeOffset.UtcNow.AddDays(-ageDays);
					var millis = made.ToUnixTimeMilliseconds();
					var path   = absolutePath.Replace("\\", "\\\\");
					
					orphans.Write($"{hash}/meta.json",
						$"{{ \"{hash}_{millis}_ab12_pre\": {{ \"absolutePath\": \"{path}\", \"projectPath\": \"{path}\", \"operation\": \"seed\", \"timestamp\": \"{made.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}\", \"fileSizeBytes\": 3, \"phase\": \"pre\" }} }}\n");
					orphans.Write($"{hash}/{Path.GetFileName(absolutePath)}_{millis}_ab12.pre.bak", "old");
				}
				
				Seed("recent",  0,   Path.Combine(gone, "Recent.cs"));
				Seed("orphan",  30,  Path.Combine(gone, "Orphan.cs"));
				Seed("deleted", 30,  fx.PathOf("Deleted.cs"));
				Seed("live",    30,  fx.PathOf("Probe.cs"));
				Seed("expired", 100, fx.PathOf("Probe.cs"));
				
				var (process, server) = await DefaultWorkspaceTests.StartServerLoggingToAsync(ctx, fx.Dir, Path.Combine(logs, $"{name}.log"),
					new Dictionary<string, string> { ["ROSLYNMCP_BACKUP_PATH"] = orphans.Dir, ["ROSLYNMCP_PRUNE_MIN_RUNS"] = "1" });
				
				try {
					
					if(server is null)
						return (false, "FAIL  (server did not answer initialize)");
					
					var    sw   = Stopwatch.StartNew();
					string line = "";
					
					while(sw.Elapsed < TimeSpan.FromSeconds(90) && line.Length == 0) {
						
						await Task.Delay(250);
						
						if(ServerLog.TryRead(logs, $"{name}*", 1, out var lines, out _))
							line = lines.FirstOrDefault(entry => entry.Contains("Backups") && entry.Contains("pruned in")) ?? "";
					}
					
					if(line.Length == 0)
						return (false, "FAIL  (no backup prune was logged within 90 s)");
					
					string[] left     = [..Directory.EnumerateDirectories(orphans.Dir).Select(dir => Path.GetFileName(dir)!).Order()];
					string[] expected = ["deleted", "live", "recent"];
					
					if(!left.SequenceEqual(expected))
						return (false, $"FAIL  (directories left: [{string.Join(", ", left)}], expected [{string.Join(", ", expected)}])");
					
					return line.Contains("5 directories, 1 skipped unread, 2 expired snapshot(s) removed, 1 of them orphaned")
						? (true, "PASS")
						: (false, $"FAIL  (the pass did the right thing and logged another: {line})");
				}
				finally {
					
					await DefaultWorkspaceTests.StopServerAsync(process, server);
					orphans.Dispose();
				}
			}),
			
		};
		
		return new TestGroup($"Backup Prune ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); store.Dispose(); return Task.CompletedTask; });
	}
}
