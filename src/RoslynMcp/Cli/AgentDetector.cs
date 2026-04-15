using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynMcp.Cli;

// Layered status for a single agent — client config found / RoslynMcp entry found / command valid.
record AgentProbeResult(
    AgentClient Client,
    string ConfigPath,
    bool ConfigExists,
    DetectedEntry? Entry
);

// Describes the RoslynMcp entry currently in an agent's config.
record DetectedEntry(
    string ServerName,
    string? CommandPath,
    bool CommandExists
);

static class AgentDetector
{
    static readonly AgentClient[] AllClients =
    [
        new ClaudeDesktopClient(),
        new ClaudeCodeClient(),
        new CursorClient(),
        new WindsurfClient(),
        new VsCodeCopilotClient(),
        new ZedClient()
    ];

    public static IReadOnlyList<AgentProbeResult> ProbeAll() =>
        AllClients.Select(Probe).ToArray()
;

    public static AgentProbeResult Probe(AgentClient client)
    {
        var paths = client.GetConfigPaths();
        var configPath = paths.FirstOrDefault(File.Exists) ?? paths[0];

        if(!File.Exists(configPath))

            return new(client, configPath, ConfigExists: false, Entry: null);

        DetectedEntry? entry = null;

        try
        {
            var json = File.ReadAllText(configPath);
            var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions {

                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            }) as JsonObject;

            if(root is not null)
            {
                var found = client.FindEntry(root);

                if(found is var (key, entryNode))
                {
                    var cmd = client.GetCommandPath(entryNode);
                    entry = new DetectedEntry(
                        ServerName: key,
                        CommandPath: cmd,
                        CommandExists: cmd is not null && File.Exists(cmd)
                    );
                }
            }
        }
        catch
        {
            // Malformed JSON or I/O failure — config exists but cannot be read.
            // The command layer will surface this when the user tries to patch.
        }

        return new(client, configPath, ConfigExists: true, Entry: entry);
    }
}
