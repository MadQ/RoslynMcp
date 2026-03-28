using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class SymbolInfoTool : RoslynMcpTool
{
	public SymbolInfoTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }

	[McpServerTool(Name = "roslyn_get_symbol_info", ReadOnly = true)]
	[Description(
		"Returns resolved symbol information at a specific file location — kind, name, containing type, return type. " +
		"Use to verify what a name resolves to without reading the full file.")]
	public async Task<object> GetSymbolInfo(
		[Description("Relative file path, e.g. 'Core/WindowTracker.cs'.")] string filePath,
		[Description("1-based line number.")] int line,
		[Description("1-based column number.")] int column,
		[Description(ProjectPathDescription)] string projectPath)
	{
		using var scope = BeginTool("roslyn_get_symbol_info", $"{filePath}:{line}");
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return error;

		var tree = FindSyntaxTree(compilation, filePath);

		if(tree is null)
			return scope.Failed("file not found", new ErrorResult($"File '{filePath}' not found in the compilation."));

		var text     = await tree.GetTextAsync();
		var position = GetPosition(text, line, column);

		if(position < 0)
			return new ErrorResult($"Line {line}, column {column} is out of range.");

		var model = compilation.GetSemanticModel(tree);
		var node  = (await tree.GetRootAsync()).FindToken(position).Parent;

		if(node is null)
			return new ErrorResult("No node at that position.");

		var info   = model.GetSymbolInfo(node);
		var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();

		if(symbol is null) {

			var typeInfo = model.GetTypeInfo(node);

			if(typeInfo.Type is not null)
				return new {
					kind = "type",
					name = typeInfo.Type.ToDisplayString(),
					containing_type = (string?) null,
					type_or_return  = (string?) null
				};

			return new ErrorResult("No symbol resolved at that position.");
		}

		return new {
			kind            = symbol.Kind.ToString().ToLowerInvariant(),
			name            = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
			containing_type = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
			type_or_return  = symbol switch {
				IMethodSymbol   m => m.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
				IPropertySymbol p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
				IFieldSymbol    f => f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
				ILocalSymbol    l => l.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
				_                 => (string?) null
			},
			_caution = AdhocCaution(projectPath)
		};
	}

	private static int GetPosition(SourceText text, int line, int column)
	{
		if(line < 1 || line > text.Lines.Count)
			return -1;

		var lineSpan = text.Lines[line - 1];

		return lineSpan.Start + Math.Min(column - 1, lineSpan.Span.Length);
	}
}
