using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

/// <summary>
///     <c>roslyn_annotate_source</c> (#355): a member's source and, apart from it, what the compiler
///     knows that the text does not say. The source is returned as the file has it, because agents
///     edit by quoting text: an annotation written into a line would be quoted back into an edit
///     and match nothing. Only what cannot be read off the text is annotated; an annotation on
///     every node would cost more than the reads it saves.
/// </summary>
[McpServerToolType]
internal sealed class AnnotateSourceTool : RoslynMcpTool
{
	// More than this is not one member's worth of facts, and the answer says it was cut.
	const int MaxAnnotations = 200;
	
	// The source comes back whole, so the range is bounded: past this, a member or a narrower
	// range is what to ask for.
	const int MaxLines = 400;
	
	[Flags]
	enum Kinds { None = 0, Types = 1, Nullability = 2, Calls = 4, All = Types | Nullability | Calls }
	
	// A call as it resolved: its name with the type arguments the compiler inferred and the types of
	// its parameters. No containing type: the receiver is in the source.
	static readonly SymbolDisplayFormat CallFormat = new(
		genericsOptions:      SymbolDisplayGenericsOptions.IncludeTypeParameters,
		memberOptions:        SymbolDisplayMemberOptions.IncludeParameters,
		parameterOptions:     SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut,
		miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
	);
	
