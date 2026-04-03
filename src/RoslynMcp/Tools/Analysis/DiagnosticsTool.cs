using System.ComponentModel;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class DiagnosticsTool : RoslynMcpTool
{
	public DiagnosticsTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, logger, paginationCache) { }

	[McpServerTool(Name = "roslyn_get_diagnostics", ReadOnly = true)]
	[Description(
		"Check your code for compiler errors and warnings — fast, in-process, no build needed. " +
		"Use this after editing code to verify correctness before committing or continuing work. " +
		"Returns a structured summary (error count, warning count) plus paginated individual items " +
		"with code, file, line, and message. Pass take: 0 for a lightweight error-count-only check " +
		"with no items returned. " +
		"Covers C# type/symbol errors only. For NuGet restore failures, MSBuild target errors, or " +
		"source generator issues, use roslyn_build_project instead.")]
	public object GetDiagnostics(
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Optional relative file path to scope results, e.g. 'Core/Foo.cs'. Omit for all files.")] string? filePath = null,
		[Description("Filter by severity: 'errors', 'warnings', or 'all' (default — errors and warnings).")] string? severity = null,
		[Description("Number of items to skip. Default: 0.")] int skip = 0,
		[Description("Maximum items to return (default 50, max 200). Pass 0 to return only the summary counts — a fast way to check if there are any errors without retrieving individual items.")] int take = 50,
		[Description("Token from a previous response to get the next page without re-running the compilation.")] string? page_token = null)
	{
		using var scope = BeginTool("roslyn_get_diagnostics", filePath);

		// Stateless page token overrides skip/severity — agents don't need to track offsets manually.
		if(page_token is not null) {

			try {
				var decoded = JsonSerializer.Deserialize<PageTokenData>(Convert.FromBase64String(page_token));

				if(decoded is not null) {
					skip     = decoded.Skip;
					severity = decoded.Severity;
				}
			}
			catch { /* malformed token — fall through to explicit params */ }
		}

		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return error;

		var rootPath = workspace.GetRootPath(projectPath);
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

		var filtered = diagnostics
			.Where(GetSeverityFilter(severity))
			.OrderByDescending(d => d.Severity)
			.ThenBy(d => d.Location.SourceTree?.FilePath)
			.ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
			.ToArray()
		;

		var errorCount   = Array.FindAll(filtered, d => d.Severity == DiagnosticSeverity.Error).Length;
		var warningCount = Array.FindAll(filtered, d => d.Severity == DiagnosticSeverity.Warning).Length;
		var total        = filtered.Length;

		var summary = errorCount == 0 && warningCount == 0
			? "0 errors, 0 warnings"
			: $"{errorCount} error(s), {warningCount} warning(s)"
		;

		take = Math.Clamp(take, 0, 200);

		// take: 0 fast path — return counts only, no items, no page token needed.
		if(take == 0) {

			return scope.Outcome(summary, new {
				summary,
				errors   = errorCount,
				warnings = warningCount,
				total,
				returned  = 0,
				has_more  = false,
				page_token = (string?) null,
				items     = Array.Empty<object>()
			});
		}

		var effectiveSkip  = Math.Clamp(skip, 0, total);
		var effectiveCount = Math.Clamp(take, 0, total - effectiveSkip);
		var hasMore        = effectiveSkip + effectiveCount < total;

		string? nextToken = hasMore
			? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
				new PageTokenData(effectiveSkip + effectiveCount, severity)))
			: null
		;

		var items = filtered[effectiveSkip..(effectiveSkip + effectiveCount)]
			.Select(d => ToItem(d, rootPath))
			.ToArray()
		;

		return scope.Outcome(summary, new {
			summary,
			errors   = errorCount,
			warnings = warningCount,
			total,
			returned  = items.Length,
			has_more  = hasMore,
			page_token = nextToken,
			items
		});
	}

	// Both tools now return project-relative paths via TryMakeRelative on the base class.
	private static DiagnosticItem ToItem(Diagnostic d, string rootPath)
	{
		var span = d.Location.GetLineSpan();

		return new DiagnosticItem(
			Code:     d.Id,
			Severity: d.Severity.ToString().ToLowerInvariant(),
			File:     TryMakeRelative(span.Path, rootPath),
			Line:     span.StartLinePosition.Line + 1,
			Column:   span.StartLinePosition.Character + 1,
			Message:  d.GetMessage()
		);
	}

	private sealed record PageTokenData(int Skip, string? Severity);
}
