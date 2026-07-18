using System.Diagnostics;
using System.Reflection;

namespace RoslynMcp.Cli;

abstract class CliCommand
{
    // Full path of the currently running roslynmcp executable.
    protected static string? ExecutablePath => Environment.ProcessPath
;

    // Informational version string of the running executable (e.g. "0.8.0-beta").
    protected static string CurrentVersion { get; } =
        Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? "unknown";

    // Reads the product version embedded in a binary on disk.
    // Returns null when the file doesn't exist or version info is unavailable.
    protected static string? GetBinaryVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);

            return string.IsNullOrWhiteSpace(info.ProductVersion)
                ? info.FileVersion
                : info.ProductVersion;
        }
        catch
        {
            return null;
        }
    }

    // Prompts before overwriting an agent entry that only matched a legacy/shared name and may
    // belong to the unrelated chrismo80/RoslynMcp package. Returns true when the user approves
    // replacing it (or when the entry is unambiguously ours — nothing to confirm).
    protected static bool ConfirmOverwriteForeignEntry(AgentProbeResult r)
    {
        if(r.Entry is not { Ambiguous: true } entry)

            return true;

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  ⚠ {r.Client.Name}: existing entry '{entry.ServerName}' → '{entry.CommandPath ?? "(no command)"}'");
        Console.WriteLine("      This name/command is shared with an unrelated tool (chrismo80/RoslynMcp).");
        Console.ResetColor();
        Console.Write("      Replace it with this RoslynMcp? [y/N]: ");

        var answer = Console.ReadLine()?.Trim() ?? "";

        return answer.Equals("y", StringComparison.OrdinalIgnoreCase)
            || answer.Equals("yes", StringComparison.OrdinalIgnoreCase)
        ;
    }

    protected static void WarnIfProcessesRunning()
    {
        var running = ProcessChecker.FindRunningInstances();

        if(running.Count == 0)

            return;

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  ⚠ {running.Count} RoslynMcp process(es) are currently running:");

        foreach(var p in running)
            Console.WriteLine($"      PID {p.Pid}  {p.FileName ?? "(unknown path)"}");

        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("  Updating config files is safe while they're running.");
        Console.WriteLine("  To also update the binary, ask your agents to disable RoslynMcp");
        Console.WriteLine("  or exit them before running 'dotnet tool update'.");
        Console.WriteLine();
    }
}
