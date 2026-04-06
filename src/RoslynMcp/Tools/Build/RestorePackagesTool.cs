using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class RestorePackagesTool : RoslynMcpTool
{
	public RestorePackagesTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_restore_packages", Title = "Restore Packages", Idempotent = true, Destructive = false)]
	[Description(
		"Downloads and restores NuGet packages for the project — makes network calls to NuGet feeds. " +
		"Use after adding or modifying package references in the .csproj, or when packages are missing. " +
		"Does not compile or validate C# source — for a full build after restore, use roslyn_build_project. " +
		"Requires a .csproj to be present.")]
	public async Task<RestoreResult> RestorePackages(
		[Description(ProjectPathDescription)] string projectPath)
	{
		using var scope = BeginTool("roslyn_restore_packages");
		
		var rootPath = workspace.GetRootPath(projectPath);
		
		string? projectFile;
		
		try {
			projectFile = FindProjectFile(rootPath);
		}
		catch(Exception ex) when(ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException) {
			return scope.Failed("Failed to access project directory.", new RestoreResult(false, "Failed to access project directory.", ex.Message));
		}
		
		if(projectFile is null)
			return scope.Failed("No .csproj file found in target directory.", new RestoreResult(false, "No .csproj file found in target directory.", null));
		
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
			return scope.Failed("Failed to start dotnet process. Is dotnet installed and in PATH?", new RestoreResult(false, "Failed to start dotnet process. Is dotnet installed and in PATH?", ex.Message));
		}
		catch(InvalidOperationException ex) {
			return scope.Failed("Failed to start dotnet restore process.", new RestoreResult(false, "Failed to start dotnet restore process.", ex.Message));
		}
		
		string output, error;
		
		try {
			output = await process.StandardOutput.ReadToEndAsync();
			error  = await process.StandardError.ReadToEndAsync();
			await process.WaitForExitAsync();
		}
		catch(IOException ex) {
			return scope.Failed("Failed to read process output.", new RestoreResult(false, "Failed to read process output.", ex.Message));
		}
		
		var success = process.ExitCode == 0;
		var message = success
			? "Packages restored successfully."
			: $"Restore failed with exit code {process.ExitCode}."
		;
		
		var details = string.IsNullOrWhiteSpace(error) ? output : error;
		
		return scope.Outcome(message, new RestoreResult(success, message, details));
	}
	
	private static string? FindProjectFile(string directory)
	{
		try {
			var csprojFiles = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);
			
			return csprojFiles.Length > 0 ? csprojFiles[0] : null;
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) {
			return null;
		}
	}
}

internal sealed record RestoreResult(
	bool Success,
	string Message,
	string? Details
);