	public AnnotateSourceTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	[McpServerTool(Name = "roslyn_annotate_source", ReadOnly = true, Title = "Annotate Source", OpenWorld = false, Idempotent = true)]
	[Description(
		"Returns the source of one member, or of a line range, followed by what the compiler knows about it that the text does not say: " +
		"the type of every implicitly typed variable (var, foreach, out var, deconstruction, lambda parameters, target-typed new), " +
		"each place where a value that is dereferenced may be null by the compiler's flow analysis, " +
		"and the overload a call resolved to when the name is overloaded or its type arguments were inferred. " +
		"Use this before changing a member whose types are not written in it, instead of one roslyn_get_symbol_info call per position. " +
		"The response is a one-line JSON header (symbol_name, file, start_line, end_line), then the source as raw text exactly as the file has it, " +
		"then a one-line JSON header ({\"annotations\": N}) and the annotations, one per line as 'line: fact'. " +
		"The annotations are not part of the file: quote only the source block in an edit. " +
		"Literals, explicitly typed declarations and calls to a method with one overload are not annotated; code that does not bind gets none. " +
		"Name a member with symbolName (and containingType), or a file with startLine and endLine, not both. " +
		"A method name with several overloads is answered with their line ranges, to name one by its lines. " +
		"For a single position with every detail (conversion, constant value, the other overloads), use roslyn_get_symbol_info.")]
	public async Task<object> AnnotateSource(
		CancellationToken cancellationToken,
		[Description("The member to annotate, by its simple declared name, e.g. 'GetCompilation'. Leave out to name a file and a line range.")] string? symbolName = null,
		[Description(OptionalProjectPathDescription)] string? projectPath = null,
		[Description("Optional containing type, to tell apart members of the same name, e.g. 'WorkspaceManager'.")] string? containingType = null,
		[Description("Relative file path, with startLine and endLine, to annotate a line range without naming a member.")] string? filePath = null,
		[Description("First line of the range, 1-based. With filePath.")] int? startLine = null,
		[Description("Last line of the range, 1-based, inclusive. With filePath.")] int? endLine = null,
		[Description("What to annotate, comma-separated: 'types', 'nullability', 'calls'. Default: all three.")] string? annotate = null)
	{
		using var scope = BeginTool("roslyn_annotate_source", symbolName ?? $"{filePath}:{startLine}-{endLine}", new { containingType, annotate });
		
		if(!TryStripChatSymbolRefOptional(ref symbolName, out var refError) || !TryStripChatSymbolRefOptional(ref containingType, out refError))
			
			return scope.Error(refError!);
		
		if(ParseKinds(annotate) is not { } kinds)
			
			return scope.Error(new ErrorResult($"Unknown value in annotate: '{annotate}'.", "annotate is a comma-separated list of: types, nullability, calls."));
		
		if(symbolName is null && (filePath is null || startLine is null || endLine is null))
			
			return scope.Error(new ErrorResult("Nothing to annotate was named.", "Pass symbolName (and containingType), or filePath with startLine and endLine."));
		
		// A range beside a member would be dropped without a word, and the answer would be the
		// whole member where less was asked for.
		if(symbolName is not null && (filePath is not null || startLine is not null || endLine is not null))
			
			return scope.Error(new ErrorResult("A member and a file range were both named.", "Pass symbolName (and containingType), or filePath with startLine and endLine, not both."));
		
		SyntaxTree? tree;
		string?     displayName = null;
		string?     symbolKind  = null;
		int         firstLine;
		int         lastLine;
		
		if(symbolName is not null) {
			
			var (resolvedProject, projectError) = await ResolveSymbolProjectArgAsync(projectPath, symbolName, containingType, SymbolSought.Any, null, cancellationToken);
			
			if(projectError is not null)
				
				return scope.Error(projectError);
			
			projectPath = resolvedProject;
		}
		else if(!TryResolveProjectArg(projectPath, filePath!, out projectPath, out var fileProjectError))
			
			return scope.Error(fileProjectError);
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return scope.Error(error!);
		
		if(!TryResolveRoot(projectPath, out var rootPath, out var rootError))
			
			return scope.Error(rootError);
		
		if(symbolName is not null) {
			
			var symbol = FindSymbol(compilation, symbolName, containingType);
			
			if(symbol is null)
				
				return scope.Failed("symbol not found", SymbolNotFoundError(symbolName, projectPath));
			
			var declarations = symbol.DeclaringSyntaxReferences;
			
			if(declarations.Length == 0)
				
				return scope.Error(new MetadataSymbolResult(FormatSymbolName(symbol), symbol.Kind.ToString().ToLowerInvariant(), "metadata") {
					Error = "This symbol is defined in metadata (compiled assembly), not source code."
				});
			
			if(declarations.Length > 1)
				
				return scope.Failed("several declarations", new ErrorResult(
					$"'{symbolName}' is declared in {declarations.Length} parts.",
					"Name one of its members, or pass filePath with startLine and endLine for the part to annotate."));
			
			// A name finds the first of its overloads, and an answer for that one alone would not
			// say there are others: the agent would go on to edit the wrong one.
			if(symbol is IMethodSymbol && Overloads(symbol, rootPath) is { Length: > 1 } overloads)
				
				return scope.Failed("several overloads", new ErrorResult(
					$"'{symbolName}' has {overloads.Length} overloads: {string.Join("; ", overloads)}.",
					"Pass filePath with the startLine and endLine of the one to annotate."));
			
			var declaration = await declarations[0].GetSyntaxAsync(cancellationToken);
			var lines       = declaration.SyntaxTree.GetLineSpan(declaration.Span);
			
			tree        = declaration.SyntaxTree;
			firstLine   = lines.StartLinePosition.Line;
			lastLine    = lines.EndLinePosition.Line;
			displayName = FormatSymbolName(symbol);
			symbolKind  = symbol.Kind.ToString().ToLowerInvariant();
		}
		else {
			
			tree = FindSyntaxTree(compilation, filePath!, projectPath, rootPath, out var ambiguous);
			
			if(ambiguous.Length > 0)
				
				return scope.Error(AmbiguousSourceFileError(filePath!, ambiguous));
			
			if(tree is null)
				
				return scope.Failed("file not found", new ErrorResult($"File '{filePath}' not found in the compilation."));
			
			var lineCount = (await tree.GetTextAsync(cancellationToken)).Lines.Count;
			
			if(startLine < 1 || endLine < startLine || endLine > lineCount)
				
				return scope.Error(new ErrorResult($"Lines {startLine}-{endLine} are not a range of '{filePath}', which has {lineCount} line(s)."));
			
			firstLine = startLine!.Value - 1;
			lastLine  = endLine!.Value - 1;
		}
		
		if(lastLine - firstLine + 1 > MaxLines)
			
			return scope.Failed("range too long", new ErrorResult(
				$"{lastLine - firstLine + 1} lines are more than this tool annotates in one call ({MaxLines}).",
				"Name a member of the type, or pass filePath with a narrower startLine and endLine."));
		
		var text        = await tree.GetTextAsync(cancellationToken);
		var root        = await tree.GetRootAsync(cancellationToken);
		var span        = TextSpan.FromBounds(text.Lines[firstLine].Start, text.Lines[lastLine].End);
		var annotations = Collect(compilation.GetSemanticModel(tree), root, text, span, kinds, cancellationToken);
		var source      = new StringBuilder();
		
		// Whole lines, as roslyn_get_member_body returns them, so the text can be quoted in an edit.
		for(var line = firstLine; line <= lastLine; line++)
			source.Append(text.Lines[line].ToString()).Append(line < lastLine ? "\n" : "");
		
		var shown  = annotations.Take(MaxAnnotations).ToArray();
		var file   = string.IsNullOrEmpty(tree.FilePath) ? "?" : Path.GetRelativePath(rootPath, tree.FilePath);
		var header = new AnnotatedSourceResult(displayName, symbolKind, file, firstLine + 1, lastLine + 1) {
			
			Note    = shown.Length == 0 ? "No annotations: nothing in these lines that the text does not say." : null,
			Caution = AdhocCaution(projectPath)
		};
		
		var parts = new List<(object? Header, string Source)> { (null, source.ToString()) };
		
		if(shown.Length > 0)
			parts.Add((
				new AnnotationsPart(shown.Length, annotations.Count > shown.Length ? $"{annotations.Count - shown.Length} more were left out: name a narrower range." : null),
				string.Join("\n", shown.Select(annotation => $"{annotation.Line}: {annotation.Text}"))));
		
		return scope.Outcome($"{lastLine - firstLine + 1} line(s), {annotations.Count} annotation(s)", header, parts);
	}
	
