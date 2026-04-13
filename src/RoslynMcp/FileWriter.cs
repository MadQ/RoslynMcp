using System.Text;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RoslynMcp;

/// <summary>
///     Centralised entry point for all file writes in RoslynMcp. Every write is wrapped in
///     exponential-backoff retry on transient <see cref="IOException"/> and emits structured
///     log entries for each retry and for terminal failures.
/// </summary>
/// <remarks>
///     Use the named overloads (<see cref="WriteAllTextAsync"/>, <see cref="WriteAllBytesAsync"/>,
///     <see cref="WriteAllLines"/>, <see cref="WriteAllBytes"/>, <see cref="WriteAllText"/>,
///     <see cref="Move"/>) in preference to <see cref="WriteWithRetryAsync"/> and
///     <see cref="WriteWithRetry"/> with custom lambdas. The named overloads bake in
///     <see cref="Utf8NoBom"/> automatically and infer the log file name from the path.
///     Call <see cref="Initialize"/> once at startup before any writes are attempted.
/// </remarks>
internal static class FileWriter
{
    /// <summary>UTF-8 encoding without BOM — RM's standard file encoding.</summary>
    internal static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // null! is safe — Initialize() is always called at startup before any writes are attempted.
    static FileLogger _logger = null!
;

    /// <summary>Wires the singleton logger. Must be called once at startup before any writes.</summary>
    internal static void Initialize(FileLogger fileLogger) => _logger = fileLogger;

    // ── Named overloads ───────────────────────────────────────────────────────

    internal static Task WriteAllTextAsync(string path, string content) =>
        WriteWithRetryAsync(() => File.WriteAllTextAsync(path, content, Utf8NoBom), path)
;

    internal static Task WriteAllBytesAsync(string path, byte[] bytes) =>
        WriteWithRetryAsync(() => File.WriteAllBytesAsync(path, bytes), path)
;

    internal static void WriteAllText(string path, string content) =>
        WriteWithRetry(() => File.WriteAllText(path, content, Utf8NoBom), path)
;

    internal static void WriteAllBytes(string path, byte[] bytes) =>
        WriteWithRetry(() => File.WriteAllBytes(path, bytes), path)
;

    internal static void WriteAllLines(string path, IEnumerable<string> lines) =>
        WriteWithRetry(() => File.WriteAllLines(path, lines, Utf8NoBom), path)
;

    internal static void Move(string source, string dest, bool overwrite) =>
        WriteWithRetry(() => File.Move(source, dest, overwrite), dest)
;

    // ── Core retry implementations ────────────────────────────────────────────

    /// <summary>
    ///     Executes an async file-write action with exponential-backoff retry on transient
    ///     <see cref="IOException"/>, logging each retry attempt and any terminal failure.
    ///     Three attempts: immediate, ~50 ms, ~150 ms cumulative.
    /// </summary>
    internal static Task WriteWithRetryAsync(Func<Task> writeAction, string? filePath) =>
        WriteWithRetryAsync(writeAction, 3, filePath)
;

    /// <summary>
    ///     Executes an async file-write action with exponential-backoff retry on transient
    ///     <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>, logging each
    ///     retry attempt and any terminal failure. The final attempt is unguarded so the exception
    ///     propagates to the caller. <see cref="NotSupportedException"/> is logged and re-thrown
    ///     immediately without retry.
    /// </summary>
    /// <param name="writeAction">The async write operation to execute.</param>
    /// <param name="maxAttempts">Maximum number of attempts.</param>
    /// <param name="filePath">Full path — used to extract a filename for log entries.</param>
    internal static async Task WriteWithRetryAsync(Func<Task> writeAction, int maxAttempts, string? filePath)
    {
        var state = new RetryState(filePath);

        for(var attempt = 0; attempt < maxAttempts - 1; attempt++) {

            try {

                await writeAction();
                state.LogRecovered();

                return;
            }
            catch(Exception ex) when(IsHandled(ex)) {

                if(!state.OnCaught(ex, attempt))
                    throw;

                await Task.Delay(state.Delay);
                state.Advance();
            }
        }

        try {
            await writeAction();
        }
        catch(Exception ex) when(IsHandled(ex)) {

            state.LogTerminal(ex);
            throw;
        }
    }

    /// <summary>
    ///     Executes a synchronous file-write action with exponential-backoff retry on transient
    ///     <see cref="IOException"/>, logging each retry attempt and any terminal failure.
    ///     Three attempts: immediate, ~50 ms, ~150 ms cumulative.
    /// </summary>
    internal static void WriteWithRetry(Action writeAction, string? filePath) =>
        WriteWithRetry(writeAction, 3, filePath)
;

    /// <summary>
    ///     Executes a synchronous file-write action with exponential-backoff retry on transient
    ///     <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>, logging each
    ///     retry attempt and any terminal failure. The final attempt is unguarded so the exception
    ///     propagates to the caller. <see cref="NotSupportedException"/> is logged and re-thrown
    ///     immediately without retry.
    /// </summary>
    /// <param name="writeAction">The synchronous write operation to execute.</param>
    /// <param name="maxAttempts">Maximum number of attempts.</param>
    /// <param name="filePath">Full path — used to extract a filename for log entries.</param>
    internal static void WriteWithRetry(Action writeAction, int maxAttempts, string? filePath)
    {
        var state = new RetryState(filePath);

        for(var attempt = 0; attempt < maxAttempts - 1; attempt++) {

            try {

                writeAction();
                state.LogRecovered();

                return;
            }
            catch(Exception ex) when(IsHandled(ex)) {

                if(!state.OnCaught(ex, attempt))
                    throw;

                Thread.Sleep(state.Delay);
                state.Advance();
            }
        }

        try {
            writeAction();
        }
        catch(Exception ex) when(IsHandled(ex)) {

            state.LogTerminal(ex);
            throw;
        }
    }

    private static bool IsHandled(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException
;

    // Mutable struct — all methods that modify fields must be called on the local directly.
    private struct RetryState(string? filePath)
    {
        private int          _delay        = 50;
        private int          _retries      = 0;
        private int          _totalBackoff = 0;
        private readonly string? _fileName = filePath is null ? null : Path.GetFileName(filePath);

        public int Delay => _delay;

        public void Advance() => _delay *= 2;

        // Returns true if retryable (IOException or UnauthorizedAccessException); false if non-retryable (caller must rethrow).
        public bool OnCaught(Exception ex, int attempt)
        {
            if(ex is not IOException and not UnauthorizedAccessException) {

                _logger.LogError("write_retry", $"non-retryable {ex.GetType().Name}{FileLabel}");

                return false;
            }

            _logger.LogInfo("write_retry", $"attempt={attempt + 1} delay_ms={_delay} hint=\"{ex.Message}\"{FileLabel}");
            _retries++;
            _totalBackoff += _delay;

            return true;
        }

        public void LogRecovered()
        {
            if(_retries > 0)
                _logger.LogInfo("write_retry", $"recovered after {_retries} retry total_backoff_ms={_totalBackoff}{FileLabel}");
        }

        public void LogTerminal(Exception ex)
        {
            var message = ex is NotSupportedException
                ? $"non-retryable {ex.GetType().Name}"
                : $"exhausted {_retries + 1} attempts";

            _logger.LogError("write_retry", message + FileLabel);
        }

        private string FileLabel => _fileName is null ? "" : $" file=\"{_fileName}\"";
    }
}
