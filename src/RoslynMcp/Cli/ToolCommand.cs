namespace RoslynMcp.Cli;

/// <summary>
///     Single source of truth for how this build is invoked as a dotnet tool.
///     Keep <see cref="Name"/> in sync with <c>&lt;ToolCommandName&gt;</c> and
///     <see cref="PackageId"/> in sync with <c>&lt;PackageId&gt;</c> in RoslynMcp.csproj.
/// </summary>
static class ToolCommand
{
	// The command users type after `dotnet tool install -g MadQ.RoslynMcp`.
	// The package is namespaced under MadQ because the plain `RoslynMcp` / `roslynmcp`
	// names belong to an unrelated package already published on NuGet (chrismo80).
	public const string Name = "madq-roslynmcp";

	// The NuGet package id — used in install hints.
	public const string PackageId = "MadQ.RoslynMcp";

	// The pre-tool-use hook invocation written into agent config files.
	// A .NET global tool is invoked directly by its command name — NOT via `dotnet <name>`
	// (that driver form only resolves a `dotnet-<name>` shim, which this package no longer
	// ships). The project hook file is committed and shared across contributors, so the
	// command name (resolved on PATH) is used rather than a machine-specific absolute path.
	public const string HookCommand = Name + " hook";

	// Command/stem names shipped by earlier versions, still recognised when detecting or
	// migrating agent configs and running processes written before the MadQ.RoslynMcp rename.
	public static readonly string[] LegacyNames = ["roslynmcp", "dotnet-roslynmcp"];

	// The key our MCP server entry is written under in agent config files
	// (mcpServers/servers/context_servers). Namespaced to the package id so it cannot collide
	// with the unrelated chrismo80/RoslynMcp package, whose docs register under the key "roslyn".
	public const string ServerKey = PackageId;

	// Config-entry keys used by earlier versions of this tool AND by chrismo80/RoslynMcp
	// (same PackageId/command). A match on one of these keys alone is ambiguous — it may belong
	// to that unrelated tool — so callers must confirm before overwriting.
	public static readonly string[] AmbiguousServerKeys = ["RoslynMcp", "roslyn", "roslynmcp"];

	// True when the file stem matches this tool's command name or any legacy name.
	// Used to recognise a configured command path as ours regardless of install vintage.
	public static bool MatchesCommandStem(string stem) =>
		stem.Equals(Name, StringComparison.OrdinalIgnoreCase)
		|| LegacyNames.Any(n => stem.Equals(n, StringComparison.OrdinalIgnoreCase))
	;

	// True when a configured command is unmistakably THIS tool — never chrismo80/RoslynMcp.
	// Only the new command name and our legacy dotnet- driver shim qualify. A bare "roslynmcp",
	// a "RoslynMcp.exe", or the shared ~/.dotnet/tools/roslynmcp shim is indistinguishable from
	// chrismo80 (identical PackageId + command) and is treated as ambiguous instead.
	public static bool IsUnambiguousCommand(string? command)
	{
		if(string.IsNullOrWhiteSpace(command))
			return false;

		var stem = Path.GetFileNameWithoutExtension(command);

		return stem.Equals(Name, StringComparison.OrdinalIgnoreCase)
			|| stem.Equals("dotnet-roslynmcp", StringComparison.OrdinalIgnoreCase)
		;
	}

	// True when a configured command MIGHT be ours but is shared with chrismo80/RoslynMcp —
	// a bare "roslynmcp" / "RoslynMcp" stem (case-insensitively equal). Callers must confirm
	// before overwriting such an entry.
	public static bool IsAmbiguousCommand(string? command)
	{
		if(string.IsNullOrWhiteSpace(command))
			return false;

		return Path.GetFileNameWithoutExtension(command)
			.Equals("roslynmcp", StringComparison.OrdinalIgnoreCase)
		;
	}

	// True when a full invocation string is this tool's hook subcommand — current or any legacy
	// form. Handles the direct command form ("madq-roslynmcp hook", "roslynmcp hook --log") and
	// the legacy dotnet-driver form ("dotnet roslynmcp hook"). Used by setup/setup-project to
	// upsert our hook entry in place rather than appending a duplicate when the command name
	// changed across versions — otherwise a rename leaves the stale entry behind.
	// The subcommand is part of the match: these entries get removed or carried over as the
	// advisor hook, and some other invocation of this tool in a hook slot is neither.
	public static bool IsOurCommandInvocation(string? command)
	{
		if(string.IsNullOrWhiteSpace(command))
			return false;

		var tokens = SplitCommand(command);

		if(tokens.Count < 2)
			return false;

		var first = Path.GetFileNameWithoutExtension(tokens[0]);

		if(MatchesCommandStem(first))
			return IsHookSubcommand(tokens[1]);

		// Legacy dotnet-driver form: `dotnet <name> hook ...`.
		return first.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
			&& tokens.Count > 2
			&& MatchesCommandStem(Path.GetFileNameWithoutExtension(tokens[1]))
			&& IsHookSubcommand(tokens[2])
		;
	}

	static bool IsHookSubcommand(string token) => token.Equals("hook", StringComparison.OrdinalIgnoreCase);

	// True when a full invocation string runs this tool under its current command name — not a
	// legacy name. Setup keeps such a command as written (an absolute path, --log) instead of
	// replacing it; a legacy-named one must be replaced because that command no longer exists.
	public static bool InvokesCurrentName(string? command)
	{
		if(string.IsNullOrWhiteSpace(command))
			return false;

		var tokens = SplitCommand(command);

		return tokens.Count > 0
			&& Path.GetFileNameWithoutExtension(tokens[0]).Equals(Name, StringComparison.OrdinalIgnoreCase)
		;
	}

	// Splits an invocation on whitespace, keeping a double-quoted run together and dropping the
	// quotes: an executable path under a profile directory with a space in it is written quoted,
	// and a plain whitespace split would cut it at the space and miss the file name.
	static List<string> SplitCommand(string command)
	{
		var tokens  = new List<string>();
		var current = new System.Text.StringBuilder();
		var quoted  = false;

		foreach(var c in command) {

			if(c == '"')
				quoted = !quoted;
			else if(char.IsWhiteSpace(c) && !quoted) {

				if(current.Length > 0)
					tokens.Add(current.ToString());

				current.Clear();
			}
			else
				current.Append(c);
		}

		if(current.Length > 0)
			tokens.Add(current.ToString());

		return tokens;
	}
}
