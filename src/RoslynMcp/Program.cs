using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RoslynMcp;
using RoslynMcp.Tools;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;


var workspaceMode    = WorkspaceModeParser.Resolve(args);
var projectsToPreload = WorkspaceModeParser.StripWorkspaceArg(args);

// Log unhandled exceptions before the host/DI is available.
// This is the last line of defence — catches crashes that occur before tool handlers run.
AppDomain.CurrentDomain.UnhandledException += (_, e) => {
	
	var path = Environment.GetEnvironmentVariable("ROSLYNMCP_LOG_PATH")
		?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoslynMcp", "logs", "roslynmcp.log");
	
	if(!string.IsNullOrEmpty(path)) {
		
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
	// UnsafeRelaxedJsonEscaping: emit printable ASCII as-is instead of \uXXXX sequences.
	// Reduces response size significantly for symbol signatures and doc comments (issue #3).
	.WithToolsFromAssembly(serializerOptions: new JsonSerializerOptions(JsonSerializerDefaults.Web) {
		
		  Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		, TypeInfoResolver = JsonSerializerOptions.Default.TypeInfoResolver
		, WriteIndented = false
	})
;

var host = builder.Build();

var logger      = host.Services.GetRequiredService<FileLogger>();
var lifetime    = host.Services.GetRequiredService<IHostApplicationLifetime>();

lifetime.ApplicationStarted.Register(() => {
	
	logger.LogStart();
	logger.LogInfo("Workspace", $"mode={workspaceMode}");

});
lifetime.ApplicationStopping.Register(() => logger.LogStop());


// Pre-warm cache if projects specified.
if(projectsToPreload.Length > 0) {
	
	var resolver = host.Services.GetRequiredService<WorkspaceResolver>();
	
	Console.Error.WriteLine($"Pre-loading {projectsToPreload.Length} project(s)...");
	
	foreach(var path in projectsToPreload) {
		
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
