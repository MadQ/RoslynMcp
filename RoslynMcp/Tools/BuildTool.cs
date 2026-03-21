using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class BuildTool(WorkspaceManager workspace)
{
    // Matches MSBuild diagnostic lines:
    //   path(line,col): error CS0103: message [proj::TargetFramework=net10.0]
    //   path(line,col): warning CS8600: message [proj]
    // The path, location, and project suffix are all optional (some messages omit them).
    private static readonly Regex DiagnosticLine = new(
        @"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s+(?<severity>error|warning)\s+(?<code>\w+):\s+(?<message>.+?)(?:\s+\[.+\])?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    [McpServerTool, Description(
        "Builds the project and returns structured diagnostics. By default, checks Roslyn diagnostics first " +
        "and skips the build if errors are found (fast path). If Roslyn reports no errors, proceeds with " +
        "'dotnet build' to validate MSBuild configuration. Set forceBuild=true to bypass Roslyn and always " +
        "run dotnet build — use sparingly, only when you suspect MSBuild-specific issues (restore, SDK, targets) " +
        "that Roslyn cannot detect. Requires a .csproj to be present.")]
    public async Task<object> BuildProject(
        [Description("Target framework to build, e.g. 'net10.0'. Omit to build the default (first) target framework.")] string? targetFramework = null,
        [Description("If true, skip Roslyn check and always run dotnet build. Use sparingly — only for MSBuild-specific validation.")] bool forceBuild = false)
    {
        if(workspace.CsprojPath is null)
            return new { error = "No .csproj found — build is only available in MSBuildWorkspace mode." };

        // Fast path: check Roslyn diagnostics first (unless forceBuild=true).
        if(!forceBuild) {

            var roslynDiagnostics = GetRoslynDiagnostics();
            var roslynErrors      = roslynDiagnostics.Where(d => d.Severity == "error").ToArray();

            if(roslynErrors.Length > 0) {

                var roslynWarnings = roslynDiagnostics.Where(d => d.Severity == "warning").ToArray();

                return new {
                    succeeded     = false,
                    errors        = roslynErrors,
                    warnings      = roslynWarnings,
                    source        = "roslyn",
                    build_skipped = true,
                    skip_reason   = "Roslyn reported errors — fix these first, then build will run automatically.",
                    duration_ms   = 0,
                    exit_code     = (int?) null
                };
            }
        }

        // Slow path: run actual dotnet build.
        var args = BuildArgs(workspace.CsprojPath, targetFramework);
        var (output, elapsed, exitCode) = await RunDotnetAsync(args);
        var diagnostics = ParseMSBuildDiagnostics(output, workspace.RootPath);
        var succeeded   = exitCode == 0;

        return new {
            succeeded,
            errors        = diagnostics.Where(d => d.Severity == "error")  .ToArray(),
            warnings      = diagnostics.Where(d => d.Severity == "warning").ToArray(),
            source        = "msbuild",
            build_skipped = false,
            skip_reason   = (string?) null,
            duration_ms   = (int) elapsed.TotalMilliseconds,
            exit_code     = (int?) exitCode,
        };
    }

    private static string BuildArgs(string csprojPath, string? tfm)
    {
        // --no-restore: restore is separate; /v:quiet: only errors/warnings + summary line.
        var tfmArg = tfm is not null ? $" -f {tfm}" : string.Empty;

        return $"build \"{csprojPath}\"{tfmArg} --no-restore /nologo /v:quiet";
    }

    private static async Task<(string output, TimeSpan elapsed, int exitCode)> RunDotnetAsync(string args)
    {
        var psi = new ProcessStartInfo("dotnet", args) {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var process = new Process { StartInfo = psi };
        var sw = Stopwatch.StartNew();
        process.Start();

        // Read both streams concurrently to avoid deadlocks on large output.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();
        sw.Stop();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        // MSBuild writes diagnostics to stdout; stderr is typically empty or SDK noise.
        var combined = string.IsNullOrWhiteSpace(stderr)
            ? stdout
            : stdout + stderr
        ;

        return (combined, sw.Elapsed, process.ExitCode);
    }

    private BuildDiagnostic[] GetRoslynDiagnostics()
    {
        var compilation = workspace.GetCompilation();
        var diagnostics = compilation.GetDiagnostics()
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(d => ConvertRoslynDiagnostic(d, workspace.RootPath))
            .ToArray();

        return diagnostics;
    }

    private static BuildDiagnostic ConvertRoslynDiagnostic(Diagnostic diagnostic, string rootPath)
    {
        var span     = diagnostic.Location.GetLineSpan();
        var filePath = span.Path;
        var relative = string.IsNullOrEmpty(filePath) ? "?" : TryMakeRelative(filePath, rootPath);
        var severity = diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning";

        return new BuildDiagnostic(
            Severity: severity,
            Code:     diagnostic.Id,
            Message:  diagnostic.GetMessage(),
            File:     relative,
            Line:     span.StartLinePosition.Line + 1,
            Column:   span.StartLinePosition.Character + 1
        );
    }

    private static BuildDiagnostic[] ParseMSBuildDiagnostics(string output, string rootPath)
    {
        var results = new List<BuildDiagnostic>();

        foreach(var raw in output.Split('\n')) {
            var line = raw.Trim();

            if(line.Length == 0)
                continue;

            var m = DiagnosticLine.Match(line);

            if(!m.Success)
                continue;

            var filePath = m.Groups["file"].Value.Trim();
            var relative = TryMakeRelative(filePath, rootPath);

            results.Add(new BuildDiagnostic(
                Severity: m.Groups["severity"].Value.ToLowerInvariant(),
                Code:     m.Groups["code"].Value,
                Message:  m.Groups["message"].Value.Trim(),
                File:     relative,
                Line:     int.Parse(m.Groups["line"].Value),
                Column:   int.Parse(m.Groups["col"].Value)
            ));
        }

        return [.. results];
    }

    private static string TryMakeRelative(string path, string rootPath)
    {
        try {
            return Path.GetRelativePath(rootPath, path);
        }
        catch {
            return path;
        }
    }

    private sealed record BuildDiagnostic(
        string Severity,
        string Code,
        string Message,
        string File,
        int    Line,
        int    Column
    );
}
