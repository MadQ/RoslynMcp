using System.Globalization;

namespace RoslynMcp;

/// <summary>
///     Max-age eviction for long-lived file stores: per-PID log files and BackupStore snapshots.
///     A run-count guard throttles pruning so it only runs once every <see cref="DefaultMinRuns"/>
///     server starts. The counter is fail-safe — missing or corrupt content is treated as 0, which
///     is always below any positive <c>minRuns</c> threshold, so pruning is skipped rather than
///     over-pruning.
/// </summary>
/// <remarks>
///     Call order required by callers in <c>Program.cs</c>:
///     <list type="number">
///         <item><see cref="IncrementAndGetRunCount"/> — once, immediately after <c>ServerArgs.Initialize</c></item>
///         <item>DI construction (FileLogger, BackupStore constructors call <see cref="Prune"/>)</item>
///         <item><see cref="ApplyPendingReset"/> — once, immediately after <c>builder.Build()</c></item>
///     </list>
/// </remarks>
internal static class FilePruner
{
    internal const int DefaultMinRuns       = 3;
    internal const int DefaultBackupAgeDays = 90;
    internal const int DefaultLogAgeDays    = 30;

    static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RoslynMcp"
    );

    static readonly string RunCountPath = Path.Combine(StateDir, "run_count.txt");

    // Set by IncrementAndGetRunCount at startup; read by Prune without re-hitting disk.
    // -1 means not yet initialized (Prune called before Increment, which shouldn't happen).
    static int  cachedRunCount = -1;
    static bool resetPending;

    /// <summary>Exposes the cached startup run count to BackupStore for its pruning gate check.</summary>
    internal static int CachedRunCount => cachedRunCount;

    /// <summary>
    ///     Increments the persistent server run counter and caches the result for this process.
    ///     Must be called once, before DI construction, so <see cref="Prune"/> sees the
    ///     incremented count when called from constructors.
    ///     Returns 0 on any failure — callers treat 0 as "don't prune."
    /// </summary>
    internal static int IncrementAndGetRunCount()
    {
        var owned = false;

        using var mutex = new Mutex(false, @"Global\RoslynMcp_RunCounter");

        try {

            try {
                owned = mutex.WaitOne(2000);
            }
            catch(AbandonedMutexException) {

                // Previous holder crashed; ownership transferred to us.
                owned = true;
            }
            catch {
                // Cannot acquire — proceed without cross-process atomicity.
            }

            try {

                Directory.CreateDirectory(StateDir);

                var count = ReadRunCountCore();

                count++;

                // Atomic write: temp + rename eliminates partial-write corruption on crash.
                var tmp = RunCountPath + ".tmp";
                File.WriteAllText(tmp, count.ToString(CultureInfo.InvariantCulture));
                File.Move(tmp, RunCountPath, overwrite: true);

                cachedRunCount = count;
                return count;
            }
            catch {

                // Counter file missing, locked, or corrupt — fail safe: don't prune.
                cachedRunCount = 0;
                return 0;
            }
        }
        finally {

            if(owned)
                try { mutex.ReleaseMutex(); }
                catch { }
        }
    }

    /// <summary>
    ///     Deletes files matching <paramref name="searchPattern"/> in <paramref name="directory"/>
    ///     that are older than <paramref name="maxAge"/>, provided the run count has reached
    ///     <paramref name="minRuns"/>. A global named mutex ensures only one process prunes at a
    ///     time; if another holds it, this call is a no-op. Silently swallows all errors.
    ///     Signals a pending counter reset (applied by <see cref="ApplyPendingReset"/>).
    /// </summary>
    internal static void Prune(
        string directory,
        string searchPattern,
        TimeSpan maxAge,
        int minRuns,
        bool recursive = false)
    {
        // Fall back to disk only if called before Increment (mis-call order).
        var runCount = cachedRunCount >= 0 ? cachedRunCount : ReadRunCountCore();

        if(runCount < minRuns)
            return;

        var owned = false;

        using var mutex = new Mutex(false, @"Global\RoslynMcp_FilePruner");

        try {

            try {
                owned = mutex.WaitOne(0);

                if(!owned)
                    return;  // another process is already pruning — skip this run
            }
            catch(AbandonedMutexException) {
                owned = true;
            }
            catch {
                return;
            }

            var cutoff = DateTime.UtcNow - maxAge;
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            try {

                foreach(var file in Directory.EnumerateFiles(directory, searchPattern, option)) {

                    try {

                        if(File.GetLastWriteTimeUtc(file) < cutoff)
                            File.Delete(file);
                    }
                    catch {
                        // Another process may hold the file; silently skip.
                    }
                }
            }
            catch {
                // Directory enumeration failed — silently skip the entire pass.
            }

            resetPending = true;
        }
        finally {

            if(owned)
                try { mutex.ReleaseMutex(); }
                catch { }
        }
    }

    /// <summary>
    ///     Called by BackupStore after its own age-aware prune pass to participate in the
    ///     deferred counter reset without taking a dependency on <see cref="Prune"/>.
    /// </summary>
    internal static void RequestReset() => resetPending = true;

    /// <summary>
    ///     Writes the counter reset to disk if any pruning ran during this startup.
    ///     Must be called from <c>Program.cs</c> after <c>builder.Build()</c>, once all
    ///     DI constructors have had a chance to trigger their prune passes.
    /// </summary>
    internal static void ApplyPendingReset()
    {
        if(!resetPending)
            return;

        try {

            var tmp = RunCountPath + ".tmp";
            File.WriteAllText(tmp, "0");
            File.Move(tmp, RunCountPath, overwrite: true);
            cachedRunCount = 0;
        }
        catch {
            // Counter reset failed — prune will re-trigger on the next run.
            // TODO #176: surface persistent failures (e.g. write a sentinel file).
        }
    }

    // Returns the current run count without modifying it.
    // Returns 0 if the file is missing, unreadable, or not a non-negative integer.
    static int ReadRunCountCore()
    {
        try {

            var text = File.ReadAllText(RunCountPath).Trim();

            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                ? count
                : 0;
        }
        catch {
            return 0;
        }
    }
}
