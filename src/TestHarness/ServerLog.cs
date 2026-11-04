/// <summary>
///     Reads the harness server's NDJSON log. The harness points ROSLYNMCP_LOG_PATH at a per-run stem,
///     and the server inserts its PID before the extension, so the log is found by glob.
/// </summary>
static class ServerLog
{
	/// <summary>
	///     Reads every line of the log, rotation siblings included, oldest file first so order-sensitive
	///     checks see entries in the order they were written. Fails when the log is missing, unreadable, or
	///     has fewer than <paramref name="minEntries"/> lines. A check that asserts something is absent from
	///     the log needs a real floor — a truncated or foreign log would pass it on missing evidence — while a
	///     check that looks for an entry it caused can pass 1: a missing entry already fails it, and a
	///     filtered run (#367) writes far fewer entries than a full one.
	/// </summary>
	internal static bool TryRead(string logDir, string logGlob, int minEntries, out string[] lines, out string failure)
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
		
		if(lines.Length < minEntries) {
			
			failure = $"FAIL  (only {lines.Length} log entries — expected at least {minEntries}; the log is not the one this run wrote)";
			
			return false;
		}
		
		return true;
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
