using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class GetMemberBodyTool : RoslynMcpTool
{
	public GetMemberBodyTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_get_member_body", ReadOnly = true, Title = "Get Member Body", OpenWorld = false, Idempotent = true)]
	[Description(
		"Returns the full source code of a single method, property, field, or type by name — including " +
		"file path and start/end line numbers. " +
		"Use this instead of roslyn_read_file when you need only one specific declaration rather than the " +
		"whole file; use roslyn_get_file_outline when you only need signatures without body content. " +
		"The response is a one-line JSON header (symbol_name, symbol_kind, file, start_line, end_line) followed by " +
		"the declaration as raw text: unescaped and unnumbered, so it can be copied verbatim into an edit. " +
		"For partial types or partial methods split across multiple files, the header carries the part count " +
		"(parts) instead of a location, and each part follows as its own one-line JSON header " +
		"(part, file, start_line, end_line) and then that part's raw text. " +
		"Lines are joined with '\\n' and one newline is added after the last line of each part. " +
		"Returns a structured metadata error (not source) if the symbol is defined in a compiled assembly " +
		"rather than project source code.")]
	public async Task<object> GetMemberBody(
		[Description("The declared symbol name, e.g. 'GetCompilation', 'RootPath', 'WorkspaceManager'. Use the simple name, not a qualified path.")] string symbolName,
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Optional containing type to disambiguate when multiple types have a member with the same name, e.g. 'WorkspaceManager'.")] string? containingType = null,
		CancellationToken ct = default)
	{
		using var scope = BeginTool("roslyn_get_member_body", symbolName, new { containingType });
		
		if(!TryStripChatSymbolRef(ref symbolName, out var refError) || !TryStripChatSymbolRefOptional(ref containingType, out refError))
			
			return scope.Error(refError!);
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return scope.Error(error!);
		
		if(!TryResolveRoot(projectPath, out var rootPath, out var rootError))
			
			return scope.Error(rootError);
		
		var symbol = FindSymbol(compilation, symbolName, containingType);
		
		if(symbol is null)
			
			return scope.Failed("symbol not found", new ErrorResult($"Symbol '{symbolName}' not found.", "Use get_type_members or find_references to verify the name."));
		
		var syntaxRefs = symbol.DeclaringSyntaxReferences;
		
		if(syntaxRefs.Length == 0)
			
			return scope.Error(new MetadataSymbolResult(
				FormatSymbolName(symbol),
				symbol.Kind.ToString().ToLowerInvariant(),
				"metadata")
			{
				Error = "This symbol is defined in metadata (compiled assembly), not source code."
			});
		
		var single     = syntaxRefs.Length == 1;
		var parts      = new List<(object? Header, string Source)>(syntaxRefs.Length);
		var files      = new HashSet<string>();
		var totalLines = 0;
		
		MemberBodyPart? first = null;
		
		for(var i = 0; i < syntaxRefs.Length; i++) {
			
			var node      = await syntaxRefs[i].GetSyntaxAsync(ct);
			var tree      = node.SyntaxTree;
			var text      = await tree.GetTextAsync(ct);
			var span      = tree.GetLineSpan(node.Span);
			var startLine = span.StartLinePosition.Line;
			var endLine   = span.EndLinePosition.Line;
			
			// Whole lines, so the declaration keeps its indentation.
			var lines = new string[endLine - startLine + 1];
			
			for(var ln = startLine; ln <= endLine; ln++)
				lines[ln - startLine] = text.Lines[ln].ToString();
			
			var filePath = string.IsNullOrEmpty(tree.FilePath)
				? "?"
				: Path.GetRelativePath(rootPath, tree.FilePath)
			;
			
			var part = new MemberBodyPart(i + 1, filePath, startLine + 1, endLine + 1);
			
			// A single declaration is located by the response header, so its source needs no header
			// of its own.
			parts.Add((single ? null : part, string.Join("\n", lines)));
			files.Add(filePath);
			
			totalLines += lines.Length;
			first     ??= part;
		}
		
		var displayName = FormatSymbolName(symbol);
		var kind        = symbol.Kind.ToString().ToLowerInvariant();
		
		ToolResult header = single
			? new MemberBodySingleResult(displayName, kind, first!.File, first.StartLine, first.EndLine)
			{
				Caution = AdhocCaution(projectPath)
			}
			: new MemberBodyPartialResult(
				displayName,
				kind,
				parts.Count,
				$"Partial declaration — {parts.Count} parts across {files.Count} file(s).")
			{
				Caution = AdhocCaution(projectPath)
			}
		;
		
		return scope.Outcome($"{totalLines} line(s)", header, parts);
	}


}
