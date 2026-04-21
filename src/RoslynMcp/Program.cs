using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RoslynMcp;
using RoslynMcp.Tools;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;


// Route CLI subcommands before starting the MCP server.
// No args + stdin is a terminal → human ran this directly; show help instead of silently starting the server.
if(args.Length == 0 && !Console.IsInputRedirected)

    return PrintHelp();

if(args.Length > 0)
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;

    var exit = args[0].ToLowerInvariant() switch {

        "setup"       => RoslynMcp.Cli.SetupCommand.Run(),
        "setup-project" => RoslynMcp.Cli.SetupProjectCommand.Run(),
        "hook"        => RoslynMcp.Cli.HookCommand.Run(args),
        "list"        => RoslynMcp.Cli.ListCommand.Run(),
        "verify"      => RoslynMcp.Cli.VerifyCommand.Run(),
        "update"      => RoslynMcp.Cli.UpdateCommand.Run(),
        "--version"   => PrintVersion(),
        "-v"          => PrintVersion(),
        "--help"      => PrintHelp(),
        "-h"          => PrintHelp(),
        _             => -1,
    };

    if(exit >= 0)

        return exit;
}

static int PrintVersion()
{
    Console.WriteLine($"roslynmcp v{Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"}");

    return 0;
}

static int PrintHelp()
{
    var version = Assembly.GetEntryAssembly()
        ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion
        ?? "unknown"
    ;

    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine($"""
        roslynmcp v{version} — Roslyn MCP server for AI coding agents

        Usage:
          roslynmcp [options]            Start MCP server (stdio transport)
          roslynmcp <command> [options]

        Commands:
          setup         Configure AI agent clients (Copilot, Claude, Cursor, ...)
          setup-project Write per-project hook file to .github/ (git repo required)
          hook          Handle pre-tool-use hook events from stdin (used by hook runners)
          list          List configured AI agent clients
          verify        Verify agent configuration paths
          update        Update agent config paths after reinstall

        Options:
              --workspace    <mode>   Workspace mode: auto|sdk|vs|adhoc (default: auto)
          -p, --preload      <path>   Pre-warm workspace on startup (repeatable)
              --log-path     <path>   Log file base path (empty string = disable logging)
              --msbuild-path <path>   Override MSBuild installation path
          -v, --version               Print version and exit
          -h, --help                  Show this help and exit

        Environment variables (override options above):
          ROSLYNMCP_WORKSPACE            Workspace mode
          ROSLYNMCP_LOG_PATH             Log file base path
          ROSLYNMCP_BACKUP_PATH          Backup storage path
          ROSLYNMCP_LOG_MAX_AGE_DAYS     Log retention in days (default: 30)
          ROSLYNMCP_BACKUP_MAX_AGE_DAYS  Backup retention in days (default: 90)

        Documentation: https://github.com/MadQ/RoslynMcp
        """);

    return 0;
}

ServerArgs.Initialize(args);

// Increment the server start counter for pruning throttle — must run before DI construction
// so FileLogger and BackupStore constructors see the updated count.
FilePruner.IncrementAndGetRunCount()
;

// Log unhandled exceptions before the host/DI is available.
// This is the last line of defence — catches crashes that occur before tool handlers run.
// Uses ResolvedLogPath (same path FileLogger writes to) so crash traces land in the same file.
// For pre-DI crashes, File.AppendAllText is the only option; once FileLogger is up, it is
// promoted into the handler (below) so the write is serialised through the logger's write lock.
FileLogger? crashLogger = null
;
AppDomain.CurrentDomain.UnhandledException += (_, e) => {

    // Post-DI path: FileLogger is up, use it — serialised through writeLock.
    var logger = Volatile.Read(ref crashLogger)
;

    if(logger is not null) {

        logger.LogFatal($"Unhandled exception: {e.ExceptionObject}");

        return;
    }

    // Pre-DI path: logger not yet created; write directly (rare, acceptable race).
    var path = ServerArgs.Current.ResolvedLogPath
;

    if(path is not null) {

        try {

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fffZ}] [FATAL ] Unhandled exception: {e.ExceptionObject}\n");
        }
        catch { /* nowhere left to report */ }
    }
};

var builder = Host.CreateApplicationBuilder(args);

builder.Logging
	.ClearProviders()
	// Only log errors — MCP uses stdio; any stray output breaks the protocol.
	.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Error)
	.SetMinimumLevel(LogLevel.Error)
;

builder.Services
	.AddSingleton<WorkspaceManager>()
	.AddSingleton<WorkspaceResolver>()
	.AddSingleton<ApprovalStore>()
	.AddSingleton<BackupStore>()
	.AddSingleton<PaginationCache>()
	.AddSingleton<FileLogger>()
	.AddMcpServer()
	.WithStdioServerTransport()
	// UnsafeRelaxedJsonEscaping: emit printable ASCII as-is instead of \uXXXX sequences —
	// reduces response size significantly for symbol signatures and doc comments (issue #3).
	// JsonSerializerDefaults.Web: camelCase property names on all tool responses.
	.WithToolsFromAssembly(serializerOptions: new JsonSerializerOptions(JsonSerializerDefaults.Web) {
		
		  Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		, TypeInfoResolver = JsonSerializerOptions.Default.TypeInfoResolver
		, WriteIndented = false
	})
;

var host = builder.Build();

// Reset the run counter after all DI constructors have run their prune passes.
FilePruner.ApplyPendingReset()
;

var logger      = host.Services.GetRequiredService<FileLogger>();

// Promote the crash handler to the locked path now that FileLogger is available.
Volatile.Write(ref crashLogger, logger)
;

FileWriter.Initialize(logger);

var lifetime    = host.Services.GetRequiredService<IHostApplicationLifetime>();

lifetime.ApplicationStarted.Register(() => {
	
	logger.LogStart();
	logger.LogInfo("Workspace", $"mode={ServerArgs.Current.WorkspaceMode}");
	
	ServerHeartbeat.Initialize();
	
	// Warn if any agent config points to a stale path (e.g. after dotnet tool update).
	var currentExe = Environment.ProcessPath
	;
	if(currentExe is not null)
	{
		foreach(var r in RoslynMcp.Cli.AgentDetector.ProbeAll()
			.Where(r => r.Entry?.CommandPath is not null
				&& !string.Equals(r.Entry.CommandPath, currentExe, StringComparison.OrdinalIgnoreCase)))
			logger.LogInfo("AgentConfig", $"WARN: {r.Client.Name} config points to '{r.Entry!.CommandPath}' — run 'roslynmcp update'");
	}

});
lifetime.ApplicationStopping.Register(() => {

	ServerHeartbeat.Delete();
	logger.LogStop();
});


// Pre-warm cache if projects specified.
if(ServerArgs.Current.PreloadPaths.Length > 0) {

    var resolver = host.Services.GetRequiredService<WorkspaceResolver>();

    Console.Error.WriteLine($"Pre-loading {ServerArgs.Current.PreloadPaths.Length} project(s)...");

    foreach(var path in ServerArgs.Current.PreloadPaths) {

        if(!Directory.Exists(path) && !File.Exists(path)) {

            logger.LogInfo("Preload", $"path not found, skipping: {path}");
            continue;
        }

        try {

            resolver.GetCompilation(path);
            Console.Error.WriteLine($"✓ Loaded: {path}");
        }
        catch(Exception ex) {
            Console.Error.WriteLine($"✗ Failed to load {path}: {ex.Message}");
        }
    }
}

await host.RunAsync();

return 0;
