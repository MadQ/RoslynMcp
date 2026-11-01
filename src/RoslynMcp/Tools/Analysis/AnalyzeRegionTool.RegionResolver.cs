using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.Tools;

internal sealed partial class AnalyzeRegionTool
{
	/// <summary>
	///     The code a line range resolves to: a run of statements from one statement list, or a
	///     single expression, in which case <see cref="First"/> and <see cref="Last"/> are the same
	///     node.
	/// </summary>
	private readonly record struct Region(SyntaxNode First, SyntaxNode Last)
	{
		public TextSpan Span => TextSpan.FromBounds(First.SpanStart, Last.Span.End);
	}
	
	/// <summary>
	///     Turns a line range into something Roslyn's flow analyses accept. They throw when given
	///     statements from two lists, so every statement region comes from a single list; a partly
	///     covered statement widens to the whole statement, and the caller reports the region it
	///     actually analyzed.
	/// </summary>
	private static class RegionResolver
	{
		/// <summary>
		///     Returns the region for a 1-based, inclusive line range, or null when the range holds
		///     no analyzable code: blank lines, comments, disabled <c>#if</c> text, or declarations
		///     with no body.
		/// </summary>
		internal static Region? Resolve(SyntaxNode root, SourceText text, int startLine, int endLine)
		{
			if(TrimmedSpan(text, startLine, endLine) is not { } span)
				
				return null;
			
			// Trivia is not code. Without this check a comment line would widen to whatever
			// statement or body encloses it.
			if(!root.DescendantTokens(span).Any(t => t.Span.OverlapsWith(span)))
				
				return null;
			
			ExpressionSyntax? initializer = null;
			
			foreach(var current in root.FindNode(span).AncestorsAndSelf()) {
				
				switch(current) {
					
					case BlockSyntax block:
						
						if(Overlapping(block.Statements, span) is { } inBlock)
							
							return inBlock;
						break;
					
					case SwitchSectionSyntax section:
						
						if(Overlapping(section.Statements, span) is { } inSection)
							
							return inSection;
						break;
					
					case CompilationUnitSyntax unit:
						
						var statements = unit.Members.OfType<GlobalStatementSyntax>().Select(g => g.Statement);
						
						return Overlapping(statements, span) ?? Single(initializer);
					
					case StatementSyntax statement:
						
						return new Region(statement, statement);
					
					case ArrowExpressionClauseSyntax arrow:
						
						return Single(arrow.Expression);
					
					// Only a fallback: the initializer of a local belongs to its statement, which
					// comes next on the way up.
					case EqualsValueClauseSyntax equals:
						
						initializer ??= equals.Value;
						break;
					
					case BaseMethodDeclarationSyntax method:
						
						return Body(method.Body, method.ExpressionBody);
					
					case AccessorDeclarationSyntax accessor:
						
						return Body(accessor.Body, accessor.ExpressionBody);
					
					case BasePropertyDeclarationSyntax property when ExpressionBodyOf(property) is { } propertyArrow:
						
						return Single(propertyArrow.Expression);
					
					case GlobalStatementSyntax:
						
						break;
					
					case MemberDeclarationSyntax member:
						
						return Single(initializer ?? InitializerOf(member));
				}
			}
			
			return Single(initializer);
		}
		
		// The span from the first to the last non-whitespace character of the range, so indentation
		// and line endings cannot touch a neighboring statement.
		static TextSpan? TrimmedSpan(SourceText text, int startLine, int endLine)
		{
			var start = text.Lines[startLine - 1].Start;
			var end   = text.Lines[endLine - 1].End;
			
			while(start < end && char.IsWhiteSpace(text[start]))
				start++;
			
			while(end > start && char.IsWhiteSpace(text[end - 1]))
				end--;
			
			return start < end ? TextSpan.FromBounds(start, end) : null;
		}
		
		static Region? Overlapping(IEnumerable<StatementSyntax> statements, TextSpan span)
		{
			StatementSyntax? first = null;
			StatementSyntax? last  = null;
			
			foreach(var statement in statements) {
				
				if(!statement.Span.OverlapsWith(span))
					continue;
				
				first ??= statement;
				last    = statement;
			}
			
			return first is null ? null : new Region(first, last!);
		}
		
		// A whole member was selected, or only its signature: the analyses work on bodies.
		static Region? Body(BlockSyntax? body, ArrowExpressionClauseSyntax? arrow)
		{
			if(body is { Statements: { Count: > 0 } statements })
				
				return new Region(statements[0], statements[^1]);
			
			return Single(arrow?.Expression);
		}
		
		static Region? Single(ExpressionSyntax? expression)
			=> expression is null ? null : new Region(expression, expression)
		;
		
		// A line holding a whole field or property lands on the member, not on its initializer. A
		// field declaring several variables has no single expression to analyze.
		static ExpressionSyntax? InitializerOf(MemberDeclarationSyntax member) => member switch {
			
			FieldDeclarationSyntax { Declaration.Variables: { Count: 1 } variables } => variables[0].Initializer?.Value,
			PropertyDeclarationSyntax property                                       => property.Initializer?.Value,
			_                                                                        => null
		};
		
		static ArrowExpressionClauseSyntax? ExpressionBodyOf(BasePropertyDeclarationSyntax property) => property switch {
			
			PropertyDeclarationSyntax p => p.ExpressionBody,
			IndexerDeclarationSyntax  i => i.ExpressionBody,
			_                           => null
		};
	}
}
