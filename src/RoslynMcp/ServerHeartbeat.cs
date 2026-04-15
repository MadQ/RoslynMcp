namespace RoslynMcp;

/// <summary>
///     Writes and reads a per-PID heartbeat file under %LOCALAPPDATA%\RoslynMcp\ so that
///     the pre-tool-use hook can suppress advice when no live server instance is present.
///     All operations are best-effort and never throw.
/// </summary>
internal static class ServerHeartbeat
{

    static readonly string HeartbeatDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RoslynMcp"
    );

    static readonly string HeartbeatPath = Path.Combine(
        HeartbeatDir,
        $"heartbeat.{Environment.ProcessId}"
    );

    /// <summary>
    ///     Writes the heartbeat file for this process and prunes stale files from crashed
    ///     or old server instances.  Call once from the ApplicationStarted handler.
    /// </summary>
    internal static void Initialize()
    {
        try {

            Directory.CreateDirectory(HeartbeatDir);

            File.WriteAllText(HeartbeatPath, $"{Environment.ProcessId}");

            FilePruner.Prune(
                  HeartbeatDir
                , "heartbeat.*"
                , TimeSpan.FromDays(ServerArgs.Current.BackupMaxAgeDays)
                , ServerArgs.Current.PruneMinRuns
            );
        }
        catch { }
    }

    /// <summary>
    ///     Refreshes the heartbeat timestamp.  Called on every tool invocation so the
    ///     hook keeps seeing a live server even during extended idle periods.
    /// </summary>
    internal static void Touch()
    {
        try {

            File.WriteAllText(HeartbeatPath, $"{Environment.ProcessId}");
        }
        catch { }
    }

    /// <summary>Removes the heartbeat file on clean shutdown.  Best-effort.</summary>
    internal static void Delete()
    {
        try {

            File.Delete(HeartbeatPath);
        }
        catch { }
    }

    /// <summary>
    ///     Returns <see langword="true"/> if any heartbeat file in the store was written
    ///     within <paramref name="staleSeconds"/> seconds (default: 600).
    ///     Fails open on unexpected exceptions — the hook must never accidentally suppress
    ///     advice when the check itself breaks.
    ///     Returns <see langword="false"/> when the directory does not exist; that is an
    ///     unambiguous signal that no server instance has ever initialised.
    /// </summary>
    internal static bool IsAlive(int staleSeconds = 600)
    {
        if(!Directory.Exists(HeartbeatDir))
            return false;

        try {

            var cutoff = DateTime.UtcNow.AddSeconds(-staleSeconds);

            foreach(var file in Directory.EnumerateFiles(HeartbeatDir, "heartbeat.*")) {

                try {

                    if(File.GetLastWriteTimeUtc(file) >= cutoff)
                        return true;
                }
                catch { }
            }

            return false;
        }
        catch {

            // Unexpected enumeration failure — fail open so advice is not suppressed.
            return true;
        }
    }
}
