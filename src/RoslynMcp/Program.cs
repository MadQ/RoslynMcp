//
// Note: While I am generally not a fan of (IMHO) overly opinionated frameworks... admittedly, the Microsoft.Extensions.Hosting pattern
//       is a good fit for this kind of long-running server application. It provides a clean way to set up dependency injection,
//       logging, and graceful shutdown.
//         but... I'm still not a fan! 😤
//

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RoslynMcp;
using RoslynMcp.Tools;

// The target project directory is passed as the first argument.
// Default: current working directory (convenient when running from the repo root).
var targetPath = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();

if(!Directory.Exists(targetPath)) {
	Console.Error.WriteLine($"RoslynMcp: directory not found: {targetPath}");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Logging
    .ClearProviders()
    // Only log errors — MCP uses stdio; any stray output breaks the protocol.
    .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Error)
    .SetMinimumLevel(LogLevel.Error)
;

builder.Services
    .AddSingleton(_ => new WorkspaceManager(targetPath))
    .AddSingleton<ApprovalStore>()
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
;

// Register tool types so DI can inject WorkspaceManager and ApprovalStore.
builder.Services
    .AddTransient<TypeMembersTool>()
    .AddTransient<DiagnosticsTool>()
    .AddTransient<FindReferencesTool>()
    .AddTransient<SymbolInfoTool>()
    .AddTransient<PreviewRenameTool>()
    .AddTransient<ApplyRenameTool>()
    .AddTransient<SearchFilesTool>()
    .AddTransient<ProjectInfoTool>()
    .AddTransient<BuildTool>()
    .AddTransient<CleanSolutionTool>()
    .AddTransient<RestorePackagesTool>()
    .AddTransient<FileOutlineTool>()
    .AddTransient<TypeHierarchyTool>()
    .AddTransient<FindImplementationsTool>()
    .AddTransient<ListTypesTool>()
    .AddTransient<GetUsingsTool>()
    .AddTransient<GetSymbolDocumentationTool>()
    .AddTransient<GetSymbolDefinitionTool>()
    .AddTransient<GetSymbolsInScopeTool>()
#if DEBUG
    .AddTransient<RespawnTool>()
#endif
;

var host = builder.Build();

await host.RunAsync();

return 0;
