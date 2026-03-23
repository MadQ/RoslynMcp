using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class RestorePackagesTool : RoslynMcpTool
{
    public RestorePackagesTool(WorkspaceResolver workspace) : base(workspace) { }

    [McpServerTool(Name = "roslyn_restore_packages", Idempotent = true)]
    [Description(
        "Restores NuGet packages for the solution. " +
        "Use this after adding package references or when packages are missing. " +
        "Does not run dotnet build — just downloads and restores dependencies.")]
    public async Task<RestoreResult> RestorePackages(
        [Description(ProjectPathDescription)] string? projectPath = null)
    {
        var rootPath = workspace.GetRootPath(projectPath);

        string? projectFile;

        try {
            projectFile = FindProjectFile(rootPath);
        }
        catch(Exception ex) when(ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException) {

            return new RestoreResult(false, "Failed to access project directory.", ex.Message);
        }

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

        Process process;

        try {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch(Win32Exception ex) {

            return new RestoreResult(false, "Failed to start dotnet process. Is dotnet installed and in PATH?", ex.Message);
        }
        catch(InvalidOperationException ex) {

            return new RestoreResult(false, "Failed to start dotnet restore process.", ex.Message);
        }

        string output, error;

        try {
            output = await process.StandardOutput.ReadToEndAsync();
            error  = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
        catch(IOException ex) {

            return new RestoreResult(false, "Failed to read process output.", ex.Message);
        }

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
