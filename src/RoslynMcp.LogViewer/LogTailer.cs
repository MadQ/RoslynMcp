using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RoslynMcp.LogViewer;

/// <summary>
///     Tails a RoslynMcp log file and yields parsed entries as they are appended.
///     Opens the file with FileShare.ReadWrite so the MCP server can keep writing.
///     On first connect, replays the last <c>tailLines</c> existing lines before switching to live tail.
/// </summary>
sealed class LogTailer
{
	// Format: [14:30:45.123] [12345] [TOOL  ] MSB OK  142ms get_type_members  WorkspaceManager — 18/18 member(s)
	// Also supports old format without PID: [14:30:45.123] [TOOL  ] ...
	static readonly Regex LinePattern = new(
		@"^\[(\d{2}:\d{2}:\d{2}\.\d{3})\] (?:\[(\d+)\] )?\[(.{6})\] (.*)$",
		RegexOptions.Compiled
	);

	// Parses the TOOL message body:
	// Group 1: workspace mode (MSB, ADH, ---)
	// Group 2: OK or ERROR
	// Group 3: elapsed ms
	// Group 4: tool name (short, without roslyn_ prefix)
	// Group 5: rest — subject + optional detail after ' — '
	static readonly Regex ToolPattern = new(
		@"^(MSB|ADH) (OK\s*|ERROR)\s+(\d+)ms (\S+)\s*(.*)?$",
		RegexOptions.Compiled
	);
	
	readonly string logPath;
	readonly int    tailLines;
	
	public LogTailer(string logPath, int tailLines = 200)
	{
		this.logPath   = logPath;
		this.tailLines = tailLines;
	}
	
	public async IAsyncEnumerable<LogEntry> TailAsync(
		[EnumeratorCancellation] CancellationToken ct)
	{
		await WaitForFileAsync(ct);
		
		using var signal  = new SemaphoreSlim(0, 1);
		using var watcher = CreateWatcher(signal);
		
		FileStream?   stream = null;
		StreamReader? reader = null;
		
		// Helper to open a fresh stream/reader pair for the current log file.
		void OpenStreamAndReader()
		{
			var newStream = new FileStream(
				logPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete
			);
			
			var newReader = new StreamReader(
				newStream,
				Encoding.UTF8,
				detectEncodingFromByteOrderMarks: false,
				leaveOpen: true
			);
			
			// Dispose previous instances (if any) before switching.
			reader?.Dispose();
			stream?.Dispose();
			
			stream = newStream;
			reader = newReader;
		}
		
		try {
			OpenStreamAndReader();
			
			// Replay last N lines from history, then tail live from EOF.
			foreach(var entry in ReadLastLines(stream!, reader!, tailLines, ct))
				yield return entry;
			
			// Live tail.
			while(!ct.IsCancellationRequested) {
			
				var line = await reader!.ReadLineAsync(ct).ConfigureAwait(false);
				
				if(line is not null) {
					yield return Parse(line);
					continue;
				}
				
				// Check for log rotation: file was truncated/replaced.
				try {
					if(File.Exists(logPath) && new FileInfo(logPath).Length < stream!.Position) {
						try {
							// Under RoslynMcp's rotation strategy, the original stream
							// now points at the renamed old file. Reopen so we follow
							// the newly created log file instead of rewinding the old one.
							OpenStreamAndReader();
						}
						catch {
							// Non-fatal — if reopening fails (e.g., during rotation window),
							// we'll try again on the next iteration.
						}
						
						continue;
					}
				}
				catch { /* non-fatal — file may be temporarily inaccessible during rotation */ }
				
				// No new data — wait for the watcher to signal, then loop to read.
				try {
					await signal.WaitAsync(ct).ConfigureAwait(false);
				}
				catch(OperationCanceledException) {
					yield break;
				}
			}
		}
		finally {
			reader?.Dispose();
			stream?.Dispose();
		}
	}
	
	// ── Private ──────────────────────────────────────────────────────────────
	
