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
			
			// #303: MSBuild entries name the MSBuild flavor (SDK/VS), not just "MSB". Lives here because
			// this group already reads the harness server's log, and only a full run produces one.
			new("workspace hygiene: MSBuild entries are labelled SDK once resolved",
				() => Task.FromResult(AssertSdkWorkspaceLabel(serverLogDir, serverLogGlob))),
			
			// #303: tools that resolve only a root or a solution must still record Adhoc.
			new("workspace hygiene: root/solution-only tools label an adhoc workspace ADH",
				() => AssertAdhocLabelAsync(ctx, serverLogDir, serverLogGlob)),
		};
		
		return new TestGroup($"Workspace Hygiene ({tests.Count} tests)", tests);
	}
	
	static (bool pass, string message) AssertNoDogfoodReload(TestContext ctx, string logDir, string logGlob)
	{
		if(!TryReadLog(logDir, logGlob, out var lines, out var failure))
			
			return (false, failure);
		
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
	
	/// <summary>
	///     Guards #303: TOOL entries used to log a bare <c>MSB</c> for every MSBuildWorkspace call, so an SDK
	///     session and a VS session were indistinguishable in the log. The harness server runs in auto mode
	///     against SDK-style projects (the dogfood solution and every <see cref="TestFixtures"/> project), so
	///     auto-detection resolves Sdk on the first MSBuild load. From that entry on, every MSBuild entry must
	///     read <c>SDK</c>; before it, an undecided auto session may still log the generic <c>MSB</c>.
	///     <c>ADH</c> entries (the adhoc fixtures) are allowed anywhere. The check is order-sensitive on
	///     purpose: a label computed from stale state would show up as an <c>MSB</c> after the first <c>SDK</c>.
	///     Against the pre-fix server every MSBuild entry reads <c>MSB</c>, so the "at least one SDK" half fails.
	/// </summary>
	static (bool pass, string message) AssertSdkWorkspaceLabel(string logDir, string logGlob)
	{
		if(!TryReadLog(logDir, logGlob, out var lines, out var failure))
			
			return (false, failure);
		
		var sdkEntries  = 0;
		var msbAfterSdk = 0;
		var otherLabels = new HashSet<string>(StringComparer.Ordinal);
		
		foreach(var line in lines) {
			
			string? mode;
			
			try {
				mode = JsonNode.Parse(line)?["workspace_mode"]?.GetValue<string>();
			}
			catch(System.Text.Json.JsonException) {
				continue;
			}
			
			switch(mode) {
				
				case null or "ADH":
					break;
				
				case "SDK":
					sdkEntries++;
					break;
				
				case "MSB":
					if(sdkEntries > 0)
						msbAfterSdk++;
					
					break;
				
				default:
					otherLabels.Add(mode);
					break;
			}
		}
		
		if(sdkEntries == 0)
			
			return (false, "FAIL  (no TOOL entry is labelled SDK — MSBuild calls in an SDK session are not distinguished)");
		
		if(msbAfterSdk > 0 || otherLabels.Count > 0)
			
			return (false, $"FAIL  ({msbAfterSdk} MSB entr(ies) after the mode resolved to SDK; unexpected labels: [{string.Join(", ", otherLabels)}])");
		
		return (true, $"PASS  ({sdkEntries} SDK entries, no MSB after resolution)");
	}
	
	/// <summary>
	///     Guards the second half of #303. The tool scope defaults to the MSBuild label, and only the guards
	///     that record the mode override it. <c>roslyn_list_files</c> resolves nothing but its root
	///     (<c>TryResolveRoot</c>) and <c>roslyn_search_files</c> nothing but the solution and root
	///     (<c>TryResolveSolution</c>) — neither recorded the mode, so both logged an AdhocWorkspace call as
	///     <c>SDK</c> (before the SDK/VS split: <c>MSB</c>). The check makes one call to each against a bare
	///     temp directory (no .csproj, so AdhocWorkspace), using a pattern no other test uses as the log
	///     subject, then finds exactly those two TOOL entries in the server's log and requires <c>ADH</c>.
	///     The scope writes its entry as the tool method returns, before the response is sent, so the
	///     entries exist once the calls complete. Against the pre-fix server both entries read <c>SDK</c>.
	/// </summary>
	static async Task<(bool pass, string message)> AssertAdhocLabelAsync(TestContext ctx, string logDir, string logGlob)
	{
		var fx = TestFixtures.NewAdhocDir("AdhocLabel");
		
		try {
			
			fx.Write("AdhocLabelProbe.cs", "class AdhocLabelProbe { }\n");
			
			// Both patterns double as the TOOL entry's subject; neither is used by any other test, and
			// the GUID makes the search entry unique even across repeated runs sharing a log.
			var listPattern   = "AdhocLabelProbe*.cs";
			var searchPattern = $"AdhocLabelProbe_{Guid.NewGuid():N}|class AdhocLabelProbe";
			
			var list   = await ctx.RunTestAsync("roslyn_list_files",   new { pattern = listPattern,   projectPath = fx.Dir }, data => data?["count"] is not null);
			var search = await ctx.RunTestAsync("roslyn_search_files", new { pattern = searchPattern, projectPath = fx.Dir }, data => data?["matches"] is not null);
			
			if(!list.pass || !search.pass)
				
				return (false, $"FAIL  (probe calls failed: list_files {list.message}; search_files {search.message})");
			
			if(!TryReadLog(logDir, logGlob, out var lines, out var failure))
				
				return (false, failure);
			
			string? listMode   = null;
			string? searchMode = null;
			
			foreach(var line in lines) {
				
				JsonNode? entry;
				
				try {
					entry = JsonNode.Parse(line);
				}
				catch(System.Text.Json.JsonException) {
					continue;
				}
				
				var subject = entry?["subject"]?.GetValue<string>();
				var mode    = entry?["workspace_mode"]?.GetValue<string>();
				
				if(subject == listPattern)
					listMode = mode;
				else if(subject == searchPattern)
					searchMode = mode;
			}
			
			return listMode == "ADH" && searchMode == "ADH"
				? (true,  "PASS  (list_files and search_files on an adhoc workspace both logged ADH)")
				: (false, $"FAIL  (expected ADH; list_files logged '{listMode ?? "no entry"}', search_files logged '{searchMode ?? "no entry"}')")
			;
		}
		finally {
			fx.Dispose();
		}
	}
	
	/// <summary>
	///     Reads every line of the harness server's log, rotation siblings included, oldest file first so
	///     order-sensitive checks see entries in the order they were written. Fails when the log is missing,
	///     unreadable, or implausibly short — the last meaning it is not the log this run wrote, in which case
	///     an assertion over it would pass on missing evidence rather than on a clean run.
	/// </summary>
	static bool TryReadLog(string logDir, string logGlob, out string[] lines, out string failure)
	{
		lines   = [];
		failure = "";
		
		try {
			
			if(!Directory.Exists(logDir)) {
				
				failure = $"FAIL  (server log directory not found: {logDir})";
				
				return false;
			}
			
			// Every rotation sibling, not just the newest file: FileLogger rotates to "<log>.1",
			// "<log>.2" at maxFileSizeBytes, and reading only the current file would silently drop
			// the early entries — the ones a dogfood reload would appear in — turning a real
			// regression into a PASS. The glob ends in '*' for that reason. Rotated files are older
			// than the live one, so write time (not name) gives the chronological order.
			var logFiles = Directory.EnumerateFiles(logDir, logGlob)
				.OrderBy(File.GetLastWriteTimeUtc)
				.ToArray()
			;
			
			if(logFiles.Length == 0) {
				
				failure = $"FAIL  (server log not found: {Path.Combine(logDir, logGlob)})";
				
				return false;
			}
			
			lines = [.. logFiles.SelectMany(ReadSharedLines)];
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			failure = $"FAIL  (server log unreadable: {ex.Message})";
			
			return false;
		}
		
		// Every run logs one TOOL entry per tool call, and the harness makes well over a hundred.
		if(lines.Length < MinExpectedLogEntries) {
			
			failure = $"FAIL  (only {lines.Length} log entries — expected at least {MinExpectedLogEntries}; the log is not the one this run wrote)";
			
			return false;
		}
		
		return true;
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
