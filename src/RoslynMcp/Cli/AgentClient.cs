using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynMcp.Cli;

// Base for all supported AI coding agent integrations.
// Each subclass knows its own config path(s) and JSON schema for MCP server entries.
abstract partial class AgentClient
{
    public abstract string Name { get; }
    public abstract string Id { get; }

    // Ordered candidate paths to probe (first existing one wins).
    // Global/profile-level only for v0.8.0 — workspace-local configs are out of scope.
    public abstract string[] GetConfigPaths()
;

    // Find an existing RoslynMcp entry by known name or by matching the command path.
    // Returns the key used in the config, the entry node, and whether the match is ambiguous
    // (i.e. only matched a legacy/shared name that chrismo80/RoslynMcp also uses, so callers
    // must confirm before overwriting). Returns null if not present.
    public abstract (string Key, JsonObject Entry, bool Ambiguous)? FindEntry(JsonObject root)
;

    // Add or update the RoslynMcp entry in root.
    // Returns true when an existing entry was updated, false when one was added.
    public abstract bool UpsertEntry(JsonObject root, string commandPath, ElicitMode elicitMode)
;

    // The single server flag setup manages. Kept as a named constant so the token and the
    // detection/removal logic never drift apart.
    protected const string ElicitFlag = "--elicit";

    // Builds the args array to write: preserves every existing arg except --elicit, then
    // re-adds --elicit when the resolved state is on. Enable/Disable set it; Preserve keeps
    // whatever the existing entry had. This means re-running setup/update never clobbers
    // other user-added args.
    protected static JsonArray ComposeArgs(IEnumerable<string> existingArgs, ElicitMode mode)
    {
        var kept       = existingArgs.Where(a => !a.Equals(ElicitFlag, StringComparison.OrdinalIgnoreCase)).ToList();
        var hadElicit  = existingArgs.Any(a => a.Equals(ElicitFlag, StringComparison.OrdinalIgnoreCase));

        var elicitOn = mode switch {

            ElicitMode.Enable  => true,
            ElicitMode.Disable => false,
            _                  => hadElicit,   // Preserve
        };

        if(elicitOn)
            kept.Add(ElicitFlag);

        var array = new JsonArray();

        foreach(var a in kept)
            array.Add(a);

        return array;
    }

    // Reads a JSON args array into plain strings, skipping non-string elements defensively.
    protected static IEnumerable<string> ReadArgs(JsonArray? args) =>
        args is null
            ? []
            : args.OfType<JsonValue>()
                  .Select(v => v.TryGetValue<string>(out var s) ? s : null)
                  .OfType<string>();

    // Extract the configured command path from an entry (schema varies per client).
    public abstract string? GetCommandPath(JsonObject entry)
;

    // Called by SetupCommand after MCP config is patched. Returns true if the hook was
    // installed or updated; false (default) means this client doesn't support user-level hooks.
    public virtual bool UpsertHook(string hookCommand) => false;

}

// Shared by Claude Desktop, Cursor, and Windsurf:
//   { "<section>": { "<name>": { "command": "...", "args": [] } } }
abstract class McpServersDictClient : AgentClient
{
    protected abstract string SectionKey { get; }

    public override (string Key, JsonObject Entry, bool Ambiguous)? FindEntry(JsonObject root)
    {
        if(root[SectionKey] is not JsonObject servers)

            return null;

        // Pass 1 — unambiguous: our namespaced key, or a command that is unmistakably ours.
        foreach(var (key, value) in servers)
        {
            if(value is not JsonObject entry)
                continue;

            if(key.Equals(ToolCommand.ServerKey, StringComparison.OrdinalIgnoreCase) ||
               ToolCommand.IsUnambiguousCommand(GetCommandPath(entry)))

                return (key, entry, false);
        }

        // Pass 2 — ambiguous: a legacy key or bare command that chrismo80/RoslynMcp also uses.
        // Report it so callers can confirm before overwriting a possibly-foreign entry.
        foreach(var (key, value) in servers)
        {
            if(value is not JsonObject entry)
                continue;

            if(ToolCommand.AmbiguousServerKeys.Contains(key, StringComparer.OrdinalIgnoreCase) ||
               ToolCommand.IsAmbiguousCommand(GetCommandPath(entry)))

                return (key, entry, true);
        }

        return null;
    }

    public override bool UpsertEntry(JsonObject root, string commandPath, ElicitMode elicitMode)
    {
        if(root[SectionKey] is not JsonObject servers)
        {
            servers = [];
            root[SectionKey] = servers;
        }

        var existing = FindEntry(root);
        var isUpdate = existing is not null;

        var existingArgs = existing is { } ex ? ReadArgs(ex.Entry["args"] as JsonArray) : [];
        var args         = ComposeArgs(existingArgs, elicitMode);

        // Land on our namespaced key; drop any legacy/foreign key we are replacing so
        // subsequent runs match unambiguously (pass 1) and don't re-prompt. Callers gate
        // ambiguous matches with a confirmation before reaching this point.
        if(existing is { } e && !e.Key.Equals(ToolCommand.ServerKey, StringComparison.OrdinalIgnoreCase))
            servers.Remove(e.Key);

        servers[ToolCommand.ServerKey] = BuildEntry(commandPath, args);

        return isUpdate;
    }

