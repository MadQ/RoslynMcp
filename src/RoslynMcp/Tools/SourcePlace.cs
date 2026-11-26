using Microsoft.CodeAnalysis;

namespace RoslynMcp.Tools;

/// <summary>
///     One place in source as a semantic tool answers it (#487): the file, the line and column, the
///     text of the line, and the declaration the place is in.
///     <para>
///         A reference, a caller or a diagnostic used to be a bare <c>file:line</c>, and what an
///         agent did next was read the member around it: every reference search in a week of
///         logs was followed by a read. <c>roslyn_search_files</c> answers with the enclosing
///         declaration since #460; this is the same answer for the tools whose places come from
///         the compiler and not from a pattern. One shape for both, so that what an agent learned
///         to do with a search hit (read the member with <c>lines</c>, edit it with <c>at</c>)
///         holds for a reference too.
///     </para>
///     <para>
///         Plain values only: a place is kept between pages by the pagination cache, which must
///         never hold a symbol, a syntax tree or a compilation.
///     </para>
/// </summary>
internal sealed record SourcePlace(string File, int Line, int Column, string Text, EnclosingDeclaration? Declaration)
{
	/// <summary>
	///     Whether the place is the name of <see cref="Declaration"/> itself. A method's name is;
	///     a local, a parameter or a positional record property is declared <em>in</em> a member
	///     and is not that member.
	/// </summary>
	public bool Declares { get; init; }
	
	/// <summary>
	///     Whether <see cref="Declaration"/> declares several names (<c>int used, spare;</c>). Its
	///     lines and its start are then the whole statement's, not those of the one name the place
	///     is: an edit or a removal made with them would take the other names along.
	/// </summary>
	public bool DeclaresSeveral { get; init; }
	
	/// <summary>
	///     What a tool found at the place beside its line, for the tools that say it: the syntax
	///     context of a semantic search's match, the decoded value of a string literal.
	/// </summary>
	public string? Context { get; init; }
	
	/// <inheritdoc cref="Context"/>
	public string? Value { get; init; }
	
	/// <summary>
	///     Whether the answer says the column too. A line is one place to read, so most answers
	///     leave it out; two string literals on one line are two places.
	/// </summary>
	public bool ColumnMatters { get; init; }
	
	/// <summary>
	///     The place a location is, with its path relative to <paramref name="rootPath"/>. A
	///     location that is in no syntax tree (metadata, a file the compilation does not hold) keeps
	///     the path and line it reports and has no text and no declaration.
	/// </summary>
	public static SourcePlace Of(Location location, string rootPath, CancellationToken cancellationToken)
	{
		var span = location.GetLineSpan();
		var file = span.Path is { Length: > 0 } path ? Path.GetRelativePath(rootPath, path) : "?";
		
		if(location.SourceTree is not { } tree)
			
			return new(file, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, "", null);
		
		var text        = tree.GetText(cancellationToken);
		var line        = text.Lines[span.StartLinePosition.Line];
		
		var (declaration, names) = EnclosingDeclaration.At(tree.GetRoot(cancellationToken), text, location.SourceSpan.Start);
		
		return new(
			file,
			span.StartLinePosition.Line + 1,
			span.StartLinePosition.Character + 1,
			SearchFilesTool.Excerpt(text.ToString(line.Span), span.StartLinePosition.Character),
			declaration)
		{
			Declares        = names.Any(name => name.OverlapsWith(location.SourceSpan)),
			DeclaresSeveral = names.Length > 1,
		};
	}
	
	/// <summary>
	///     The places in the order an answer lists them, by file and line, one per line: two
	///     references on one line are one place to read.
	/// </summary>
	public static SourcePlace[] Sorted(IEnumerable<SourcePlace> places)
		=> [..
			places
				.OrderBy(place => place.File, StringComparer.Ordinal)
				.ThenBy(place => place.Line)
				.ThenBy(place => place.Column)
				// After the sort: of several places on one line the leftmost is kept, and a long
				// line's text is cut around that one whatever order the places were found in.
				.DistinctBy(place => (place.File, place.Line))
		]
	;
	
