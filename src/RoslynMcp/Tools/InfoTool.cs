using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class InfoTool(FileLogger logger)
{
	[McpServerTool(Name = "roslyn_info", Title = "Server Info", ReadOnly = true, OpenWorld = false, Idempotent = true)]
	[Description(
		"Returns RoslynMcp server info: version, build hash, process ID, uptime, MSBuild discovery method. " +
		"Also logs a marker entry — useful for identifying test boundaries in the log viewer.")]
	public object Info(
		[Description("Optional label for the log marker, e.g. 'benchmark test 1 start'.")] string? marker = null)
	{
		var asm     = typeof(InfoTool).Assembly;
		var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
		var pid     = Environment.ProcessId;
		var uptime  = Environment.TickCount64 / 1000;
		var msbuild = MSBuildBootstrap.DiscoveryMethod;

		var msg = marker is not null ? $"INFO marker: {marker}" : "INFO requested";
		logger.LogInfo("Info", msg);

		return new {
			version,
			pid,
			uptime_seconds = uptime,
			msbuild_discovery = msbuild,
			marker
		};
	}
}
