namespace RoslynMcp.Cli;

class UpdateCommand : CliCommand
{
    public static int Run()
    {
        var executablePath = ExecutablePath;

        if(executablePath is null)
        {
            Console.Error.WriteLine("error: could not determine the current executable path.");

            return 1;
        }

        var results = AgentDetector.ProbeAll()
            .Where(r => r.ConfigExists && r.Entry is not null)
            .ToArray()
;

        if(results.Length == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  No configured agents found. Run 'roslynmcp setup' to configure.");
            Console.WriteLine();

            return 0;
        }

        WarnIfProcessesRunning();

        Console.WriteLine();
        Console.WriteLine($"  Updating all RoslynMcp entries to: {executablePath}");
        Console.WriteLine();

        var updatedCount = 0;
        var failedCount = 0;

        foreach(var r in results)
        {
            var outcome = AgentConfigPatcher.Patch(r.ConfigPath, r.Client, executablePath);

            switch(outcome.Result)
            {
                case PatchResult.Updated:
                    Console.WriteLine($"  ✓ {r.Client.Name}");
                    Console.WriteLine($"      {r.ConfigPath}");

                    if(outcome.BackupPath is not null)
                        Console.WriteLine($"      ⚠ Comments stripped, JSON reformatted. Backup: {outcome.BackupPath}");

                    updatedCount++;
                    break;

                case PatchResult.Added:
                    // update only touches existing entries, but Patch may still add if FindEntry missed it
                    Console.WriteLine($"  ✓ {r.Client.Name}  (entry added — was missing)");
                    Console.WriteLine($"      {r.ConfigPath}");
                    updatedCount++;
                    break;

                case PatchResult.Failed:
                    Console.WriteLine($"  ✗ {r.Client.Name}  — {outcome.Error}");
                    Console.WriteLine($"      {r.ConfigPath}");

                    if(outcome.BackupPath is not null)
                        Console.WriteLine($"      Backup preserved at: {outcome.BackupPath}");

                    failedCount++;
                    break;
            }

            Console.WriteLine();
        }

        Console.WriteLine($"  {updatedCount} updated, {failedCount} failed.");

        if(updatedCount > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  RoslynMcp v{CurrentVersion} — feedback & issues: https://github.com/MadQ/RoslynMcp");
        }

        Console.WriteLine();

        return failedCount > 0 ? 1 : 0;
    }


}
