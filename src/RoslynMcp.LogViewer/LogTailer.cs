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
///     appear after startup — merging their entries into a single stream in arrival order.
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
	
	// Directory-watch mode — tails ALL currently matching files simultaneously and picks up
	// new files as they appear (i.e. when new RoslynMcp server processes start).
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
	
	// Starts one file-mode LogTailer per matching file and merges their entries into a
	// single channel. New files picked up via FileSystemWatcher are added to the pool
	// automatically. Entries are yielded in arrival order (no timestamp sorting).
	async IAsyncEnumerable<LogEntry> TailDirectoryAsync(
		[EnumeratorCancellation] CancellationToken ct)
	{
		if(!Directory.Exists(watchDir))
			Directory.CreateDirectory(watchDir!);
		
		var channel = Channel.CreateUnbounded<LogEntry>(
			new UnboundedChannelOptions { SingleReader = true }
		);
		
		// Guard against double-tailing a file detected by both DiscoverAll and the watcher
		// in a race (e.g. a file created between watcher start and enumeration completing).
		var startedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		;
		var startedLock  = new object();
		
		void StartFileTailer(string path)
		{
			lock(startedLock) {
				
				if(!startedFiles.Add(path))
					
					return;
			}
			
			var fileTailer = new LogTailer(path, tailLines);
			
			// Fire-and-forget per-file task. CancellationToken.None for the Task itself —
			// the CT is threaded into TailAsync so tailers stop when it fires.
			_ = Task.Run(async () => {
				
				try {
					
					await foreach(var entry in fileTailer.TailAsync(ct))
						await channel.Writer.WriteAsync(entry, ct).ConfigureAwait(false);
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
		
		dirWatcher.Created += (_, e) => StartFileTailer(e.FullPath);
		
		// Start tailing all currently existing files.
		var hasExisting = false
		;
		
		foreach(var file in DiscoverAll()) {
			
			hasExisting = true;
			StartFileTailer(file);
		}
		
		if(!hasExisting)
			Console.Error.WriteLine($"No matching log found in {watchDir} — waiting for a server to start...");
		
		// Merge: yield entries from all file tailers in arrival order.
		await foreach(var entry in channel.Reader.ReadAllAsync(ct))
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
