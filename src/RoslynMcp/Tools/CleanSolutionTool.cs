#if FALSE
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

        string? projectFile;

        try {
            projectFile = FindProjectFile(rootPath);
        }
        catch(Exception ex) when(ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException) {

            return new CleanResult(false, "Failed to access project directory.", ex.Message);
        }

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

        Process process;

        try {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch(Win32Exception ex) {

            return new CleanResult(false, "Failed to start dotnet process. Is dotnet installed and in PATH?", ex.Message);
        }
        catch(InvalidOperationException ex) {

            return new CleanResult(false, "Failed to start dotnet clean process.", ex.Message);
        }

        string output, error;

        try {
            output = await process.StandardOutput.ReadToEndAsync();
            error  = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
        }
        catch(IOException ex) {

            return new CleanResult(false, "Failed to read process output.", ex.Message);
        }

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
#endif
