using System.Collections.Immutable;
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed partial class AnalyzeRegionTool : RoslynMcpTool
{
	public AnalyzeRegionTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_analyze_region", ReadOnly = true, Title = "Analyze Region", OpenWorld = false, Idempotent = true)]
	[Description(
		"Returns the compiler's data-flow and control-flow facts for a range of lines — what text alone cannot prove. " +
		"Use it before extracting a method (data_flow.data_flows_in are its parameters, data_flows_out its return values, " +
		"control_flow.exit_points the jumps that would need rewriting), to learn what a lambda in the region captures, whether a " +
		"local is used outside the region (read_outside and written_outside, before or after it), or whether every path returns. " +
		"The range resolves to whole statements from one statement list: a partly covered statement widens to all of it, and " +
		"'file' and 'region' report what was actually analyzed. A range over a whole method or local function analyzes its body. " +
		"An expression-bodied member or an initializer is analyzed as one expression and has no control_flow. " +
		"data_flow covers local variables, parameters and 'this' only: fields and properties never appear, so an empty list " +
		"does not mean the region touches no state. Entries read 'type name'; an entry such as 'Order this' in data_flows_in " +
		"means extracted code must be an instance method. " +
		"control_flow.end_reachable false means every path leaves the region; true just before the closing brace of a " +
		"non-void method means a return is missing. start_reachable false means dead code. " +
		"exit_points lists return, break, continue, goto and yield_break statements that leave the region. " +
		"throws lists explicit throw sites apart from exit_points, because a try in the region may catch them; exceptions " +
		"from calls are not included. Sites inside lambdas and local functions that lie in the region are not reported. " +
		"Empty lists are omitted. Returns a structured error when the range holds no code (blank lines, comments, disabled #if text), " +
		"misses a body (an attribute or signature line), or spans several members or accessors.")]
	public async Task<object> AnalyzeRegion(
		[Description("Relative path to a C# file in the compilation, e.g. 'Core/WindowTracker.cs'.")] string filePath,
		[Description("1-based first line of the region.")] int startLine,
		[Description("1-based last line of the region, inclusive.")] int endLine,
		CancellationToken cancellationToken,
		[Description(OptionalProjectPathDescription)] string? projectPath = null)
	{
		using var scope = BeginTool("roslyn_analyze_region", filePath, new { startLine, endLine });
		
		if(!TryResolveProjectArg(projectPath, filePath, out projectPath, out var projectError))
			
			return scope.Error(projectError);
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return scope.Error(error!);
		
		if(!TryResolveRoot(projectPath, out var rootPath, out var rootError))
			
			return scope.Error(rootError);
		
		var tree = FindSyntaxTree(compilation, filePath, projectPath, rootPath, out var ambiguous);
		
		if(ambiguous.Length > 0)
			
			return scope.Error(AmbiguousSourceFileError(filePath, ambiguous));
		
		if(tree is null)
			
			return scope.Error(new ErrorResult($"File '{filePath}' not found in the compilation."));
		
		var text      = await tree.GetTextAsync(cancellationToken);
		var lineCount = text.Lines.Count;
		
		if(startLine < 1 || endLine < startLine || endLine > lineCount)
			
			return scope.Error(new ErrorResult($"Lines {startLine}-{endLine} are not a valid range: the file has {lineCount} line(s)."));
		
		var root = await tree.GetRootAsync(cancellationToken);
		
		if(RegionResolver.Resolve(root, text, startLine, endLine) is not { } region)
			
			return scope.Error(new ErrorResult(
				$"No analyzable code in lines {startLine}-{endLine}.",
				"The range holds no statement or expression: it may be blank, a comment, disabled #if text, or a line outside a body. A range across several members or accessors must be narrowed to one body."));
		
		var model = compilation.GetSemanticModel(tree);
		
		DataFlowAnalysis?    dataFlow;
		ControlFlowAnalysis? controlFlow = null;
		
		// The resolver only hands over statements from one list, so a throw here would be a resolver
		// bug. An error result still beats losing the whole call to it.
		try {
			
			if(region.First is StatementSyntax first) {
				
				var last = (StatementSyntax) region.Last;
				
				dataFlow    = model.AnalyzeDataFlow(first, last);
				controlFlow = model.AnalyzeControlFlow(first, last);
			}
			else
				dataFlow = model.AnalyzeDataFlow((ExpressionSyntax) region.First);
		}
		catch(ArgumentException ex) {
			return scope.Error(new ErrorResult($"Roslyn rejected the region: {ex.Message}"));
		}
		
		// Roslyn's overloads are annotated to return null; a missing analysis is a failed one.
		if(dataFlow is not { Succeeded: true } || controlFlow is { Succeeded: false })
			
			return scope.Error(new ErrorResult(
				$"Roslyn could not analyze lines {startLine}-{endLine}.",
				"The region must lie inside one method body or be a single expression."));
		
		var span = tree.GetLineSpan(region.Span);
		
		return scope.Outcome($"{region.First.Kind()} region", new AnalyzeRegionResult(
			Path.GetRelativePath(rootPath, tree.FilePath),
			new LineRange(span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1),
			ToFacts(dataFlow),
			controlFlow is null ? null : ToFacts(controlFlow, root, tree, region.Span))
		{
			Caution = AdhocCaution(projectPath)
		});
	}
	
	static DataFlowFacts ToFacts(DataFlowAnalysis flow) => new(
		Names(flow.VariablesDeclared),
		Names(flow.DataFlowsIn),
		Names(flow.DataFlowsOut),
		Names(flow.AlwaysAssigned),
		Names(flow.DefinitelyAssignedOnEntry),
		Names(flow.DefinitelyAssignedOnExit),
		Names(flow.ReadInside),
		Names(flow.WrittenInside),
		Names(flow.ReadOutside),
		Names(flow.WrittenOutside),
		Names(flow.CapturedInside)
	);
	
	static ControlFlowFacts ToFacts(ControlFlowAnalysis flow, SyntaxNode root, SyntaxTree tree, TextSpan span)
	{
		int LineOf(SyntaxNode node) => tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
		
		var exits = flow.ExitPoints
			.Select(exit => new ExitPoint(ExitKind(exit), LineOf(exit)))
			.ToArray()
		;
		
		var throws = ThrowSites(root, span)
			.Select(LineOf)
			.ToArray()
		;
		
		return new ControlFlowFacts(
			flow.StartPointIsReachable,
			flow.EndPointIsReachable,
			NullIfEmpty(flow.EntryPoints.Select(LineOf).ToArray()),
			NullIfEmpty(exits),
			NullIfEmpty(throws)
		);
	}
	
	// ExitPoints leaves out throw. A lambda or local function wholly inside the region throws when
	// it is called, not here, so the walk stays out of them; a region inside one still sees its own.
	static IEnumerable<SyntaxNode> ThrowSites(SyntaxNode root, TextSpan span)
		=> root.DescendantNodes(span, node => !(span.Contains(node.Span) && node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
			.Where(node => node is ThrowStatementSyntax or ThrowExpressionSyntax && span.Contains(node.Span))
	;
	
	static string ExitKind(SyntaxNode exit) => exit.Kind() switch {
		
		SyntaxKind.ReturnStatement      => "return",
		SyntaxKind.BreakStatement       => "break",
		SyntaxKind.ContinueStatement    => "continue",
		SyntaxKind.GotoStatement        => "goto",
		SyntaxKind.GotoCaseStatement    => "goto",
		SyntaxKind.GotoDefaultStatement => "goto",
		SyntaxKind.YieldBreakStatement  => "yield_break",
		var other                       => other.ToString()
	};
	
	// An unresolved type displays as an empty name, leaving ' total' for a local of that type.
	static string[]? Names(ImmutableArray<ISymbol> symbols)
		=> symbols.IsEmpty
			? null
			: symbols.Select(symbol => symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).TrimStart()).ToArray()
	;
	
	static T[]? NullIfEmpty<T>(T[] items) => items.Length == 0 ? null : items;
}
