using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

#if DEBUG
[McpServerToolType]
internal sealed class RespawnTool
{
	readonly FileLogger logger;
	
	public RespawnTool(FileLogger logger) { this.logger = logger; }
	
	[McpServerTool(Name = "roslyn_respawn", Destructive = true)]
	[Description(
		"DEBUG ONLY: Terminates the MCP server process, forcing the client to respawn it. " +
		"Use this to reload code changes after rebuilding without restarting your IDE. " +
		"The server will exit gracefully after responding.")]
	public object Respawn()
	{
		logger.LogTool("roslyn_respawn", 0, true, detail: "process terminating");
		var pid = Environment.ProcessId;
		
		// Exit after a brief delay to let the response flush.
		Task.Run(async () => {
		
			await Task.Delay(150);
			Environment.Exit(0);
		});
		
		return new {
			message = "RoslynMcp server terminating — client will respawn automatically.",
			pid = pid,
			tip = "Rebuild first, then call respawn to load the new build."
		};
	}
}
#endif
