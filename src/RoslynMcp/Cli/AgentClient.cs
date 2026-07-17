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

    // Checked in order when searching for an existing entry — catches hand-edited names.
    public virtual string[] KnownServerNames => ["RoslynMcp", "roslyn", "roslynmcp"]
;

    // Ordered candidate paths to probe (first existing one wins).
    // Global/profile-level only for v0.8.0 — workspace-local configs are out of scope.
    public abstract string[] GetConfigPaths()
;

    // Find an existing RoslynMcp entry by known name or by matching the command path.
    // Returns the key used in the config and the entry node, or null if not present.
    public abstract (string Key, JsonObject Entry)? FindEntry(JsonObject root)
;

    // Add or update the RoslynMcp entry in root.
    // Returns true when an existing entry was updated, false when one was added.
    public abstract bool UpsertEntry(JsonObject root, string commandPath)
;

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

    public override (string Key, JsonObject Entry)? FindEntry(JsonObject root)
    {
        if(root[SectionKey] is not JsonObject servers)

            return null;

        foreach(var name in KnownServerNames)
        {
            if(servers[name] is JsonObject entry)

                return (name, entry);
        }

        // Fall back to matching by command path in case the user named it something else.
        foreach(var (key, value) in servers)
        {
            if(value is JsonObject entry && IsRoslynMcpCommand(entry))

                return (key, entry);
        }

        return null;
    }

    public override bool UpsertEntry(JsonObject root, string commandPath)
    {
        if(root[SectionKey] is not JsonObject servers)
        {
            servers = [];
            root[SectionKey] = servers;
        }

        var existing = FindEntry(root);
        var key = existing?.Key ?? "RoslynMcp";
        var isUpdate = existing is not null;

        servers[key] = BuildEntry(commandPath);

        return isUpdate;
    }

    public override string? GetCommandPath(JsonObject entry) =>
        entry["command"]?.GetValue<string>()
;

    protected virtual JsonObject BuildEntry(string commandPath) =>
        new() {

            ["command"] = commandPath,
            ["args"] = new JsonArray()
        };

    static bool IsRoslynMcpCommand(JsonObject entry)
    {
        var cmd = entry["command"]?.GetValue<string>();

        if(cmd is null)
            return false;

        var stem = Path.GetFileNameWithoutExtension(cmd);

        return ToolCommand.MatchesCommandStem(stem);
    }
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

        // Check if our entry is already present — avoid duplicates.
        foreach(var item in preToolUse)
        {
            if(item is not JsonObject itemObj)
                continue;

            if(itemObj["hooks"] is not JsonArray innerHooks)
                continue;

            foreach(var h in innerHooks)
            {
                if(h is JsonObject hObj &&
                   hObj["command"]?.GetValue<string>() == hookCommand)
                    return true;
            }
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

    protected override JsonObject BuildEntry(string commandPath) =>
        new() {

            ["type"] = "stdio",
            ["command"] = commandPath,
            ["args"] = new JsonArray()
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

    protected override JsonObject BuildEntry(string commandPath) =>
        new() {

            ["type"]    = "stdio",
            ["command"] = commandPath,
            ["args"]    = new JsonArray()
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

    public override (string Key, JsonObject Entry)? FindEntry(JsonObject root)
    {
        if(root["context_servers"] is not JsonObject servers)

            return null;

        foreach(var name in KnownServerNames)
        {
            if(servers[name] is JsonObject entry)

                return (name, entry);
        }

        foreach(var (key, value) in servers)
        {
            if(value is not JsonObject entry || GetCommandPath(entry) is not string cmd)
                continue;

            var stem = Path.GetFileNameWithoutExtension(cmd);

            if(ToolCommand.MatchesCommandStem(stem))
                return (key, entry);
        }

        return null;
    }

    public override bool UpsertEntry(JsonObject root, string commandPath)
    {
        if(root["context_servers"] is not JsonObject servers)
        {
            servers = [];
            root["context_servers"] = servers;
        }

        var existing = FindEntry(root);
        var key = existing?.Key ?? "RoslynMcp";
        var isUpdate = existing is not null;

        servers[key] = new JsonObject {

            ["command"] = new JsonObject {

                ["path"] = commandPath,
                ["args"] = new JsonArray()
            }
        };

        return isUpdate;
    }

    public override string? GetCommandPath(JsonObject entry) =>
        (entry["command"] as JsonObject)?["path"]?.GetValue<string>()
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
