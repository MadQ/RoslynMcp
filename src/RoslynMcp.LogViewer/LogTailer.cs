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
    // Format: [2026-03-26 14:30:45.123Z] [TOOL  ] roslyn_get_type_members 142ms OK
    static readonly Regex LinePattern = new(
        @"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}Z)\] \[(.{6})\] (.*)$",
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

        using var stream = new FileStream(
            logPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        // Replay last N lines from history, then tail live from EOF.
        foreach(var entry in ReadLastLines(stream, reader, tailLines))
            yield return entry;

        // Live tail.
        while(!ct.IsCancellationRequested) {

            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);

            if(line is not null) {
                yield return Parse(line);
                continue;
            }

            // Check for log rotation: file was truncated/replaced.
            try {
                if(File.Exists(logPath) && new FileInfo(logPath).Length < stream.Position) {
                    stream.Seek(0, SeekOrigin.Begin);
                    reader.DiscardBufferedData();
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

    // ── Private ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     Reads all lines from the beginning of the file, returns the last <paramref name="count"/>,
    ///     and leaves the stream positioned at EOF for live tailing.
    /// </summary>
    static IEnumerable<LogEntry> ReadLastLines(FileStream stream, StreamReader reader, int count)
    {
        stream.Seek(0, SeekOrigin.Begin);
        reader.DiscardBufferedData();

        var all = new List<string>(capacity: count + 1);

        string? line;

        while((line = reader.ReadLine()) is not null)
            all.Add(line);

        // stream/reader are now at EOF — ready for live tail without a seek.
        var start = Math.Max(0, all.Count - count);

        for(var i = start; i < all.Count; i++)
            yield return Parse(all[i]);
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
            return new LogEntry("", "OTHER", raw, raw);

        return new LogEntry(
            Timestamp: m.Groups[1].Value,
            Level:     m.Groups[2].Value.TrimEnd(),
            Message:   m.Groups[3].Value,
            Raw:       raw
        );
    }
}
