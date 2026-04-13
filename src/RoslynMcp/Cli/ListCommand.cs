namespace RoslynMcp.Cli;

class ListCommand : CliCommand
{
    public static int Run()
    {
        var results = AgentDetector.ProbeAll();
        var current = CurrentVersion;

        Console.WriteLine();
        Console.WriteLine($"  {"Agent",-22} {"Status",-14} {"Entry",-14} {"Command"}");
        Console.WriteLine($"  {"─────",-22} {"──────",-14} {"─────",-14} {"───────"}");

        foreach(var r in results)
        {
            string status;
            string entry = "—";
            string command = "—";

            if(!r.ConfigExists)
            {
                status = "not found";
            }
            else if(r.Entry is null)
            {
                status = "not configured";
            }
            else
            {
                entry = r.Entry.ServerName;

                if(!r.Entry.CommandExists)
                {
                    status = "⚠ stale path";
                    command = r.Entry.CommandPath ?? "—";
                }
                else
                {
                    var configuredVersion = r.Entry.CommandPath is not null
                        ? GetBinaryVersion(r.Entry.CommandPath)
                        : null;

                    var versionNote = configuredVersion is not null
                        ? (configuredVersion == current ? $"v{configuredVersion}" : $"⚠ v{configuredVersion} (current: v{current})")
                        : "";

                    status = configuredVersion == current ? "✓ ok" : "⚠ outdated";
                    command = r.Entry.CommandPath is not null
                        ? $"{r.Entry.CommandPath}  {versionNote}".TrimEnd()
                        : "—";
                }
            }

            Console.WriteLine($"  {r.Client.Name,-22} {status,-14} {entry,-14} {command}");
        }

        Console.WriteLine();
        return 0;
    }
}
