using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class CleanSolutionTool : RoslynMcpTool
{
public CleanSolutionTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }

[McpServerTool(Name = "roslyn_clean_solution", Title = "Clean Solution", OpenWorld = false, Destructive = true)]
[Description(
"Removes all build artifacts (bin/ and obj/ directories) - source files are never touched. " +
"Use when the build is in a bad state, producing stale artifacts, or before a full rebuild from scratch. " +
"Safe to run at any time; only compiled output is deleted. " +
"To rebuild after cleaning, use roslyn_build_project. " +
"For package restore only, use roslyn_restore_packages. " +
"Omit projectPath to clean the whole default solution in one run rather than one call per project; " +
"the server runs one dotnet build/clean/restore at a time, so parallel calls only queue.")]
public async Task<object> CleanSolution(
CancellationToken cancellationToken,
[Description(OptionalProjectPathDescription)] string? projectPath = null)
{
using var scope = BeginTool("roslyn_clean_solution");

if(!TryResolveProjectArg(projectPath, out projectPath, out var projectError))

return scope.Error(projectError);

if(!TryResolveWorkspaceInfo(projectPath, out var rootPath, out _, out var csprojPath, out var wsError))

return scope.Error(wsError);

if(csprojPath is null)
return scope.Error(new ErrorResult("No .csproj found - clean requires a project file."));

// A solution target cleans every project in it, not just the first one the workspace lists.
var target = ResolvedSolutionPath(projectPath) ?? csprojPath;

string output;
int exitCode;

try {
// -tl:off: disable terminal logger for predictable plain-text output.
		(output, _, exitCode) = await DotnetRunner.RunAsync(["clean", target, "/nologo", "/v:quiet", "-tl:off"], rootPath, scope.Record, cancellationToken)
		;
}
catch(OperationCanceledException) {
// Logged as a cancellation rather than an unhandled exception; the SDK answers the client.
scope.Failed("cancelled");
throw;
}
catch(InvalidOperationException ex) {
return scope.Failed(ex.Message, new CleanResult(false, ex.Message, ex.InnerException?.Message));
}

var success = exitCode == 0;
var message = success
? "Solution cleaned successfully. All build artifacts removed."
: $"Clean failed with exit code {exitCode}."
;

return scope.Outcome(message, new CleanResult(success, message, output));
}
}

internal sealed record CleanResult(
bool Success,
string Message,
string? Details
);