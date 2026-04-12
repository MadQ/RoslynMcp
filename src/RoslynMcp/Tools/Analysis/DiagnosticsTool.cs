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
	
	[McpServerTool(Name = "roslyn_get_diagnostics", ReadOnly = true, Title = "Get Diagnostics", OpenWorld = false, Idempotent = true)]
	[Description(
		"Check your code for compiler errors and warnings — fast, in-process, no build needed. " +
		"Use this after editing code to verify correctness before committing or continuing work. " +
		"Returns a structured summary (error count, warning count) plus paginated individual items " +
		"with code, file, line, and message. Pass take: 0 for a lightweight error-count-only check " +
		"with no items returned. When take: 0, the response includes full counts but items is null — " +
		"not an empty array. An empty array means items were requested but none matched; null means items were not requested. " +
		"Covers C# type/symbol errors only. For NuGet restore failures, MSBuild target errors, or " +
		"source generator issues, use roslyn_build_project instead.")]
	public object GetDiagnostics(
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Optional relative file path to scope results, e.g. 'Core/Foo.cs'. Omit to check all files in the project.")] string? filePath = null,
		[Description("Filter by severity: 'errors', 'warnings', or 'all'. Omit to return all (errors and warnings).")] string? severity = null,
		[Description("Number of items to skip. Default: 0.")] int skip = 0,
		[Description("Maximum items to return (default 50, max 200). Pass 0 to return only the summary counts — a fast way to check if there are any errors without retrieving individual items. When take: 0, items in the response is null (not an empty array).")] int take = 50,
		[Description("Token from a previous response to get the next page without re-running the compilation.")] string? page_token = null)
	{
		using var scope = BeginTool("roslyn_get_diagnostics", filePath, new { severity, skip, take });
		
		// Stateless page token overrides skip/severity — agents don't need to track offsets manually.
		if(page_token is not null)
			try {
				var decoded = JsonSerializer.Deserialize<PageTokenData>(Convert.FromBase64String(page_token));
				
				if(decoded is not null) {
					skip     = decoded.Skip;
					severity = decoded.Severity;
				}
			}
			catch { /* malformed token — fall through to explicit params */ }
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return scope.Error(error!);
		
		var rootPath = workspace.GetRootPath(projectPath);
		IEnumerable<Diagnostic> diagnostics;
		
		if(filePath is not null) {
			
			// Single-file: use SemanticModel for that tree only — avoids compiling the entire project.
			var tree = FindSyntaxTree(compilation, filePath);
			
			diagnostics = tree is not null
				? compilation.GetSemanticModel(tree).GetDiagnostics()
				: [];
		}
		else
			diagnostics = compilation.GetDiagnostics()
				.Where(d => IsUnderRoot(d, rootPath))
			;
		
		// Deduplicate by identity tuple — multi-TFM workspaces can surface the same
		// diagnostic from duplicate document entries across target frameworks.
		var filtered = diagnostics
			.Where(GetSeverityFilter(severity))
			.DistinctBy(d => (d.Severity, d.Id, d.Location.SourceTree?.FilePath, d.Location.GetLineSpan().StartLinePosition.Line, d.Location.GetLineSpan().StartLinePosition.Character, d.GetMessage()))
			.OrderByDescending(d => d.Severity)
			.ThenBy(d => d.Location.SourceTree?.FilePath)
			.ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
			.ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Character)
			.ThenBy(d => d.Id)
			.ThenBy(d => d.GetMessage())
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
		
		// take: 0 fast path — return counts only. items is null (not []) to distinguish
		// "not requested" from "requested but empty".
		if(take == 0) {
			return scope.Outcome(summary, new {
				summary,
				source     = "roslyn",
				errors     = errorCount,
				warnings   = warningCount,
				total,
				returned   = 0,
				has_more   = total > 0,
				page_token = (string?) null,
				items      = (object[]?) null
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
			source    = "roslyn",
			errors    = errorCount,
			warnings  = warningCount,
			total,
			returned   = items.Length,
			has_more   = hasMore,
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
