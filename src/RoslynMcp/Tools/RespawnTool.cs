#if FALSE
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

#if DEBUG
[McpServerToolType]
internal sealed class RespawnTool
{
    [McpServerTool, Description(
        "DEBUG ONLY: Terminates the MCP server process, forcing the client to respawn it. " +
        "Use this to reload code changes after rebuilding without restarting your IDE. " +
        "The server will exit gracefully after responding.")]
    public object Respawn()
    {
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
#endif
