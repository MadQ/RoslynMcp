using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class FileOutlineTool : RoslynMcpTool
{
	public FileOutlineTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_get_file_outline", ReadOnly = true, Title = "Get File Outline", OpenWorld = false, Idempotent = true)]
	[Description(
		"Returns a structured outline of a file: types and their members (signatures only, no bodies). " +
		"Saves tokens by avoiding full file reads. Results are paged; use skip/take for large files.")]
	public async Task<object> GetFileOutline(
		[Description("Relative file path, e.g. 'Core/WindowTracker.cs'.")] string filePath,
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Number of types to skip (for paging). Default: 0.")] int skip = 0,
		[Description("Maximum number of types to return. Default: 20, max: 100.")] int take = 20,
		[Description("Token from a previous response to get the next page without re-executing the query.")] string? page_token = null)
	{
		using var scope = BeginTool("roslyn_get_file_outline", filePath);


		var cachedPage = TryServeCachedPage<object>(scope, page_token, ref skip, ref take, 100);
		if(cachedPage is not null)
			return cachedPage;

		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return error;
		
		var rootPath   = workspace.GetRootPath(projectPath);
		var normalized = NormalizePath(filePath);
		
		var tree = compilation.SyntaxTrees
			.FirstOrDefault(t => t.FilePath.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
		;
		
		if(tree is null)
			return scope.Failed("file not found", new ErrorResult($"File '{filePath}' not found in the compilation."));
		
		var root     = await tree.GetRootAsync();
		var model    = compilation.GetSemanticModel(tree);
		var allTypes = ExtractTypes(root, model);
		var result   = PaginateAndStore(allTypes, ref skip, take);

		return scope.Outcome($"{result.Items.Length}/{result.Total} type(s)", new FileOutlineResult(
			File:        Path.GetRelativePath(rootPath, tree.FilePath),
			Total_types: result.Total,
			Skip: skip, Take: take,
			Types:      result.Items,
			Page_token: result.PageToken,
			Has_more:   result.HasMore,
			_caution:   AdhocCaution(projectPath)
		));
	}
	
	private static TypeOutline[] ExtractTypes(SyntaxNode root, SemanticModel model)
	{
		var results = new List<TypeOutline>();
		
		// Walk all type declarations: class, struct, interface, enum, record.
		foreach(var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()) {
		
			var symbol = model.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
			
			if(symbol is null)
				continue;
			
			var kind    = symbol.TypeKind.ToString().ToLowerInvariant();
			var name    = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
			var members = ExtractMembers(symbol);
			
			results.Add(new TypeOutline(kind, name, members));
		}
		
		return [.. results];
	}
	
	private static MemberOutline[] ExtractMembers(INamedTypeSymbol type)
	{
		var results = new List<MemberOutline>();
		
		foreach(var member in type.GetMembers()) {
		
			if(member.IsImplicitlyDeclared)
				continue;
			
			var kind = member.Kind.ToString().ToLowerInvariant();
			var signature = member switch {
				IMethodSymbol m => SymbolFormatter.FormatMethod(m),
				IPropertySymbol p => SymbolFormatter.FormatProperty(p),
				IFieldSymbol f => SymbolFormatter.FormatField(f),
				IEventSymbol e => SymbolFormatter.FormatEvent(e),
				_ => member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
			};
			
			results.Add(new MemberOutline(kind, signature));
		}
		
		return [.. results];
	}
	
	
	
	
	
	
	private sealed record TypeOutline(string Kind, string Name, MemberOutline[] Members);
	private sealed record MemberOutline(string Kind, string Signature);
}
