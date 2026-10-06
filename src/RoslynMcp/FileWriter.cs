using System.Text;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RoslynMcp;

/// <summary>What <see cref="FileWriter.ReplaceAtomicAsync(string, byte[], AtomicReplace)"/> does beyond the plain replace.</summary>
[Flags]
internal enum AtomicReplace
{
    None = 0,

    /// <summary>
    ///     When the target is a symbolic link, write to the file it points at and leave the link in
    ///     place. A link is there on purpose — an agent config file living in a dotfiles
    ///     repository, a source file shared between two folders of a repository — and replacing
    ///     it with a regular file would silently cut it off. Where the link may lead is not this
    ///     method's concern: for workspace files the workspace boundary has already refused a
    ///     link that leaves the workspace.
    /// </summary>
    FollowLink = 1,

    /// <summary>
    ///     When the target does not exist yet, create it readable and writable by its owner only.
    ///     For config files in the home directory, which can come to hold tokens. An existing file
    ///     always keeps the mode it has.
    /// </summary>
    PrivateWhenNew = 2,
}

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

    // Null in the command-line subcommands (setup, setup-project), which write files without
    // starting the server and so never call Initialize. A retry there is simply not logged.
    static FileLogger? _logger
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

    // ── Atomic replace ────────────────────────────────────────────────────────

    /// <summary>
    ///     Replaces the content of <paramref name="path"/> so that a reader sees either the old
    ///     file or the new one, never a half-written one: the bytes go to a temp file beside the
    ///     target, which is then renamed over it (with the usual retry).
    ///     <para>
    ///         A rename puts the temp file in the target's place with the temp file's own
    ///         attributes, which is wrong in two ways this method corrects (#322). On Linux and
    ///         macOS the target's permission bits would be replaced by the default mode — a
    ///         private file becomes readable by others, an executable script stops being
    ///         executable — so the target's mode is copied onto the temp file first. And a target
    ///         that is a symbolic link would be replaced by a regular file; see
    ///         <see cref="AtomicReplace.FollowLink"/>.
    ///     </para>
    ///     The temp file is removed when anything fails, and the exception propagates.
    /// </summary>
    internal static Task ReplaceAtomicAsync(string path, byte[] bytes) =>
        ReplaceAtomicAsync(path, bytes, AtomicReplace.None)