    public override string? GetCommandPath(JsonObject entry) =>
        entry["command"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null
;

    protected virtual JsonObject BuildEntry(string commandPath, JsonArray args) =>
        new() {

            ["command"] = commandPath,
            ["args"] = args
        };
}

// Claude Desktop: %APPDATA%\Claude\claude_desktop_config.json (Windows)
//                ~/Library/Application Support/Claude/claude_desktop_config.json (macOS)
sealed class ClaudeDesktopClient : McpServersDictClient
{
    public override string Name => "Claude Desktop";
    public override string Id => "claude-desktop";
    protected override string SectionKey => "mcpServers";

    public override string[] GetConfigPaths()
    {
        if(RuntimeInformation.IsOSPlatform(OSPlatform.OSX))

            return [Path.Combine(Home, "Library", "Application Support", "Claude", "claude_desktop_config.json")];

        if(RuntimeInformation.IsOSPlatform(OSPlatform.Windows))

            return [Path.Combine(AppData, "Claude", "claude_desktop_config.json")];

        return [Path.Combine(XdgConfig, "Claude", "claude_desktop_config.json")];
    }
}

// Cursor (v0.47+): ~/.cursor/mcp.json (all platforms)

// Claude Code: ~/.claude.json (all platforms)
sealed class ClaudeCodeClient : McpServersDictClient
{
    public override string Name => "Claude Code";
    public override string Id => "claude-code";
    protected override string SectionKey => "mcpServers";

    public override string[] GetConfigPaths() =>
        [Path.Combine(Home, ".claude.json")];

    // Adds a pre-tool-use advisor hook to ~/.claude.json that guides Claude Code to prefer
    // roslyn_* tools for .cs files. Hook applies globally to all Claude Code sessions.
    public override bool UpsertHook(string hookCommand)
    {
        var configPath = GetConfigPaths()[0];
        JsonObject root;

        if(File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions {

                    AllowTrailingCommas = true,
                    CommentHandling    = JsonCommentHandling.Skip
                }) as JsonObject ?? [];
            }
            catch
            {
                return false;
            }
        }
        else
        {
            root = [];
        }

        if(root["hooks"] is not JsonObject hooks)
        {
            hooks = [];
            root["hooks"] = hooks;
        }

        if(hooks["PreToolUse"] is not JsonArray preToolUse)
        {
            preToolUse = [];
            hooks["PreToolUse"] = preToolUse;
        }

        // Remove any existing RoslynMcp hook entries (current or legacy command forms) so a
        // renamed command doesn't leave a stale duplicate, then append a single fresh entry.
        for(var i = preToolUse.Count - 1; i >= 0; i--)
        {
            if(preToolUse[i] is not JsonObject itemObj || itemObj["hooks"] is not JsonArray innerHooks)
                continue;

            for(var j = innerHooks.Count - 1; j >= 0; j--)
            {
                if(innerHooks[j] is JsonObject hObj &&
                   ToolCommand.IsOurCommandInvocation(hObj["command"]?.GetValue<string>()))
                    innerHooks.RemoveAt(j);
            }

            // Drop the wrapper only if removing our hook left it empty — preserve unrelated hooks.
            if(innerHooks.Count == 0)
                preToolUse.RemoveAt(i);
        }

        preToolUse.Add(new JsonObject {

            ["matcher"] = "",
            ["hooks"]   = new JsonArray {

                new JsonObject {

                    ["type"]    = "command",
                    ["command"] = hookCommand
                }
            }
        });

        var dir = Path.GetDirectoryName(configPath);

        if(dir is not null)
            Directory.CreateDirectory(dir);

        var updated = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var tmp     = configPath + ".roslynmcp.tmp";

        File.WriteAllText(tmp, updated);
        File.Move(tmp, configPath, overwrite: true);

        return true;
    }

}


sealed class CursorClient : McpServersDictClient
{
    public override string Name => "Cursor";
    public override string Id => "cursor";
    protected override string SectionKey => "mcpServers";

    public override string[] GetConfigPaths() =>
        [Path.Combine(Home, ".cursor", "mcp.json")]
;
}

// Windsurf: ~/.codeium/windsurf/mcp_config.json (all platforms)
sealed class WindsurfClient : McpServersDictClient
{
    public override string Name => "Windsurf";
    public override string Id => "windsurf";
    protected override string SectionKey => "mcpServers";

    public override string[] GetConfigPaths() =>
        [Path.Combine(Home, ".codeium", "windsurf", "mcp_config.json")]
;
}

