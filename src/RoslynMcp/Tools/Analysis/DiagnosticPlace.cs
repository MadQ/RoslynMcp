using Microsoft.CodeAnalysis;

namespace RoslynMcp.Tools;

/// <summary>
///     One finding of <c>roslyn_get_diagnostics</c> at one place, as it is kept between pages
///     (#487): what was found, and where, with the text of the line and the declaration it is in.
///     <para>
///         The answer lists findings by file, by declaration and, inside a declaration, by what
///         was found: an analyzer reports one message dozens of times, and each of those used to
///         repeat the code, the message, the file's whole path and the project. The page is still
///         counted and cut in these places, so <c>take</c> means what it meant.
///     </para>
///     <para>Plain values only: the pagination cache must never hold a compilation.</para>
/// </summary>
internal sealed record DiagnosticPlace(
	string Code, string Severity, int Rank, string Message,
	string? File, string? Project, int Line, int Column, string? Text, EnclosingDeclaration? Declaration)
{
	/// <summary>
	///     The target frameworks a build reported the finding under, joined by commas, where
	///     MSBuild says (<c>roslyn_build_project</c>). One string and not a list, so that two
	///     places are equal when their values are.
	/// </summary>
	public string? Frameworks { get; init; }
	
	/// <summary>The finding at the place a diagnostic reports. <paramref name="relativeFile"/> is its file as the answer names it, or null for a finding with no place in a file.</summary>
	public static DiagnosticPlace Of(Diagnostic diagnostic, string? project, string? relativeFile, CancellationToken cancellationToken)
	{
		var span = diagnostic.Location.GetLineSpan();
		
		string?               text        = null;
		EnclosingDeclaration? declaration = null;
		
		if(diagnostic.Location.SourceTree is { } tree) {
			
			var source = tree.GetText(cancellationToken);
			
			// A span past the text's end is an analyzer's mistake, and no reason to fail the call.
			if(span.StartLinePosition.Line < source.Lines.Count) {
				
				text        = SearchFilesTool.Excerpt(source.ToString(source.Lines[span.StartLinePosition.Line].Span), span.StartLinePosition.Character);
				declaration = EnclosingDeclaration.At(tree.GetRoot(cancellationToken), source, Math.Min(diagnostic.Location.SourceSpan.Start, source.Length)).Declaration;
			}
		}
		
		return new(
			diagnostic.Id,
			diagnostic.Severity.ToString().ToLowerInvariant(),
			(int) diagnostic.Severity,
			diagnostic.GetMessage(),
			relativeFile,
			project,
			span.StartLinePosition.Line + 1,
			span.StartLinePosition.Character + 1,
			text,
			declaration);
	}
	
	/// <summary>
	///     The findings in the order an answer lists them: the ones with no place in a file first,
	///     then file by file, the file with the most severe finding first, and inside a file by
	///     line. A file's findings stay together, which is what lets its path be said once.
	/// </summary>
	public static DiagnosticPlace[] Sorted(IEnumerable<DiagnosticPlace> places)
	{
		var all = places.ToArray();
		
		var general = all
			.Where(place => place.File is null)
			.OrderByDescending(place => place.Rank)
			.ThenBy(place => place.Code, StringComparer.Ordinal)
			.ThenBy(place => place.Message, StringComparer.Ordinal)
		;
		
		var located = all
			.Where(place => place.File is not null)
			.GroupBy(place => (place.File, place.Project))
			.OrderByDescending(file => file.Max(place => place.Rank))
			.ThenBy(file => file.Key.File, StringComparer.Ordinal)
			.ThenBy(file => file.Key.Project, StringComparer.Ordinal)
			.SelectMany(file => file
				.OrderBy(place => place.Line)
				.ThenBy(place => place.Column)
				.ThenBy(place => place.Code, StringComparer.Ordinal)
				.ThenBy(place => place.Message, StringComparer.Ordinal))
		;
		
		return [..general, ..located];
	}
	
	/// <summary>The findings of one page that have no place in a file, or null when it has none.</summary>
	public static DiagnosticFinding[]? General(IReadOnlyList<DiagnosticPlace> page)
	{
		DiagnosticFinding[] general = [..
			page.Where(place => place.File is null)
				.Select(place => new DiagnosticFinding(place.Code, place.Severity, place.Message) { Project = place.Project, TargetFrameworks = place.Frameworks?.Split(',') })
		];
		
		return general.Length > 0 ? general : null;
	}
	
	/// <summary>
	///     The located findings of one page as the answer shows them: by file, by the declaration
	///     each is in, and there one entry per code and message with all its places. The page
	///     comes sorted, so files and declarations are runs.
	/// </summary>
	public static DiagnosticFileGroup[] Group(IReadOnlyList<DiagnosticPlace> page)
	{
		var files = new List<DiagnosticFileGroup>();
		
		for(var index = 0; index < page.Count;) {
			
			if(page[index].File is not { } file) {
				
				index++;
				continue;
			}
			
			var project = page[index].Project;
			var members = new List<DiagnosticDeclarationGroup>();
			
			while(index < page.Count && page[index].File == file && page[index].Project == project) {
				
				var declaration = page[index].Declaration;
				
				// By code, severity, message and the frameworks a build named, in the order each
				// first appears.
				var findings = new List<(DiagnosticPlace First, List<DiagnosticPlaceAt> Places)>();
				
				for(; index < page.Count && page[index].File == file && page[index].Project == project && page[index].Declaration == declaration; index++) {
					
					var place = page[index];
					var entry = findings.Find(found => found.First.Code == place.Code && found.First.Severity == place.Severity && found.First.Message == place.Message && found.First.Frameworks == place.Frameworks);
					
					if(entry.Places is null)
						findings.Add(entry = (place, []));
					
					entry.Places.Add(new(place.Line, place.Column, place.Text));
				}
				
				DiagnosticFinding[] listed = [..findings.Select(found => new DiagnosticFinding(found.First.Code, found.First.Severity, found.First.Message) { Places = [..found.Places], TargetFrameworks = found.First.Frameworks?.Split(',') })];
				
				members.Add(declaration is { } inside
					? new($"{inside.Kind} {inside.Name}".TrimEnd(), $"{inside.FirstLine}-{inside.EndLine}", $"{inside.StartLine}:{inside.StartColumn}", listed)
					: new(null, null, null, listed));
			}
			
			files.Add(new(file, project, [..members]));
		}
		
		return [..files];
	}
	
	/// <summary>
	///     What the grouping of one page came to, for the log: how many places it lists, in how
	///     many files, declaration groups and entries, and the characters the same findings took
	///     as flat items, the form the answer had before (#487).
	/// </summary>
	public static string GroupingNote(IReadOnlyList<DiagnosticPlace> page)
	{
		if(page.Count == 0)
			
			return "";
		
		var files   = Group(page);
		var groups  = files.Sum(file => file.In.Length);
		var entries = files.Sum(file => file.In.Sum(group => group.Findings.Length));
		
		// {"code":"…","severity":"…","file":"…","line":N,"column":N,"message":"…"}, and the comma.
		var flat = page.Sum(place => 68 + place.Code.Length + place.Severity.Length + (place.File?.Length ?? 4) + place.Message.Length + (place.Project is { } project ? 13 + project.Length : 0));
		
		return $"{page.Count} place(s) in {files.Length} file(s), {groups} group(s), {entries} finding(s); flat ~{flat} chars";
	}
}
