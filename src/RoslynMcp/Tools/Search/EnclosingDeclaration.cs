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
	
	// How many matches of a line beyond the leftmost are asked for a declaration of their own.
	// A line of code holds one declaration, rarely two; a pattern of one letter matches a line
	// dozens of times, and each question is a walk up the tree.
	const int OtherMatchesAsked = 8;
	
	/// <summary>
	///     The declaration the matches of one line are listed under, whether one of them is on a
	///     declaration's own name, and which match that is (#515). A line is one place in an
	///     answer however often the pattern matches it, so one of its matches has to say where
	///     that place is:
	///     <list type="bullet">
	///         <item>
	///             the leftmost, with any match of the line on the name of the leftmost's
	///             declaration marking it: in <c>SearchResult BuildResult()</c> the pattern
	///             <c>Result</c> matches the return type first;
	///         </item>
	///         <item>
	///             or, when none is, the first match that is the name of a declaration of its
	///             own. A line can hold two declarations (<c>int A; int Foo;</c>), and a match on
	///             <c>Foo</c> was filed under <c>A</c> and declared nothing.
	///         </item>
	///     </list>
	///     Two names on one line that both match are still one place, under the first.
	///     <para>
	///         <paramref name="leftmost"/> is the line's first match, a position in the file. The
	///         line's other matches are asked for (<paramref name="all"/>, every match in order,
	///         the leftmost included) only where one of them can change the answer: on a line
	///         that holds a name of the leftmost's declaration, or one that can hold a second
	///         declaration. Most hits of a search are lines inside the body of a member, which
	///         are neither, and cost the one lookup they always did. The state is handed through
	///         so that a caller in a loop need not allocate a closure a line.
	///     </para>
	/// </summary>
	public static (EnclosingDeclaration? Declaration, bool Declares, TextSpan Match) Among<TState>(SyntaxNode root, SourceText text, TextSpan leftmost, TState state, Func<TState, TextSpan[]> all)
	{
		var (first, names) = At(root, text, leftmost.Start);
		
		if(Overlaps(names, leftmost))
			
			return (first, true, leftmost);
		
		var line      = text.Lines.GetLineFromPosition(leftmost.Start);
		var holdsName = false;
		
		foreach(var name in names)
			holdsName |= name.Start < line.End && name.End > line.Start;
		
		// Inside the body of a member no other declaration can start: accessors, local
		// functions and lambdas are no declarations of their own here.
		var inBody = first is { Kind: "method" or "constructor" or "destructor" or "property" or "indexer" or "event" or "operator" } body
			&& body.StartLine < line.LineNumber + 1 && line.LineNumber + 1 < body.EndLine;
		
		if(!holdsName && inBody)
			
			return (first, false, leftmost);
		
		var matches = all(state);
		
		if(holdsName)
			foreach(var match in matches)
				if(Overlaps(names, match))
					
					return (first, true, match);
		
		if(!inBody)
			for(var index = 1; index < matches.Length && index <= OtherMatchesAsked; index++) {
				
				var (other, otherNames) = At(root, text, matches[index].Start);
				
				if(other != first && Overlaps(otherNames, matches[index]))
					
					return (other, true, matches[index]);
			}
		
		return (first, false, leftmost);
		
		static bool Overlaps(TextSpan[] names, TextSpan match)
		{
			foreach(var name in names)
				if(name.OverlapsWith(match))
					
					return true;
			
			return false;
		}
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
			
			// 'IDisposable.Dispose' is the explicit implementation, whatever type it is in, and
			// 'Names.IDisposable.Dispose' the one in Names: the interface is then a segment of
			// its own before the types.
			var qualifiers = segments.AsSpan(0, segments.Length - 1);
			
			if(In(types, qualifiers) || qualifiers.Length > 0 && ExplicitInterface(member) == qualifiers[^1] && In(types, qualifiers[..^1]))
				found.Add((described.Declaration, described.Span));
		}
		
		return [..found];
	}
	
	// Whether the types around a member, innermost first, are the ones a name's qualifiers say,
	// outermost first. Fewer qualifiers than types is a match: 'Inner.Foo' names Outer.Inner.Foo.
	static bool In(string[] types, ReadOnlySpan<string> qualifiers)
	{
		if(types.Length < qualifiers.Length)
			
			return false;
		
		for(var i = 0; i < qualifiers.Length; i++)
			if(types[i] != qualifiers[qualifiers.Length - 1 - i])
				
				return false;
		
		return true;
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
	///     <c>Type</c>, <c>Foo</c>. Attributes before it, what stands before the name (a return
	///     type, modifiers), its parameter list (or an indexer's) and type arguments are dropped.
	///     <para>
	///         The parameter list is the first bracket that follows a name, and everything from
	///         it on is dropped with it: an expression body, a constructor initializer, constraints.
	///         The first parenthesis of <c>(int, string) Pair()</c> and of
	///         <c>Task&lt;(int, int)&gt; Foo()</c> follows no name and belongs to the return type.
	///     </para>
	/// </summary>
	public static string[] NameSegments(string name)
	{
		var text = name.AsSpan().Trim();
		
		// Comment lines copied along with the signature, as a search reports a member from its
		// attached comment on: what a comment says is no part of the name, and its punctuation
		// would end the text below.
		while(text.StartsWith("//")) {
			
			var lineEnd = text.IndexOfAny('\r', '\n');
			
			text = lineEnd < 0 ? default : text[lineEnd..].TrimStart();
		}
		
		// Attribute lists in front: '[Obsolete("x")] void Foo()'.
		while(text.Length > 0 && text[0] == '[' && MatchingClose(text, 0) is var close and >= 0)
			text = text[(close + 1)..].TrimStart();
		
		// The parameter list (or an indexer's): the first bracket that follows a name. From it on
		// nothing is the name: not the list, not what comes after it ('=> Add(a, a)', ': base(a)',
		// 'where T : new()', a body). A bracket that follows no name is part of a type and is
		// stepped over whole: the tuple of '(int, string) Pair()', and the rank of 'int[] All()',
		// which follows a name but holds nothing.
		var angles = 0;
		
		for(var i = 0; i < text.Length; i++) {
			
			var c = text[i];
			
			if(c == '<')
				angles++;
			else if(c == '>')
				angles = Math.Max(0, angles - 1);
			
			if(angles > 0)
				continue;
			
			// A member without a parameter list ends at its initializer, its expression or its
			// accessors: the first bracket of 'int Total => Sum(items)' is a call, not a list.
			if(c is '=' or '{' or ';') {
				
				text = text[..i];
				break;
			}
			
			if(c is not ('(' or '['))
				continue;
			
			var before = text[..i].TrimEnd();
			var close  = MatchingClose(text, i);
			var rank   = c == '[' && close >= 0 && text[(i + 1)..close].Trim().Trim(',').Trim().IsEmpty;
			
			// A tuple type behind a modifier: 'public (int, string) Pair()'. What tells it from a
			// parameter list is that a name follows it, where a list is followed by nothing, a
			// body, an arrow, a colon or constraints.
			var after = close >= 0 ? text[(close + 1)..].TrimStart() : default;
			var tuple = c == '(' && after.Length > 0
				&& (char.IsLetter(after[0]) || after[0] is '_' or '@' or '[' or '?')
				&& !(after.StartsWith("where") && (after.Length == 5 || char.IsWhiteSpace(after[5])));
			
			if(!rank && !tuple && before.Length > 0 && (char.IsLetterOrDigit(before[^1]) || before[^1] is '_' or '>')) {
				
				text = before;
				break;
			}
			
			// Not closed and after no name: nothing behind it can be told apart.
			if(close < 0)
				break;
			
			i = close;
		}
		
		// Type arguments out, at any depth, before the split: 'Map<string, int>.Add' has a dot-free
		// argument list, 'Outer<A.B>.Add' has not.
		var plain = new System.Text.StringBuilder(text.Length);
		var depth = 0;
		
		foreach(var c in text) {
			
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
	
	// The index of the bracket that closes the one at 'open', or -1. Round and square brackets
	// nest; what is in a string literal is not looked at, a name holds none.
	static int MatchingClose(ReadOnlySpan<char> text, int open)
	{
		var depth = 0;
		
		for(var i = open; i < text.Length; i++) {
			
			if(text[i] is '(' or '[')
				depth++;
			else if(text[i] is ')' or ']' && --depth == 0)
				
				return i;
		}
		
		return -1;
	}
	
	/// <summary>
	///     The interface an explicit implementation names (<c>IDisposable</c> in
	///     <c>void IDisposable.Dispose()</c>), without its type arguments, or null. A name written
	///     as <c>IDisposable.Dispose</c> means that member, though the type it is in is another.
	/// </summary>
	public static string? ExplicitInterface(SyntaxNode member)
	{
		var specifier = member switch {
			
			MethodDeclarationSyntax method         => method.ExplicitInterfaceSpecifier,
			BasePropertyDeclarationSyntax property => property.ExplicitInterfaceSpecifier,
			_                                      => null,
		};
		
		return specifier is null ? null : NameSegments(specifier.Name.ToString()) is [.., var last] ? last : null;
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
