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

	// True when the file stem matches this tool's command name or any legacy name.
	// Used to recognise a configured command path as ours regardless of install vintage.
	public static bool MatchesCommandStem(string stem) =>
		stem.Equals(Name, StringComparison.OrdinalIgnoreCase)
		|| LegacyNames.Any(n => stem.Equals(n, StringComparison.OrdinalIgnoreCase))
	;

	// True when a full invocation string belongs to this tool — current or any legacy form.
	// Handles the direct command form ("madq-roslynmcp hook", "roslynmcp hook --log") and the
	// legacy dotnet-driver form ("dotnet roslynmcp hook"). Used by setup/setup-project to
	// upsert our hook entry in place rather than appending a duplicate when the command name
	// changed across versions — otherwise a rename leaves the stale entry behind.
	public static bool IsOurCommandInvocation(string? command)
	{
		if(string.IsNullOrWhiteSpace(command))
			return false;

		var tokens = command.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries);

		if(tokens.Length == 0)
			return false;

		var first = Path.GetFileNameWithoutExtension(tokens[0]);

		if(MatchesCommandStem(first))
			return true;

		// Legacy dotnet-driver form: `dotnet <name> ...`.
		if(first.Equals("dotnet", StringComparison.OrdinalIgnoreCase) && tokens.Length > 1)
			return MatchesCommandStem(Path.GetFileNameWithoutExtension(tokens[1]));

		return false;
	}
}
