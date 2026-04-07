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
"Requires a .csproj to be present.")]
public async Task<object> RestorePackages(
[Description(ProjectPathDescription)] string projectPath)
{
using var scope = BeginTool("roslyn_restore_packages");

var (rootPath, _, csprojPath) = workspace.GetWorkspaceInfo(projectPath);

if(csprojPath is null)
return scope.Error(new ErrorResult("No .csproj found - restore requires a project file."));

string output;
int exitCode;

try {
(output, _, exitCode) = await DotnetRunner.RunAsync(["restore", csprojPath, "/nologo"], rootPath, scope.Record);
}
catch(InvalidOperationException ex) {
return scope.Failed(ex.Message, new RestoreResult(false, ex.Message, ex.InnerException?.Message));
}

var success = exitCode == 0;
var message = success
? "Packages restored successfully."
: $"Restore failed with exit code {exitCode}."
;

return scope.Outcome(message, new RestoreResult(success, message, output));
}
}

internal sealed record RestoreResult(
bool Success,
string Message,
string? Details
);