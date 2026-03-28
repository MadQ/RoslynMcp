using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class SymbolInfoTool : RoslynMcpTool
{
	public SymbolInfoTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_get_symbol_info", ReadOnly = true)]
	[Description(
		"Returns resolved symbol information at a specific file location — type, kind, containing type, return type. " +
		"Use to verify what a name resolves to without reading the full file.")]
	public async Task<string> GetSymbolInfo(
		[Description("Relative file path, e.g. 'Core/WindowTracker.cs'.")] string filePath,
		[Description("1-based line number.")] int line,
		[Description("1-based column number.")] int column,
		[Description(ProjectPathDescription)] string projectPath)
	{
		using var scope = BeginTool("roslyn_get_symbol_info", $"{filePath}:{line}");
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return error.ToString()!;
		
		
		var normalized  = filePath.Replace('/', Path.DirectorySeparatorChar);
		
		var tree = compilation.SyntaxTrees
			.FirstOrDefault(t => t.FilePath.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
		;
		
		if(tree is null)
			return scope.Failed("file not found", $"File '{filePath}' not found in the compilation.");
		
		var text     = await tree.GetTextAsync();
		var position = GetPosition(text, line, column);
		
		if(position < 0)
			return $"Line {line}, column {column} is out of range.";
		
		var model = compilation.GetSemanticModel(tree);
		var node  = (await tree.GetRootAsync()).FindToken(position).Parent;
		
		if(node is null)
			return "No node at that position.";
		
		var info   = model.GetSymbolInfo(node);
		var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
		
		if(symbol is null) {
			// Try type info — might be a type expression rather than a symbol reference.
			var typeInfo = model.GetTypeInfo(node);
			
			if(typeInfo.Type is not null)
				return $"Type: {typeInfo.Type.ToDisplayString()}";
			
			return "No symbol resolved at that position.";
		}
		
		return FormatSymbol(symbol);
	}
	
	private static int GetPosition(SourceText text, int line, int column)
	{
		if(line < 1 || line > text.Lines.Count)
			return -1;
		
		var lineSpan = text.Lines[line - 1];
		
		return lineSpan.Start + Math.Min(column - 1, lineSpan.Span.Length);
	}
	
	private static string FormatSymbol(ISymbol symbol)
	{
		var kind        = symbol.Kind.ToString();
		var name        = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
		var containing  = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
		var returnType  = symbol switch {
			IMethodSymbol   m => m.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
			IPropertySymbol p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
			IFieldSymbol    f => f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
			ILocalSymbol    l => l.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
			_                 => null
		};
		
		var parts = new List<string> { $"Kind: {kind}", $"Name: {name}" };
		
		if(containing is not null)
			parts.Add($"ContainingType: {containing}");
		if(returnType  is not null)
			parts.Add($"Type/ReturnType: {returnType}");
		
		return string.Join("  |  ", parts);
	}
}
