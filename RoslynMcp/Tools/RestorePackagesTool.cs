using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class RestorePackagesTool(WorkspaceManager workspace)
{
    [McpServerTool, Description(
        "Restores NuGet packages for the solution. " +
        "Use this after adding package references or when packages are missing. " +
        "Does not run dotnet build — just downloads and restores dependencies.")]
    public async Task<RestoreResult> RestorePackages()
    {
        var rootPath = workspace.RootPath;
        var projectFile = FindProjectFile(rootPath);

        if(projectFile is null)
            return new RestoreResult(false, "No .csproj file found in target directory.", null);

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"restore \"{projectFile}\"",
            WorkingDirectory = rootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo);

        if(process is null)
            return new RestoreResult(false, "Failed to start dotnet restore process.", null);

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var success = process.ExitCode == 0;
        var message = success
            ? "Packages restored successfully."
            : $"Restore failed with exit code {process.ExitCode}.";

        var details = string.IsNullOrWhiteSpace(error) ? output : error;

        return new RestoreResult(success, message, details);
    }

    private static string? FindProjectFile(string directory)
    {
        var csprojFiles = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);

        return csprojFiles.Length > 0 ? csprojFiles[0] : null;
    }
}

internal sealed record RestoreResult(
    bool Success,
    string Message,
    string? Details
);
