namespace RoslynMcp;

/// <summary>
///     Writes and reads a per-PID heartbeat file under %LOCALAPPDATA%\RoslynMcp\ so that
///     the tool-use hook can suppress advice when no live server instance is present.
///     All operations are best-effort and never throw.
///     <para>
///         The file holds the server's PID and, on a second line, the directory its default
///         workspace is looked for in (#310). One server alive anywhere on the machine used to
///         make the hook advise in every session, also in one that has no RoslynMcp at all, and
///         the advice then named tools that session does not have. With the directory in the
///         file the hook can ask for a server that serves where the session is working.
///     </para>
///     <c>ROSLYNMCP_HEARTBEAT_PATH</c> names another directory. The harness uses it, so that its
///     servers do not make the developer's own sessions look served, and so that a test can put
///     the heartbeats it needs in a directory of its own.
/// </summary>
internal static class ServerHeartbeat
{

    static readonly string HeartbeatDir = Environment.GetEnvironmentVariable("ROSLYNMCP_HEARTBEAT_PATH") is { Length: > 0 } configured
        ? configured
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoslynMcp");

    static readonly string HeartbeatPath = Path.Combine(
        HeartbeatDir,
        $"heartbeat.{Environment.ProcessId}"
    );

    // What the file holds: written whole at every touch, since a touch is a write anyway.
    static string content = $"{Environment.ProcessId}";

    /// <summary>
    ///     Writes the heartbeat file for this process and prunes stale files from crashed
    ///     or old server instances.  Call once from the ApplicationStarted handler.
    /// </summary>
    internal static void Initialize()
    {
        try {

            // The directory the default workspace is looked for in: --root or ROSLYNMCP_ROOT, else
            // where the server was started. Not the workspace that was found: that may be a
            // solution above this directory, and a call may name any other project.
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ServerArgs.Current.Root ?? Environment.CurrentDirectory));

            content = $"{Environment.ProcessId}\n{root}";
        }
        catch { }

        try {

            Directory.CreateDirectory(HeartbeatDir);

            File.WriteAllText(HeartbeatPath, content);

            RemoveDeadHeartbeats();

            // Analyzer shadow copies from dead servers. Copies still loaded by a live
            // server survive — Windows keeps loaded images locked and Prune skips them.
            FilePruner.Prune(
                  ShadowCopyAnalyzerLoader.ShadowRoot
                , "*.dll"
                , TimeSpan.FromDays(ServerArgs.Current.BackupMaxAgeDays)
                , ServerArgs.Current.PruneMinRuns
                , recursive: true
            );

            // Temp files of shadow copies whose server died between the write and the rename (#412).
            FilePruner.Prune(
                  ShadowCopyAnalyzerLoader.ShadowRoot
                , "*.tmp"
                , TimeSpan.FromDays(ServerArgs.Current.BackupMaxAgeDays)
                , ServerArgs.Current.PruneMinRuns
                , recursive: true
            );
        }
        catch { }
    }

    // A heartbeat this old is of a server that is gone, or of one so idle that the hook stopped
    // counting it long ago; the next call of a server that is still there writes its file again.
    static readonly TimeSpan DeadAfter = TimeSpan.FromHours(1);

    /// <summary>
    ///     Deletes the heartbeats of servers that are gone: a server that is killed, as the
    ///     development host kills the build it replaces and a client kills the server of a
    ///     session that ends, never removes its own. They were pruned with the backups, after
    ///     ninety days and in a pruning run only, although the hook ignores a heartbeat after
    ///     ten minutes; forty of them stood beside one that was live. At every start: it is one
    ///     short directory listing.
    /// </summary>
    static void RemoveDeadHeartbeats()
    {
        var cutoff = DateTime.UtcNow - DeadAfter;

        foreach(var file in Directory.EnumerateFiles(HeartbeatDir, "heartbeat.*")) {

            try {

                if(File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch { }
        }
    }

    /// <summary>
    ///     Refreshes the heartbeat timestamp.  Called on every tool invocation so the
    ///     hook keeps seeing a live server even during extended idle periods.
    /// </summary>
    internal static void Touch()
    {
        try {

            File.WriteAllText(HeartbeatPath, content);
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

    // A heartbeat older than this is a server that has stopped, or one nobody has called for so long
    // that advising its tools is a guess.
    const int StaleSeconds = 600;

    /// <summary>
    ///     Whether a live server serves <paramref name="workingDirectory"/>, the directory a
    ///     hook's session is working in: a heartbeat written within the last ten minutes whose
    ///     directory is that one, holds it, or lies inside it. The last case is a session at a
    ///     repository's root and a server started on a folder of it.
    ///     <para>
    ///         Counts as serving, since neither says where: a heartbeat without a directory (a
    ///         server from before #310), and a session whose hook input names no directory
    ///         (<paramref name="workingDirectory"/> null). A server started without a root in
    ///         the user's home serves everything beneath it by this rule; that is what every
    ///         server did before, and narrower than it only where the server says more.
    ///     </para>
    ///     Fails open on unexpected exceptions — the hook must never accidentally suppress
    ///     advice when the check itself breaks.
    ///     Returns <see langword="false"/> when the directory does not exist; that is an
    ///     unambiguous signal that no server instance has ever initialised.
    /// </summary>
    internal static bool IsAliveFor(string? workingDirectory)
    {
        if(!Directory.Exists(HeartbeatDir))
            return false;

        try {

            var cutoff  = DateTime.UtcNow.AddSeconds(-StaleSeconds);
            var session = workingDirectory is { Length: > 0 } ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory)) : null;

            foreach(var file in Directory.EnumerateFiles(HeartbeatDir, "heartbeat.*")) {

                try {

                    if(File.GetLastWriteTimeUtc(file) >= cutoff && Serves(file, session))
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

    // Whether the server of one heartbeat file serves the session's directory.
    static bool Serves(string heartbeatFile, string? session)
    {
        if(session is null)
            return true;

        // Shared with the server that is rewriting it: a heartbeat is touched on every call.
        using var stream = new FileStream(heartbeatFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        reader.ReadLine();

        if(reader.ReadLine() is not { Length: > 0 } root)
            return true;

        return SecurityBoundary.PathComparer.Equals(root, session)
            || SecurityBoundary.IsUnderDirectory(session, root)
            || SecurityBoundary.IsUnderDirectory(root, session);
    }
}
