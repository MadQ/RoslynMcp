using System.ComponentModel;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class DiagnosticsTool : RoslynMcpTool
{
    public DiagnosticsTool(WorkspaceResolver workspace, FileLogger logger) : base(workspace, logger) { }

    [McpServerTool(Name = "roslyn_get_diagnostics", ReadOnly = true)]
    [Description(
        "Returns compiler diagnostics (errors and warnings) for the project or a single file. " +
        "Faster than running dotnet build — uses the in-process Roslyn compilation.")]
    public string[] GetDiagnostics(
        [Description("Optional relative file path to scope diagnostics, e.g. 'Core/WindowTracker.cs'. Omit for all files.")] string? filePath = null,
        [Description(ProjectPathDescription)] string? projectPath = null)
    {
        using var scope = BeginTool("roslyn_get_diagnostics", filePath);
        if(!TryGetCompilation(projectPath, out var compilation, out var error))
            return [error.ToString()!];


        IEnumerable<Diagnostic> diagnostics = compilation.GetDiagnostics();

        if(filePath is not null) {
            var normalized = filePath.Replace('/', Path.DirectorySeparatorChar);
            diagnostics = diagnostics
                .Where(d => d.Location.SourceTree?.FilePath
                    .EndsWith(normalized, StringComparison.OrdinalIgnoreCase) == true)
            ;
		}

		var results = diagnostics
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .OrderBy(d => d.Severity)
            .ThenBy(d => d.Location.SourceTree?.FilePath)
            .ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
            .Select(Format)
            .ToArray()
        ;

        return results.Length > 0
            ? scope.Outcome($"{results.Length} diagnostic(s)", results)
            : ["No diagnostics."];
	}

	private static string Format(Diagnostic d)
    {
        var span = d.Location.GetLineSpan();
        var file = span.Path is { Length: > 0 } p ? Path.GetFileName(p) : "?";
        var line = span.StartLinePosition.Line + 1;
        var col  = span.StartLinePosition.Character + 1;

        return $"{d.Severity.ToString()[0]}  {file}:{line}:{col}  {d.Id}  {d.GetMessage()}";
    }
}
