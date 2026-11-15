using System.Text.Json.Nodes;

/// <summary>
///     The findings of a <c>roslyn_get_diagnostics</c> answer as a flat list. The answer groups them
///     by file, by the declaration each is in and by what was found (#487); most tests ask only
///     whether a code was reported, in which file, on which line.
/// </summary>
static class DiagnosticItems
{
	/// <summary>
	///     One object per place a finding was reported at, with <c>code</c>, <c>severity</c>,
	///     <c>message</c>, <c>file</c>, <c>project</c>, <c>line</c>, <c>column</c>, <c>text</c>,
	///     and the <c>declaration</c>, <c>lines</c> and <c>at</c> of the group it was listed
	///     under; the findings with no place in a file come first, without those. Null when the
	///     answer lists no findings at all (<c>take: 0</c>, or no diagnostics answer): a missing
	///     list must fail a test, not read as "nothing found".
	/// </summary>
	internal static JsonArray? Of(JsonNode? answer)
	{
		if(answer?["files"] is not JsonArray files)
			
			return null;
		
		var items = new JsonArray();
		
		foreach(var finding in answer["general"]?.AsArray() ?? [])
			items.Add(new JsonObject {
				
				["code"]     = finding?["code"]?.GetValue<string>(),
				["severity"] = finding?["severity"]?.GetValue<string>(),
				["message"]  = finding?["message"]?.GetValue<string>(),
				["project"]  = finding?["project"]?.GetValue<string>(),
			});
		
		foreach(var file in files)
			foreach(var group in file?["in"]?.AsArray() ?? [])
				foreach(var finding in group?["findings"]?.AsArray() ?? [])
					foreach(var place in finding?["places"]?.AsArray() ?? [])
						items.Add(new JsonObject {
							
							["code"]        = finding?["code"]?.GetValue<string>(),
							["severity"]    = finding?["severity"]?.GetValue<string>(),
							["message"]     = finding?["message"]?.GetValue<string>(),
							["file"]        = file?["file"]?.GetValue<string>(),
							["project"]     = file?["project"]?.GetValue<string>(),
							["line"]        = place?["line"]?.GetValue<int>(),
							["column"]      = place?["column"]?.GetValue<int>(),
							["text"]        = place?["text"]?.GetValue<string>(),
							["declaration"] = group?["declaration"]?.GetValue<string>(),
							["lines"]       = group?["lines"]?.GetValue<string>(),
							["at"]          = group?["at"]?.GetValue<string>(),
						});
		
		return items;
	}
}
