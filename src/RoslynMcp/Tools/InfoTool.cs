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
		"Analyzer references that loaded without a problem and hold no refactoring or fix are only counted " +
		"(sources_without_providers) unless allSources is true. " +
		"Use to confirm which server instance is running, verify the version, or diagnose server state. " +
		"Optionally logs a marker entry — useful for marking test boundaries in the log viewer.")]
	public ToolResult Info(
		[Description("Optional label for the log marker, e.g. 'benchmark test 1 start'.")] string? marker = null,
		[Description(
			"Default false: a project's analyzer reference is listed as a provider source only when it holds a refactoring or a fix, " +
			"or had a problem; the rest are counted in sources_without_providers. Set true to list every source, when chasing a " +
			"provider that does not show up.")] bool allSources = false)
	{
		using var scope = BeginTool("roslyn_info", marker);
		
		var asm     = typeof(InfoTool).Assembly;
		var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
		var pid     = Environment.ProcessId;
		var uptime  = (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds;
		var msbuild = MSBuildBootstrap.DiscoveryMethod;
		
		if(marker is not null)
			scope.Record($"marker: {marker}");
		
		// A project's analyzer reference that loaded and holds no provider says nothing an agent acts
		// on, and there are many: every helper assembly of an analyzer package, every source
		// generator (#448). The fixed sources and the configured entries are always listed, since
		// their absence would be a question.
		ProviderSourceReport[] reports = [..codeActionHost.Reports];
		ProviderSourceReport[] listed  = allSources ? reports : [..reports.Where(SaysSomething)];
		
		// Reporting never composes: a server that has loaded no workspace yet says so.
		var codeActions = new CodeActionsInfo(
			MefComposition.IsComposed,
			MefComposition.IsComposed ? (long) MefComposition.Elapsed.TotalMilliseconds : null,
			MefComposition.IsComposed ? [..MefComposition.Assemblies.Select(assembly => assembly.GetName().Name ?? "?")] : null,
			UserConfig.FilePath,
			[..listed.Select(ProviderSourceInfo.From)])
		{
			SourcesWithoutProviders = listed.Length < reports.Length ? reports.Length - listed.Length : null,
		};
		
		return scope.Outcome("info", new InfoResult(version, pid, uptime, msbuild, FilePruner.GetPruneErrors(), marker, codeActions, workspace.DescribeDefaultWorkspace()));
	}
	
	static bool SaysSomething(ProviderSourceReport report)
		=> report.Kind != ProviderSourceKind.Project || report.Refactorings > 0 || report.Fixes > 0 || !report.Problems.IsEmpty
	;
}