	// The overloads of a method that are declared in source, each as its signature, its file and
	// its lines: a partial type can have them in several files.
	static string[] Overloads(ISymbol method, string rootPath)
		=> [..(method.ContainingType?.GetMembers(method.Name) ?? [])
			.OfType<IMethodSymbol>()
			.SelectMany(overload => overload.DeclaringSyntaxReferences.Take(1).Select(reference => {
				
				var lines = reference.SyntaxTree.GetLineSpan(reference.Span);
				var file  = string.IsNullOrEmpty(reference.SyntaxTree.FilePath) ? "?" : Path.GetRelativePath(rootPath, reference.SyntaxTree.FilePath);
				
				return $"{overload.ToDisplayString(CallFormat)} ({file}, lines {lines.StartLinePosition.Line + 1}-{lines.EndLinePosition.Line + 1})";
			}))]
	;
	
	// Null for a value that is none of the three; all three for no value.
	static Kinds? ParseKinds(string? annotate)
	{
		if(string.IsNullOrWhiteSpace(annotate))
			
			return Kinds.All;
		
		var kinds = Kinds.None;
		
		foreach(var part in annotate.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			
			switch(part.ToLowerInvariant()) {
				
				case "types":       kinds |= Kinds.Types;       break;
				case "nullability": kinds |= Kinds.Nullability; break;
				case "calls":       kinds |= Kinds.Calls;       break;
				default:            return null;
			}
		}
		
		return kinds == Kinds.None ? Kinds.All : kinds;
	}
	
