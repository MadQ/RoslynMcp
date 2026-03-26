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
            return new LogEntry("", "OTHER", raw, raw);

        return new LogEntry(
            Timestamp: m.Groups[1].Value,
            Level:     m.Groups[2].Value.TrimEnd(),
            Message:   m.Groups[3].Value,
            Raw:       raw
        );
    }
}