// VS Code (Copilot): %APPDATA%\Code\User\mcp.json (Windows)
// Schema: { "servers": { "<name>": { "type": "stdio", "command": "...", "args": [] } } }
sealed class VsCodeCopilotClient : McpServersDictClient
{
    public override string Name => "VS Code (Copilot)";
    public override string Id => "vscode";
    protected override string SectionKey => "servers";

    public override string[] GetConfigPaths()
    {
        if(RuntimeInformation.IsOSPlatform(OSPlatform.OSX))

            return [Path.Combine(Home, "Library", "Application Support", "Code", "User", "mcp.json")];

        if(RuntimeInformation.IsOSPlatform(OSPlatform.Windows))

            return [Path.Combine(AppData, "Code", "User", "mcp.json")];

        return [Path.Combine(XdgConfig, "Code", "User", "mcp.json")];
    }

    protected override JsonObject BuildEntry(string commandPath, JsonArray args) =>
        new() {

            ["type"] = "stdio",
            ["command"] = commandPath,
            ["args"] = args
        };
}

// Copilot CLI (GitHub Copilot for CLI): ~/.copilot/mcp-config.json (global, all platforms)
// Schema: { "mcpServers": { "<name>": { "type": "stdio", "command": "...", "args": [] } } }
sealed class CopilotCliClient : McpServersDictClient
{
    public override string Name => "Copilot CLI";
    public override string Id   => "copilot-cli";
    protected override string SectionKey => "mcpServers";

    public override string[] GetConfigPaths() =>
        [Path.Combine(Home, ".copilot", "mcp-config.json")]
    ;

    protected override JsonObject BuildEntry(string commandPath, JsonArray args) =>
        new() {

            ["type"]    = "stdio",
            ["command"] = commandPath,
            ["args"]    = args
        }
    ;
}


// Zed: ~/.config/zed/settings.json (macOS/Linux), %APPDATA%\Zed\settings.json (Windows)
// Schema: { "context_servers": { "<name>": { "command": { "path": "...", "args": [] } } } }
sealed class ZedClient : AgentClient
{
    public override string Name => "Zed";
    public override string Id => "zed";

    public override string[] GetConfigPaths()
    {
        if(RuntimeInformation.IsOSPlatform(OSPlatform.Windows))

            return [Path.Combine(AppData, "Zed", "settings.json")];

        return [Path.Combine(XdgConfig, "zed", "settings.json")];
    }

    public override (string Key, JsonObject Entry, bool Ambiguous)? FindEntry(JsonObject root)
    {
        if(root["context_servers"] is not JsonObject servers)

            return null;

        // Pass 1 — unambiguous: our namespaced key, or a command that is unmistakably ours.
        foreach(var (key, value) in servers)
        {
            if(value is not JsonObject entry)
                continue;

            if(key.Equals(ToolCommand.ServerKey, StringComparison.OrdinalIgnoreCase) ||
               ToolCommand.IsUnambiguousCommand(GetCommandPath(entry)))

                return (key, entry, false);
        }

        // Pass 2 — ambiguous: a legacy key or bare command shared with chrismo80/RoslynMcp.
        foreach(var (key, value) in servers)
        {
            if(value is not JsonObject entry)
                continue;

            if(ToolCommand.AmbiguousServerKeys.Contains(key, StringComparer.OrdinalIgnoreCase) ||
               ToolCommand.IsAmbiguousCommand(GetCommandPath(entry)))

                return (key, entry, true);
        }

        return null;
    }

    public override bool UpsertEntry(JsonObject root, string commandPath, ElicitMode elicitMode)
    {
        if(root["context_servers"] is not JsonObject servers)
        {
            servers = [];
            root["context_servers"] = servers;
        }

        var existing = FindEntry(root);
        var isUpdate = existing is not null;

        var existingArgs = existing is { } ex ? ReadArgs((ex.Entry["command"] as JsonObject)?["args"] as JsonArray) : [];
        var args         = ComposeArgs(existingArgs, elicitMode);

        // Land on our namespaced key; drop any legacy/foreign key we are replacing.
        if(existing is { } e && !e.Key.Equals(ToolCommand.ServerKey, StringComparison.OrdinalIgnoreCase))
            servers.Remove(e.Key);

        servers[ToolCommand.ServerKey] = new JsonObject {

            ["command"] = new JsonObject {

                ["path"] = commandPath,
                ["args"] = args
            }
        };

        return isUpdate;
    }

    public override string? GetCommandPath(JsonObject entry) =>
        (entry["command"] as JsonObject)?["path"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null
;
}

abstract partial class AgentClient
{
    protected static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // %APPDATA% on Windows already includes \Roaming — no \Roaming suffix needed.
    protected static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
;

    // Prefer $XDG_CONFIG_HOME when set; fall back to ~/.config per XDG spec.
    protected static string XdgConfig =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Home, ".config");
}
