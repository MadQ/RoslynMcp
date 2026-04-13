namespace RoslynMcp;

/// <summary>
///     Process-global startup configuration parsed once from CLI args and environment variables.
///     CLI flags take precedence over env vars.
/// </summary>
/// <remarks>
///     <para>
///         This is a static singleton, not a DI service. Startup configuration is process-global
///         and immutable after init — it is not a replaceable or scoped dependency. Threading it
///         through DI constructors would add indirection with no benefit. The established precedent
///         is <see cref="MSBuildBootstrap"/>, which uses the same pattern.
///     </para>
///     <para>
///         Call <see cref="Initialize"/> as the very first statement in <c>Program.cs</c> so that
///         all DI-constructed services and static fields can safely read <see cref="Current"/>.
///     </para>
/// </remarks>
internal sealed class ServerArgs
{
    static ServerArgs? current;

    /// <summary>
    ///     The parsed startup arguments. Throws <see cref="InvalidOperationException"/> if accessed
    ///     before <see cref="Initialize"/> is called.
    /// </summary>
    public static ServerArgs Current => current
        ?? throw new InvalidOperationException(
            $"{nameof(ServerArgs)}.{nameof(Initialize)}() must be called before accessing {nameof(Current)}.");

    /// <summary>
    ///     Parses all CLI args and environment variables. Must be called exactly once, as the very
    ///     first statement in <c>Program.cs</c>, before any DI setup or static field access.
    ///     Throws if called a second time.
    /// </summary>
    public static void Initialize(string[] rawArgs)
    {
        if(current is not null)
            throw new InvalidOperationException(
                $"{nameof(ServerArgs)}.{nameof(Initialize)}() has already been called.");

        current = new(rawArgs);
    }

    // ── CLI + env var ────────────────────────────────────────────────────────

    /// <summary>Workspace loading mode. CLI: <c>--workspace</c>. Env: <c>ROSLYNMCP_WORKSPACE</c>.</summary>
    public WorkspaceMode WorkspaceMode { get; }

    /// <summary>
    ///     Paths to preload on startup. CLI: <c>-p &lt;path&gt;</c> or <c>--preload &lt;path&gt;</c>
    ///     (may repeat). Positional args are not supported — quote paths with spaces.
    /// </summary>
    public string[] PreloadPaths { get; }

    /// <summary>
    ///     Configured log file base path. CLI: <c>--log-path</c>. Env: <c>ROSLYNMCP_LOG_PATH</c>.
    ///     <c>null</c> → default path. Empty string → logging disabled.
    ///     The actual log file uses <see cref="ResolvedLogPath"/>, which injects the PID.
    /// </summary>
    public string? LogPath { get; }

    /// <summary>
    ///     Effective log file path with PID injected before the extension — the single source of
    ///     truth for <see cref="FileLogger"/>, the unhandled-exception handler, and LogViewer
    ///     discovery. <c>null</c> if logging is disabled.
    ///     Resolved once at startup so all consumers agree on the same path.
    /// </summary>
    public string? ResolvedLogPath { get; }

    /// <summary>Maximum age for backup snapshots in days. Env: <c>ROSLYNMCP_BACKUP_MAX_AGE_DAYS</c>. Default: 90.</summary>
    public int BackupMaxAgeDays { get; }

    /// <summary>Maximum age for per-PID log files in days. Env: <c>ROSLYNMCP_LOG_MAX_AGE_DAYS</c>. Default: 30.</summary>
    public int LogMaxAgeDays { get; }

    /// <summary>
    ///     Minimum server start count before a pruning pass runs.
    ///     Env: <c>ROSLYNMCP_PRUNE_MIN_RUNS</c>. Default: 3.
    /// </summary>
    public int PruneMinRuns { get; }

    /// <summary>
    ///     Override MSBuild installation path. CLI: <c>--msbuild-path</c>. Env: <c>ROSLYNMCP_MSBUILD_PATH</c>.
    ///     Integration with MSBuildBootstrap is deferred (issue #138).
    /// </summary>
    public string? MsBuildPath { get; }

    // ── Env var only ─────────────────────────────────────────────────────────

    /// <summary>
    ///     Backup storage path. Env: <c>ROSLYNMCP_BACKUP_PATH</c>.
    ///     <c>null</c> → default path. Empty string → backups disabled.
    /// </summary>
    public string? BackupPath { get; }