	/// <summary>
	///     The annotations of the nodes that start inside <paramref name="span"/>, by line and column,
	///     each fact once per line. A node is annotated where its name is, so a call that runs over
	///     several lines is annotated on the line that has its name.
	/// </summary>
	static List<(int Line, string Text)> Collect(SemanticModel model, SyntaxNode root, SourceText text, TextSpan span, Kinds kinds, CancellationToken cancellationToken)
	{
		var found = new List<(int Position, string Text)>();
		
		void Add(int position, string fact)
		{
			if(span.Contains(position))
				found.Add((position, fact));
		}
		
		void AddType(SyntaxToken name, ITypeSymbol? type)
		{
			if(ExpressionFacts.Bound(type) is { } bound)
				Add(name.SpanStart, $"{name.ValueText}: {bound.ToDisplayString(ExpressionFacts.TypeFormat)}");
		}
		
		foreach(var node in root.DescendantNodes(span)) {
			
			cancellationToken.ThrowIfCancellationRequested();
			
			if(kinds.HasFlag(Kinds.Types)) {
				
				switch(node) {
					
					case VariableDeclarationSyntax { Type.IsVar: true } declaration:
						
						// The declared type of a `var` local of a reference type is always nullable,
						// whatever it is given: what the initializer yields at this point is the fact.
						foreach(var declarator in declaration.Variables)
							if(declarator.Initializer?.Value is { } value && !SaysItsType(value) && model.GetDeclaredSymbol(declarator, cancellationToken) is ILocalSymbol local)
								AddType(declarator.Identifier, Flowed(local.Type, model.GetTypeInfo(value, cancellationToken).Nullability.FlowState));
						
						break;
					
					case ForEachStatementSyntax { Type.IsVar: true } loop:
						AddType(loop.Identifier, model.GetForEachStatementInfo(loop).ElementType);
						break;
					
					case SingleVariableDesignationSyntax designation when IsImplicitlyTyped(designation) && model.GetDeclaredSymbol(designation, cancellationToken) is ILocalSymbol designated:
						AddType(designation.Identifier, DesignatedType(model, designation, designated, cancellationToken));
						break;
					
					case ParameterSyntax { Type: null, Identifier.ValueText: not "_" } parameter when IsLambdaParameter(parameter) && model.GetDeclaredSymbol(parameter, cancellationToken) is { } lambdaParameter:
						AddType(parameter.Identifier, lambdaParameter.Type);
						break;
					
					case ImplicitObjectCreationExpressionSyntax creation when !IsInitializerOfTypedDeclaration(creation):
						
						if(ExpressionFacts.Bound(model.GetTypeInfo(creation, cancellationToken).Type) is { } created)
							Add(creation.SpanStart, $"new(): {created.ToDisplayString(ExpressionFacts.TypeFormat)}");
						
						break;
				}
			}
			
			if(kinds.HasFlag(Kinds.Nullability)) {
				
				var receiver = node switch {
					
					MemberAccessExpressionSyntax access when access.IsKind(SyntaxKind.SimpleMemberAccessExpression) && Dereferences(model, access, cancellationToken) => access.Expression,
					ElementAccessExpressionSyntax element => element.Expression,
					_                                     => null
				};
				
				if(receiver is not null && MayBeNull(model, receiver, cancellationToken))
					Add(receiver.SpanStart, $"{Shown(receiver)} may be null here");
			}
			
			if(kinds.HasFlag(Kinds.Calls) && node is InvocationExpressionSyntax invocation
				&& model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method) {
				
				var overloads = Alternatives(model.GetMemberGroup(invocation.Expression, cancellationToken), invocation.ArgumentList.Arguments.Count, method.ReducedFrom is not null);
				var inferred  = method.IsGenericMethod && !WritesTypeArguments(invocation.Expression);
				
				if(overloads > 1 || inferred)
					Add(NameStart(invocation.Expression),
						$"{method.ToDisplayString(CallFormat)} → {method.ReturnType.ToDisplayString(ExpressionFacts.TypeFormat)}{(overloads > 1 ? $", 1 of {overloads} overloads" : "")}");
			}
		}
		
		var lines = new List<(int Line, string Text)>(found.Count);
		var said  = new HashSet<(int, string)>();
		
		foreach(var (position, fact) in found.OrderBy(annotation => annotation.Position)) {
			
			var line = text.Lines.GetLineFromPosition(position).LineNumber + 1;
			
			if(said.Add((line, fact)))
				lines.Add((line, fact));
		}
		
		return lines;
	}
	
