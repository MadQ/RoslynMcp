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
			
			if(node is not MemberDeclarationSyntax member || Of(member, text) is not { } found)
				continue;
			
			// From the start of that line, not from the comment or the node: a match anchored at the
			// line's start (every glob is, and so is '^\s*public') begins in the indentation, which
			// is trivia before both. Compared with the comment's own start, such a hit on a method's
			// first line was listed under the class.
			if(position < found.Span.Start)
				continue;
			
			return (found.Declaration, found.Names);
		}
		
		return (null, []);
	}
	
	/// <summary>
	///     The declarations of a file that <paramref name="name"/> names, each with the span it takes
	///     from the start of its first line (its attached comment's, when it has one) to its end:
	///     the scope a text edit is limited to with <c>inMember</c> (#476). <c>Foo</c> names every
	///     declaration called Foo, overloads included; <c>Type.Foo</c> only those directly in a type
	///     called Type, and further segments name the types around that one. A parameter list and
	///     type arguments on a segment are ignored, since an agent copies a name as it reads it.
	/// </summary>
	public static (EnclosingDeclaration Declaration, TextSpan Span)[] Named(SyntaxNode root, SourceText text, string name)
	{
		var segments = NameSegments(name);
		
		if(segments.Length == 0)
			
			return [];
		
		var found = new List<(EnclosingDeclaration, TextSpan)>();
		
		foreach(var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>()) {
			
			if(Of(member, text) is not { } described)
				continue;
			
			if(!described.Names.Any(span => text.ToString(span) == segments[^1]))
				continue;
			
			// The types around it, innermost first, against the segments before the last.
			var types = member.Ancestors().OfType<BaseTypeDeclarationSyntax>().Select(type => type.Identifier.Text).ToArray();
			
			if(types.Length < segments.Length - 1)
				continue;
			
			var inTypes = true;
			
			for(var i = 0; i < segments.Length - 1 && inTypes; i++)
				inTypes = types[i] == segments[segments.Length - 2 - i];
			
			if(inTypes)
				found.Add((described.Declaration, described.Span));
		}
		
		return [..found];
	}
	
	/// <summary>The names of every declaration in the file, for an answer that says what is there.</summary>
	public static IEnumerable<EnclosingDeclaration> All(SyntaxNode root, SourceText text)
		=> root.DescendantNodes().OfType<MemberDeclarationSyntax>()
			.Select(member => Of(member, text))
			.Where(found => found is not null)
			.Select(found => found!.Value.Declaration)
	;
	
	/// <summary>
	///     A name as an agent writes it, in its segments: <c>void Type.Foo&lt;T&gt;(int a)</c> is
	///     <c>Type</c>, <c>Foo</c>. What stands before the name (a return type, modifiers) and from
	///     the first parenthesis on is dropped, and so are type arguments.
	/// </summary>
	public static string[] NameSegments(string name)
	{
		var paren = name.IndexOf('(');
		
		if(paren >= 0)
			name = name[..paren];
		
		// Type arguments out, at any depth, before the split: 'Map<string, int>.Add' has a dot-free
		// argument list, 'Outer<A.B>.Add' has not.
		var plain = new System.Text.StringBuilder(name.Length);
		var depth = 0;
		
		foreach(var c in name) {
			
			if(c == '<')
				depth++;
			else if(c == '>')
				depth = Math.Max(0, depth - 1);
			else if(depth == 0)
				plain.Append(c);
		}
		
		// The last word: 'public static void Foo' is Foo.
		var words = plain.ToString().Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries);
		
		return words.Length == 0
			? []
			: words[^1].Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
		;
	}
	
	// A member as a declaration, where its name is written, and the span from the start of its first
	// line to its end. Null for what is no declaration a hit is reported in.
	static (EnclosingDeclaration Declaration, TextSpan[] Names, TextSpan Span)? Of(MemberDeclarationSyntax member, SourceText text)
	{
		if(Describe(member) is not { } described)
			
			return null;
		
		var (kind, name, names) = described;
		var leading  = member.GetLeadingTrivia();
		var attached = ReplaceInCodeTool.AttachedCommentStart(leading);
		var first    = attached >= 0 ? leading[attached].SpanStart : member.SpanStart;
		var start    = text.Lines.GetLinePosition(member.SpanStart);
		
		var declaration = new EnclosingDeclaration(
			kind,
			name,
			text.Lines.GetLinePosition(first).Line + 1,
			start.Line + 1,
			start.Character + 1,
			text.Lines.GetLinePosition(member.Span.End).Line + 1);
		
		return (declaration, names, TextSpan.FromBounds(text.Lines.GetLineFromPosition(first).Start, member.Span.End));
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
