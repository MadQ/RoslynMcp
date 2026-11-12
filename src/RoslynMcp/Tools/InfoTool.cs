using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class InfoTool : RoslynMcpTool
{
	readonly CodeActionHost codeActionHost;
	
	public InfoTool(WorkspaceResolver workspace, CodeActionHost codeActionHost, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, logger, paginationCache)
	{
		this.codeActionHost = codeActionHost;
	}
	
	[McpServerTool(Name = "roslyn_info", Title = "Server Info", ReadOnly = true, OpenWorld = false, Idempotent = true)]
	[Description(
		"Returns server metadata: version, build hash, process ID, uptime, and MSBuild discovery method; the default workspace " +
		"(default_workspace: the solution or project an omitted projectPath stands for, or why there is none, and which setting " +
		"chose its root); and the code-action " +
		"provider sources loaded so far (built-in, bundled, the projects' analyzer references, configured entries) with what " +
		"each yielded and any load problems — the place to look when a configured refactoring or fix provider does not show up. " +
		"Use to confirm which server instance is running, verify the version, or diagnose server state. " +
		"Optionally logs a marker entry — useful for marking test boundaries in the log viewer.")]
	public ToolResult Info(
		[Description("Optional label for the log marker, e.g. 'benchmark test 1 start'.")] string? marker = null)
	{
		using var scope = BeginTool("roslyn_info", marker);
		
		var asm     = typeof(InfoTool).Assembly;
		var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
		var pid     = Environment.ProcessId;
		var uptime  = (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds;
		var msbuild = MSBuildBootstrap.DiscoveryMethod;
		
		if(marker is not null)
			scope.Record($"marker: {marker}");
		
		// Reporting never composes: a server that has loaded no workspace yet says so.
		var codeActions = new CodeActionsInfo(
			MefComposition.IsComposed,
			MefComposition.IsComposed ? (long) MefComposition.Elapsed.TotalMilliseconds : null,
			MefComposition.IsComposed ? [..MefComposition.Assemblies.Select(assembly => assembly.GetName().Name ?? "?")] : null,
			UserConfig.FilePath,
			[..codeActionHost.Reports.Select(ProviderSourceInfo.From)]);
		
		return scope.Outcome("info", new InfoResult(version, pid, uptime, msbuild, FilePruner.GetPruneErrors(), marker, codeActions, workspace.DescribeDefaultWorkspace()));
	}
}
