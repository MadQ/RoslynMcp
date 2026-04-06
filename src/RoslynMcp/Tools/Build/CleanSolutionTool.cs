using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class CleanSolutionTool : RoslynMcpTool
{
	public CleanSolutionTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_clean_solution", Title = "Clean Solution", OpenWorld = false, Destructive = true)]
	[Description(
		"Removes all build artifacts (bin/ and obj/ directories) — source files are never touched. " +
		"Use when the build is in a bad state, producing stale artifacts, or before a full rebuild from scratch. " +
		"Safe to run at any time; only compiled output is deleted. " +
		"To rebuild after cleaning, use roslyn_build_project. " +
		"For package restore only, use roslyn_restore_packages.")]
	public async Task<CleanResult> CleanSolution(
		[Description(ProjectPathDescription)] string projectPath)
	{
		using var scope = BeginTool("roslyn_clean_solution");
		
		var rootPath = workspace.GetRootPath(projectPath);
		
		string? projectFile;
		
		try {
			projectFile = FindProjectFile(rootPath);
		}
		catch(Exception ex) when(ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException) {
			return scope.Failed("Failed to access project directory.", new CleanResult(false, "Failed to access project directory.", ex.Message));
		}
		
		if(projectFile is null)
			return scope.Failed("No .csproj file found in target directory.", new CleanResult(false, "No .csproj file found in target directory.", null));
		
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
			return scope.Failed("Failed to start dotnet process. Is dotnet installed and in PATH?", new CleanResult(false, "Failed to start dotnet process. Is dotnet installed and in PATH?", ex.Message));
		}
		catch(InvalidOperationException ex) {
			return scope.Failed("Failed to start dotnet clean process.", new CleanResult(false, "Failed to start dotnet clean process.", ex.Message));
		}
		
		string output, error;
		
		try {
			output = await process.StandardOutput.ReadToEndAsync();
			error  = await process.StandardError.ReadToEndAsync();
			await process.WaitForExitAsync();
		}
		catch(IOException ex) {
			return scope.Failed("Failed to read process output.", new CleanResult(false, "Failed to read process output.", ex.Message));
		}
		
		var success = process.ExitCode == 0;
		var message = success
			? "Solution cleaned successfully. All build artifacts removed."
			: $"Clean failed with exit code {process.ExitCode}.";
		
		var details = string.IsNullOrWhiteSpace(error) ? output : error;
		
		return scope.Outcome(message, new CleanResult(success, message, details));
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

internal sealed record CleanResult(
	bool Success,
	string Message,
	string? Details
);
