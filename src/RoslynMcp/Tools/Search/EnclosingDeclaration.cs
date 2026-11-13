using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp.Tools;

/// <summary>
///     The declaration a position in a C# file is in, as a text search reports it for each hit (#460):
///     what it is, the lines it takes with the comment attached to it, and where its node starts.
///     <para>
///         From the syntax tree alone. A search hit is a place in a file, and what an agent does
///         with it next is read the member around it or edit that member; both need the member's
///         boundaries, which took a guessed line range or a whole-file read to find. What a
///         <em>use</em> refers to is another question, a semantic one, and not answered here.
///     </para>
/// </summary>
/// <param name="Kind">What is declared: <c>method</c>, <c>class</c>, <c>field</c>, …</param>
/// <param name="Name">Its name; the names of a field declaration with several, joined.</param>
/// <param name="FirstLine">The 1-based line its attached comment starts on, or its own first line.</param>
/// <param name="StartLine">The 1-based line the node starts on: its first attribute or modifier.</param>
/// <param name="StartColumn">The 1-based column of that start, as <c>roslyn_replace_in_code</c> takes it.</param>
/// <param name="EndLine">The 1-based line the node ends on.</param>
internal readonly record struct EnclosingDeclaration(string Kind, string Name, int FirstLine, int StartLine, int StartColumn, int EndLine)
{
	/// <summary>
	///     The innermost declaration that holds <paramref name="position"/>, and where that
	///     declaration's own name is written (several places, for a field declaration with several
	///     names): a match that overlaps one of them is on the name. Null for a position in no
	///     declaration: a using directive, a namespace line, a top-level statement.
	///     <para>
	///         A position in the trivia before a member is that member's only from its attached
	///         comment on (the rule of <see cref="ReplaceInCodeTool.AttachedCommentStart"/>). A
	///         section heading three lines above a method is in the type, not in the method, and so
	///         the lines reported for a hit always hold the hit.
	///     </para>
	/// </summary>
	public static (EnclosingDeclaration? Declaration, TextSpan[] Names) At(SyntaxNode root, SourceText text, int position)
	{
		var token = root.FindToken(position);
		
		foreach(var node in token.Parent?.AncestorsAndSelf() ?? []) {
			
			if(node is not MemberDeclarationSyntax member || Describe(member) is not { } described)
				continue;
			
			var (kind, name, names) = described;
			var leading  = member.GetLeadingTrivia();
			var attached = ReplaceInCodeTool.AttachedCommentStart(leading);
			var first    = attached >= 0 ? leading[attached].SpanStart : member.SpanStart;
			
			// From the start of that line, not from the comment or the node: a match anchored at the
			// line's start (every glob is, and so is '^\s*public') begins in the indentation, which
			// is trivia before both. Compared with the comment's own start, such a hit on a method's
			// first line was listed under the class.
			if(position < text.Lines.GetLineFromPosition(first).Start)
				continue;
			
			var start = text.Lines.GetLinePosition(member.SpanStart);
			
			var declaration = new EnclosingDeclaration(
				kind,
				name,
				text.Lines.GetLinePosition(first).Line + 1,
				start.Line + 1,
				start.Character + 1,
				text.Lines.GetLinePosition(member.Span.End).Line + 1);
			
			return (declaration, names);
		}
		
		return (null, []);
	}
	
	// The kind, the name, and where the name is written, for the declarations a hit is reported in.
	// Null for what is no such declaration: a namespace, a top-level statement, an incomplete member.
	static (string Kind, string Name, TextSpan[] Names)? Describe(MemberDeclarationSyntax member) => member switch {
		
		EnumDeclarationSyntax type                    => ("enum", type.Identifier.Text, [type.Identifier.Span]),
		TypeDeclarationSyntax type                    => (type.Keyword.Text, type.Identifier.Text, [type.Identifier.Span]),
		DelegateDeclarationSyntax type                => ("delegate", type.Identifier.Text, [type.Identifier.Span]),
		EnumMemberDeclarationSyntax value             => ("enum member", value.Identifier.Text, [value.Identifier.Span]),
		MethodDeclarationSyntax method                => ("method", method.Identifier.Text, [method.Identifier.Span]),
		ConstructorDeclarationSyntax constructor      => ("constructor", constructor.Identifier.Text, [constructor.Identifier.Span]),
		DestructorDeclarationSyntax destructor        => ("destructor", destructor.Identifier.Text, [destructor.Identifier.Span]),
		PropertyDeclarationSyntax property            => ("property", property.Identifier.Text, [property.Identifier.Span]),
		IndexerDeclarationSyntax indexer              => ("indexer", "this", [indexer.ThisKeyword.Span]),
		EventDeclarationSyntax @event                 => ("event", @event.Identifier.Text, [@event.Identifier.Span]),
		OperatorDeclarationSyntax @operator           => ("operator", @operator.OperatorToken.Text, [@operator.OperatorToken.Span]),
		ConversionOperatorDeclarationSyntax conversion => ("operator", conversion.Type.ToString(), [conversion.Type.Span]),
		EventFieldDeclarationSyntax field             => Variables("event", field.Declaration),
		FieldDeclarationSyntax field                  => Variables("field", field.Declaration),
		_                                             => null,
	};
	
	static (string Kind, string Name, TextSpan[] Names) Variables(string kind, VariableDeclarationSyntax declaration)
		=> (kind, string.Join(", ", declaration.Variables.Select(variable => variable.Identifier.Text)), [..declaration.Variables.Select(variable => variable.Identifier.Span)])
	;
}
