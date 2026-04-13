namespace RoslynMcp.Cli;

class VerifyCommand : CliCommand
{
    public static int Run()
    {
        var results = AgentDetector.ProbeAll();
        var current = CurrentVersion;
        var hasIssues = false;

        Console.WriteLine();
        Console.WriteLine("Verifying RoslynMcp agent configurations...");
        Console.WriteLine();

        foreach(var r in results)
        {
            Console.WriteLine($"  {r.Client.Name}");
            Console.WriteLine($"    Config:  {r.ConfigPath}");

            if(!r.ConfigExists)
            {
                Console.WriteLine("    Status:  ✗ config file not found — run 'roslynmcp setup' to configure");
                Console.WriteLine();
                continue;
            }

            Console.WriteLine("    Config:  ✓ file exists");

            if(r.Entry is null)
            {
                Console.WriteLine("    Entry:   ✗ no RoslynMcp entry found — run 'roslynmcp setup' to configure");
                hasIssues = true;
                Console.WriteLine();
                continue;
            }

            Console.WriteLine($"    Entry:   ✓ {r.Entry.ServerName}");

            if(r.Entry.CommandPath is null)
            {
                Console.WriteLine("    Command: ✗ missing command path in entry");
                hasIssues = true;
                Console.WriteLine();
                continue;
            }

            Console.WriteLine($"    Command: {r.Entry.CommandPath}");

            if(!r.Entry.CommandExists)
            {
                Console.WriteLine("    Path:    ✗ file not found — run 'roslynmcp update' to fix");
                hasIssues = true;
                Console.WriteLine();
                continue;
            }

            Console.WriteLine("    Path:    ✓ exists");

            var configuredVersion = GetBinaryVersion(r.Entry.CommandPath);

            if(configuredVersion is null)
            {
                Console.WriteLine("    Version: ⚠ could not read version from binary");
            }
            else if(configuredVersion != current)
            {
                Console.WriteLine($"    Version: ⚠ outdated — configured v{configuredVersion}, current v{current}");
                Console.WriteLine("             Run 'roslynmcp update' to update all entries to the current binary.");
                hasIssues = true;
            }
            else
            {
                Console.WriteLine($"    Version: ✓ v{configuredVersion}");
            }

            Console.WriteLine();
        }

        var anyConfigured = results.Any(r => r.ConfigExists);

        if(hasIssues)
            Console.WriteLine("  One or more issues found. Run 'roslynmcp setup' or 'roslynmcp update' to fix.");
        else if(!anyConfigured)
            Console.WriteLine("  No agents configured. Run 'roslynmcp setup' to get started.");
        else
            Console.WriteLine("  All configured agents look good.");

        Console.WriteLine();
        return hasIssues ? 1 : 0;
    }
}