	/// <summary>
	///     Returns the last <paramref name="count"/> lines from the file using a ring buffer,
	///     keeping memory bounded to <paramref name="count"/> strings regardless of file size.
	///     Leaves the stream positioned at EOF for live tailing.
	/// </summary>
	static IEnumerable<LogEntry> ReadLastLines(FileStream stream, StreamReader reader, int count, CancellationToken ct)
	{
		stream.Seek(0, SeekOrigin.Begin);
		reader.DiscardBufferedData();
		
		if(count <= 0)
			yield break;
		
		// Ring buffer — evicts the oldest entry once full, so memory is bounded by `count`.
		var ring     = new string[count];
		var ringHead = 0;
		var ringSize = 0;
		
		string? line;
		
		while((line = reader.ReadLine()) is not null) {
			ct.ThrowIfCancellationRequested();
			ring[ringHead] = line;
			ringHead       = (ringHead + 1) % count;
			
			if(ringSize < count)
				ringSize++;
		}
		
		// stream/reader are now at EOF — ready for live tail without a seek.
		var start = ringSize < count ? 0 : ringHead;
		
		for(var i = 0; i < ringSize; i++)
			yield return Parse(ring[(start + i) % count]);
	}
	
	FileSystemWatcher CreateWatcher(SemaphoreSlim signal)
	{
		var fullPath = Path.GetFullPath(logPath);
		var dir      = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
		
		var w = new FileSystemWatcher(dir) {
			NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
			IncludeSubdirectories = false,
			EnableRaisingEvents   = true
		};
		
		void Notify(object _, FileSystemEventArgs e)
		{
			if(!string.Equals(e.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
				return;
			
			if(signal.CurrentCount == 0)
				signal.Release();
		}
		
		w.Changed += Notify;
		w.Created += Notify;
		
		return w;
	}
	
	/// <summary>Waits until the log file exists, watching the directory for creation if possible.</summary>
	async Task WaitForFileAsync(CancellationToken ct)
	{
		if(File.Exists(logPath))
			return;
		
		Console.Error.WriteLine($"Log file not found — waiting: {logPath}");
		
		var fullPath = Path.GetFullPath(logPath);
		var dir      = Path.GetDirectoryName(fullPath);
		
		if(dir is null || !Directory.Exists(dir)) {
			while(!File.Exists(logPath))
				await Task.Delay(1000, ct).ConfigureAwait(false);
			
			return;
		}
		
		var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		
		await using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
		
		using var w = new FileSystemWatcher(dir) {
			NotifyFilter        = NotifyFilters.FileName,
			EnableRaisingEvents = true
		};
		
		w.Created += (_, e) => {
			if(string.Equals(e.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
				tcs.TrySetResult();
		};
		
		// Double-check after watcher is set up to avoid a race between the File.Exists
		// check at the top and the watcher starting.
		if(!File.Exists(logPath))
			await tcs.Task.ConfigureAwait(false);
	}
	
	static LogEntry Parse(string raw)
	{
		var m = LinePattern.Match(raw);

		if(!m.Success)
			return new LogEntry("", null, "OTHER", raw, raw);

		var timestamp = m.Groups[1].Value;
		var pid       = m.Groups[2].Success ? m.Groups[2].Value : null;
		var level     = m.Groups[3].Value.TrimEnd();
		var message   = m.Groups[4].Value;

		if(level == "TOOL") {

			var t = ToolPattern.Match(message);

			if(t.Success) {

				// Group 5 is "subject — detail" or just "subject" or just "— detail" or empty.
				var rest    = t.Groups[5].Value.Trim();
				var dashIdx = rest.IndexOf(" — ", StringComparison.Ordinal);
				var subject = dashIdx >= 0 ? rest[..dashIdx].Trim() : rest;
				var detail  = dashIdx >= 0 ? rest[(dashIdx + 3)..].Trim() : null;

				return new LogEntry(
					Timestamp:     timestamp,
					Pid:           pid,
					Level:         level,
					Message:       message,
					Raw:           raw,
					WorkspaceMode: t.Groups[1].Value,
					ToolName:      t.Groups[4].Value,
					ElapsedMs:     long.TryParse(t.Groups[3].Value, out var ms) ? ms : null,
					Success:       t.Groups[2].Value.TrimEnd() == "OK",
					Subject:       subject is { Length: > 0 } s ? s : null,
					Detail:        detail is { Length: > 0 } d ? d : null
				);
			}
		}

		return new LogEntry(timestamp, pid, level, message, raw);
	}
}