	// A type that can hold a null reference, as flow analysis has it at a point: nullable where
	// the value may be null, not nullable where it cannot, and unmarked where nullable analysis
	// is off. An unconstrained type parameter is neither a reference nor a value type and counts.
	static ITypeSymbol? Flowed(ITypeSymbol? type, NullableFlowState flow)
		=> type is { IsReferenceType: true } or ITypeParameterSymbol { IsValueType: false }
			? type.WithNullableAnnotation(flow switch {
				
				NullableFlowState.MaybeNull => NullableAnnotation.Annotated,
				NullableFlowState.NotNull   => NullableAnnotation.NotAnnotated,
				_                           => NullableAnnotation.None
			})
			: type
	;
	
	// What a designation is given. `out var x` takes the parameter's type, nullable too when the
	// method says the value may be null on some outcome (TryGetValue's [MaybeNullWhen(false)]).
	// The pattern `e is var x` takes what e yields at that point. A deconstruction has no one
	// place to read from and keeps its declared type, which for a reference is always nullable:
	// that may say too much, never too little.
	static ITypeSymbol? DesignatedType(SemanticModel model, SingleVariableDesignationSyntax designation, ILocalSymbol local, CancellationToken cancellationToken)
	{
		if(designation.Parent is DeclarationExpressionSyntax { Parent: ArgumentSyntax argument }
			&& model.GetOperation(argument, cancellationToken) is Microsoft.CodeAnalysis.Operations.IArgumentOperation { Parameter: { } parameter })
			
			return parameter.GetAttributes().Any(attribute => attribute.AttributeClass?.Name is "MaybeNullWhenAttribute" or "MaybeNullAttribute")
				? Flowed(parameter.Type, NullableFlowState.MaybeNull)
				: parameter.Type;
		
		if(designation.Parent is VarPatternSyntax { Parent: IsPatternExpressionSyntax tested })
			
			return Flowed(local.Type, model.GetTypeInfo(tested.Expression, cancellationToken).Nullability.FlowState);
		
		return local.Type;
	}
	
	// A member access that needs its receiver to be there. On a Nullable<T> only Value does:
	// HasValue and GetValueOrDefault are how its null is asked about. An extension method takes
	// its receiver as an argument, so it needs it unless its first parameter is nullable.
	static bool Dereferences(SemanticModel model, MemberAccessExpressionSyntax access, CancellationToken cancellationToken)
	{
		if(model.GetSymbolInfo(access, cancellationToken).Symbol is IMethodSymbol { ReducedFrom.Parameters: [var receiver, ..] })
			
			return receiver.Type.NullableAnnotation != NullableAnnotation.Annotated;
		
		return model.GetTypeInfo(access.Expression, cancellationToken).Type?.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
			|| access.Name.Identifier.ValueText == "Value";
	}
	
	// How many of a name's methods a call with this many arguments could have meant, counting
	// the ones that differ in what they take. In a call on a receiver the receiver is not among
	// them: overloads that differ only in its type (the Sum() of LINQ) are told apart by the
	// receiver, which is in the source. A params method takes any number from its last parameter on.
	static int Alternatives(System.Collections.Immutable.ImmutableArray<ISymbol> group, int argumentCount, bool onReceiver)
		=> group.OfType<IMethodSymbol>()
			.Select(method => onReceiver && method.ReducedFrom is null && method.IsExtensionMethod && method.Parameters.Length > 0 ? method.Parameters.RemoveAt(0) : method.Parameters)
			.Where(parameters => parameters is [.., { IsParams: true }]
				? argumentCount >= parameters.Length - 1
				: parameters.Length >= argumentCount && parameters.Skip(argumentCount).All(parameter => parameter.IsOptional))
			.Select(parameters => string.Join(",", parameters.Select(parameter => parameter.Type.ToDisplayString())))
			.Distinct(StringComparer.Ordinal)
			.Count()
	;
	
