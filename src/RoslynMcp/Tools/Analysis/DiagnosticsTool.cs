using System.ComponentModel;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class DiagnosticsTool : RoslynMcpTool
{
	public DiagnosticsTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_get_diagnostics", ReadOnly = true)]
	[Description(
		"Returns compiler diagnostics (errors and warnings) for the project or a single file. " +
		"Uses the in-process Roslyn compilation — instant, no process spawn. " +
		"Covers C# type/symbol errors only. Does NOT validate NuGet restore, MSBuild targets, " +
		"SDK props, or source generators — use roslyn_build_project for those.")]
	public object GetDiagnostics(
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Optional relative file path to scope diagnostics, e.g. 'Core/WindowTracker.cs'. Omit for all files.")] string? filePath = null)
	{
		using var scope = BeginTool("roslyn_get_diagnostics", filePath);
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return error;

		IEnumerable<Diagnostic> diagnostics;

		if(filePath is not null) {

			// Single-file: use SemanticModel for that tree only — avoids compiling the entire project.
			var normalized = NormalizePath(filePath);
			var tree = compilation.SyntaxTrees
				.FirstOrDefault(t => t.FilePath.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
			;

			diagnostics = tree is not null
				? compilation.GetSemanticModel(tree).GetDiagnostics()
				: [];
		}
		else {

			diagnostics = compilation.GetDiagnostics();
		}
		
		string[] results = [..
			diagnostics
				.Where(d => d.Severity >= DiagnosticSeverity.Warning)
				.OrderByDescending(d => d.Severity)
				.ThenBy(d => d.Location.SourceTree?.FilePath)
				.ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
				.Select(Format)
		];
		
		return results.Length > 0
			? scope.Outcome($"{results.Length} diagnostic(s)", results)
			: (object) new[] { "No diagnostics." };
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
