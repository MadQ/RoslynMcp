using System.Text.Json.Nodes;

/// <summary>
///     #487: the tools that answer with places found by the compiler or the syntax tree list them
///     as <c>roslyn_search_files</c> lists its hits, by file and by the declaration each is in
///     with the lines to read it and the place to edit it, and say the text of the line. This
///     group covers <c>roslyn_find_callers</c>, <c>roslyn_get_call_graph</c>,
///     <c>roslyn_find_string_literal</c> and <c>roslyn_semantic_search</c>; references and
///     diagnostics have theirs in <see cref="DiagnosticPlacesTests"/>.
/// </summary>
/// <remarks>
///     One file. <c>Say</c> takes lines 3 to 5 with its comment and starts at its attribute on
///     line 4. <c>Twice</c> (lines 7 to 11) calls it twice on line 9 and once on line 10;
///     <c>Once</c> (line 13) calls it once. The literals are chosen so that a pattern finds
///     exactly the ones a test is about.
/// </remarks>
static class SemanticPlacesTests
{
	const string Source =
		"class Calls\n" +
		"{\n" +
		"\t/// <summary>Says hello.</summary>\n" +
		"\t[System.Obsolete(\"say-marker\")]\n" +
		"\tstatic string Say(string who) => \"hello \" + who;\n" +
		"\n" +
		"\tstatic string Twice()\n" +
		"\t{\n" +
		"\t\tvar first = Say(\"lit-a\"); var again = Say(\"lit-b\");\n" +
		"\t\treturn first + again + Say(\"other\");\n" +
		"\t}\n" +
		"\n" +
		"\tstatic string Once() => Say(\"single\");\n" +
		"}\n";
	
