using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace RoslynMcp.LogViewer;

using RoslynMcp;

/// <summary>
///     Tails a RoslynMcp log file and yields parsed entries as they are appended.
///     Opens the file with FileShare.ReadWrite so the MCP server can keep writing.
///     On first connect, replays the last <c>tailLines</c> existing lines before switching to live tail.
/// </summary>
sealed class LogTailer
{
	readonly string? logPath;
	readonly string? watchDir;
	readonly string? watchPattern;
	readonly int     tailLines;

	// File mode — tail a specific log file.
	public LogTailer(string logPath)
		: this(logPath, 200) { }

	public LogTailer(string logPath, int tailLines)
		: this(logPath, tailLines, null, null) { }

	// Directory-watch mode — discovers the newest matching file and switches automatically
	// when a new one appears (i.e. when a new RoslynMcp server process starts).
	public LogTailer(string watchDir, string watchPattern)
		: this(null, 200, watchDir, watchPattern) { }

	public LogTailer(string watchDir, string watchPattern, int tailLines)
		: this(null, tailLines, watchDir, watchPattern) { }

	LogTailer(string? logPath, int tailLines, string? watchDir, string? watchPattern)
	{
		this.logPath      = logPath;
		this.tailLines    = tailLines;
		this.watchDir     = watchDir;
		this.watchPattern = watchPattern;
	}

