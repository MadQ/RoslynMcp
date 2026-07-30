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
    ///     Override MSBuild installation path — a dotnet SDK directory or a Visual Studio
    ///     <c>MSBuild\Current\Bin</c> directory. CLI: <c>--msbuild-path</c>.
    ///     Env: <c>ROSLYNMCP_MSBUILD_PATH</c>. Consumed by <see cref="MSBuildBootstrap.EnsureReady"/>,
    ///     which applies it in every mode except <see cref="WorkspaceMode.Adhoc"/> (that mode skips
    ///     MSBuild entirely). An empty value from either source is treated as absent — unlike
    ///     <see cref="LogPath"/>, an empty MSBuild path carries no meaning.
    /// </summary>
    public string? MsBuildPath { get; }

    /// <summary>
    ///     Enables live MCP elicitation on ambiguous symbol matches (interactive picker) instead
    ///     of the default structured candidate-list failure that agents recover from on their own.
    ///     CLI: <c>--elicit</c> (bare flag, or explicit <c>true</c>/<c>false</c> value).
    ///     Env: <c>ROSLYNMCP_ELICIT</c> = <c>true</c>. Default: <c>false</c>.
    /// </summary>
    public bool Elicit { get; }

    /// <summary>
    ///     Whether <see cref="Elicit"/> was explicitly set by a CLI arg or env var. Exists so the
    ///     project-config layer (<see cref="ProjectConfig"/>) knows when it may fill in a value:
    ///     an explicit <c>--elicit false</c> blocks a project file's <c>elicit: true</c>. Each
    ///     source binds only when it parses to a valid bool — an unparseable <c>--elicit</c> value
    ///     falls through to <c>ROSLYNMCP_ELICIT</c>, and an absent/invalid value in both sources
    ///     lets the project file decide.
    /// </summary>
    public bool ElicitSpecified { get; }

    /// <summary>
    ///     Whether <see cref="WorkspaceMode"/> was explicitly set to a concrete mode by a CLI arg
    ///     or env var. Exists so the project-config layer (<see cref="ProjectConfig"/>) knows when
    ///     it may fill in a value. An explicit <c>--workspace auto</c> is non-binding: it falls
    ///     through to <c>ROSLYNMCP_WORKSPACE</c>, and when neither source names a concrete mode
    ///     the value counts as unspecified — auto is "let the server decide", so a project file's
    ///     mode may still apply.
    /// </summary>
    public bool WorkspaceModeSpecified { get; }

    // ── Env var only ─────────────────────────────────────────────────────────

    /// <summary>
    ///     Backup storage path. Env: <c>ROSLYNMCP_BACKUP_PATH</c>.
    ///     <c>null</c> → default path. Empty string → backups disabled.
    /// </summary>
    public string? BackupPath { get; }

    /// <summary>Maximum number of simultaneously cached workspaces. Env: <c>ROSLYNMCP_MAX_CACHED_WORKSPACES</c>. Minimum 1.</summary>
    public int MaxCachedWorkspaces { get; }

    /// <summary>
    ///     Timeout for MSBuild workspace loads, in seconds. Env: <c>ROSLYNMCP_LOAD_TIMEOUT_SECONDS</c>.
    ///     Default: 300. Values 1–9 clamp to 10; 0 or negative disables the timeout.
    /// </summary>
    public int LoadTimeoutSeconds { get; }

    /// <summary>Disables the project-path inference cache. Env: <c>ROSLYNMCP_DISABLE_PATH_CACHE</c> = <c>true</c>.</summary>
    public bool DisablePathCache { get; }

    ServerArgs(string[] args)
    {
        // Single forward pass: collect all flag–value pairs.
        // A token is treated as a flag value only if it does not itself start with '-'.
        // Paths starting with '-' are not supported — use './' prefix or absolute paths.
        string? workspaceFlag = null
;
        string? logPathFlag   = null;
        string? msBuildFlag   = null;
        string? elicitFlag    = null; // "true"/"false" from CLI; null = flag absent
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

                case "--elicit":
                    // Bare flag means enabled; an explicit true/false value is also accepted.
                    elicitFlag = value ?? "true";
                    break;
            }
        }

        // Per-value precedence: a source binds only when it parses to a concrete mode. A merge
        // on mere presence (?? on the raw strings) would let a non-binding CLI value mask a
        // valid env var — e.g. `--workspace auto` silently ignoring ROSLYNMCP_WORKSPACE=sdk.
        var workspaceFromCli = ParseWorkspaceMode(workspaceFlag);

        WorkspaceMode = workspaceFromCli != WorkspaceMode.Auto
            ? workspaceFromCli
            : ParseWorkspaceMode(Env("ROSLYNMCP_WORKSPACE"));

        // Specified iff CLI or env parsed to a concrete mode — "auto" from either source stays
        // unspecified so a project file's mode may still apply (see the property doc).
        WorkspaceModeSpecified = WorkspaceMode != WorkspaceMode.Auto;

        PreloadPaths = [..preload];

        // LogPath keeps a presence-based merge: its empty string is load-bearing (logging disabled),
        // so an explicit --log-path "" must mask ROSLYNMCP_LOG_PATH. MsBuildPath has no such
        // meaning for empty, so each source binds only when non-empty — otherwise --msbuild-path ""
        // would silently suppress a valid ROSLYNMCP_MSBUILD_PATH and then fall through to
        // auto-discovery, the same inverted precedence this property was fixed for (issue #231).
        LogPath     = logPathFlag ?? Env("ROSLYNMCP_LOG_PATH");
        MsBuildPath = NullIfEmpty(msBuildFlag) ?? NullIfEmpty(Env("ROSLYNMCP_MSBUILD_PATH"));

        // Per-value precedence: the CLI flag binds only when it parses to a valid bool;
        // otherwise the env var is consulted. An unparseable --elicit value must not
        // suppress a valid ROSLYNMCP_ELICIT. A valid value from either source counts as
        // "specified" and blocks the project-config layer; otherwise the decision stays open.
        // That tri-state is why this uses bool.TryParse rather than the EnvTrue helper below —
        // it needs to distinguish "parsed" from "false". Plain on/off flags use EnvTrue.
        if(bool.TryParse(elicitFlag, out var cliElicit)) {

            ElicitSpecified = true;
            Elicit          = cliElicit;
        }
        else {

            ElicitSpecified = bool.TryParse(Env("ROSLYNMCP_ELICIT"), out var envElicit);
            Elicit          = ElicitSpecified && envElicit;
        }

        BackupPath = Env("ROSLYNMCP_BACKUP_PATH");

        BackupMaxAgeDays = EnvInt("ROSLYNMCP_BACKUP_MAX_AGE_DAYS", 1, FilePruner.DefaultBackupAgeDays);
        LogMaxAgeDays    = EnvInt("ROSLYNMCP_LOG_MAX_AGE_DAYS",    1, FilePruner.DefaultLogAgeDays);
        PruneMinRuns     = EnvInt("ROSLYNMCP_PRUNE_MIN_RUNS",      1, FilePruner.DefaultMinRuns);

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

        DisablePathCache = EnvTrue("ROSLYNMCP_DISABLE_PATH_CACHE");

        MaxCachedWorkspaces = EnvInt("ROSLYNMCP_MAX_CACHED_WORKSPACES", 1, 5);

        // Not EnvInt: the clamp is two-sided — 0 or negative disables the timeout outright,
        // while 1–9 raise to a 10s floor. A single minimum cannot express both.
        LoadTimeoutSeconds = int.TryParse(Env("ROSLYNMCP_LOAD_TIMEOUT_SECONDS"), out var loadTimeout)
            ? (loadTimeout <= 0 ? 0 : Math.Max(10, loadTimeout))
            : 300;
    }

    static WorkspaceMode ParseWorkspaceMode(string? value) => value?.ToLowerInvariant() switch {

        "sdk"   => WorkspaceMode.Sdk,
        "vs"    => WorkspaceMode.Vs,
        "adhoc" => WorkspaceMode.Adhoc,
        _       => WorkspaceMode.Auto,
    };

    // ── Env var readers ──────────────────────────────────────────────────────
    // Private on purpose. ServerArgs is already the process-wide sanitizing layer for every
    // ROSLYNMCP_* setting; these just remove the repetition inside it. Env vars read elsewhere
    // (DOTNET_ROOT, PATH, XDG_CONFIG_HOME) are discovery probes, not configuration, and are
    // validated at their point of use — they deliberately do not route through here.

    static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Parses an int env var, clamping to <paramref name="min"/>; unset or unparseable → <paramref name="fallback"/>.</summary>
    static int EnvInt(string name, int min, int fallback) =>
        int.TryParse(Env(name), out var value) ? Math.Max(min, value) : fallback;

    /// <summary>True only when the env var is set to the literal "true" (case-insensitive).</summary>
    static bool EnvTrue(string name) =>
        string.Equals(Env(name), "true", StringComparison.OrdinalIgnoreCase);
}
