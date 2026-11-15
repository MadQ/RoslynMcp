using System.Text.Json.Nodes;

/// <summary>
///     #476: agents edit C# with the text tool (788 of 808 edits in eight days of logs), so the
///     text tool carries what the semantic tool was preferred for. On a <c>.cs</c> file it parses
///     the result and refuses an edit that adds a syntax error; in any file, a literal pattern that
///     matches several times replaces nothing unless the call says all are meant; and
///     <c>inMember</c> limits the matches to one declaration. <c>roslyn_replace_in_code</c>, for
///     its part, says what is there when it found nothing, quotes the parser with a line, and takes
///     a name as an agent writes it.
/// </summary>
/// <remarks>
///     Every test that writes has a copy of the fixture of its own and compares the whole file
///     afterwards: "nothing was written" and "one place changed" are claims about the file, not
///     about the answer.
/// </remarks>
static class EditSafetyTests
{
	// Three methods that end in the same line, two of them overloads of one name.
	const string Source = "class Safe\n{\n\tint First()\n\t{\n\t\treturn 0;\n\t}\n\n\tint Second()\n\t{\n\t\treturn 0;\n\t}\n\n\tint Second(int a)\n\t{\n\t\treturn 0;\n\t}\n}\n";
	
	internal static TestGroup Build(TestContext ctx)
	{
		var fx = TestFixtures.NewMsBuildProject("EditSafety");
		
		foreach(var name in new[] { "brace", "brace_dry", "allowed", "several", "member", "overload", "unknown", "all", "code_name", "code_missing", "code_stale", "code_syntax", "code_force", "cross" })
			fx.Write($"{name}.cs", Source.Replace("class Safe", $"class Safe_{name}"));
		
		// A file that does not parse before the edit: the class is never closed.
		var brokenSource = Source.Replace("class Safe", "class Safe_broken")[..^2];
		
		fx.Write("broken.cs", brokenSource);
		// Members whose signatures are not 'type Name(parameters)'.
		fx.Write("names.cs", "class Names : System.IDisposable\n{\n\t(int, string) Pair() { return (0, \"\"); }\n\tvoid System.IDisposable.Dispose() { }\n\tint this[int i] { get { return 0; } }\n\tint Total => Pair().Item1;\n}\n");
		
		// An unterminated comment, and behind it a statement without its semicolon.
		fx.Write("uncover.cs", "class Uncover\n{\n\tint a = 1; /* open\n\tint b = 2\n}\n");
		fx.Write("twice.txt", "same\nother\nsame\n");
		fx.Write("twice_all.txt", "same\nother\nsame\n");
		fx.Write("twice_regex.txt", "same\nother\nsame\n");
		
		string SourceOf(string name) => Source.Replace("class Safe", $"class Safe_{name}");
		string Read(string file)     => File.ReadAllText(fx.PathOf(file));
		
		Task<JsonNode?> Replace(object args) => ctx.CallToolJsonAsync("roslyn_replace_in_file", args);
		Task<JsonNode?> InCode(object args)  => ctx.CallToolJsonAsync("roslyn_replace_in_code", args);
		
		static (bool, string) Verdict(bool pass, JsonNode? answer, string? file) => pass
			? (true, "PASS")
			: (false, $"FAIL  ({answer?.ToJsonString() ?? "null"}{(file is null ? "" : $"; file: '{file}'")})");
		
		// The declarations an answer lists its matches under, in order.
		static string[] Declarations(JsonNode? answer) => [
			..(answer?["matches"]?.AsArray() ?? []).Select(group => group?["declaration"]?.GetValue<string>() ?? "-")];
		
		var tests = new List<TestCase> {
			
			// The acceptance case: a closing brace goes missing. The answer must quote the parser
			// with a line, and the file must be the one it was. Before #476 this edit was written
			// and the next roslyn_get_diagnostics call found it.
			new("edit safety: a text edit that unbalances a brace in a .cs file is refused, with the parser's message", async () => {
				
				var answer = await Replace(new { filePath = "brace.cs", pattern = "\t\treturn 0;\n\t}\n\n\tint Second()", replacement = "\t\treturn 0;\n\n\tint Second()", projectPath = fx.Csproj });
				var error  = answer?["error"]?.GetValue<string>() ?? "";
				
				return Verdict(error.Contains("syntax error") && error.Contains("line ") && error.Contains("CS") && Read("brace.cs") == SourceOf("brace"), answer, Read("brace.cs"));
			}),
			
			// A dry run's verdict is the write's: an agent that previews an edit and is told it
			// would be made must not be refused when it then makes it.
			new("edit safety: a dry run of that edit is refused the same way", async () => {
				
				var answer = await Replace(new { filePath = "brace_dry.cs", pattern = "\t\treturn 0;\n\t}\n\n\tint Second()", replacement = "\t\treturn 0;\n\n\tint Second()", dryRun = true, projectPath = fx.Csproj });
				
				return Verdict((answer?["error"]?.GetValue<string>() ?? "").Contains("syntax error"), answer, null);
			}),
			
			// The override, for an edit that is half of a change: it writes, and says what it left.
			new("edit safety: allowSyntaxErrors writes the edit and names the errors in _caution", async () => {
				
				var answer = await Replace(new { filePath = "allowed.cs", pattern = "\t\treturn 0;\n\t}\n\n\tint Second()", replacement = "\t\treturn 0;\n\n\tint Second()", allowSyntaxErrors = true, projectPath = fx.Csproj });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true
					&& (answer?["_caution"]?.GetValue<string>() ?? "").Contains("syntax error")
					&& Read("allowed.cs") != SourceOf("allowed"), answer, Read("allowed.cs"));
			}),
			
			// Only errors the edit adds count. A file that is already broken must stay editable, or
			// the tool that could repair it refuses to; this edit touches nothing near the error.
			new("edit safety: a file that already has a syntax error stays editable", async () => {
				
				var answer = await Replace(new { filePath = "broken.cs", pattern = "int First()", replacement = "int Renamed()", projectPath = fx.Csproj });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true
					&& answer?["_caution"] is null
					&& Read("broken.cs") == brokenSource.Replace("int First()", "int Renamed()"), answer, Read("broken.cs"));
			}),
			
