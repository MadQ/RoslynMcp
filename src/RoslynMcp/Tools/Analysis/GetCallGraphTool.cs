using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class GetCallGraphTool : RoslynMcpTool
{
	public GetCallGraphTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_get_call_graph", ReadOnly = true, Title = "Get Call Graph", OpenWorld = false, Idempotent = true)]
	[Description(
		"Returns all methods directly invoked within the named method body — answers 'what does this method depend on?' without reading it. " +
		"Walks the Roslyn IOperation tree for precise semantic results: finds actual invocations, not text patterns. " +
		"Includes constructors, extension methods, property accessor calls, and event member references made within the body. " +
		"Each result shows the fully qualified callee name, file path of the call site, and 1-based line number. " +
		"Provide containingType when multiple methods share the same name. " +
		"Results are paged; pass page_token from a previous response to get subsequent pages. " +
		"Pair with roslyn_find_callers to trace the full call chain in both directions.")]
	public async Task<object> GetCallGraph(
		[Description("Method name to analyze, e.g. 'GetCompilation', 'ProcessOrder'.")] string symbolName,
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken,
		[Description("Optional containing type to disambiguate when multiple methods share the same name, e.g. 'WorkspaceManager'.")] string? containingType = null,
		[Description("Number of call sites to skip. Default: 0.")] int skip = 0,
		[Description("Maximum number of call sites to return. Default: 50, max: 200.")] int take = 50,
		[Description(PageTokenDescription)] string? page_token = null)
	{
		using var scope = BeginTool("roslyn_get_call_graph", symbolName, new { containingType, skip, take });
		
		if(!TryStripChatSymbolRef(ref symbolName, out var refError) || !TryStripChatSymbolRefOptional(ref containingType, out refError))
			
			return scope.Error(refError!);
		
		if(scope.TryServeCachedPage<CallSiteEntry>(page_token, ref skip, ref take, 50, 200, out var cached))
			
			return scope.Outcome("cached page", cached);
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return scope.Error(error!);
		
		if(!TryResolveRoot(projectPath, out var rootPath, out var rootError))
			
			return scope.Error(rootError);
		var symbol   = FindSymbol(compilation, symbolName, containingType);
		
		if(symbol is null)
			
			return scope.Failed("symbol not found", SymbolNotFoundError(symbolName));
		
		var allCalls = new List<CallSiteEntry>();
		
		foreach(var syntaxRef in symbol.DeclaringSyntaxReferences) {
			
			var syntaxNode    = await syntaxRef.GetSyntaxAsync(cancellationToken);
			var semanticModel = compilation.GetSemanticModel(syntaxNode.SyntaxTree);
			var operation     = semanticModel.GetOperation(syntaxNode, cancellationToken);
			
			if(operation is null)
				continue;
			
			foreach(var descendant in operation.DescendantsAndSelf()) {
				
				ISymbol? callee = descendant switch {
					
					IInvocationOperation inv             => inv.TargetMethod,
					IObjectCreationOperation ctor        => ctor.Constructor,
					IPropertyReferenceOperation prop     => prop.Property,
					IEventReferenceOperation evt         => evt.Event,
					_ => null
				};
				
				if(callee is null)
					continue;
				
				var loc  = descendant.Syntax.GetLocation();
				var span = loc.GetLineSpan();
				var file = span.Path is { Length: > 0 } p
					? Path.GetRelativePath(rootPath, p)
					: "?";
				
				allCalls.Add(new CallSiteEntry(
					Callee: FormatSymbolName(callee),
					File:   file,
					Line:   span.StartLinePosition.Line + 1
				));
			}
		}
		
		var allResults = allCalls
			.OrderBy(c => c.File)
			.ThenBy(c => c.Line)
			.ToArray()
		;
		
		var shape  = PageShape(FormatSymbolName(symbol), AdhocCaution(projectPath));
		var result = PaginateAndStore(allResults, ref skip, take, shape);
		
		return scope.Outcome(result.Total == 0 ? "no outgoing calls" : $"{result.Items.Length}/{result.Total} call(s)", shape(result));
	}
	
	// Builds every page of one query, first and cached alike. A static factory taking plain values, so
	// the function the pagination cache holds never captures a symbol or the compilation.
	static Func<PaginatedResult<CallSiteEntry>, object> PageShape(string method, string? caution)
		=> page => new GetCallGraphResult(
			Method:     method,
			TotalCalls: page.Total,
			Skip: page.Skip, Take: page.Take,
			Calls:     page.Items,
			PageToken: page.PageToken,
			HasMore:   page.HasMore)
		{
			Caution = caution
		}
	;
}
