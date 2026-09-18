using System.Text.Json.Nodes;

/// <summary>
///     Runs last and asserts the run did not disturb the dogfood workspace (#274). The harness used to
///     write ~16 scratch .cs files into src/RoslynMcp; each one forced a full reload of the solution in
///     the harness's own server and in the developer's live one. Fixtures now live in temp projects, so
///     any reload flagged for a path inside the repo is a regression — and since #273 the server log
///     names the file that caused it, so a failure here says which test wrote where.
/// </summary>
static class WorkspaceHygieneTests
{
	static readonly string[] ScratchPatterns = ["_*_.cs", ".test_*", "0_FindUnused*"];
	
	// Floor for "this really is the log the run wrote". Without it, a log that was truncated,
	// redirected elsewhere, or never written passes the reload assertion on missing evidence. This
	// group only runs in a full run, which logs one TOOL entry per tool call and makes hundreds.
	const int MinExpectedLogEntries = 50;
	
	/// <param name="ctx">Shared harness context (repo root).</param>
	/// <param name="serverLogDir">Directory the harness pointed ROSLYNMCP_LOG_PATH at.</param>
	/// <param name="serverLogGlob">
	///     Glob for the harness server's NDJSON log: the harness sets ROSLYNMCP_LOG_PATH to a per-run
	///     stem and the server inserts its own PID before the extension.
	/// </param>
	internal static TestGroup Build(TestContext ctx, string serverLogDir, string serverLogGlob)
	{
		var tests = new List<TestCase> {
			
			// Every "Reload — Flagged (<reason>): <path>" line is the decision point that costs a reload
			// (#273). One inside the repo means a test wrote a compilation input into the dogfood tree.
			// "Reloading workspace (Solution: …RoslynMcp.slnx)" is the reload itself — counted too, so a
			// flag raised by something the log did not attribute still fails the run.
			new("workspace hygiene: no reload flagged for a path inside the repo",
				() => Task.FromResult(AssertNoDogfoodReload(ctx, serverLogDir, serverLogGlob))),
			
			// The same property from the file system's side: nothing may be left in src/RoslynMcp.
			new("workspace hygiene: no scratch files left in src/RoslynMcp",
				() => Task.FromResult(AssertNoScratchFiles(ctx))),
		};
		
		return new TestGroup($"Workspace Hygiene ({tests.Count} tests)", tests);
	}
	
	static (bool pass, string message) AssertNoDogfoodReload(TestContext ctx, string logDir, string logGlob)
	{
		string[] lines;
		
		try {
			
			if(!Directory.Exists(logDir))
				
				return (false, $"FAIL  (server log directory not found: {logDir})");
			
			// Every rotation sibling, not just the newest file: FileLogger rotates to "<log>.1",
			// "<log>.2" at maxFileSizeBytes, and reading only the current file would silently drop
			// the early entries — the ones a dogfood reload would appear in — turning a real
			// regression into a PASS. The glob ends in '*' for that reason.
			var logFiles = Directory.EnumerateFiles(logDir, logGlob).ToArray();
			
			if(logFiles.Length == 0)
				
				return (false, $"FAIL  (server log not found: {Path.Combine(logDir, logGlob)})");
			
			lines = [.. logFiles.SelectMany(ReadSharedLines)];
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			return (false, $"FAIL  (server log unreadable: {ex.Message})");
		}
		
		// A log this short means it was truncated, redirected, or never written — in which case the
		// assertion below would pass on missing evidence rather than on a clean run. Every run logs
		// one TOOL entry per tool call, and the harness makes well over a hundred.
		if(lines.Length < MinExpectedLogEntries)
			
			return (false, $"FAIL  (only {lines.Length} log entries — expected at least {MinExpectedLogEntries}; the log is not the one this run wrote)");
		
		var repoRoot  = ctx.RepoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var offenders = new List<string>();
		var reloads   = 0;
		
		foreach(var line in lines) {
			
			string? message;
			
			try {
				message = JsonNode.Parse(line)?["message"]?.GetValue<string>();
			}
			catch(System.Text.Json.JsonException) {
				continue;
			}
			
			if(message is null)
				continue;
			
			if(message.StartsWith("Reload — Flagged", StringComparison.Ordinal)
				&& message.Contains(repoRoot, StringComparison.OrdinalIgnoreCase))
				
				offenders.Add(message);
			
			if(message.StartsWith("Reload — Reloading workspace (Solution:", StringComparison.Ordinal)
				&& message.Contains(repoRoot, StringComparison.OrdinalIgnoreCase))
				
				reloads++;
		}
		
		if(offenders.Count == 0 && reloads == 0)
			
			return (true, $"PASS  ({lines.Length} log entries, no dogfood reload)");
		
		var sample = string.Join(" | ", offenders.Take(3));
		
		return (false, $"FAIL  ({offenders.Count} reload flag(s) inside the repo, {reloads} dogfood reload(s): {sample})");
	}
	
	static (bool pass, string message) AssertNoScratchFiles(TestContext ctx)
	{
		var dogfood  = Path.Combine(ctx.RepoRoot, "src", "RoslynMcp");
		var leftover = new List<string>();
		
		foreach(var pattern in ScratchPatterns) {
			
			try {
				leftover.AddRange(Directory.EnumerateFiles(dogfood, pattern, SearchOption.TopDirectoryOnly).Select(Path.GetFileName)!);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				
				return (false, $"FAIL  (cannot enumerate {dogfood}: {ex.Message})");
			}
		}
		
		return leftover.Count == 0
			? (true,  "PASS  (src/RoslynMcp clean)")
			: (false, $"FAIL  (left behind: {string.Join(", ", leftover)})");
	}
	
	// The server still has the file open for appending; read through a shared handle.
	static string[] ReadSharedLines(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using var reader = new StreamReader(stream);
		
		var lines = new List<string>();
		
		while(reader.ReadLine() is { } line)
			lines.Add(line);
		
		return [.. lines];
	}
}