	internal static TestGroup Build(TestContext ctx)
	{
		var fx = TestFixtures.NewMsBuildProject("SemanticPlaces");
		
		fx.Write("Calls.cs", Source);
		
		Task<JsonNode?> Call(string tool, object args) => ctx.CallToolJsonAsync(tool, args);
		
		static (bool, string) Verdict(bool pass, JsonNode? answer) => pass
			? (true, "PASS")
			: (false, $"FAIL  ({answer?.ToJsonString() ?? "null"})");
		
		var tests = new List<TestCase> {
			
			// The caller is the declaration a calling line is listed under, with the lines to
			// read it and the place to edit it; it used to be a qualified name beside every
			// line, and the read of the caller came next. Two calls on one line are one line.
			new("semantic places: callers are listed under the declaration that calls, with the line's text", async () => {
				
				var answer = await Call("roslyn_find_callers", new { symbolName = "Say", projectPath = fx.Csproj });
				var hits   = SearchHits.In(answer?["callers"]) ?? [];
				var twice  = hits.Where(hit => hit.Declaration == "method Twice").ToArray();
				
				return Verdict(answer?["total_callers"]?.GetValue<int>() == 3
					&& hits.Select(hit => hit.Line).SequenceEqual(new[] { 9, 10, 13 })
					&& twice is [{ Lines: "7-11", At: "7:2" }, { Text: "return first + again + Say(\"other\");" }]
					&& hits[2] is { Declaration: "method Once", Lines: "13-13", At: "13:2" }
					&& hits.All(hit => hit.File.EndsWith("Calls.cs") && !hit.Declares), answer);
			}),
			
			// take counts calling lines, not the groups they are listed in: a page of one holds
			// the first line of Twice, and the next page names Twice again for its second.
			new("semantic places: take counts calling lines, and a caller cut by a page is named again on the next", async () => {
				
				var first  = await Call("roslyn_find_callers", new { symbolName = "Say", take = 1, projectPath = fx.Csproj });
				var second = await Call("roslyn_find_callers", new { symbolName = "Say", page_token = first?["page_token"]?.GetValue<string>(), projectPath = fx.Csproj });
				
				return Verdict(SearchHits.In(first?["callers"]) is [{ Line: 9, Declaration: "method Twice" }]
					&& first?["has_more"]?.GetValue<bool>() == true
					&& SearchHits.In(second?["callers"]) is [{ Line: 10, Declaration: "method Twice", Lines: "7-11" }]
					&& second?["has_more"]?.GetValue<bool>() == true, new JsonArray(first?.DeepClone(), second?.DeepClone()));
			}),
			
			// Every call of a call graph is in the one method asked about, so the answer says
			// where that method is once, and each call is its callee, its line and the line's
			// text. Two calls on one line are two calls: the callee is what is asked for.
			new("semantic places: a call graph names its method's place once, and each call with the line's text", async () => {
				
				var answer = await Call("roslyn_get_call_graph", new { symbolName = "Twice", projectPath = fx.Csproj });
				var calls  = answer?["calls"]?.AsArray() ?? [];
				
				return Verdict((answer?["file"]?.GetValue<string>() ?? "").EndsWith("Calls.cs")
					&& answer?["lines"]?.GetValue<string>() == "7-11"
					&& answer["at"]?.GetValue<string>() == "7:2"
					&& answer["total_calls"]?.GetValue<int>() == 3
					&& calls.Select(call => call?["line"]?.GetValue<int>()).SequenceEqual(new int?[] { 9, 9, 10 })
					&& calls.All(call => call?["callee"]?.GetValue<string>().Contains("Say") == true && call["file"] is null)
					&& calls[2]?["text"]?.GetValue<string>() == "return first + again + Say(\"other\");", answer);
			}),
			
			// A literal in an attribute is on the declaration's own lines, before its name: it is
			// listed under that declaration, whose lines begin at its comment and whose position
			// is where the attribute starts. The match says the literal's column and decoded
			// value beside the line; the raw token is on the line.
			new("semantic places: a string literal in an attribute is under the declaration the attribute is on", async () => {
				
				var answer = await Call("roslyn_find_string_literal", new { pattern = "say-marker", projectPath = fx.Csproj });
				
				return Verdict(answer?["total_matches"]?.GetValue<int>() == 1
					&& SearchHits.Of(answer) is [{ Line: 4, Column: 19, Value: "say-marker", Text: "[System.Obsolete(\"say-marker\")]", Declaration: "method Say", Lines: "3-5", At: "4:2" }], answer);
			}),
			
			// Two literals on one line are two matches, each with its column and value: a line
			// is one place to read, but a literal is what was asked for.
			new("semantic places: two string literals on one line are two matches, told apart by column", async () => {
				
				var answer = await Call("roslyn_find_string_literal", new { pattern = "^lit-", projectPath = fx.Csproj });
				var hits   = SearchHits.Of(answer) ?? [];
				
				return Verdict(hits is [{ Line: 9, Value: "lit-a", Declaration: "method Twice" }, { Line: 9, Value: "lit-b", Declaration: "method Twice" }]
					&& hits[0].Column < hits[1].Column, answer);
			}),
			
			// A semantic search groups as the text search does, and marks the match that is a
			// declaration's own name. Several matches on a line are that line once. The context
			// is said once, in the answer, when the search was for one.
			new("semantic places: a semantic search lists its matches under their declarations and marks the declaring one", async () => {
				
				var answer = await Call("roslyn_semantic_search", new { pattern = "^Say$", context = "identifiers", caseSensitive = true, projectPath = fx.Csproj });
				var hits   = SearchHits.Of(answer) ?? [];
				
				return Verdict(answer?["total_matches"]?.GetValue<int>() == 4
					&& answer["context"]?.GetValue<string>() == "identifiers"
					&& hits.Select(hit => hit.Line).SequenceEqual(new[] { 5, 9, 10, 13 })
					&& hits[0] is { Declares: true, Declaration: "method Say", Lines: "3-5", At: "4:2" }
					&& hits[1] is { Declares: false, Declaration: "method Twice", Lines: "7-11" }
					&& hits.All(hit => hit.Context is null), answer);
			}),
			
			// A line's matches are one place, made from the leftmost; the place still declares when
			// a later match of the line is the name. 's' matches 'class' before 'Calls' on line 1
			// and 'static' and 'string' before 'Say' on line 5; no other declaration's name has
			// the letter. Line 9 has it many times and is one place.
			new("semantic places: a line declares when any of its matches is the name, not only the leftmost", async () => {
				
				var all  = await Call("roslyn_semantic_search", new { pattern = "s", projectPath = fx.Csproj });
				var code = await Call("roslyn_semantic_search", new { pattern = "s", context = "code", projectPath = fx.Csproj });
				
				return Verdict(SearchHits.Of(all)?.Where(hit => hit.Declares).Select(hit => hit.Line).SequenceEqual(new[] { 1, 5 }) == true
					&& SearchHits.Of(code)?.Where(hit => hit.Declares).Select(hit => hit.Line).SequenceEqual(new[] { 1, 5 }) == true
					&& SearchHits.Of(all)?.Count(hit => hit.Line == 9) == 1, new JsonArray(all?.DeepClone(), code?.DeepClone()));
			}),
			
			// The label of a match is what its first character that is not whitespace is in: a
			// pattern that starts in the indentation before a doc comment is about the comment.
			new("semantic places: a match that starts in whitespace is labelled by what follows it", async () => {
				
				var answer = await Call("roslyn_semantic_search", new { pattern = @"\s+/// <summary>", projectPath = fx.Csproj });
				
				return Verdict(SearchHits.Of(answer) is [{ Line: 3, Context: "xmldoc" }], answer);
			}),
			
			// A search over all contexts finds matches of several kinds, so each says its own: what
			// the match itself is in, not what its line begins with. The doc comment's line and
			// the line with the string both used to say "identifier".
			new("semantic places: a semantic search over all contexts names each match's context", async () => {
				
				var answer = await Call("roslyn_semantic_search", new { pattern = "Says hello|single", caseSensitive = true, projectPath = fx.Csproj });
				
				return Verdict(SearchHits.Of(answer) is [{ Line: 3, Context: "xmldoc", Declaration: "method Say" }, { Line: 13, Context: "string", Declaration: "method Once" }], answer);
			}),
		};
		
		return new TestGroup($"Semantic Places ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); return Task.CompletedTask; });
	}
}
