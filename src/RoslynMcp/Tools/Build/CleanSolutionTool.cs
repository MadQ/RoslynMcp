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
"For package restore only, use roslyn_restore_packages.")]
public async Task<object> CleanSolution(
[Description(ProjectPathDescription)] string projectPath)
{
using var scope = BeginTool("roslyn_clean_solution");

var (rootPath, _, csprojPath) = workspace.GetWorkspaceInfo(projectPath);

if(csprojPath is null)
return scope.Error(new ErrorResult("No .csproj found - clean requires a project file."));

string output;
int exitCode;

try {
// -tl:off: disable terminal logger for predictable plain-text output.
		(output, _, exitCode) = await DotnetRunner.RunAsync(["clean", csprojPath, "/nologo", "/v:quiet", "-tl:off"], rootPath, scope.Record)
		;
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