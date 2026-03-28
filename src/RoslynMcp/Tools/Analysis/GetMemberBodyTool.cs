using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class GetMemberBodyTool : RoslynMcpTool
{
	public GetMemberBodyTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }

	[McpServerTool(Name = "roslyn_get_member_body", ReadOnly = true)]
	[Description(
		"Returns the full source of a single method, property, field, or type by name. " +
		"Much more token-efficient than reading an entire file — returns only the declaration you need. " +
		"For partial types/methods with multiple declarations, returns all parts.")]
	public async Task<object> GetMemberBody(
		[Description("The symbol name, e.g. 'GetCompilation', 'RootPath', 'WorkspaceManager'.")] string symbolName,
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Optional containing type to disambiguate, e.g. 'WorkspaceManager'.")] string? containingType = null)
	{
		using var scope = BeginTool("roslyn_get_member_body", symbolName);
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return error;

		var rootPath = workspace.GetRootPath(projectPath);
		var symbol   = FindSymbol(compilation, symbolName, containingType);

		if(symbol is null)
			return scope.Failed("symbol not found", new {
				error = $"Symbol '{symbolName}' not found. Use get_type_members or find_references to verify the name."
			});

		var syntaxRefs = symbol.DeclaringSyntaxReferences;

		if(syntaxRefs.Length == 0)
			return new {
				symbol_name = FormatSymbolName(symbol),
				symbol_kind = symbol.Kind.ToString().ToLowerInvariant(),
				location    = "metadata",
				message     = "This symbol is defined in metadata (compiled assembly), not source code."
			};

		var parts = new List<object>();

		for(var i = 0; i < syntaxRefs.Length; i++) {

			var syntaxRef = syntaxRefs[i];
			var node      = await syntaxRef.GetSyntaxAsync();
			var tree      = node.SyntaxTree;
			var text      = await tree.GetTextAsync();
			var span      = tree.GetLineSpan(node.Span);
			var startLine = span.StartLinePosition.Line;
			var endLine   = span.EndLinePosition.Line;

			// Extract source lines with 1-based line numbers.
			var lines = new string[endLine - startLine + 1];

			for(var ln = startLine; ln <= endLine; ln++)
				lines[ln - startLine] = text.Lines[ln].ToString();

			var filePath = string.IsNullOrEmpty(tree.FilePath)
				? "?"
				: Path.GetRelativePath(rootPath, tree.FilePath)
			;

			parts.Add(new {
				file       = filePath,
				start_line = startLine + 1,
				end_line   = endLine + 1,
				body       = string.Join("\n", lines),
				part_index = syntaxRefs.Length > 1 ? i + 1 : (int?) null
			});
		}

		var totalLines = parts.Cast<dynamic>().Sum(p => (int) p.end_line - (int) p.start_line + 1);

		return scope.Outcome($"{totalLines} line(s)", syntaxRefs.Length == 1
			? new {
				symbol_name = FormatSymbolName(symbol),
				symbol_kind = symbol.Kind.ToString().ToLowerInvariant(),
				file        = ((dynamic) parts[0]).file,
				start_line  = ((dynamic) parts[0]).start_line,
				end_line    = ((dynamic) parts[0]).end_line,
				body        = ((dynamic) parts[0]).body,
				_caution    = AdhocCaution(projectPath)
			}
			: (object) new {
				symbol_name = FormatSymbolName(symbol),
				symbol_kind = symbol.Kind.ToString().ToLowerInvariant(),
				parts,
				note        = $"Partial declaration — {syntaxRefs.Length} parts across {parts.Select(p => ((dynamic) p).file).Distinct().Count()} file(s).",
				_caution    = AdhocCaution(projectPath)
			}
		);
	}


}
