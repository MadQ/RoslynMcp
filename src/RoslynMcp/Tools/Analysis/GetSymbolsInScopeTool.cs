using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class GetSymbolsInScopeTool : RoslynMcpTool
{
	public GetSymbolsInScopeTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_get_symbols_in_scope", ReadOnly = true)]
	[Description(
		"Returns all symbols accessible at a specific file location: locals, parameters, fields, properties, methods, types. " +
		"Use when generating code to understand what's available in scope.")]
	public async Task<object> GetSymbolsInScope(
		[Description("Relative file path, e.g. 'Core/WindowTracker.cs'.")] string filePath,
		[Description("1-based line number.")] int line,
		[Description("1-based column number.")] int column,
		[Description(ProjectPathDescription)] string projectPath)
	{
		using var scope = BeginTool("roslyn_get_symbols_in_scope", $"{filePath}:{line}");
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return error;
		
		
		var normalized = NormalizePath(filePath);
		var tree = compilation.SyntaxTrees
			.FirstOrDefault(t => t.FilePath.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
		;
		
		if(tree is null)
			return scope.Failed("file not found", new ErrorResult($"File '{filePath}' not found in the compilation."));
		
		var text     = await tree.GetTextAsync();
		var position = GetPosition(text, line, column);
		
		if(position < 0)
			return new ErrorResult($"Line {line}, column {column} is out of range.");
		
		var model   = compilation.GetSemanticModel(tree);
		var symbols = model.LookupSymbols(position);
		
		// Group symbols by kind for easier consumption.
		var locals     = new List<SymbolInfo>();
		var parameters = new List<SymbolInfo>();
		var fields     = new List<SymbolInfo>();
		var properties = new List<SymbolInfo>();
		var methods    = new List<SymbolInfo>();
		var types      = new List<SymbolInfo>();
		var other      = new List<SymbolInfo>();
		
		foreach(var symbol in symbols) {
		
			if(symbol.IsImplicitlyDeclared)
				continue;
			
			var info = FormatSymbol(symbol);
			
			switch(symbol) {
				case ILocalSymbol:
					locals.Add(info);
					break;
				case IParameterSymbol:
					parameters.Add(info);
					break;
				case IFieldSymbol:
					fields.Add(info);
					break;
				case IPropertySymbol:
					properties.Add(info);
					break;
				case IMethodSymbol m when m.MethodKind is not MethodKind.PropertyGet and not MethodKind.PropertySet and not MethodKind.EventAdd and not MethodKind.EventRemove:
					methods.Add(info);
					break;
				case INamedTypeSymbol:
					types.Add(info);
					break;
				default:
					other.Add(info);
					break;
			}
		}
		
		var rootPath = workspace.GetRootPath(projectPath);

		SymbolInfo[] localsArr     = [.. locals];
		SymbolInfo[] parametersArr = [.. parameters];
		SymbolInfo[] fieldsArr     = [.. fields];
		SymbolInfo[] propertiesArr = [.. properties];
		SymbolInfo[] methodsArr    = [.. methods];
		SymbolInfo[] typesArr      = [.. types];
		SymbolInfo[] otherArr      = [.. other];

		return new SymbolsInScopeResult(
			Path.GetRelativePath(rootPath, tree.FilePath),
			line,
			column,
			localsArr,
			parametersArr,
			fieldsArr,
			propertiesArr,
			methodsArr,
			typesArr,
			otherArr,
			AdhocCaution(projectPath)
		);
	}
	
	private static int GetPosition(SourceText text, int line, int column)
	{
		if(line < 1 || line > text.Lines.Count)
			return -1;
		
		var lineSpan = text.Lines[line - 1];
		
		return lineSpan.Start + Math.Min(column - 1, lineSpan.Span.Length);
	}
	
	private static SymbolInfo FormatSymbol(ISymbol symbol)
	{
		var kind = symbol.Kind.ToString().ToLowerInvariant();
		var name = symbol.Name;
		var type = symbol switch {
			ILocalSymbol l    => l.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
			IParameterSymbol p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
			IFieldSymbol f    => f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
			IPropertySymbol pr => pr.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
			IMethodSymbol m   => m.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
			INamedTypeSymbol t => t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
			_                 => null
		};
		
		var containingType = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
		
		return new SymbolInfo(kind, name, type, containingType);
	}
	
	internal sealed record SymbolInfo(string Kind, string Name, string? Type, string? ContainingType);
}