	// An initializer that names the type it gives: `new Foo()`, a literal, a cast, `x as Foo`.
	static bool SaysItsType(ExpressionSyntax? initializer)
		=> initializer is ObjectCreationExpressionSyntax or LiteralExpressionSyntax or CastExpressionSyntax or ArrayCreationExpressionSyntax
			or DefaultExpressionSyntax or TypeOfExpressionSyntax or InterpolatedStringExpressionSyntax
		|| initializer.IsKind(SyntaxKind.AsExpression)
	;
	
	// `out var x`, `var (a, b) = …` and the pattern `var x`: the designation has no type written.
	static bool IsImplicitlyTyped(SingleVariableDesignationSyntax designation)
	{
		SyntaxNode? parent = designation.Parent;
		
		while(parent is ParenthesizedVariableDesignationSyntax)
			parent = parent.Parent;
		
		return parent is DeclarationExpressionSyntax { Type.IsVar: true } or VarPatternSyntax;
	}
	
	static bool IsLambdaParameter(ParameterSyntax parameter)
		=> parameter.Parent is SimpleLambdaExpressionSyntax || parameter.Parent?.Parent is ParenthesizedLambdaExpressionSyntax
	;
	
	// `Foo x = new();` and `Foo X { get; } = new();` have the type beside them.
	static bool IsInitializerOfTypedDeclaration(ImplicitObjectCreationExpressionSyntax creation)
		=> creation.Parent is EqualsValueClauseSyntax {
			Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Type.IsVar: false } } or PropertyDeclarationSyntax or ParameterSyntax
		}
	;
	
	// A value, not a type or a namespace, that can be null and that flow analysis has as maybe-null
	// here. Where nullable analysis is off there is no flow state, and nothing is said.
	static bool MayBeNull(SemanticModel model, ExpressionSyntax receiver, CancellationToken cancellationToken)
	{
		var typeInfo = model.GetTypeInfo(receiver, cancellationToken);
		
		return typeInfo.Nullability.FlowState == NullableFlowState.MaybeNull
			&& ExpressionFacts.CanBeNull(ExpressionFacts.Bound(typeInfo.Type))
			&& model.GetSymbolInfo(receiver, cancellationToken).Symbol is not (ITypeSymbol or INamespaceSymbol);
	}
	
	static bool WritesTypeArguments(ExpressionSyntax callee) => callee switch {
		
		GenericNameSyntax                                         => true,
		MemberAccessExpressionSyntax { Name: GenericNameSyntax }  => true,
		MemberBindingExpressionSyntax { Name: GenericNameSyntax } => true,
		_                                                         => false
	};
	
	static int NameStart(ExpressionSyntax callee) => callee switch {
		
		MemberAccessExpressionSyntax access   => access.Name.SpanStart,
		MemberBindingExpressionSyntax binding => binding.Name.SpanStart,
		_                                     => callee.SpanStart
	};
	
	// An expression on one line and short enough to sit in an annotation.
	static string Shown(ExpressionSyntax expression)
	{
		var shown = string.Join(" ", expression.ToString().Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries));
		
		return shown.Length <= 60 ? shown : shown[..57] + "…";
	}
}

/// <summary>
///     Header of <c>roslyn_annotate_source</c> (#355). The source follows as raw text, and after it
///     the annotations under an <see cref="AnnotationsPart"/> line.
/// </summary>
internal sealed record AnnotatedSourceResult(
	[property: JsonPropertyName("symbol_name")] string? SymbolName,
	[property: JsonPropertyName("symbol_kind")] string? SymbolKind,
	[property: JsonPropertyName("file")]        string  File,
	[property: JsonPropertyName("start_line")]  int     StartLine,
	[property: JsonPropertyName("end_line")]    int     EndLine
) : ToolResult
{
	[JsonPropertyName("note")]
	public string? Note { get; init; }
}

/// <summary>
///     The line between the source and its annotations: how many follow, one per line as
///     <c>line: fact</c>, and what was left out when there were too many.
/// </summary>
internal sealed record AnnotationsPart(
	[property: JsonPropertyName("annotations")] int     Annotations,
	[property: JsonPropertyName("note")]        string? Note
);