;

    /// <inheritdoc cref="ReplaceAtomicAsync(string, byte[])"/>
    internal static async Task ReplaceAtomicAsync(string path, byte[] bytes, AtomicReplace options)
    {
        var target = ResolveReplaceTarget(path, options);
        var temp   = TempPathBeside(target);

        EnsureDirectoryOf(target);

        try {

            await WriteAllBytesAsync(temp, bytes);
            ApplyReplaceMode(temp, target, options);
            Move(temp, target, overwrite: true);
        }
        catch {

            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    ///     Synchronous form of <see cref="ReplaceAtomicAsync(string, byte[], AtomicReplace)"/>,
    ///     for the command-line setup code, which is not asynchronous.
    /// </summary>
    internal static void ReplaceAtomic(string path, byte[] bytes, AtomicReplace options)
    {
        var target = ResolveReplaceTarget(path, options);
        var temp   = TempPathBeside(target);

        EnsureDirectoryOf(target);

        try {

            WriteAllBytes(temp, bytes);
            ApplyReplaceMode(temp, target, options);
            Move(temp, target, overwrite: true);
        }
        catch {

            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    ///     Gives <paramref name="copyPath"/> the Unix permission bits of
    ///     <paramref name="sourcePath"/>, so that a copy of a file is never readable by more
    ///     people than the file itself. Does nothing on Windows, or when the source does not exist.
    /// </summary>
    internal static void CopyUnixMode(string sourcePath, string copyPath)
    {
        if(!OperatingSystem.IsWindows() && File.Exists(sourcePath))
            File.SetUnixFileMode(copyPath, File.GetUnixFileMode(sourcePath));
    }

    /// <summary>
    ///     The file a write to <paramref name="path"/> must land in: the file a symbolic link
    ///     points at, or <paramref name="path"/> itself when it is not a link. For callers that do
    ///     their own swap and must not replace a link with a regular file.
    /// </summary>
    internal static string FollowLink(string path) => ResolveReplaceTarget(path, AtomicReplace.FollowLink);

    /// <summary>
    ///     The size of the content at <paramref name="path"/>, through a symbolic link. Asking the
    ///     link itself is wrong on Windows, where a link reports a length of zero whatever the
    ///     file behind it holds — which made a successful write through a link look truncated.
    /// </summary>
    internal static long ContentLength(string path) => new FileInfo(FollowLink(path)).Length;

    // The file the bytes must end up in. For a symbolic link that is the file it points at —
    // also when that file does not exist yet (a dangling link): the link was put there to say
    // where the content lives.
    static string ResolveReplaceTarget(string path, AtomicReplace options)
    {
        var target = Path.GetFullPath(path);

        if(options.HasFlag(AtomicReplace.FollowLink)) {

            var info = new FileInfo(target);

            // LinkTarget is null for anything that is not a link, a missing file included.
            if(info.LinkTarget is { } direct) {

                try {

                    if(info.ResolveLinkTarget(returnFinalTarget: true) is { } final)
                        target = final.FullName;
                }
                catch(IOException) {

                    // The chain cannot be followed to its end. One step is still better than
                    // overwriting the link itself.
                    target = Path.GetFullPath(direct, Path.GetDirectoryName(target)!);
                }
            }
        }

        return target;
    }

    static void EnsureDirectoryOf(string target)
    {
        if(Path.GetDirectoryName(target) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
    }

    // Beside the target, so the rename stays on one volume; unique, so two writers never share it.
    static string TempPathBeside(string target) =>
        Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp")
;

    static void ApplyReplaceMode(string temp, string target, AtomicReplace options)
    {
        if(OperatingSystem.IsWindows())
            return;

        if(File.Exists(target))
            File.SetUnixFileMode(temp, File.GetUnixFileMode(target));
        else if(options.HasFlag(AtomicReplace.PrivateWhenNew))
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    static void TryDelete(string path)
    {
        try {
            File.Delete(path);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
    }

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
        // Delay schedule is shared with the workspace-reload deferral — see Backoff.
        // At the default 3 attempts this is 50 then 100 ms; the cap never binds there and
        // exists only to bound a caller that passes a large maxAttempts.
        private const int    MaxDelayMs    = 30_000;

        private int          _attempt      = 0;
        private int          _delay        = Backoff.DelayMs(0, Backoff.DefaultSeedMs, MaxDelayMs);
        private int          _retries      = 0;
        private int          _totalBackoff = 0;
        private readonly string? _fileName = filePath is null ? null : Path.GetFileName(filePath);

        public int Delay => _delay;

        public void Advance() => _delay = Backoff.DelayMs(++_attempt, Backoff.DefaultSeedMs, MaxDelayMs);

        // Returns true if retryable (IOException or UnauthorizedAccessException); false if non-retryable (caller must rethrow).
        public bool OnCaught(Exception ex, int attempt)
        {
            if(ex is not IOException and not UnauthorizedAccessException) {

                _logger?.LogError("write_retry", $"non-retryable {ex.GetType().Name}{FileLabel}");

                return false;
            }

            _logger?.LogInfo("write_retry", $"attempt={attempt + 1} delay_ms={_delay} hint=\"{ex.Message}\"{FileLabel}");
            _retries++;
            _totalBackoff += _delay;

            return true;
        }

        public void LogRecovered()
        {
            if(_retries > 0)
                _logger?.LogInfo("write_retry", $"recovered after {_retries} retry total_backoff_ms={_totalBackoff}{FileLabel}");
        }

        public void LogTerminal(Exception ex)
        {
            var message = ex is NotSupportedException
                ? $"non-retryable {ex.GetType().Name}"
                : $"exhausted {_retries + 1} attempts";

            _logger?.LogError("write_retry", message + FileLabel);
        }

        private string FileLabel => _fileName is null ? "" : $" file=\"{_fileName}\"";
    }
}
