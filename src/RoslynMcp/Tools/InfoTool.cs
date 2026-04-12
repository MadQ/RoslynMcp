using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class InfoTool : RoslynMcpTool
{
	public InfoTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_info", Title = "Server Info", ReadOnly = true, OpenWorld = false, Idempotent = true)]
	[Description(
		"Returns server metadata: version, build hash, process ID, uptime, and MSBuild discovery method. " +
		"Use to confirm which server instance is running, verify the version, or diagnose server state. " +
		"Optionally logs a marker entry — useful for marking test boundaries in the log viewer.")]
	public ToolResult Info(
		[Description("Optional label for the log marker, e.g. 'benchmark test 1 start'.")] string? marker = null)
	{
		using var scope = BeginTool("roslyn_info", marker);
		
		var asm     = typeof(InfoTool).Assembly;
		var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
		var pid     = Environment.ProcessId;
		var uptime  = Environment.TickCount64 / 1000;
		var msbuild = MSBuildBootstrap.DiscoveryMethod;
		
		if(marker is not null)
			scope.Record($"marker: {marker}");
		
		return scope.Outcome("info", new InfoResult(version, pid, uptime, msbuild, marker));
	}
}