	public async IAsyncEnumerable<LogEntry> TailAsync(
		[EnumeratorCancellation] CancellationToken ct)
	{
		string activePath;

		if(watchDir is not null) {

			var discovered = DiscoverLatest();

			if(discovered is not null) {
				activePath = discovered;
			}
			else {

				Console.Error.WriteLine($"No matching log found in {watchDir} — waiting for a server to start...");
				activePath = await WaitForNewFileInDirAsync(ct);

				if(string.IsNullOrEmpty(activePath))
					yield break;
			}
		}
		else {

			await WaitForFileAsync(logPath!, ct);
			activePath = logPath!;
		}

		using var signal = new SemaphoreSlim(0, 1);

		// pendingSwitch[0] is written by the dir watcher thread (before signal.Release)
		// and read by the main loop (after signal.WaitAsync). The semaphore provides
		// the required happens-before guarantee without an additional volatile/Interlocked.
		var pendingSwitch = new string?[1];

		FileStream?       stream      = null;
		StreamReader?     reader      = null;
		FileSystemWatcher fileWatcher = CreateFileWatcher(signal, activePath);

		using var dirWatcher = watchDir is not null
			? CreateDirWatcher(signal, pendingSwitch)
			: null;

		void OpenStreamAndReader(string path)
		{
			var newStream = new FileStream(
				path,
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
			OpenStreamAndReader(activePath);

			// Replay last N lines from history, then tail live from EOF.
			foreach(var entry in ReadLastLines(stream!, reader!, tailLines, ct))
				yield return entry;

			// Live tail.
			while(!ct.IsCancellationRequested) {

				// Check for a pending file switch (new RoslynMcp process started).
				var newPath = pendingSwitch[0];

				if(newPath is not null && !string.Equals(newPath, activePath, StringComparison.OrdinalIgnoreCase)) {

					pendingSwitch[0] = null;

					if(File.Exists(newPath)) {

						activePath = newPath;

						// Update file watcher to follow the new file.
						fileWatcher.Dispose();
						fileWatcher = CreateFileWatcher(signal, activePath);

						OpenStreamAndReader(activePath);

						Console.Error.WriteLine($"Switched to {Path.GetFileName(activePath)}");

						// Separator so the viewer shows a clear process boundary.
						yield return new LogEntry {
							Timestamp = DateTimeOffset.UtcNow.ToString("o"),
							Pid       = 0,
							Level     = "NEW",
							Message   = $"── new process: {Path.GetFileName(activePath)} ──",
							Raw       = string.Empty
						};

						foreach(var entry in ReadLastLines(stream!, reader!, tailLines, ct))
							yield return entry;

						continue;
					}
				}

				var line = await reader!.ReadLineAsync(ct).ConfigureAwait(false);

				if(line is not null) {
					yield return Parse(line);
					continue;
				}

				// Check for log rotation: file was truncated/replaced.
				try {

					if(File.Exists(activePath) && new FileInfo(activePath).Length < stream!.Position) {

						try {
							// Under RoslynMcp's rotation strategy, the original stream
							// now points at the renamed old file. Reopen so we follow
							// the newly created log file instead of rewinding the old one.
							OpenStreamAndReader(activePath);
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
			fileWatcher.Dispose();
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
		var start = ringSize < count
			? 0
			: ringHead
		;

		for(var i = 0; i < ringSize; i++)
			yield return Parse(ring[(start + i) % count]);
	}

	/// <summary>Returns the most recently modified file matching the watch pattern, or null.</summary>
	string? DiscoverLatest()
	{
		if(watchDir is null || watchPattern is null || !Directory.Exists(watchDir))
			return null;

		try {

			return Directory
				.EnumerateFiles(watchDir, watchPattern)
				.OrderByDescending(File.GetLastWriteTimeUtc)
				.FirstOrDefault()
			;
		}
		catch {
			return null;
		}
	}

	/// <summary>Watches the log directory for a new matching file and returns its path.</summary>
	async Task<string> WaitForNewFileInDirAsync(CancellationToken ct)
	{
		if(watchDir is null)
			return string.Empty;

		if(!Directory.Exists(watchDir))
			Directory.CreateDirectory(watchDir);

		var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

		await using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

		using var w = new FileSystemWatcher(watchDir) {
			NotifyFilter        = NotifyFilters.FileName,
			Filter              = watchPattern ?? string.Empty,
			EnableRaisingEvents = true
		};

		w.Created += (_, e) => tcs.TrySetResult(e.FullPath);

		// Double-check after watcher is set up to avoid the race between File.Exists
		// and watcher start.
		var latest = DiscoverLatest();

		if(latest is not null)
			return latest;

		try {
			return await tcs.Task.ConfigureAwait(false);
		}
		catch(OperationCanceledException) {
			return string.Empty;
		}
	}

	FileSystemWatcher CreateFileWatcher(SemaphoreSlim signal, string path)
	{
		var fullPath = Path.GetFullPath(path);
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

	// Watches for new matching log files (new RoslynMcp processes).
	// Stores the new path in pendingSwitch[0] before releasing the signal so the
	// main loop can pick it up after WaitAsync (semaphore provides the memory barrier).
	FileSystemWatcher CreateDirWatcher(SemaphoreSlim signal, string?[] pendingSwitch)
	{
		var w = new FileSystemWatcher(watchDir!) {
			NotifyFilter          = NotifyFilters.FileName,
			Filter                = watchPattern ?? string.Empty,
			IncludeSubdirectories = false,
			EnableRaisingEvents   = true
		};

		w.Created += (_, e) => {
			pendingSwitch[0] = e.FullPath;

			if(signal.CurrentCount == 0)
				signal.Release();
		};

		return w;
	}

	/// <summary>Waits until the log file exists, watching the directory for creation if possible.</summary>
	static async Task WaitForFileAsync(string logPath, CancellationToken ct)
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

	static readonly JsonSerializerOptions JsonOptions = new() {
		PropertyNameCaseInsensitive = true
	};

	static LogEntry Parse(string raw)
	{
		try {
			var entry = JsonSerializer.Deserialize<LogEntry>(raw, JsonOptions);

			if(entry is not null)
				return entry with { Raw = raw };
		}
		catch { }

		// Unrecognized line (e.g. truncated write, non-JSON content).
		return new LogEntry {
			Timestamp = "",
			Pid       = 0,
			Level     = "OTHER",
			Message   = raw,
			Raw       = raw
		};
	}
}
