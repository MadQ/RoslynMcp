using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class CheckSyntaxTool : RoslynMcpTool
{
	public CheckSyntaxTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_check_syntax", ReadOnly = true, Title = "Check Syntax", OpenWorld = false, Idempotent = true)]
	[Description(
		"Validates a C# code snippet for syntax and optionally semantic errors without writing it to disk. " +
		"Use this as a pre-flight check before calling roslyn_replace_in_code or roslyn_write_file to catch " +
		"mistakes early without wasting a write round-trip. " +
		"Returns valid (bool), error_count, warning_count, and items with line, column, code, and message. " +
		"Line numbers correspond to positions in the original snippet (not the wrapped source). " +
		"By default (wrapInClass: true), the snippet is wrapped in a dummy class so member declarations " +
		"and method bodies are valid inputs without a surrounding class. " +
		"Pass wrapInClass: false when the snippet is a complete class, namespace, or compilation unit, " +
		"or when it contains using directives or namespace declarations (which are invalid inside a class body). " +
		"By default (includeSemantics: false), only syntax is checked — fast, no workspace loaded. " +
		"Set includeSemantics: true to use the full project compilation so the snippet is checked against " +
		"project-defined types, global usings, and all referenced assemblies.")]
	public object CheckSyntax(
		[Description("The C# code snippet to validate.")] string code,
		[Description(ProjectPathDescription)] string projectPath,
		[Description("When true (default), wraps the snippet in a dummy class so member declarations and method bodies are valid inputs. Set to false when the snippet is a complete class, namespace, or compilation unit, or when it contains using directives or namespace declarations (which are invalid inside a class body).")] bool wrapInClass = true,
		[Description("When false (default), only syntax is checked — fast, no workspace loaded. When true, the full project compilation is used so the snippet is validated against project-defined types, global usings, and referenced assemblies.")] bool includeSemantics = false)
	{
		using var scope = BeginTool("roslyn_check_syntax", null, new { wrapInClass, includeSemantics });
		
		// Wrap in a dummy class so the parser accepts method bodies and member declarations without
		// requiring the caller to supply boilerplate. The offset is exactly one header line.
		var source = wrapInClass
			? $"class __CheckSyntax__ {{\n{code}\n}}"
			: code
		;
		
		Diagnostic[] diagnostics;
		
		if(includeSemantics) {
			
			if(!TryGetCompilation(projectPath, out var compilation, out var error))
				
				return scope.Error(error!);
			
			// Mirror the project's parse options so the snippet is checked with the same language
			// version, preprocessor symbols, and nullable context as the rest of the project.
			var parseOptions = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions
				?? new CSharpParseOptions(LanguageVersion.Preview)
			;
			
			var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
			
			// Add the snippet tree to the existing project compilation so project-defined types and
			// global usings are visible — results in far fewer false CS0246/CS0103 positives vs a
			// fresh compilation built from references alone.
			// Filter to the added tree to avoid surfacing pre-existing project errors.
			var check = compilation.AddSyntaxTrees(tree)
			;
			
			diagnostics = [..check.GetDiagnostics()
				.Where(d =>
					d.Location.IsInSource &&
					d.Location.SourceTree == tree &&
					d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
			];
		}
		else {
			
			var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
			
			diagnostics = [..tree.GetDiagnostics()
				.Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
			]
			;
		}
		
		var items      = Array.ConvertAll(diagnostics, d => ToItem(d, wrapInClass));
		var errorCount = Array.FindAll(items, i => i.Severity == "error").Length;
		var warnCount  = Array.FindAll(items, i => i.Severity == "warning").Length;
		var valid      = errorCount == 0;
		var summary    = valid
			? "valid"
			: $"{errorCount} error(s), {warnCount} warning(s)"
		;
		
		return scope.Outcome(summary, new CheckSyntaxResult(valid, errorCount, warnCount, items));
	}
	
	private static DiagnosticItem ToItem(Diagnostic d, bool wrapInClass)
	{
		var span = d.Location.GetLineSpan();
		var line = span.StartLinePosition.Line + 1; // 1-indexed
		
		// Subtract the single wrapper-class header line so positions map back to the original snippet.
		if(wrapInClass && line > 1)
			line--;
		
		return new DiagnosticItem(
			Code:     d.Id,
			Severity: d.Severity.ToString().ToLowerInvariant(),
			File:     null,
			Line:     line,
			Column:   span.StartLinePosition.Character + 1,
			Message:  d.GetMessage()
		);
	}
}
