using System.Text.Json.Nodes;

/// <summary>
///     The hits of a <c>roslyn_search_files</c> answer as a flat list. The answer groups them by file
///     and by the declaration each is in (#460); most tests ask only which files and lines matched.
/// </summary>
static class SearchHits
{
	/// <summary>One matching line, with the declaration group it was listed under.</summary>
	internal sealed record Hit(string File, int Line, string Text, bool Declares, string? Declaration, string? Lines, string? At);
	
	/// <summary>
	///     Every hit of <paramref name="answer"/> in the order given, or null when it is no search
	///     answer: a missing <c>files</c> must fail a test, not read as "no hits".
	/// </summary>
	internal static Hit[]? Of(JsonNode? answer)
	{
		if(answer?["files"] is not JsonArray files)
			
			return null;
		
		var hits = new List<Hit>();
		
		foreach(var file in files)
			foreach(var group in file?["in"]?.AsArray() ?? [])
				foreach(var match in group?["matches"]?.AsArray() ?? [])
					hits.Add(new(
						file?["file"]?.GetValue<string>() ?? "",
						match?["line"]?.GetValue<int>() ?? 0,
						match?["text"]?.GetValue<string>() ?? "",
						match?["declares"]?.GetValue<bool>() == true,
						group?["declaration"]?.GetValue<string>(),
						group?["lines"]?.GetValue<string>(),
						group?["at"]?.GetValue<string>()));
		
		return [..hits];
	}
}
