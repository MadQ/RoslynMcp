using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class RestorePackagesTool : RoslynMcpTool
{
	public RestorePackagesTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_restore_packages", Title = "Restore Packages", Idempotent = true, Destructive = false)]
	[Description(
		"Downloads and restores NuGet packages for the project - makes network calls to NuGet feeds. " +
		"Use after adding or modifying package references in the .csproj, or when packages are missing. " +
		"Does not compile or validate C# source - for a full build after restore, use roslyn_build_project. " +
		"Requires a .csproj to be present. " +
		"Omit projectPath to restore the whole default solution in one run rather than one call per project; " +
		"the server runs one dotnet build/clean/restore at a time, so parallel calls only queue.")]
	public async Task<object> RestorePackages(
		CancellationToken cancellationToken,
		[Description(OptionalProjectPathDescription)] string? projectPath = null)
	{
		using var scope = BeginTool("roslyn_restore_packages");
		
		if(!TryResolveProjectArg(projectPath, out projectPath, out var projectError))
			
			return scope.Error(projectError);
		
		if(!TryResolveWorkspaceInfo(projectPath, out var rootPath, out var isMSBuild, out var csprojPath, out var wsError))
			
			return scope.Error(wsError);
		
		if(!isMSBuild)
			return scope.Error(new ErrorResult("No .csproj found - restore requires a project file."));
		
		// A solution target restores every project in it, not just the first one the workspace lists.
		var target = ResolvedTargetPath(projectPath, csprojPath);
		
		string output;
		int    exitCode;
		
		try {
			// -tl:off: disable terminal logger for predictable plain-text output.
			(output, _, exitCode) = await DotnetRunner.RunAsync(["restore", target, "/nologo", "-tl:off"], rootPath, scope.Record, cancellationToken)
			;
		}
		catch(OperationCanceledException) {
			// Logged as a cancellation rather than an unhandled exception; the SDK answers the client.
			scope.Failed("cancelled");
			throw;
		}
		catch(InvalidOperationException ex) {
			return scope.Failed(ex.Message, new RestoreResult(false, ex.Message, ex.InnerException?.Message));
		}
		
		var success = exitCode == 0;
		var message = success
			? "Packages restored successfully."
			: $"Restore failed with exit code {exitCode}."
		;
		
		// A project loaded before its restore has no references until it reloads, and nothing else would
		// reload it: the restore writes project.assets.json under obj\, which the watcher ignores (#399).
		// Also after a failed restore: one project of a solution can fail while the others were restored.
		try {
			workspace.RequestReload(projectPath, success ? "packages restored" : "restore ran (failed)");
			scope.Record("workspace reload flagged");
		}
		catch(Exception ex) when(ex is not OperationCanceledException) {
			scope.Record($"workspace reload not flagged: {ex.GetType().Name}");
		}
		
		// A restore that succeeded has nothing to say but its warnings (#490); one that failed
		// keeps the whole output, which is where the reason is.
		return scope.Outcome(message, new RestoreResult(success, message, success ? Warnings(output) : output));
	}
	
	// The warning lines of a dotnet output, each once: the same warning comes once per project,
	// with the project's path in brackets at its end. Null when there is none.
	static string? Warnings(string output)
	{
		string[] warnings = [..output.Split('\n')
			.Select(line => line.Trim())
			.Where(line => line.Contains("warning", StringComparison.OrdinalIgnoreCase))
			.Select(line => line.EndsWith(']') && line.LastIndexOf(" [", StringComparison.Ordinal) is > 0 and var bracket ? line[..bracket] : line)
			.Distinct(StringComparer.Ordinal)];
		
		return warnings.Length > 0 ? string.Join("\n", warnings) : null;
	}
}

internal sealed record RestoreResult(
	bool    Success,
	string  Message,
	string? Details
);