	/// <summary>
	///     The places as an answer shows them: by file, and within a file by the declaration each
	///     is in, as a text search groups its hits. The places come sorted, so the groups are runs.
	///     <paramref name="sayDeclares"/> marks a place that is its declaration's own name, for the
	///     tools whose places can be one: a search by pattern finds a name where it is declared, a
	///     search for references or callers does not.
	/// </summary>
	public static SearchFileGroup[] Group(IReadOnlyList<SourcePlace> places, bool sayDeclares)
	{
		var files = new List<SearchFileGroup>();
		
		for(var index = 0; index < places.Count;) {
			
			var file    = places[index].File;
			var members = new List<SearchDeclarationGroup>();
			
			while(index < places.Count && places[index].File == file) {
				
				var declaration = places[index].Declaration;
				var lines       = new List<SearchHit>();
				
				for(; index < places.Count && places[index].File == file && places[index].Declaration == declaration; index++) {
					
					var place = places[index];
					
					lines.Add(new(place.Line, place.Text, sayDeclares && place.Declares ? true : null) {
						
						Column  = place.ColumnMatters ? place.Column : null,
						Context = place.Context,
						Value   = place.Value,
					});
				}
				
				members.Add(declaration is { } found
					? new($"{found.Kind} {found.Name}".TrimEnd(), $"{found.FirstLine}-{found.EndLine}", $"{found.StartLine}:{found.StartColumn}", [..lines])
					: new(null, null, null, [..lines]));
			}
			
			files.Add(new(file, null, [..members]));
		}
		
		return [..files];
	}
	
	/// <summary>
	///     Where a symbol is declared. What is declared, the lines it takes and where its node
	///     starts are said only when the place is that declaration's own name: for a local or a
	///     parameter they would be the enclosing method's, and an edit made with them would
	///     replace the method. For one of several names declared together the lines and the start
	///     are left out for the same reason: they are the whole statement's.
	/// </summary>
	public DeclaredAt AsDeclared()
		=> Declares && Declaration is { } found
			? DeclaresSeveral
				? new(File, Line) { Declaration = $"{found.Kind} {found.Name}".TrimEnd() }
				: new(File, Line) { Declaration = $"{found.Kind} {found.Name}".TrimEnd(), Lines = $"{found.FirstLine}-{found.EndLine}", At = $"{found.StartLine}:{found.StartColumn}" }
			: new(File, Line)
	;
	
	/// <summary>
	///     What the grouping of one page came to, for the log, as a search notes it (#460):
	///     <c>12 reference(s) in 3 file(s), 5 group(s), 5 in a declaration; flat ~310 chars</c>. The
	///     flat figure is what the same places took as <c>file:line</c> strings, the form the
	///     answer had before; beside the entry's own size it says what the grouping costs.
	/// </summary>
	public static string GroupingNote(IReadOnlyList<SourcePlace> places, string what)
	{
		if(places.Count == 0)
			
			return "";
		
		var files  = 0;
		var groups = 0;
		var named  = 0;
		var flat   = 0;
		
		for(var index = 0; index < places.Count; index++) {
			
			var place    = places[index];
			var newFile  = index == 0 || places[index - 1].File != place.File;
			var newGroup = newFile || places[index - 1].Declaration != place.Declaration;
			
			files  += newFile ? 1 : 0;
			groups += newGroup ? 1 : 0;
			named  += newGroup && place.Declaration is not null ? 1 : 0;
			
			// "file:line", and the comma after it.
			flat += 4 + place.File.Length + place.Line.ToString().Length;
		}
		
		return $"{places.Count} {what} in {files} file(s), {groups} group(s), {named} in a declaration; flat ~{flat} chars";
	}
}
