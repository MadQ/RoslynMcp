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
if(args.Length > 0)
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;

    var exit = args[0].ToLowerInvariant() switch {

        "setup"       => RoslynMcp.Cli.SetupCommand.Run(),
        "list"        => RoslynMcp.Cli.ListCommand.Run(),
        "verify"      => RoslynMcp.Cli.VerifyCommand.Run(),
        "update"      => RoslynMcp.Cli.UpdateCommand.Run(),
        "setup-hooks" => RoslynMcp.Cli.SetupHooksCommand.Run(),
        "hook"        => RoslynMcp.Cli.HookCommand.Run(),
        "--version"   => PrintVersion(),
        "-v"          => PrintVersion(),
        _             => -1,
    };

    if(exit >= 0)

        return exit;

    static int PrintVersion()
    {
        Console.WriteLine($"roslynmcp v{Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"}");

        return 0;
    }
}

ServerArgs.Initialize(args);

// Increment the server start counter for pruning throttle — must run before DI construction
// so FileLogger and BackupStore constructors see the updated count.
FilePruner.IncrementAndGetRunCount()
;

// Log unhandled exceptions before the host/DI is available.
// This is the last line of defence — catches crashes that occur before tool handlers run.
// Uses ResolvedLogPath (same path FileLogger writes to) so crash traces land in the same file.
// TODO: File.AppendAllText here races with FileLogger's writeLock on the same process
;
//       acceptable for now — crash handler and logger share the same per-PID file so only
//       the intra-process lock race remains. Track as a separate issue.
AppDomain.CurrentDomain.UnhandledException += (_, e) => {

    var path = ServerArgs.Current.ResolvedLogPath;

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

FileWriter.Initialize(logger);

var lifetime    = host.Services.GetRequiredService<IHostApplicationLifetime>();

lifetime.ApplicationStarted.Register(() => {
	
	logger.LogStart();
	logger.LogInfo("Workspace", $"mode={ServerArgs.Current.WorkspaceMode}");
	
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
lifetime.ApplicationStopping.Register(() => logger.LogStop());


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
