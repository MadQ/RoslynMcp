using System.Text.Json.Nodes;

/// <summary>
///     #487: <c>roslyn_get_diagnostics</c> lists its findings by file, by the declaration each is
///     in, and there once per code and message with every place it was found. A finding used to
///     be a flat item that repeated the file's path, and what an agent did with it next was read
///     the member around the line.
/// </summary>
/// <remarks>
///     One file, one method, three findings: the same warning twice (an empty statement, whose
///     message names nothing, so the two are one entry) and one error.
/// </remarks>
static class DiagnosticPlacesTests
{
	// One starts on line 3 and ends on line 8; the empty statements are on lines 5 and 6, the
	// unknown name on line 7.
	const string Source = "class Places\n{\n\tint One(bool a)\n\t{\n\t\tif(a) ;\n\t\tif(!a) ;\n\t\treturn missing;\n\t}\n}\n";
	
	internal static TestGroup Build(TestContext ctx)
	{
		var fx = TestFixtures.NewMsBuildProject("DiagnosticPlaces");
		
		fx.Write("Places.cs", Source);
		
		Task<JsonNode?> Diagnose(object args) => ctx.CallToolJsonAsync("roslyn_get_diagnostics", args);
		
		static (bool, string) Verdict(bool pass, JsonNode? answer) => pass
			? (true, "PASS")
			: (false, $"FAIL  ({answer?.ToJsonString() ?? "null"})");
		
		var tests = new List<TestCase> {
			
			// The whole shape in one answer: the file once, the method with the lines to read
			// it and the place to edit it, the warning once with its two places, the error
			// with the text of its line.
			new("diagnostic places: findings are listed under their declaration, the same finding once with its places", async () => {
				
				var answer = await Diagnose(new { filePath = "Places.cs", projectPath = fx.Csproj });
				var file   = answer?["files"]?.AsArray() is [var only] ? only : null;
				var group  = file?["in"]?.AsArray() is [var one] ? one : null;
				
				var findings = group?["findings"]?.AsArray() ?? [];
				var empty    = findings.FirstOrDefault(finding => finding?["code"]?.GetValue<string>() == "CS0642");
				var unknown  = findings.FirstOrDefault(finding => finding?["code"]?.GetValue<string>() == "CS0103");
				
				return Verdict(answer?["error_count"]?.GetValue<int>() == 1
					&& answer["warning_count"]?.GetValue<int>() == 2
					&& answer["returned"]?.GetValue<int>() == 3
					&& answer["general"] is null
					&& (file?["file"]?.GetValue<string>() ?? "").EndsWith("Places.cs")
					&& group?["declaration"]?.GetValue<string>() == "method One"
					&& group["lines"]?.GetValue<string>() == "3-8"
					&& group["at"]?.GetValue<string>() == "3:2"
					&& findings.Count == 2
					&& empty?["places"]?.AsArray().Select(place => place?["line"]?.GetValue<int>()).SequenceEqual(new int?[] { 5, 6 }) == true
					&& unknown?["severity"]?.GetValue<string>() == "error"
					&& unknown["places"]?[0]?["line"]?.GetValue<int>() == 7
					&& unknown["places"]?[0]?["text"]?.GetValue<string>() == "return missing;", answer);
			}),
			
			// take counts places, as it counted items: a page of two holds two of the three, and
			// the page after it the third, under the same file and declaration said again.
			new("diagnostic places: take counts places, and a group cut by a page is named again on the next", async () => {
				
				var first  = await Diagnose(new { filePath = "Places.cs", take = 2, projectPath = fx.Csproj });
				var second = await Diagnose(new { filePath = "Places.cs", page_token = first?["page_token"]?.GetValue<string>(), projectPath = fx.Csproj });
				
				return Verdict(DiagnosticItems.Of(first)?.Count == 2
					&& first?["has_more"]?.GetValue<bool>() == true
					&& DiagnosticItems.Of(second) is [var last]
					&& last?["declaration"]?.GetValue<string>() == "method One"
					&& last["line"]?.GetValue<int>() == 7, new JsonArray(first?.DeepClone(), second?.DeepClone()));
			}),
			
			// A declaration says what it is, the lines it takes and where it starts only when it
			// is a member or type of its own. A parameter is declared in a method: with the
			// method's range and position beside it, an edit made from the answer replaced the
			// method. Its uses are still listed under the method they are in.
			new("diagnostic places: a parameter's declaration does not borrow the enclosing method's range", async () => {
				
				var parameter = await ctx.CallToolJsonAsync("roslyn_find_references", new { symbolName = "a", filePath = "Places.cs", line = 3, column = 15, projectPath = fx.Csproj });
				var method    = await ctx.CallToolJsonAsync("roslyn_find_references", new { symbolName = "One", projectPath = fx.Csproj });
				
				var declared = parameter?["declarations"]?.AsArray() is [var only] ? only : null;
				var uses     = SearchHits.In(parameter?["references"]) ?? [];
				
				return Verdict(declared?["line"]?.GetValue<int>() == 3
					&& declared["declaration"] is null && declared["lines"] is null && declared["at"] is null
					&& uses.Select(use => use.Line).SequenceEqual(new[] { 5, 6 })
					&& uses.All(use => use.Declaration == "method One")
					&& method?["declarations"]?[0]?["declaration"]?.GetValue<string>() == "method One"
					&& method["declarations"]?[0]?["at"]?.GetValue<string>() == "3:2", new JsonArray(parameter?.DeepClone(), method?.DeepClone()));
			}),
			
			// The count-only call lists nothing: no files, as it had no items.
			new("diagnostic places: take 0 answers the counts without the findings", async () => {
				
				var answer = await Diagnose(new { filePath = "Places.cs", take = 0, projectPath = fx.Csproj });
				
				return Verdict(answer?["total"]?.GetValue<int>() == 3 && answer["files"] is null && answer["general"] is null, answer);
			}),
		};
		
		return new TestGroup($"Diagnostic Places ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); return Task.CompletedTask; });
	}
}