    /// <summary>Maximum number of simultaneously cached workspaces. Env: <c>ROSLYNMCP_MAX_CACHED_WORKSPACES</c>. Minimum 1.</summary>
    public int MaxCachedWorkspaces { get; }

    /// <summary>Disables the project-path inference cache. Env: <c>ROSLYNMCP_DISABLE_PATH_CACHE</c> = <c>true</c>.</summary>
    public bool DisablePathCache { get; }

    ServerArgs(string[] args)
    {
        // Single forward pass: collect all flag–value pairs.
        // A token is treated as a flag value only if it does not itself start with '-'.
        // Paths starting with '-' are not supported — use './' prefix or absolute paths.
        string? workspaceFlag = null;
        string? logPathFlag   = null;
        string? msBuildFlag   = null;
        var     preload       = new List<string>();

		var length = args.Length;
		
		for(var i = 0; i < length; i++) {

            var arg = args[i];

            if(!arg.StartsWith('-'))
                continue;

            var value = i + 1 < length && !args[i + 1].StartsWith('-')
                ? args[++i]
                : (string?) null;

            switch(arg.ToLowerInvariant()) {

                case "--workspace":
                    workspaceFlag = value;
                    break;

                case "-p":
                case "--preload":
                    if(value is not null)
                        preload.Add(value);

                    break;

                case "--log-path":
                    logPathFlag = value;
                    break;

                case "--msbuild-path":
                    msBuildFlag = value;
                    break;
            }
        }

        WorkspaceMode = ParseWorkspaceMode(
            workspaceFlag ?? Environment.GetEnvironmentVariable("ROSLYNMCP_WORKSPACE"));

        PreloadPaths = [..preload];

        LogPath     = logPathFlag ?? Environment.GetEnvironmentVariable("ROSLYNMCP_LOG_PATH");
        MsBuildPath = msBuildFlag ?? Environment.GetEnvironmentVariable("ROSLYNMCP_MSBUILD_PATH");

        BackupPath = Environment.GetEnvironmentVariable("ROSLYNMCP_BACKUP_PATH");

        BackupMaxAgeDays = int.TryParse(Environment.GetEnvironmentVariable("ROSLYNMCP_BACKUP_MAX_AGE_DAYS"), out var bakAge)
            ? Math.Max(1, bakAge)
            : FilePruner.DefaultBackupAgeDays;

        LogMaxAgeDays = int.TryParse(Environment.GetEnvironmentVariable("ROSLYNMCP_LOG_MAX_AGE_DAYS"), out var logAge)
            ? Math.Max(1, logAge)
            : FilePruner.DefaultLogAgeDays;

        PruneMinRuns = int.TryParse(Environment.GetEnvironmentVariable("ROSLYNMCP_PRUNE_MIN_RUNS"), out var minRuns)
            ? Math.Max(1, minRuns)
            : FilePruner.DefaultMinRuns;

        // Resolve the effective log path once — PID injected before the extension.
        // FileLogger, the crash handler, and LogViewer all read this instead of
        // independently computing a path.
        var logBase = LogPath?.Length == 0
            ? null  // explicitly disabled
            : LogPath
              ?? Path.Combine(
                  Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                  "RoslynMcp", "logs", "roslynmcp.log"
              );

        if(logBase is null) {
            ResolvedLogPath = null;
        }
        else {

            var dir  = Path.GetDirectoryName(logBase) ?? ".";
            var stem = Path.GetFileNameWithoutExtension(logBase);
            var ext  = Path.GetExtension(logBase);

            ResolvedLogPath = Path.Combine(dir, $"{stem}.{Environment.ProcessId}{ext}");
        }

        DisablePathCache = string.Equals(
            Environment.GetEnvironmentVariable("ROSLYNMCP_DISABLE_PATH_CACHE"),
            "true",
            StringComparison.OrdinalIgnoreCase
        );

        MaxCachedWorkspaces = int.TryParse(
            Environment.GetEnvironmentVariable("ROSLYNMCP_MAX_CACHED_WORKSPACES"),
            out var max
        ) ? Math.Max(1, max) : 5;
    }

    static WorkspaceMode ParseWorkspaceMode(string? value) => value?.ToLowerInvariant() switch {
        "sdk"   => WorkspaceMode.Sdk,
        "vs"    => WorkspaceMode.Vs,
        "adhoc" => WorkspaceMode.Adhoc,
        _       => WorkspaceMode.Auto,
    };
}
