using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class SymbolInfoTool : RoslynMcpTool
{
	public SymbolInfoTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_get_symbol_info", ReadOnly = true, Title = "Get Symbol Info", OpenWorld = false, Idempotent = true)]
	[Description(
		"Returns the resolved symbol at a specific location in a file — kind, name, containing type, " +
		"and return type (for methods, properties, fields, and locals). " +
		"Use this when you have a line and column from a reference or search result and need to identify " +
		"exactly what symbol is at that position — useful for disambiguating overloaded names or verifying " +
		"what an identifier resolves to in context. " +
		"For looking up a symbol by name without a position, use roslyn_get_symbol_definition instead.")]
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
			
			return scope.Error(new ErrorResult($"Line {line}, column {column} is out of range."));
		
		var model = compilation.GetSemanticModel(tree);
		var node  = (await tree.GetRootAsync()).FindToken(position).Parent;
		
		if(node is null)
			
			return scope.Error(new ErrorResult("No node at that position."));
		
		var info   = model.GetSymbolInfo(node);
		var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
		
		if(symbol is null) {
			
			var typeInfo = model.GetTypeInfo(node);
			
			if(typeInfo.Type is not null)
				
				return scope.Outcome("type", new SymbolInfoResult("type", typeInfo.Type.ToDisplayString(), null, null));
			
			return scope.Error(new ErrorResult("No symbol resolved at that position."));
		}
		
		return scope.Outcome(symbol.Name, new SymbolInfoResult(
			symbol.Kind.ToString().ToLowerInvariant(),
			symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
			symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
			symbol switch {
				
				IMethodSymbol   m => m.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
				IPropertySymbol p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
				IFieldSymbol    f => f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
				ILocalSymbol    l => l.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
				_                 => null
			},
			AdhocCaution(projectPath)
		));
	}
	
	private static int GetPosition(SourceText text, int line, int column)
	{
		if(line < 1 || line > text.Lines.Count)
			
			return -1;
		
		var lineSpan = text.Lines[line - 1];
		
		return lineSpan.Start + Math.Min(column - 1, lineSpan.Span.Length);
	}
}
