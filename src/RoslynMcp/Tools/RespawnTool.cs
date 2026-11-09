using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

/// <summary>
///     Ends the server process on purpose so the MCP client starts a fresh one. The hints of the
///     diagnostics, drift, build and reload paths name this tool as the way to reload a workspace that
///     loaded badly, so it ships in every build (#406): a hint naming a tool the build does not have is a
///     dead end for an agent, which has no other way to restart the server. The clients that spawn stdio
///     servers (Claude Code, Copilot CLI, VS Code) restart one that exits with code 0; where the client
///     does not, the user restarts it by hand — which is where a workspace in that state had left them anyway.
/// </summary>
[McpServerToolType]
internal sealed class RespawnTool : RoslynMcpTool
{
	// Long enough for the SDK to write the response to stdout before the host stops. The exit is what
	// the caller asked for, so a little latency costs nothing; a response lost to a fast exit would.
	static readonly TimeSpan ResponseFlushDelay = TimeSpan.FromMilliseconds(250);
	
	// The graceful stop disposes the workspaces; should one of them hang, the process still goes away,
	// because a server that neither answers nor exits is the one state no client recovers from.
	// Kill, not Environment.Exit: the latter waits for the same host shutdown that is hanging.
	static readonly TimeSpan GracefulStopLimit = TimeSpan.FromSeconds(10);
	
	readonly IHostApplicationLifetime lifetime;
	
	public RespawnTool(WorkspaceResolver workspace, IHostApplicationLifetime lifetime, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, logger, paginationCache)
	{
		this.lifetime = lifetime;
	}
	
	[McpServerTool(Name = "roslyn_respawn", Title = "Respawn Server", OpenWorld = false, Destructive = true)]
	[Description(
		"Ends the MCP server process gracefully with exit code 0, so the client starts a fresh one. " +
		"Use when a workspace loaded badly and no reload has repaired it — roslyn_check_drift reports workspace_healthy false, or " +
		"roslyn_get_diagnostics reports phantom errors that a real build does not — or, after rebuilding RoslynMcp itself, to load the new binary. " +
		"Every cached workspace is dropped; the next call loads it again from disk. " +
		"The restart is the client's: Claude Code, Copilot CLI and VS Code restart a stdio server that exits, while a client that does not " +
		"leaves the server unavailable until the user restarts it. " +
		"The server exits after this call has been answered.")]
	public object Respawn()
	{
		using var scope = BeginTool("roslyn_respawn");
		
		var pid = Environment.ProcessId;
		
		// After the response is on its way: StopApplication runs the ordinary shutdown (heartbeat
		// removed, STOP logged, workspaces disposed) and Program returns 0. A shutdown that hangs is
		// ended by force — a non-zero exit then, but an exit.
		_ = Task.Run(async () => {
			
			await Task.Delay(ResponseFlushDelay);
			lifetime.StopApplication();
			await Task.Delay(GracefulStopLimit);
			Process.GetCurrentProcess().Kill();
		});
		
		return scope.Outcome("terminating", new RespawnResult(
			$"RoslynMcp server (pid {pid}) is exiting; the client restarts it. Every cached workspace is dropped.",
			pid,
			"Wait a moment, then call roslyn_info: a different pid means the new server is up. Rebuild first when the point is to load a new build."
		));
	}
}