			// The second acceptance case: one line, three places. Nothing is replaced, and each
			// place is listed under the declaration it is in, the overloads apart by their lines.
			new("edit safety: a literal snippet that matches three times replaces nothing and lists the places", async () => {
				
				var answer = await Replace(new { filePath = "several.cs", pattern = "return 0;", replacement = "return 1;", projectPath = fx.Csproj });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == false
					&& answer?["match_count"]?.GetValue<int>() == 3
					&& Declarations(answer) is ["method First", "method Second", "method Second"]
					&& answer?["matches"]?[2]?["matches"]?[0]?["line"]?.GetValue<int>() == 15
					&& Read("several.cs") == SourceOf("several"), answer, Read("several.cs"));
			}),
			
			// The third: the same edit with a scope changes one of them, and only that one.
			new("edit safety: inMember limits the same edit to one declaration", async () => {
				
				var answer = await Replace(new { filePath = "member.cs", pattern = "return 0;", replacement = "return 1;", inMember = "Safe_member.First", projectPath = fx.Csproj });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true
					&& answer?["match_count"]?.GetValue<int>() == 1
					&& Read("member.cs") == SourceOf("member").Replace("int First()\n\t{\n\t\treturn 0;", "int First()\n\t{\n\t\treturn 1;"), answer, Read("member.cs"));
			}),
			
			// A name is written as it is read, signature and all, and names every overload: the
			// two of Second hold the line twice, so the call is answered with the two places.
			new("edit safety: inMember written as a signature names the overloads of that name", async () => {
				
				var answer = await Replace(new { filePath = "overload.cs", pattern = "return 0;", replacement = "return 1;", inMember = "int Second(int a)", projectPath = fx.Csproj });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == false
					&& answer?["match_count"]?.GetValue<int>() == 2
					&& Declarations(answer) is ["method Second", "method Second"]
					&& Read("overload.cs") == SourceOf("overload"), answer, Read("overload.cs"));
			}),
			
			// A scope that names nothing is an error that says what the file declares, never an
			// edit of the whole file.
			new("edit safety: an inMember that names no declaration lists what the file declares", async () => {
				
				var answer = await Replace(new { filePath = "unknown.cs", pattern = "return 0;", replacement = "return 1;", inMember = "Third", projectPath = fx.Csproj });
				
				return Verdict((answer?["error"]?.GetValue<string>() ?? "").Contains("Third")
					&& (answer?["hint"]?.GetValue<string>() ?? "").Contains("First")
					&& Read("unknown.cs") == SourceOf("unknown"), answer, Read("unknown.cs"));
			}),
			
			new("edit safety: inMember on a file that is not C# is an error", async () => {
				
				var answer = await Replace(new { filePath = "twice.txt", pattern = "same", replacement = "new", inMember = "First", projectPath = fx.Csproj });
				
				return Verdict((answer?["error"]?.GetValue<string>() ?? "").Contains("inMember"), answer, null);
			}),
			
			// The rule holds for every file type: two matches in a text file replace nothing, and
			// are listed without a declaration.
			new("edit safety: several literal matches in a text file replace nothing either", async () => {
				
				var answer = await Replace(new { filePath = "twice.txt", pattern = "same", replacement = "new", projectPath = fx.Csproj });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == false
					&& answer?["match_count"]?.GetValue<int>() == 2
					&& Declarations(answer) is ["-"]
					&& Read("twice.txt") == "same\nother\nsame\n", answer, Read("twice.txt"));
			}),
			
			new("edit safety: replaceAll replaces every literal match", async () => {
				
				var answer = await Replace(new { filePath = "twice_all.txt", pattern = "same", replacement = "new", replaceAll = true, projectPath = fx.Csproj });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true && Read("twice_all.txt") == "new\nother\nnew\n", answer, Read("twice_all.txt"));
			}),
			
			// A regex says "every place like this" by being one, and keeps replacing them all.
			new("edit safety: a regex still replaces every match", async () => {
				
				var answer = await Replace(new { filePath = "twice_regex.txt", pattern = "sa(me)", replacement = "$1", mode = "regex", projectPath = fx.Csproj });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true && Read("twice_regex.txt") == "me\nother\nme\n", answer, Read("twice_regex.txt"));
			}),
			
			// The fourth acceptance case, first half. Two of the eight failures in the logs passed
			// a return type with the name; a type in front is the other form an agent copies.
			new("edit safety: roslyn_replace_in_code reads 'int First' and 'Type.First' as the name", async () => {
				
				var typed     = await InCode(new { filePath = "code_name.cs", nodeKind = "method", textPattern = "int First", dryRun = true, projectPath = fx.Csproj });
				var qualified = await InCode(new { filePath = "code_name.cs", nodeKind = "method", textPattern = "Safe_code_name.First()", dryRun = true, projectPath = fx.Csproj });
				var elsewhere = await InCode(new { filePath = "code_name.cs", nodeKind = "method", textPattern = "Other.First", dryRun = true, projectPath = fx.Csproj });
				
				return Verdict(typed?["change_count"]?.GetValue<int>() == 1
					&& qualified?["change_count"]?.GetValue<int>() == 1
					&& elsewhere?["change_count"]?.GetValue<int>() == 0, new JsonArray(typed?.DeepClone(), qualified?.DeepClone(), elsewhere?.DeepClone()), null);
			}),
			
			// Second half: a pattern that matches nothing lists what is there, with positions that
			// can be passed back.
			new("edit safety: roslyn_replace_in_code lists the nodes of the kind when nothing matched", async () => {
				
				var answer     = await InCode(new { filePath = "code_missing.cs", nodeKind = "method", textPattern = "Missing", replacement = "void Missing() { }", projectPath = fx.Csproj });
				var candidates = answer?["candidates"]?.AsArray() ?? [];
				
				return Verdict(candidates.Count == 3
					&& candidates[0]?["name"]?.GetValue<string>() == "First"
					&& candidates[0]?["line"]?.GetValue<int>() == 3
					&& candidates[2]?["line"]?.GetValue<int>() == 13
					&& Read("code_missing.cs") == SourceOf("code_missing"), answer, Read("code_missing.cs"));
			}),
			
			// The third of the failures in the logs: a line that had gone stale. The answer says
			// where the node the kind and the name do match starts now.
			new("edit safety: a stale line is answered with where the named node starts", async () => {
				
				var answer     = await InCode(new { filePath = "code_stale.cs", nodeKind = "method", textPattern = "First", line = 9, replacement = "int First() => 1;", projectPath = fx.Csproj });
				var candidates = answer?["candidates"]?.AsArray() ?? [];
				
				return Verdict(candidates.Count == 1
					&& candidates[0]?["line"]?.GetValue<int>() == 3
					&& Read("code_stale.cs") == SourceOf("code_stale"), answer, Read("code_stale.cs"));
			}),
			
			// A signature is copied as it stands: a tuple return type has the text's first
			// parenthesis, an explicit implementation is named by its interface, an indexer has
			// no name but 'this'. Each must find its member; cut at the first parenthesis, the
			// first named nothing and the other two the wrong thing.
			new("edit safety: inMember finds a tuple-returning method, an explicit implementation and an indexer", async () => {
				
				var tuple    = await Replace(new { filePath = "names.cs", pattern = "return (0", replacement = "return (1", inMember = "(int, string) Pair()", dryRun = true, projectPath = fx.Csproj });
				var explicit_ = await Replace(new { filePath = "names.cs", pattern = "{ }", replacement = "{ return; }", inMember = "void IDisposable.Dispose()", dryRun = true, projectPath = fx.Csproj });
				var indexer  = await Replace(new { filePath = "names.cs", pattern = "return 0;", replacement = "return 1;", inMember = "this[int i]", dryRun = true, projectPath = fx.Csproj });
				var inType   = await Replace(new { filePath = "names.cs", pattern = "{ }", replacement = "{ return; }", inMember = "Names.IDisposable.Dispose", dryRun = true, projectPath = fx.Csproj });
				
				// With a modifier in front the tuple follows a word, as a parameter list does; and
				// a property's expression holds a call, whose bracket is no parameter list.
				var modified = await Replace(new { filePath = "names.cs", pattern = "return (0", replacement = "return (1", inMember = "/// <summary>A pair; never null.</summary>\n\t// total = a + b\n\tpublic (int, string) Pair()", dryRun = true, projectPath = fx.Csproj });
				var property = await Replace(new { filePath = "names.cs", pattern = "Pair()", replacement = "Pair( )", inMember = "int Total => Pair().Item1;", dryRun = true, projectPath = fx.Csproj });
				
				if(modified?["match_count"]?.GetValue<int>() != 1 || property?["match_count"]?.GetValue<int>() != 1)
					
					return Verdict(false, new JsonArray(modified?.DeepClone(), property?.DeepClone()), null);
				
				return Verdict(tuple?["match_count"]?.GetValue<int>() == 1
					&& inType?["match_count"]?.GetValue<int>() == 1
					&& explicit_?["match_count"]?.GetValue<int>() == 1
					&& indexer?["match_count"]?.GetValue<int>() == 1, new JsonArray(tuple?.DeepClone(), explicit_?.DeepClone(), indexer?.DeepClone()), null);
			}),
			
			// A snippet that quotes a member's end takes the line break after its brace along,
			// one character past the member. It is a match in that member, not one "elsewhere".
			new("edit safety: a match that reaches over the end of the inMember declaration counts", async () => {
				
				var answer = await Replace(new { filePath = "cross.cs", pattern = "\t\treturn 0;\n\t}\n", replacement = "\t\treturn 1;\n\t}\n", inMember = "First", projectPath = fx.Csproj });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true
					&& Read("cross.cs") == SourceOf("cross").Replace("int First()\n\t{\n\t\treturn 0;", "int First()\n\t{\n\t\treturn 1;"), answer, Read("cross.cs"));
			}),
			
			// Closing an unterminated comment shows the parser what was behind it: here a
			// missing semicolon, which is a new error by its id but not one the edit made. The
			// file has fewer errors afterwards, so the repair must go through.
			new("edit safety: a repair that uncovers an error the file already had is not refused", async () => {
				
				var answer = await Replace(new { filePath = "uncover.cs", pattern = "/* open", replacement = "/* open */", projectPath = fx.Csproj });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true && Read("uncover.cs").Contains("/* open */"), answer, Read("uncover.cs"));
			}),
			
			// A pattern read as a name has lost its parameter list: 'First(string s)' finds the
			// only First, which takes none. The answer must say which node was taken, and with
			// force a signature must not reach every overload of its name.
			new("edit safety: roslyn_replace_in_code says so when a signature was read as a name, and force does not spread it", async () => {
				
				var taken  = await InCode(new { filePath = "code_name.cs", nodeKind = "method", textPattern = "First(string s)", dryRun = true, projectPath = fx.Csproj });
				var forced = await InCode(new { filePath = "code_force.cs", nodeKind = "method", textPattern = "Second(int a)", replacement = "int Second() => 0;", force = true, projectPath = fx.Csproj });
				
				// What follows a parameter list is not the name either: an expression body's call,
				// a constructor initializer, a constraint. Each of these named another member, or none.
				var bodied   = await InCode(new { filePath = "code_name.cs", nodeKind = "method", textPattern = "int First() => Second(1)", dryRun = true, projectPath = fx.Csproj });
				var arrayed  = await InCode(new { filePath = "code_name.cs", nodeKind = "method", textPattern = "int[] First(int[] a) where T : new()", dryRun = true, projectPath = fx.Csproj });
				var unforced = await InCode(new { filePath = "code_force.cs", nodeKind = "method", textPattern = "Second(int a)", replacement = "int Second() => 0;", projectPath = fx.Csproj });
				var dryForce = await InCode(new { filePath = "code_force.cs", nodeKind = "method", textPattern = "Second(int a)", replacement = "int Second() => 0;", force = true, dryRun = true, projectPath = fx.Csproj });
				
				return Verdict(taken?["change_count"]?.GetValue<int>() == 1
					&& bodied?["change_count"]?.GetValue<int>() == 1
					&& (bodied?["_caution"]?.GetValue<string>() ?? "").Contains("read as the name 'First'")
					&& arrayed?["change_count"]?.GetValue<int>() == 1
					&& (unforced?["message"]?.GetValue<string>() ?? "").Contains("read as the name 'Second'")
					&& (dryForce?["message"]?.GetValue<string>() ?? "").Contains("Nothing would be replaced")
					&& (taken?["_caution"]?.GetValue<string>() ?? "").Contains("read as the name 'First'")
					&& forced?["applied"]?.GetValue<bool>() == false
					&& forced?["change_count"]?.GetValue<int>() == 2
					&& Read("code_force.cs") == SourceOf("code_force"), new JsonArray(taken?.DeepClone(), forced?.DeepClone()), Read("code_force.cs"));
			}),
			
			// Five of the eight failures said "Replacement text contains syntax errors". The parser
			// knows which and where: here a semicolon is missing on line 3 of the replacement.
			new("edit safety: roslyn_replace_in_code quotes the parser with the line in the replacement text", async () => {
				
				var answer  = await InCode(new { filePath = "code_syntax.cs", nodeKind = "method", textPattern = "First", replacement = "int First()\n{\n\treturn 0\n}", projectPath = fx.Csproj });
				var details = answer?["details"]?.GetValue<string>() ?? "";
				
				return Verdict(details.Contains("line 3:") && details.Contains("CS1002") && Read("code_syntax.cs") == SourceOf("code_syntax"), answer, Read("code_syntax.cs"));
			}),
		};
		
		return new TestGroup($"Edit Safety ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); return Task.CompletedTask; });
	}
}
