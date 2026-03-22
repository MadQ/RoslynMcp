#if FALSE
using System.ComponentModel;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class DiagnosticsTool(WorkspaceManager workspace)
{
    [McpServerTool, Description(
        "Returns compiler diagnostics (errors and warnings) for the project or a single file. " +
        "Faster than running dotnet build — uses the in-process Roslyn compilation.")]
    public string[] GetDiagnostics(
        [Description("Optional relative file path to scope diagnostics, e.g. 'Core/WindowTracker.cs'. Omit for all files.")] string? filePath = null)
    {
        var compilation = workspace.GetCompilation();
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

        return results.Length > 0 ? results : ["No diagnostics."];
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
#endif
