using System.Globalization;

using System.Collections.Concurrent;

namespace RoslynMcp;

/// <summary>
///     Max-age eviction for long-lived file stores: per-PID log files and BackupStore snapshots.
///     A run-count guard throttles pruning so it only runs once every <see cref="DefaultMinRuns"/>
///     server starts. The counter is fail-safe — missing or corrupt content is treated as 0, which
///     is always below any positive <c>minRuns</c> threshold, so pruning is skipped rather than
///     over-pruning.
/// </summary>
/// <remarks>
///     <see cref="IncrementAndGetRunCount"/> runs once in <c>Program.cs</c>, immediately after
///     <c>ServerArgs.Initialize</c>. It decides there and then whether this process is a pruning run,
///     and when it is, the counter on disk starts over in that same step. The pruners (the log
///     directory, the heartbeat and shadow-copy directories, <see cref="BackupStore"/>) run whenever
///     their owner is first built: the logger and the heartbeat as the server starts, the backup
///     store when a pruning run builds it right after them (and otherwise at the first call of a
///     tool that writes). A reset applied at a fixed point of startup came before every one of
///     them, was never written, and left every process pruning (#445).
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
    static int  cachedRunCount = -1
;

    static readonly ConcurrentDictionary<string, ConcurrentBag<DateTimeOffset>> _pruneErrors = new();


    /// <summary>
    ///     Whether this process prunes: its start brought the counter to
    ///     <see cref="ServerArgs.PruneMinRuns"/>, and <see cref="IncrementAndGetRunCount"/> started
    ///     the counter on disk over for it.
    /// </summary>
    internal static bool IsPruningRun => cachedRunCount >= ServerArgs.Current.PruneMinRuns;

    internal static void RecordPruneError(Exception ex)
    {
        var bag = _pruneErrors.GetOrAdd(ex.Message, _ => new ConcurrentBag<DateTimeOffset>());
        bag.Add(DateTimeOffset.UtcNow);
    }

    /// <summary>Returns a snapshot of all prune errors grouped by message with sorted timestamps, or null if none.</summary>
    internal static IReadOnlyDictionary<string, DateTimeOffset[]>? GetPruneErrors()
    {
        if(_pruneErrors.IsEmpty)
            return null;

        return _pruneErrors.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.OrderBy(t => t).ToArray()
        );
    }


    /// <summary>
    ///     Increments the persistent server run counter and caches the result for this process.
    ///     Must be called once, before DI construction, so <see cref="Prune"/> sees the
    ///     incremented count when called from constructors.
    ///     Returns 0 on any failure — callers treat 0 as "don't prune."
    ///     <para>
    ///         A count that has reached <see cref="ServerArgs.PruneMinRuns"/> makes this process a
    ///         pruning run, and the file gets 0 in the same write, under the same mutex: the next
    ///         processes count up from there whatever becomes of this one's pruning. A process that
    ///         dies before it prunes costs one round, which the next starts make up for.
    ///     </para>
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
                owned = true
;
            }
            catch {
                // Cannot acquire — proceed without cross-process atomicity.
            }

            try {

                Directory.CreateDirectory(StateDir);

                var count = ReadRunCountCore();

                count++;

                var stored = count >= ServerArgs.Current.PruneMinRuns ? 0 : count;

                // Atomic write: temp + rename eliminates partial-write corruption on crash.
                var tmp = RunCountPath + ".tmp"
;
                File.WriteAllText(tmp, stored.ToString(CultureInfo.InvariantCulture));
                File.Move(tmp, RunCountPath, overwrite: true);

                cachedRunCount = count;

                return count;
            }
            catch {

                // Counter file missing, locked, or corrupt — fail safe: don't prune.
                cachedRunCount = 0
;

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
    /// </summary>
    internal static void Prune(
        string directory,
        string searchPattern,
        TimeSpan maxAge,
        int minRuns,
        bool recursive = false)
    {
        // Fall back to disk only if called before Increment (mis-call order).
        var runCount = cachedRunCount >= 0 ? cachedRunCount : ReadRunCountCore()
;

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
        }
        finally {

            if(owned)
                try { mutex.ReleaseMutex(); }
                catch { }
        }
    }

    // Returns the current run count without modifying it.
    // Returns 0 if the file is missing, unreadable, or not a non-negative integer.
    static int ReadRunCountCore()
    {
        try {

            var text = FileReader.ReadAllText(RunCountPath, FileReader.ConfigLimit).Trim();

            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                ? count
                : 0;
        }
        catch {
            return 0;
        }
    }
}
