using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class CleanSolutionTool(WorkspaceManager workspace)
{
    [McpServerTool, Description(
        "Cleans the solution by removing all build artifacts (bin/ and obj/ directories). " +
        "Use this when the build is in a bad state or before a fresh rebuild. " +
        "Does not run dotnet build — just removes compiled output.")]
    public async Task<CleanResult> CleanSolution()
    {
        var rootPath = workspace.RootPath;
        var projectFile = FindProjectFile(rootPath);

        if(projectFile is null)
            return new CleanResult(false, "No .csproj file found in target directory.", null);

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"clean \"{projectFile}\"",
            WorkingDirectory = rootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo);

        if(process is null)
            return new CleanResult(false, "Failed to start dotnet clean process.", null);

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var success = process.ExitCode == 0;
        var message = success
            ? "Solution cleaned successfully. All build artifacts removed."
            : $"Clean failed with exit code {process.ExitCode}.";

        var details = string.IsNullOrWhiteSpace(error) ? output : error;

        return new CleanResult(success, message, details);
    }

    private static string? FindProjectFile(string directory)
    {
        var csprojFiles = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);

        return csprojFiles.Length > 0 ? csprojFiles[0] : null;
    }
}

internal sealed record CleanResult(
    bool Success,
    string Message,
    string? Details
);
