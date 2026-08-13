using System.Diagnostics;

namespace RoslynMcp.Cli;

record RunningProcess(int Pid, string? FileName);

static class ProcessChecker
{
    public static IReadOnlyList<RunningProcess> FindRunningInstances()
    {
        var results = new List<RunningProcess>();
        var currentPid = Environment.ProcessId;

        // Match all known binary names — current MadQ.RoslynMcp command, legacy bare/dotnet-prefixed
        // tools, and the PascalCase dev/publish build (exe/assembly name is still RoslynMcp).
        foreach(var p in Process.GetProcessesByName(ToolCommand.Name)
            .Concat(Process.GetProcessesByName("roslynmcp"))
            .Concat(Process.GetProcessesByName("RoslynMcp"))
            .Concat(Process.GetProcessesByName("dotnet-roslynmcp")))
        {
            if(p.Id == currentPid)
            {
                p.Dispose();
                continue;
            }

            string? fileName = null;
            try { fileName = p.MainModule?.FileName; } catch { }

            results.Add(new(p.Id, fileName));
            p.Dispose();
        }

        return results;
    }
}
