using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynMcp.Cli;

// Outcome of installing a client's user-level hook. The stale-entry case is its own value because
// the hook does work, yet setup must still say that an old entry was left behind.
enum HookInstall
{
    NotInstalled,
    Installed,
    InstalledStaleEntryRemains
}

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

    // Called by SetupCommand after MCP config is patched. NotInstalled (default) also covers a
    // client that doesn't support user-level hooks.
    public virtual HookInstall UpsertHook(string hookCommand) => HookInstall.NotInstalled;

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

    // Claude Code reads hooks from its settings file. ~/.claude.json holds mcpServers only;
    // earlier versions wrote the hook there, where it never ran (#319).
    static string HookSettingsPath => Path.Combine(Home, ".claude", "settings.json");

    // The built-in tools the hook acts on. Naming them keeps Claude Code from starting the hook
    // process for every other tool call (Bash, MCP tools, and so on).
    const string HookMatcher = "Read|Grep|Glob|Edit|MultiEdit|Write";

    // The hook answers in well under a second; Claude Code's default for a command hook is 600.
    const int HookTimeoutSeconds = 5;

    // Adds a pre-tool-use advisor hook that guides Claude Code to prefer roslyn_* tools for .cs
    // files. The hook applies globally to all Claude Code sessions.
    public override HookInstall UpsertHook(string hookCommand)
        => InstallHook(HookSettingsPath, GetConfigPaths()[0], hookCommand);

    // Installs into settingsPath, then removes the entry earlier versions left in legacyPath.
    // A failed cleanup does not undo the install — the leftover entry is inert, because Claude
    // Code does not read hooks from that file — but it is reported, so setup can say so instead
    // of claiming the old entry is gone.
    internal static HookInstall InstallHook(string settingsPath, string legacyPath, string hookCommand)
    {
        if(!UpsertHookIn(settingsPath, hookCommand))
            return HookInstall.NotInstalled;

        return RemoveHookFrom(legacyPath)
            ? HookInstall.Installed
            : HookInstall.InstalledStaleEntryRemains;
    }

    // Writes a single RoslynMcp hook entry into the settings file at settingsPath, replacing any
    // earlier one. Everything else in the file is preserved. False when the file cannot be
    // parsed or written, or holds a "hooks" or "PreToolUse" value of an unexpected shape — it
    // is then left untouched.
    internal static bool UpsertHookIn(string settingsPath, string hookCommand)
    {
        if(!TryLoadJsonObject(settingsPath, out var root))
            return false;

        // A missing (or null) container is created. One that exists with another shape — "hooks"
        // as an array, "PreToolUse" as a string — is not understood, and replacing it would
        // discard whatever the user put there: the file is left alone, like one that is not JSON.
        if(root["hooks"] is not JsonObject hooks)
        {
            if(root["hooks"] is not null)
                return false;

            hooks = [];
            root["hooks"] = hooks;
        }

        if(hooks["PreToolUse"] is not JsonArray preToolUse)
        {
            if(hooks["PreToolUse"] is not null)
                return false;

            preToolUse = [];
            hooks["PreToolUse"] = preToolUse;
        }

        RemoveOurHooks(preToolUse, out var existingCommand);

        preToolUse.Add(new JsonObject {

            ["matcher"] = HookMatcher,
            ["hooks"]   = new JsonArray {

                new JsonObject {

                    ["type"]    = "command",
                    // An entry that already invokes this tool keeps its command: an absolute
                    // path or --log is a deliberate local choice a rerun must not undo.
                    ["command"] = existingCommand ?? hookCommand,
                    ["timeout"] = HookTimeoutSeconds
                }
            }
        });

        return TryWriteJson(settingsPath, root);
    }

    // Removes RoslynMcp hook entries from a config file and nothing else, dropping the
    // "PreToolUse" and "hooks" containers only when that leaves them empty. True when the file
    // holds no such entry afterwards (including when it does not exist).
    internal static bool RemoveHookFrom(string configPath)
    {
        if(!File.Exists(configPath))
            return true;

        if(!TryLoadJsonObject(configPath, out var root))
            return false;

        if(root["hooks"] is not JsonObject hooks || hooks["PreToolUse"] is not JsonArray preToolUse)
            return true;

        if(!RemoveOurHooks(preToolUse, out _))
            return true;

        if(preToolUse.Count == 0)
            hooks.Remove("PreToolUse");

        if(hooks.Count == 0)
            root.Remove("hooks");

        return TryWriteJson(configPath, root);
    }

    // Removes every RoslynMcp hook (current or legacy command form) from a PreToolUse array, so
    // a renamed command never leaves a stale duplicate. A matcher group is dropped only when
    // that empties it — unrelated hooks in the same group stay. existingCommand is the command
    // of a removed entry that invokes the current tool name, or null.
    static bool RemoveOurHooks(JsonArray preToolUse, out string? existingCommand)
    {
        var removed = false;

        existingCommand = null;

        for(var i = preToolUse.Count - 1; i >= 0; i--)
        {
            if(preToolUse[i] is not JsonObject itemObj || itemObj["hooks"] is not JsonArray innerHooks)
                continue;

            var removedHere = false;

            for(var j = innerHooks.Count - 1; j >= 0; j--)
            {
                if(innerHooks[j] is not JsonObject hObj
                   || hObj["command"] is not JsonValue commandValue
                   || !commandValue.TryGetValue<string>(out var command)
                   || !ToolCommand.IsOurCommandInvocation(command))
                    continue;

                if(ToolCommand.InvokesCurrentName(command))
                    existingCommand = command;

                innerHooks.RemoveAt(j);
                removedHere = true;
            }

            removed |= removedHere;

            // Only a group this pass emptied goes. One that was already empty is not ours.
            if(removedHere && innerHooks.Count == 0)
                preToolUse.RemoveAt(i);
        }

        return removed;
    }

    // A missing or blank file loads as an empty object. False when the file holds anything that
    // is not a JSON object: writing back would then destroy it.
    static bool TryLoadJsonObject(string path, out JsonObject root)
    {
        root = [];

        if(!File.Exists(path))
            return true;

        try
        {
            var text = File.ReadAllText(path);

            if(string.IsNullOrWhiteSpace(text))
                return true;

            var parsed = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions {

                // Rejected here, as a parse error. Left to JsonObject, a duplicate key throws
                // ArgumentException only when the object is first indexed — outside this guard.
                AllowDuplicateProperties = false,
                AllowTrailingCommas = true,
                CommentHandling     = JsonCommentHandling.Skip
            });

            if(parsed is not JsonObject parsedObject)
                return false;

            root = parsedObject;

            return true;
        }
        catch(Exception ex) when(ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Temp file plus rename, so an interrupted write never leaves a truncated config behind.
    static bool TryWriteJson(string path, JsonObject root)
    {
        var tmp = path + ".roslynmcp.tmp";

        try
        {
            // A settings file kept in a dotfiles repository is a symlink. Renaming over the
            // link would replace it with a regular file and detach it from the repository, so
            // the write goes to the file the link points at.
            if(File.Exists(path) && File.ResolveLinkTarget(path, returnFinalTarget: true) is { } target)
            {
                path = target.FullName;
                tmp  = path + ".roslynmcp.tmp";
            }

            var dir = Path.GetDirectoryName(path);

            if(dir is not null)
                Directory.CreateDirectory(dir);

            // Relaxed escaping: the default encoder would turn every '&', '<', '>' and quote in
            // the user's own hook commands into \uXXXX, in a file people edit by hand.
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions {

                WriteIndented = true,
                Encoder       = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));

            // The rename puts the temp file in the target's place, mode included. Without this a
            // settings file the user made private (0600) would come back with the default mode.
            if(!OperatingSystem.IsWindows() && File.Exists(path))
                File.SetUnixFileMode(tmp, File.GetUnixFileMode(path));

            File.Move(tmp, path, overwrite: true);

            return true;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            // A failed rename must not leave a full copy of the user's config lying next to it.
            try { File.Delete(tmp); }
            catch(Exception cleanup) when(cleanup is IOException or UnauthorizedAccessException) { }

            return false;
        }
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
