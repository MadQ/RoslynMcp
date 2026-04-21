using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace RoslynMcp.LogViewer;

using RoslynMcp;

/// <summary>
///     Tails one or more RoslynMcp log files and yields parsed entries as they are appended.
///     Opens files with FileShare.ReadWrite so the MCP server can keep writing.
///     In file mode, replays the last <c>tailLines</c> lines then switches to live tail.
///     In directory-watch mode, tails ALL matching files simultaneously — including files that
///     appear after startup — merge-sorting each existing file's backlog by timestamp before
///     switching to live arrival-order streaming. Files created after startup are always live.
/// </summary>
static class LogTailerDefaults
{
	// Default per-file backlog lines replayed on connection. Matches the prior hardcoded value
	// so default behaviour is unchanged; override via the ?tail= query string on /logs/stream.
	public const int DefaultTail = 200;
}

sealed class LogTailer
{
	readonly string? logPath;
	readonly string? watchDir;
	readonly string? watchPattern;
	readonly int     tailLines;
	
	// File mode — tail a specific log file.
	public LogTailer(string logPath)
		: this(logPath, LogTailerDefaults.DefaultTail) { }

	public LogTailer(string logPath, int tailLines)
		: this(logPath, tailLines, null, null) { }

	// Directory-watch mode — tails ALL currently matching files simultaneously and picks up
	// new files as they appear (i.e. when new RoslynMcp process starts).
	public LogTailer(string watchDir, string watchPattern)
		: this(null, LogTailerDefaults.DefaultTail, watchDir, watchPattern) { }
	
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
		[EnumeratorCancellation] CancellationToken ct,
		Action? onBacklogDone = null)
	{
		if(watchDir is not null) {

			await foreach(var entry in TailDirectoryAsync(ct))
				yield return entry;

			yield break;
		}

		// ── File mode ─────────────────────────────────────────────────────────

		await WaitForFileAsync(logPath!, ct);
		
		var activePath = logPath!;
		
		using var signal = new SemaphoreSlim(0, 1);
		
		FileStream?       stream      = null;
		StreamReader?     reader      = null;
		FileSystemWatcher fileWatcher = CreateFileWatcher(signal, activePath);
		
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

			// Signal the directory-mode aggregator that this tailer has finished its backlog
			// and is about to enter live tailing. Callers in file-only mode pass null.
			onBacklogDone?.Invoke();

			while(!ct.IsCancellationRequested) {
				
				var line = await reader!.ReadLineAsync(ct).ConfigureAwait(false);
				
				if(line is not null) {
					
					yield return Parse(line);
					continue;
				}
				
				// Check for log rotation: file was truncated/replaced.
				try {
					
					if(File.Exists(activePath) && new FileInfo(activePath).Length < stream!.Position) {
						
						try {
							// Under RoslynMcp's rotation strategy, the original stream points at
							// the renamed old file. Reopen to follow the new log file.
							OpenStreamAndReader(activePath)
							;
						}
						catch {
							// Non-fatal — retry on next iteration.
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
	
	// Soft deadline for the initial-load timestamp merge. If an existing file's backlog read
	// hasn't completed by this cutoff, its remaining entries fall through to live streaming
	// rather than blocking the viewer. In practice local log-file reads complete in well
	// under 1s; 5s is a generous ceiling for pathological cases (slow disk, many files).
	static readonly TimeSpan BacklogMergeDeadline = TimeSpan.FromSeconds(5);

	// Starts one file-mode LogTailer per matching file.
	//
	// Each EXISTING file's backlog entries are buffered and merge-sorted by timestamp before
	// yielding, so initial load is chronological across files rather than whichever tailer's
	// scheduler slot landed first. After the merge, all tailers switch to arrival-order
	// streaming via a shared live channel. Files CREATED post-startup (via the dir watcher)
	// are treated as live from the start — they post-date the merged backlog.
	async IAsyncEnumerable<LogEntry> TailDirectoryAsync(
		[EnumeratorCancellation] CancellationToken ct)
	{
		if(!Directory.Exists(watchDir))
			Directory.CreateDirectory(watchDir!);

		var liveChannel = Channel.CreateUnbounded<LogEntry>(
			new UnboundedChannelOptions { SingleReader = true }
		);

		// Guard against double-tailing a file detected by both DiscoverAll and the watcher
		// in a race (e.g. a file created between watcher start and enumeration completing).
		var startedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var startedLock  = new object();

		// Per-existing-file backlog buffers. Each file tailer is the sole writer to its own
		// buffer; the lock protects the narrow race between a late writer and the main-thread
		// snapshot at merge time (relevant only when the BacklogMergeDeadline fires).
		var backlogBuffers      = new Dictionary<string, List<LogEntry>>(StringComparer.OrdinalIgnoreCase);
		var pendingBacklogCount = 0;
		var backlogCompleteTcs  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var mergingStarted      = false;

		void StartFileTailer(string path, bool isExistingAtStartup)
		{
			lock(startedLock) {

				if(!startedFiles.Add(path))

					return;
			}

			List<LogEntry>? buffer = null;

			if(isExistingAtStartup) {

				buffer = [];
				backlogBuffers[path] = buffer;
			}

			// Start with backlog-done=true for new files — they have no historical phase
			// relative to the viewer's startup, so every entry is live.
			var backlogDoneLocal = !isExistingAtStartup;
			var fileTailer       = new LogTailer(path, tailLines);

			// Fire-and-forget per-file task. CancellationToken.None for the Task itself —
			// the CT is threaded into TailAsync so tailers stop when it fires.
			_ = Task.Run(async () => {

				try {

					await foreach(var entry in fileTailer.TailAsync(
						ct,
						onBacklogDone: () => {

							if(backlogDoneLocal)
								return;

							backlogDoneLocal = true;

							if(isExistingAtStartup
								&& Interlocked.Decrement(ref pendingBacklogCount) == 0)
								backlogCompleteTcs.TrySetResult();
						})) {

						var buffered = false;

						if(!backlogDoneLocal && buffer is not null) {

							lock(buffer) {

								if(!Volatile.Read(ref mergingStarted)) {

									buffer.Add(entry);
									buffered = true;
								}
							}
						}

						if(!buffered)
							await liveChannel.Writer.WriteAsync(entry, ct).ConfigureAwait(false);
					}
				}
				catch(OperationCanceledException) { }
				catch(Exception) { /* non-fatal — file may be deleted or locked */ }
			}, CancellationToken.None);
		}

		// Set up the directory watcher BEFORE enumerating existing files so there is no
		// window where a newly created file is missed by both paths.
		using var dirWatcher = new FileSystemWatcher(watchDir!) {

			NotifyFilter          = NotifyFilters.FileName,
			Filter                = watchPattern ?? string.Empty,
			IncludeSubdirectories = false,
			EnableRaisingEvents   = true
		};

		dirWatcher.Created += (_, e) => StartFileTailer(e.FullPath, isExistingAtStartup: false);

		// Snapshot the existing-file set and seed the backlog counter before starting any
		// tailers, so the TCS completion decrement can't fire before we know the target.
		var existing = DiscoverAll().ToList();
		pendingBacklogCount = existing.Count;

		if(pendingBacklogCount == 0) {

			backlogCompleteTcs.TrySetResult();
			Console.Error.WriteLine($"No matching log found in {watchDir} — waiting for a server to start...");
		}

		foreach(var file in existing)
			StartFileTailer(file, isExistingAtStartup: true);

		// Wait for all existing files' backlogs to drain, with a soft deadline.
		await Task.WhenAny(backlogCompleteTcs.Task, Task.Delay(BacklogMergeDeadline, ct)).ConfigureAwait(false);

		// Flip the flag BEFORE snapshotting — any entry a tailer tries to buffer after this
		// point will hit the lock, observe mergingStarted=true, and route to the live channel.
		Volatile.Write(ref mergingStarted, true);

		var snapshots = new List<List<LogEntry>>(backlogBuffers.Count);

		foreach(var buf in backlogBuffers.Values) {

			lock(buf)
				snapshots.Add([..buf]);
		}

		// Timestamp-ordered merge. Empty-timestamp entries (parse failures) sort to the top
		// but preserve within-file order via the (fileIdx, lineIdx) tiebreaker.
		var merged = snapshots
			.SelectMany((buf, fileIdx) => buf.Select((entry, lineIdx) => (entry, fileIdx, lineIdx)))
			.OrderBy(t => t.entry.Timestamp, StringComparer.Ordinal)
			.ThenBy(t => t.fileIdx)
			.ThenBy(t => t.lineIdx)
			.Select(t => t.entry);

		foreach(var entry in merged)
			yield return entry;

		// After the merge, everything flows through the live channel in arrival order.
		await foreach(var entry in liveChannel.Reader.ReadAllAsync(ct))
			yield return entry;
	}
	
	/// <summary>Returns all files matching the watch pattern, ordered oldest-modified first.</summary>
	IEnumerable<string> DiscoverAll()
	{
		if(watchDir is null || watchPattern is null || !Directory.Exists(watchDir))
			
			return [];
		
		try {
			
			return Directory
				.EnumerateFiles(watchDir, watchPattern)
				.OrderBy(File.GetLastWriteTimeUtc)
			;
		}
		catch {
			return [];
		}
	}
	
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
		var ring     = new string[count]
		;
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
