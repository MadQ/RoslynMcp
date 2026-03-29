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
			return scope.Failed("symbol not found", new ErrorResult($"Symbol '{symbolName}' not found.", Hint: "Use get_type_members or find_references to verify the name."));

		var syntaxRefs = symbol.DeclaringSyntaxReferences;

		if(syntaxRefs.Length == 0)
			return new MetadataSymbolResult(
				FormatSymbolName(symbol),
				symbol.Kind.ToString().ToLowerInvariant(),
				"metadata",
				"This symbol is defined in metadata (compiled assembly), not source code."
			);

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

			parts.Add(new MemberBodyPart(
				filePath,
				startLine + 1,
				endLine + 1,
				string.Join("\n", lines),
				syntaxRefs.Length > 1 ? i + 1 : null
			));
		}

		var totalLines = parts.Cast<MemberBodyPart>().Sum(p => p.End_line - p.Start_line + 1);

		return scope.Outcome($"{totalLines} line(s)", syntaxRefs.Length == 1
			? new MemberBodySingleResult(
				FormatSymbolName(symbol),
				symbol.Kind.ToString().ToLowerInvariant(),
				((MemberBodyPart) parts[0]).File,
				((MemberBodyPart) parts[0]).Start_line,
				((MemberBodyPart) parts[0]).End_line,
				((MemberBodyPart) parts[0]).Body,
				AdhocCaution(projectPath)
			)
			: (object) new MemberBodyPartialResult(
				FormatSymbolName(symbol),
				symbol.Kind.ToString().ToLowerInvariant(),
				parts,
				$"Partial declaration — {syntaxRefs.Length} parts across {parts.Cast<MemberBodyPart>().Select(p => p.File).Distinct().Count()} file(s).",
				AdhocCaution(projectPath)
			)
		);
	}


}
