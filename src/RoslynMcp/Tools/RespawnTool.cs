using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

#if DEBUG
[McpServerToolType]
internal sealed class RespawnTool : RoslynMcpTool
{
	public RespawnTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_respawn", Title = "Respawn Server", OpenWorld = false, Destructive = true)]
	[Description(
		"DEBUG ONLY — terminates the MCP server process so the client respawns it with a fresh executable. " +
		"Use after rebuilding RoslynMcp to load the new binary without restarting your IDE. " +
		"All cached workspace state is lost on exit; the client must reconnect and reload workspaces. " +
		"Not fully reliable — if the client does not auto-respawn, the server will be unavailable. " +
		"The server exits gracefully after responding to this call.")]
	public object Respawn()
	{
		using var scope = BeginTool("roslyn_respawn");
		
		var pid = Environment.ProcessId;
		
		// Exit after a brief delay to let the response flush.
		Task.Run(async () => {
			
			await Task.Delay(150);
			Environment.Exit(0);
		});
		
		return scope.Outcome("terminating", new RespawnResult(
			"RoslynMcp server terminating — client will respawn automatically.",
			pid,
			"Rebuild first, then call respawn to load the new build."
		));
	}
}
#endif
