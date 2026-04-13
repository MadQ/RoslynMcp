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
		"Returns a structured outline of a C# file: all types with their kind (class, struct, interface, enum, record), " +
		"name, and member signatures — without body content. " +
		"Use this to understand a file's structure at a glance before deciding which members to read in full with " +
		"roslyn_get_member_body, or to enumerate a type's API without fetching the whole file with roslyn_read_file. " +
		"Significantly more token-efficient than a full file read when you only need signatures. " +
		"Results are paged by type; use skip/take for files with many types. " +
		"Only works on .cs files tracked in the Roslyn compilation — not on .json, .xml, or other files.")]
	public async Task<object> GetFileOutline(
		[Description("Relative path to a C# file in the compilation, e.g. 'Core/WindowTracker.cs'. Must be a .cs file.")] string filePath,
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Number of types to skip. Default: 0.")] int skip = 0,
		[Description("Maximum number of types to return. Default: 20, max: 100.")] int take = 20,
		[Description("Token from a previous response to get the next page without re-executing the query.")] string? page_token = null)
	{
		using var scope = BeginTool("roslyn_get_file_outline", filePath, new { skip, take });
		
		
		if(scope.TryServeCachedPage<object>(page_token, ref skip, ref take, 100, out var cached))
			
			return scope.Outcome("cached page", cached);
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return scope.Error(error!);
		
		var rootPath = workspace.GetRootPath(projectPath);
		var tree     = FindSyntaxTree(compilation, filePath);
		
		if(tree is null)
			
			return scope.Failed("file not found", new ErrorResult($"File '{filePath}' not found in the compilation."));
		
		var root     = await tree.GetRootAsync();
		var model    = compilation.GetSemanticModel(tree);
		var allTypes = ExtractTypes(root, model);
		var result   = PaginateAndStore(allTypes, ref skip, take);
		
		return scope.Outcome($"{result.Items.Length}/{result.Total} type(s)", new FileOutlineResult(
			File:       Path.GetRelativePath(rootPath, tree.FilePath),
			TotalTypes: result.Total,
			Skip: skip, Take: take,
			Types:     result.Items,
			PageToken: result.PageToken,
			HasMore:   result.HasMore)
		{
			Caution = AdhocCaution(projectPath)
		});
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
