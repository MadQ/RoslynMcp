using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynMcp.Cli;

enum PatchResult { Added, Updated, Failed }

// How setup/update should treat the --elicit server flag when writing an entry.
// Preserve: keep whatever the existing entry had (used by `update`); Enable/Disable: set it (used by `setup`).
enum ElicitMode { Preserve, Enable, Disable }

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

    public static PatchOutcome Patch(string configPath, AgentClient client, string commandPath, ElicitMode elicitMode)
    {
        string? backupPath = null;

        try
        {
            // Through a link: a dangling one "exists" on Windows although there is nothing behind
            // it to read, and it must count as a config that has not been created yet.
            var isNewFile = !File.Exists(FileWriter.FollowLink(configPath));

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
                backupPath = configPath + ".roslynmcp.bak"
;
                File.WriteAllText(backupPath, json);

                // These files can hold tokens. The backup must not be readable by more people
                // than the config it copies.
                FileWriter.CopyUnixMode(configPath, backupPath);

                var parsed = JsonNode.Parse(json, documentOptions: ReadOptions);

                if(parsed is not JsonObject obj)

                    return new(PatchResult.Failed, backupPath,
                        Error: "Config file does not contain a JSON object at the root");

                root = obj;
            }

            var isUpdate = client.UpsertEntry(root, commandPath, elicitMode);

            // Atomic write: temp → final so a crash mid-write can't corrupt the config. A config
            // that is a symbolic link (kept in a dotfiles repository) is written through, the
            // link stays; an existing file keeps its permissions; a file created here is
            // private to its owner, since agent config files come to hold tokens (#322).
            FileWriter.ReplaceAtomic(
                configPath,
                FileWriter.Utf8NoBom.GetBytes(root.ToJsonString(WriteOptions)),
                AtomicReplace.FollowLink | AtomicReplace.PrivateWhenNew);

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
    }
}
