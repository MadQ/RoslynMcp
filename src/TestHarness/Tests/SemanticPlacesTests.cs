using System.Text.Json.Nodes;

/// <summary>
///     #487: the tools that answer with places found by the compiler or the syntax tree list them
///     as <c>roslyn_search_files</c> lists its hits, by file and by the declaration each is in
///     with the lines to read it and the place to edit it, and say the text of the line. This
///     group covers <c>roslyn_find_callers</c>, <c>roslyn_get_call_graph</c>,
///     <c>roslyn_find_string_literal</c> and <c>roslyn_semantic_search</c>; references and
///     diagnostics have theirs in <see cref="DiagnosticPlacesTests"/>. It also covers the tools
///     whose answer is a declaration, which say the lines it takes and where its node starts
///     (<c>roslyn_find_implementations</c>, <c>roslyn_find_unused</c>,
///     <c>roslyn_get_symbol_definition</c>), and the errors of <c>roslyn_build_project</c>, which
///     are listed as diagnostics are.
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
	
	// Square takes lines 3 to 9 with its comment and starts on line 4; its unused field is on
	// line 8. Derived overrides Size on line 15. Pair declares two fields in one statement on
	// line 20. Child, on line 26, is a record whose ToString the compiler writes.
	const string Shapes =
		"interface IShape { int Area(); }\n" +
		"\n" +
		"/// <summary>A square.</summary>\n" +
		"class Square : IShape\n" +
		"{\n" +
		"\tpublic int Area() => 4;\n" +
		"\n" +
		"\tint spare;\n" +
		"}\n" +
		"\n" +
		"abstract class Base { public abstract int Size(); }\n" +
		"\n" +
		"class Derived : Base\n" +
		"{\n" +
		"\tpublic override int Size() => 1;\n" +
		"}\n" +
		"\n" +
		"class Pair\n" +
		"{\n" +
		"\tint left, right;\n" +
		"\tint Sum() => left + right;\n" +
		"}\n" +
		"\n" +
		"abstract record Named { public override string ToString() => \"n\"; }\n" +
		"\n" +
		"record Child : Named;\n";
	
	// Bad takes lines 3 to 6; the unknown name is on line 5.
	const string Broken = "class Broken\n{\n\tint Bad()\n\t{\n\t\treturn missing;\n\t}\n}\n";
	
	internal static TestGroup Build(TestContext ctx)
	{
		var fx = TestFixtures.NewMsBuildProject("SemanticPlaces");
		
		fx.Write("Calls.cs", Source);
		fx.Write("Shapes.cs", Shapes);
		fx.Write("Broken.cs", Broken);
		fx.Write("Lonely.cs", "interface ILonely { }\n");
		
		// Two fields declared on line 3, each in a statement of its own, and on line 4 a name
		// once in code and once in a comment (#515).
		fx.Write("Twins.cs", "class Twins\n{\n\tint alpha; int beta;\n\tvoid Use() { alpha = beta; } // beta\n}\n");
		
		// A project reached through a link above its directory (#516): 'link' leads to 'real',
		// and the project is given as link/proj. A build prints a source file by the path
		// the link leads to, where the system resolves the working directory (Linux, macOS),
		// so the files it names are not under the root as the workspace spells it. On macOS
		// every fixture is in that position, the temporary directory being a link; this one is
		// on every system that can make a link. The link is above the project and is not the
		// project's own directory: through that, a build on Linux finds no sources at all.
		var box    = TestFixtures.NewAdhocDir("SemanticPlacesLinked");
		var link   = box.PathOf("link");
		var linked = false;
		
		box.Write("real/proj/Linked.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
		box.Write("real/proj/Broken.cs", Broken);
		
		// And a project whose error is in the project it references, beside it: that file is
		// behind the same link and not under the root of the project that is built.
		box.Write("real/app/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n  <ItemGroup>\n    <ProjectReference Include=\"../lib/Lib.csproj\" />\n  </ItemGroup>\n</Project>\n");
		box.Write("real/app/App.cs", "class App { }\n");
		box.Write("real/lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
		box.Write("real/lib/Broken.cs", Broken);
		
		try {
			
			Directory.CreateSymbolicLink(link, box.PathOf("real"));
			
			linked = true;
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
		
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
				
				var all  = await Call("roslyn_semantic_search", new { pattern = "s", filePattern = "Calls.cs", projectPath = fx.Csproj });
				var code = await Call("roslyn_semantic_search", new { pattern = "s", context = "code", filePattern = "Calls.cs", projectPath = fx.Csproj });
				
				return Verdict(SearchHits.Of(all)?.Where(hit => hit.Declares).Select(hit => hit.Line).SequenceEqual(new[] { 1, 5 }) == true
					&& SearchHits.Of(code)?.Where(hit => hit.Declares).Select(hit => hit.Line).SequenceEqual(new[] { 1, 5 }) == true
					&& SearchHits.Of(all)?.Count(hit => hit.Line == 9) == 1, new JsonArray(all?.DeepClone(), code?.DeepClone()));
			}),
			
			// The label of a match is what its first character that is not whitespace is in: a
			// pattern that starts in the indentation before a doc comment is about the comment.
			new("semantic places: a match that starts in whitespace is labelled by what follows it", async () => {
				
				var answer = await Call("roslyn_semantic_search", new { pattern = @"\s+/// <summary>", filePattern = "Calls.cs", projectPath = fx.Csproj });
				
				return Verdict(SearchHits.Of(answer) is [{ Line: 3, Context: "xmldoc" }], answer);
			}),
			
			// A search over all contexts finds matches of several kinds, so each says its own: what
			// the match itself is in, not what its line begins with. The doc comment's line and
			// the line with the string both used to say "identifier".
			new("semantic places: a semantic search over all contexts names each match's context", async () => {
				
				var answer = await Call("roslyn_semantic_search", new { pattern = "Says hello|single", caseSensitive = true, projectPath = fx.Csproj });
				
				return Verdict(SearchHits.Of(answer) is [{ Line: 3, Context: "xmldoc", Declaration: "method Say" }, { Line: 13, Context: "string", Declaration: "method Once" }], answer);
			}),
			
			// A line with two declarations is one place, and it is the declaration a match names
			// that the place is listed under (#515). 'int' matches first, in the statement of
			// alpha, and the line was filed under alpha with nothing declared; beta is the name
			// the pattern asks for. The text search goes by the same rule.
			new("semantic places: a line with two declarations is listed under the one a match names, in both searches", async () => {
				
				var semantic = await Call("roslyn_semantic_search", new { pattern = "int|beta", context = "code", caseSensitive = true, filePattern = "Twins.cs", projectPath = fx.Csproj });
				var textual  = await Call("roslyn_search_files", new { pattern = "int|beta", caseSensitive = true, filePattern = "Twins.cs", projectPath = fx.Csproj });
				
				static bool UnderBeta(JsonNode? answer) => SearchHits.Of(answer)?.FirstOrDefault(hit => hit.Line == 3) is { Declaration: "field beta", Declares: true };
				
				return Verdict(UnderBeta(semantic) && UnderBeta(textual), new JsonArray(semantic?.DeepClone(), textual?.DeepClone()));
			}),
			
			// A line whose matches are in two contexts is one place that names both, in their
			// order. It named the first alone, as if that were what the line is.
			new("semantic places: a line with matches in two contexts names both", async () => {
				
				var answer = await Call("roslyn_semantic_search", new { pattern = "beta", caseSensitive = true, filePattern = "Twins.cs", projectPath = fx.Csproj });
				
				return Verdict(SearchHits.Of(answer) is [{ Line: 3, Context: "identifier" }, { Line: 4, Context: "identifier, comment" }], answer);
			}),
			
			// A pattern that can match nothing matches every position. Each of those was a
			// place: every line of the file, labelled by whatever stood at its start.
			new("semantic places: a match of nothing is no place", async () => {
				
				var nothing = await Call("roslyn_semantic_search", new { pattern = "q*", filePattern = "Twins.cs", projectPath = fx.Csproj });
				var some    = await Call("roslyn_semantic_search", new { pattern = "w*", filePattern = "Twins.cs", projectPath = fx.Csproj });
				
				// 'w' is in the file once, in 'Twins'; 'q' nowhere.
				return Verdict(nothing?["error"] is null && nothing?["total_matches"]?.GetValue<int>() == 0
					&& SearchHits.Of(some) is [{ Line: 1 }], new JsonArray(nothing?.DeepClone(), some?.DeepClone()));
			}),
			
			// An implementation is a declaration: the answer says where it is, the lines to read
			// it (from its comment) and the place to edit it. It was a name, and the lookup of
			// that name came next.
			new("semantic places: an implementing type and an overriding method come with their lines and place", async () => {
				
				var types   = await Call("roslyn_find_implementations", new { symbolName = "IShape", projectPath = fx.Csproj });
				var methods = await Call("roslyn_find_implementations", new { symbolName = "Size", containingType = "Base", projectPath = fx.Csproj });
				
				var square = types?["implementations"]?.AsArray() is [var only] ? only : null;
				var size   = methods?["overrides"]?.AsArray() is [var one] ? one : null;
				
				return Verdict(square?["name"]?.GetValue<string>() == "Square"
					&& (square["file"]?.GetValue<string>() ?? "").EndsWith("Shapes.cs")
					&& square["line"]?.GetValue<int>() == 4
					&& square["lines"]?.GetValue<string>() == "3-9"
					&& square["at"]?.GetValue<string>() == "4:1"
					&& size?["name"]?.GetValue<string>().StartsWith("Derived.Size(") == true
					&& size["line"]?.GetValue<int>() == 15
					&& size["lines"]?.GetValue<string>() == "15-15"
					&& size["at"]?.GetValue<string>() == "15:2", new JsonArray(types?.DeepClone(), methods?.DeepClone()));
			}),
			
			// No implementations is an empty list. It was a list of one sentence, which a caller
			// that counted the entries took for an implementation.
			new("semantic places: a type nothing implements has an empty list of implementations", async () => {
				
				var answer = await Call("roslyn_find_implementations", new { symbolName = "ILonely", projectPath = fx.Csproj });
				
				return Verdict(answer?["total_implementations"]?.GetValue<int>() == 0 && answer["implementations"]?.AsArray().Count == 0, answer);
			}),
			
			// An unused symbol is read next, or removed: the entry says the lines it takes, from
			// its comment, and the place roslyn_replace_in_code deletes it at. Nothing names
			// Square; the members of an unused type are not listed beside it.
			new("semantic places: an unused symbol comes with its lines and place", async () => {
				
				var answer = await Call("roslyn_find_unused", new { take = 200, projectPath = fx.Csproj });
				var square = answer?["unused"]?.AsArray().FirstOrDefault(entry => entry?["name"]?.GetValue<string>() == "Square");
				
				return Verdict((square?["file"]?.GetValue<string>() ?? "").EndsWith("Shapes.cs")
					&& square?["line"]?.GetValue<int>() == 4
					&& square["lines"]?.GetValue<string>() == "3-9"
					&& square["at"]?.GetValue<string>() == "4:1", answer);
			}),
			
			// line and column are where the name is; lines and at are the declaration's, from its
			// comment and from its first token. The same in the answer to several names.
			new("semantic places: a symbol's definition comes with its lines and place, also for several names", async () => {
				
				var one     = await Call("roslyn_get_symbol_definition", new { symbolName = "Square", projectPath = fx.Csproj });
				var several = await Call("roslyn_get_symbol_definition", new { symbolName = "Square|Say", projectPath = fx.Csproj });
				var say     = several?["definitions"]?[1]?["definition"];
				
				return Verdict(one?["line"]?.GetValue<int>() == 4
					&& one["column"]?.GetValue<int>() == 7
					&& one["lines"]?.GetValue<string>() == "3-9"
					&& one["at"]?.GetValue<string>() == "4:1"
					&& several?["definitions"]?[0]?["definition"]?["lines"]?.GetValue<string>() == "3-9"
					&& say?["line"]?.GetValue<int>() == 5
					&& say["lines"]?.GetValue<string>() == "3-5"
					&& say["at"]?.GetValue<string>() == "4:2", new JsonArray(one?.DeepClone(), several?.DeepClone()));
			}),
			
			// One of two fields declared in one statement. The statement's lines and start are
			// not the field's: removed or replaced there, the other field went with it. The
			// answer says where the name is and leaves the rest out.
			new("semantic places: a field declared with another has no lines and place of its own", async () => {
				
				var definition = await Call("roslyn_get_symbol_definition", new { symbolName = "right", containingType = "Pair", projectPath = fx.Csproj });
				var references = await Call("roslyn_find_references", new { symbolName = "right", containingType = "Pair", projectPath = fx.Csproj });
				var declared   = references?["declarations"]?.AsArray() is [var only] ? only : null;
				
				return Verdict(definition?["line"]?.GetValue<int>() == 20
					&& definition["lines"] is null && definition["at"] is null
					&& declared?["line"]?.GetValue<int>() == 20
					&& declared["declaration"]?.GetValue<string>() == "field left, right"
					&& declared["lines"] is null && declared["at"] is null, new JsonArray(definition?.DeepClone(), references?.DeepClone()));
			}),
			
			// A record's ToString is written by the compiler: its place is the record's name, and
			// the lines and start found there are the record's. Listed as an override, it says
			// where the record is and no more.
			new("semantic places: an override the compiler wrote does not borrow its type's lines and place", async () => {
				
				var answer = await Call("roslyn_find_implementations", new { symbolName = "ToString", containingType = "Named", projectPath = fx.Csproj });
				var child  = answer?["overrides"]?.AsArray().FirstOrDefault(entry => (entry?["name"]?.GetValue<string>() ?? "").StartsWith("Child."));
				
				// The same where a reference search says where the member is declared: not as
				// "record Child" with the record's lines.
				var references = await Call("roslyn_find_references", new { symbolName = "ToString", containingType = "Child", projectPath = fx.Csproj });
				var declared   = references?["declarations"]?.AsArray() is [var only] ? only : null;
				
				return Verdict(child?["line"]?.GetValue<int>() == 26 && child["lines"] is null && child["at"] is null
					&& declared?["line"]?.GetValue<int>() == 26
					&& declared["declaration"] is null && declared["lines"] is null && declared["at"] is null, new JsonArray(answer?.DeepClone(), references?.DeepClone()));
			}),
			
			// The errors a build stops at are listed as roslyn_get_diagnostics lists them: under
			// the declaration, with the lines to read it and the text of the line. They were flat
			// items in two lists, and the read of the method came next.
			new("semantic places: a build's errors are listed under their declaration, with the line's text", async () => {
				
				var answer = await Call("roslyn_build_project", new { projectPath = fx.Csproj });
				var error  = DiagnosticItems.Of(answer)?.FirstOrDefault(item => item?["code"]?.GetValue<string>() == "CS0103");
				
				return Verdict(answer?["succeeded"]?.GetValue<bool>() == false
					&& answer["source"]?.GetValue<string>() == "roslyn"
					&& answer["error_count"]?.GetValue<int>() == 1
					&& answer["warning_count"]?.GetValue<int>() > 0
					&& answer["errors"] is null && answer["warnings"] is null
					&& (error?["file"]?.GetValue<string>() ?? "").EndsWith("Broken.cs")
					&& error?["declaration"]?.GetValue<string>() == "method Bad"
					&& error["lines"]?.GetValue<string>() == "3-6"
					&& error["at"]?.GetValue<string>() == "3:2"
					&& error["line"]?.GetValue<int>() == 5
					&& error["text"]?.GetValue<string>() == "return missing;", answer);
			}),
			
			// The same from a build that ran: MSBuild prints a file, a line and a column, and the
			// place gets its line's text and its declaration from that file on disk. The project
			// is restored first, or the build stops at the SDK before it compiles anything. Last
			// in the group: the restore flags a reload.
			new("semantic places: the errors of a build that ran are found in the file and listed the same way", async () => {
				
				var restored = await Call("roslyn_restore_packages", new { projectPath = fx.Csproj });
				var answer   = await Call("roslyn_build_project", new { projectPath = fx.Csproj, forceBuild = true });
				var error    = DiagnosticItems.Of(answer)?.FirstOrDefault(item => item?["code"]?.GetValue<string>() == "CS0103");
				
				return Verdict(answer?["source"]?.GetValue<string>() == "msbuild"
					&& answer["succeeded"]?.GetValue<bool>() == false
					&& answer["error_count"]?.GetValue<int>() == 1
					// The file as every tool names it, relative to the root: on macOS, where the
					// build prints the real path of the temporary directory, it was a path that
					// climbed out of the root and back in (#516), and ended in the same name.
					&& error?["file"]?.GetValue<string>() == "Broken.cs"
					&& error["declaration"]?.GetValue<string>() == "method Bad"
					&& error["lines"]?.GetValue<string>() == "3-6"
					&& error["line"]?.GetValue<int>() == 5
					&& error["text"]?.GetValue<string>() == "return missing;", new JsonArray(restored?.DeepClone(), answer?.DeepClone()));
			}),
			
			// The same for a project given through a link above it. The build names the file by
			// where the link leads; the answer names it under the root, and reads it.
			new("semantic places: a build through a linked directory names its files under the root, and reads them", async () => {
				
				if(!linked)
					
					return Skip.SetupUnavailable("cannot create symbolic links here");
				
				var project  = Path.Combine(link, "proj", "Linked.csproj");
				var restored = await Call("roslyn_restore_packages", new { projectPath = project });
				var answer   = await Call("roslyn_build_project", new { projectPath = project, forceBuild = true });
				var error    = DiagnosticItems.Of(answer)?.FirstOrDefault(item => item?["code"]?.GetValue<string>() == "CS0103");
				
				return Verdict(answer?["source"]?.GetValue<string>() == "msbuild"
					&& error?["file"]?.GetValue<string>() == "Broken.cs"
					&& error["declaration"]?.GetValue<string>() == "method Bad"
					&& error["text"]?.GetValue<string>() == "return missing;", new JsonArray(restored?.DeepClone(), answer?.DeepClone()));
			}),
			
			// The error is in the referenced project, which is beside the one built and so not
			// under its root: the link is above both, and the file is named from the root as a
			// file of that project is, one directory up and down again. It was the path the
			// link leads to, climbing out of the workspace altogether. Its line is not read:
			// a workspace of one project reads below that project's directory only, with a
			// link above it or without.
			new("semantic places: a build through a linked directory names a referenced project's file beside the root", async () => {
				
				if(!linked)
					
					return Skip.SetupUnavailable("cannot create symbolic links here");
				
				var project  = Path.Combine(link, "app", "App.csproj");
				var restored = await Call("roslyn_restore_packages", new { projectPath = project });
				var answer   = await Call("roslyn_build_project", new { projectPath = project, forceBuild = true });
				var error    = DiagnosticItems.Of(answer)?.FirstOrDefault(item => item?["code"]?.GetValue<string>() == "CS0103");
				
				return Verdict(answer?["source"]?.GetValue<string>() == "msbuild"
					&& (error?["file"]?.GetValue<string>() ?? "").Replace('\\', '/') == "../lib/Broken.cs"
					&& error?["line"]?.GetValue<int>() == 5, new JsonArray(restored?.DeepClone(), answer?.DeepClone()));
			}),
		};
		
		return new TestGroup($"Semantic Places ({tests.Count} tests)", tests, Teardown: () => {
			
			fx.Dispose();
			
			// The link first: it is a name for a directory of the fixture, not one of its own.
			// One that cannot be removed is left to the sweep of the next run, as a fixture's
			// own files are: a teardown that throws ends the run without its results.
			try {
				
				if(linked)
					Directory.Delete(link);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			
			box.Dispose();
			
			return Task.CompletedTask;
		});
	}
}
