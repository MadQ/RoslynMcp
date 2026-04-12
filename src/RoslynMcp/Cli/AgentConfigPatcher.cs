using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynMcp.Cli;

enum PatchResult { Added, Updated, Failed }

record PatchOutcome(
    PatchResult Result,
    string? BackupPath = null,
    bool IsNewFile = false,
    string? Error = null
);

static class AgentConfigPatcher
{
    static readonly JsonSerializerOptions WriteOptions = new() {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static readonly JsonDocumentOptions ReadOptions = new() {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static PatchOutcome Patch(string configPath, AgentClient client, string commandPath)
    {
        string? backupPath = null;
        string? tempPath = null;

        try
        {
            var isNewFile = !File.Exists(configPath);

            JsonObject root;

            if(isNewFile)
            {
                var dir = Path.GetDirectoryName(configPath);
                if(dir is not null)
                    Directory.CreateDirectory(dir);

                root = [];
            }
            else
            {
                var json = File.ReadAllText(configPath);

                // Backup before touching anything — one .roslynmcp.bak per config,
                // overwritten on each run so it always holds the last pre-roslynmcp state.
                backupPath = configPath + ".roslynmcp.bak";
                File.WriteAllText(backupPath, json);

                var parsed = JsonNode.Parse(json, documentOptions: ReadOptions);

                if(parsed is not JsonObject obj)
                    return new(PatchResult.Failed, backupPath,
                        Error: "Config file does not contain a JSON object at the root");

                root = obj;
            }

            var isUpdate = client.UpsertEntry(root, commandPath);

            // Atomic write: temp → final so a crash mid-write can't corrupt the config.
            tempPath = configPath + ".roslynmcp.tmp";
            File.WriteAllText(tempPath, root.ToJsonString(WriteOptions));
            File.Move(tempPath, configPath, overwrite: true);

            return new(isUpdate ? PatchResult.Updated : PatchResult.Added, backupPath, isNewFile);
        }
        catch(JsonException ex)
        {
            return new(PatchResult.Failed, backupPath,
                Error: $"Malformed JSON ({ex.Path}, line {ex.LineNumber}): {ex.Message}");
        }
        catch(Exception ex)
        {
            return new(PatchResult.Failed, backupPath, Error: ex.Message);
        }
        finally
        {
            // Clean up temp file if the move didn't happen (exception path).
            if(tempPath is not null && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }
}
