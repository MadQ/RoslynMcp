using System.Text.Json.Nodes;

/// <summary>
///     #475: <c>roslyn_multi_edit</c> makes several edits in one call, and every position in the
///     call refers to the file as it was before the call. Nine of ten edits in the server's logs
///     were part of a run of edits, and the order agents make them in, top-down, is the order in
///     which a line number from the last read goes stale after the first edit.
/// </summary>
/// <remarks>
///     Every test has copies of the fixture of its own and compares whole files: "all or none" and
///     "each edit landed where the read said" are claims about the files, not about the answer.
///     The single edit tools run on the same cores since this change; their own groups
///     (<em>File Editing Tools</em>, <em>Edit Safety</em>) are what holds them to their behaviour.
/// </remarks>
static class MultiEditTests
{
	// Three methods, starting on lines 3, 8 and 13.
	const string Code  = "class A\n{\n\tint One()\n\t{\n\t\treturn 1;\n\t}\n\n\tint Two()\n\t{\n\t\treturn 2;\n\t}\n\n\tint Three()\n\t{\n\t\treturn 3;\n\t}\n}\n";
	const string Other = "class B\n{\n\tint value = 1;\n}\n";
	const string Notes = "alpha\nbeta\ngamma\n";
	
	// A props file no project imports (#245).
	const string Props = "<Project>\n  <PropertyGroup>\n    <Foo>1</Foo>\n  </PropertyGroup>\n</Project>\n";
	
	internal static TestGroup Build(TestContext ctx)
	{
		var fx = TestFixtures.NewMsBuildProject("MultiEdit");
		
		string CodeOf(string name)  => Code.Replace("class A", $"class A_{name}");
		string OtherOf(string name) => Other.Replace("class B", $"class B_{name}");
		
		foreach(var name in new[] { "mixed", "order", "overlap", "miss", "syntax", "halves", "dry", "several", "same", "edge", "op", "claim", "delete", "edges", "pairs" })
			fx.Write($"{name}_A.cs", CodeOf(name));
		
		fx.Write("tail.txt", "alpha");
		fx.Write("halves.props", Props);
		fx.Write("lost.props", Props);
		fx.Write("adjacent.cs", "class Adjacent\n{\n\tint One() => 1;\n\tint Two() => 2;\n}\n");
		
		var real   = fx.Write("real_A.cs", CodeOf("real"));
		var linked = false;
		
		try {
			
			File.CreateSymbolicLink(fx.PathOf("link_A.cs"), real);
			
			linked = true;
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
		fx.Write("mixed_B.cs", OtherOf("mixed"));
		fx.Write("mixed_notes.txt", Notes);
		fx.Write("miss_B.cs", OtherOf("miss"));
		
		string Read(string file) => File.ReadAllText(fx.PathOf(file));
		
		Task<JsonNode?> Batch(params object[] edits)        => ctx.CallToolJsonAsync("roslyn_multi_edit", new { edits, projectPath = fx.Csproj });
		Task<JsonNode?> DryBatch(params object[] edits)     => ctx.CallToolJsonAsync("roslyn_multi_edit", new { edits, dryRun = true, projectPath = fx.Csproj });
		
		static (bool, string) Verdict(bool pass, JsonNode? answer, string? file) => pass
			? (true, "PASS")
			: (false, $"FAIL  ({answer?.ToJsonString() ?? "null"}{(file is null ? "" : $"; file: '{file}'")})");
		
		// Which edits of an answer are ok, in order.
		static bool[] Ok(JsonNode? answer) => [..(answer?["edits"]?.AsArray() ?? []).Select(edit => edit?["ok"]?.GetValue<bool>() == true)];
		
		var tests = new List<TestCase> {
			
			// The acceptance case: five edits of the three kinds over three files in one call. They
			// are given so that single calls would fail: the insertion before line 13 comes
			// first and would move nothing above it, but the node edit of Two adds no line and
			// the text edit above it follows last. Every file must hold exactly its change, and
			// each has a token that restores it.
			new("multi edit: five edits of mixed kinds over three files are written in one call", async () => {
				
				var answer = await Batch(
					new { op = "roslyn_insert_lines", filePath = "mixed_A.cs", atLine = 13, text = "\t// before Three" },
					new { op = "roslyn_replace_in_code", filePath = "mixed_A.cs", nodeKind = "method", textPattern = "Two", replacement = "int Two()\n\t{\n\t\treturn 20;\n\t}" },
					new { op = "roslyn_replace_in_file", filePath = "mixed_A.cs", pattern = "return 1;", replacement = "return 10;" },
					new { op = "roslyn_replace_in_file", filePath = "mixed_B.cs", pattern = "value = 1", replacement = "value = 2" },
					new { op = "roslyn_insert_lines", filePath = "mixed_notes.txt", insertAfter = "beta", text = "delta" });
				
				var files    = answer?["files"]?.AsArray() ?? [];
				var expected = CodeOf("mixed").Replace("return 1;", "return 10;").Replace("return 2;", "return 20;").Replace("\tint Three()", "\t// before Three\n\tint Three()");
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true
					&& Ok(answer) is [true, true, true, true, true]
					&& files.Count == 3
					&& files.All(file => file?["backup_token"]?.GetValue<string>() is { Length: > 0 })
					&& Read("mixed_A.cs") == expected
					&& Read("mixed_B.cs") == OtherOf("mixed").Replace("value = 1", "value = 2")
					&& Read("mixed_notes.txt") == "alpha\nbeta\ndelta\ngamma\n", answer, Read("mixed_A.cs"));
			}),
			
			// Three insertions by line number, top-down: with single calls the second lands a line
			// early and the third two. In a batch every number is one of the file as it was read,
			// and the answer says where the lines are now.
			new("multi edit: line numbers of one read hold for every edit, in the order that goes stale", async () => {
				
				var answer = await Batch(
					new { op = "roslyn_insert_lines", filePath = "order_A.cs", atLine = 3,  text = "\t// one" },
					new { op = "roslyn_insert_lines", filePath = "order_A.cs", atLine = 8,  text = "\t// two" },
					new { op = "roslyn_insert_lines", filePath = "order_A.cs", atLine = 13, text = "\t// three" });
				
				var expected = CodeOf("order").Replace("\tint One()", "\t// one\n\tint One()").Replace("\tint Two()", "\t// two\n\tint Two()").Replace("\tint Three()", "\t// three\n\tint Three()");
				var ranges   = answer?["files"]?[0]?["changed_line_ranges"]?.AsArray() ?? [];
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true
					&& Read("order_A.cs") == expected
					&& ranges.Select(range => range?["start"]?.GetValue<int>()).SequenceEqual(new int?[] { 3, 9, 15 }), answer, Read("order_A.cs"));
			}),
			
			// Two edits that change the same text have no right order: each was written against
			// the text as it was. Both are named, and the file is as it was.
			new("multi edit: two edits that change the same text are an error, and nothing is written", async () => {
				
				var answer = await Batch(
					new { op = "roslyn_replace_in_code", filePath = "overlap_A.cs", nodeKind = "method", textPattern = "Two", replacement = "int Two() => 2;" },
					new { op = "roslyn_replace_in_file", filePath = "overlap_A.cs", pattern = "return 2;", replacement = "return 22;" });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == false
					&& Ok(answer) is [false, false]
					&& (answer?["edits"]?[0]?["detail"]?.GetValue<string>() ?? "").Contains("same text as edit 2")
					&& Read("overlap_A.cs") == CodeOf("overlap"), answer, Read("overlap_A.cs"));
			}),
			
			// One edit of three matches nothing. Nothing is written, in either file, the good edits
			// say ok, and the bad one carries the answer its own tool gives.
			new("multi edit: one edit that cannot be made stops the batch and is named", async () => {
				
				var answer = await Batch(
					new { op = "roslyn_replace_in_file", filePath = "miss_A.cs", pattern = "return 1;", replacement = "return 10;" },
					new { op = "roslyn_replace_in_code", filePath = "miss_A.cs", nodeKind = "method", textPattern = "Four", replacement = "int Four() => 4;" },
					new { op = "roslyn_replace_in_file", filePath = "miss_B.cs", pattern = "value = 1", replacement = "value = 2" });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == false
					&& Ok(answer) is [true, false, true]
					&& answer?["edits"]?[1]?["error"]?["candidates"]?.AsArray() is { Count: 3 }
					&& Read("miss_A.cs") == CodeOf("miss")
					&& Read("miss_B.cs") == OtherOf("miss"), answer, Read("miss_A.cs"));
			}),
			
			// A batch that leaves a C# file with a syntax error it did not have is refused, with
			// the parser's message under the file.
			new("multi edit: a batch that unbalances a brace is refused, quoting the parser", async () => {
				
				var answer = await Batch(
					new { op = "roslyn_replace_in_file", filePath = "syntax_A.cs", pattern = "return 1;", replacement = "return 10;" },
					new { op = "roslyn_replace_in_file", filePath = "syntax_A.cs", pattern = "\t\treturn 2;\n\t}", replacement = "\t\treturn 2;" });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == false
					&& (answer?["files"]?[0]?["error"]?.GetValue<string>() ?? "").Contains("syntax error")
					&& Read("syntax_A.cs") == CodeOf("syntax"), answer, Read("syntax_A.cs"));
			}),
			
			// The check is on the file as the whole batch leaves it: one edit opens a block and
			// another closes it. Each alone is refused by roslyn_replace_in_file; together they
			// are one sound change.
			new("multi edit: two edits that are each half of a change pass together", async () => {
				
				var answer = await Batch(
					new { op = "roslyn_replace_in_file", filePath = "halves_A.cs", pattern = "int One()\n\t{", replacement = "int One()\n\t{\n\t\tif(true) {" },
					new { op = "roslyn_replace_in_file", filePath = "halves_A.cs", pattern = "\t\treturn 1;\n\t}", replacement = "\t\t\treturn 1;\n\t\t}\n\n\t\treturn 0;\n\t}" });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true
					&& Read("halves_A.cs") == CodeOf("halves").Replace("int One()\n\t{\n\t\treturn 1;\n\t}", "int One()\n\t{\n\t\tif(true) {\n\t\t\treturn 1;\n\t\t}\n\n\t\treturn 0;\n\t}"), answer, Read("halves_A.cs"));
			}),
			
			// The first op is written without the tools' prefix: it is taken, and reported under
			// the tool's name, the only one an answer or a description ever shows (#326).
			new("multi edit: a dry run resolves and checks everything and writes nothing", async () => {
				
				var answer = await DryBatch(
					new { op = "replace_in_file", filePath = "dry_A.cs", pattern = "return 1;", replacement = "return 10;" },
					new { op = "roslyn_insert_lines", filePath = "dry_A.cs", atLine = 8, text = "\t// two" });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == false
					&& Ok(answer) is [true, true]
					&& answer?["files"]?[0]?["backup_token"] is null
					&& answer?["edits"]?[0]?["op"]?.GetValue<string>() == "roslyn_replace_in_file"
					&& (answer?["message"]?.GetValue<string>() ?? "").StartsWith("Dry run")
					&& Read("dry_A.cs") == CodeOf("dry"), answer, Read("dry_A.cs"));
			}),
			
			// An edit of a batch is held to its tool's rules (#476): a literal pattern that matches
			// three times is not made, and its answer lists the places.
			new("multi edit: a literal pattern that matches several times fails its edit, with the places", async () => {
				
				var answer = await Batch(new { op = "roslyn_replace_in_file", filePath = "several_A.cs", pattern = "return", replacement = "yield return" });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == false
					&& Ok(answer) is [false]
					&& answer?["edits"]?[0]?["error"]?["matches"]?.AsArray() is { Count: 3 }
					&& Read("several_A.cs") == CodeOf("several"), answer, Read("several_A.cs"));
			}),
			
			// Two insertions at one place are no overlap, and are written in the order given.
			new("multi edit: two insertions at one line keep the order they were given in", async () => {
				
				var answer = await Batch(
					new { op = "roslyn_insert_lines", filePath = "same_A.cs", atLine = 8, text = "\t// first" },
					new { op = "roslyn_insert_lines", filePath = "same_A.cs", atLine = 8, text = "\t// second" });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true
					&& Read("same_A.cs") == CodeOf("same").Replace("\tint Two()", "\t// first\n\t// second\n\tint Two()"), answer, Read("same_A.cs"));
			}),
			
			// A comment inserted before a method that the same batch replaces: the insertion is
			// at the edge of the replaced text, not inside it.
			new("multi edit: an insertion before a node the batch replaces is not an overlap", async () => {
				
				var answer = await Batch(
					new { op = "roslyn_insert_lines", filePath = "edge_A.cs", atLine = 8, text = "\t// about Two" },
					new { op = "roslyn_replace_in_code", filePath = "edge_A.cs", nodeKind = "method", textPattern = "Two", replacement = "int Two() => 2;" });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true
					&& Read("edge_A.cs") == CodeOf("edge").Replace("\tint Two()\n\t{\n\t\treturn 2;\n\t}", "\t// about Two\n\tint Two() => 2;"), answer, Read("edge_A.cs"));
			}),
			
			// A node edit lays claim to its node, not to the characters its text happens to differ
			// in: this replacement differs from the method in one character, far from the name the
			// text edit changes. Judged by the difference, both were written and the file held
			// 'long Two()' with a body nobody had seen beside it.
			new("multi edit: a text edit inside a node the batch replaces is an overlap, wherever it is", async () => {
				
				var answer = await Batch(
					new { op = "roslyn_replace_in_code", filePath = "claim_A.cs", nodeKind = "method", textPattern = "Two", replacement = "int Two()\n\t{\n\t\treturn 20;\n\t}" },
					new { op = "roslyn_replace_in_file", filePath = "claim_A.cs", pattern = "int Two()", replacement = "long Two()" });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == false
					&& Ok(answer) is [false, false]
					&& Read("claim_A.cs") == CodeOf("claim"), answer, Read("claim_A.cs"));
			}),
			
			// Deleting a method takes its lines, not the indentation of the next one: a line
			// inserted before that next method in the same batch does not touch the deletion.
			new("multi edit: a deleted node does not reach into the line after it", async () => {
				
				var answer = await Batch(
					new { op = "roslyn_replace_in_code", filePath = "delete_A.cs", nodeKind = "method", textPattern = "One", replacement = "" },
					new { op = "roslyn_insert_lines", filePath = "delete_A.cs", atLine = 8, text = "\t// two" });
				
				var content = Read("delete_A.cs");
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true
					&& !content.Contains("One")
					&& content.Contains("\t// two\n\tint Two()\n"), answer, content);
			}),
			
			// The edge rule itself: an insertion exactly where a replaced span starts goes before
			// it, and one exactly where a span ends goes behind it. Neither is inside.
			new("multi edit: insertions at the very start and the very end of a replaced span are written around it", async () => {
				
				var answer = await Batch(
					new { op = "roslyn_replace_in_file", filePath = "edges_A.cs", pattern = "\tint Two()\n", replacement = "\tlong Two()\n" },
					new { op = "roslyn_insert_lines", filePath = "edges_A.cs", atLine = 8, text = "\t// before" },
					new { op = "roslyn_insert_lines", filePath = "edges_A.cs", atLine = 9, text = "\t// behind" });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true
					&& Read("edges_A.cs") == CodeOf("edges").Replace("\tint Two()\n", "\t// before\n\tlong Two()\n\t// behind\n"), answer, Read("edges_A.cs"));
			}),
			
			// Two tangles in one file: all four edits are named in one answer, not the first pair
			// now and the second after the next call.
			new("multi edit: every overlap of a file is named, not the first", async () => {
				
				var answer = await Batch(
					new { op = "roslyn_replace_in_file", filePath = "pairs_A.cs", pattern = "return 1;", replacement = "return 10;" },
					new { op = "roslyn_replace_in_code", filePath = "pairs_A.cs", nodeKind = "method", textPattern = "One", replacement = "int One() => 1;" },
					new { op = "roslyn_replace_in_file", filePath = "pairs_A.cs", pattern = "return 3;", replacement = "return 30;" },
					new { op = "roslyn_replace_in_code", filePath = "pairs_A.cs", nodeKind = "method", textPattern = "Three", replacement = "int Three() => 3;" });
				
				return Verdict(Ok(answer) is [false, false, false, false] && Read("pairs_A.cs") == CodeOf("pairs"), answer, Read("pairs_A.cs"));
			}),
			
			// A file whose last line has no line break: two insertions at its end follow each
			// other, and the text ends as it ended. With a break before and behind each, a blank
			// line stood between them.
			new("multi edit: two insertions at the end of a file without a final line break", async () => {
				
				var answer = await Batch(
					new { op = "roslyn_insert_lines", filePath = "tail.txt", insertAfter = "alpha", text = "x" },
					new { op = "roslyn_insert_lines", filePath = "tail.txt", atLine = 99, text = "y" });
				
				// The lines written are 2 and 3; line 1 only got its line break.
				var range = answer?["files"]?[0]?["changed_line_ranges"]?.AsArray() is [var only] ? only : null;
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true
					&& Read("tail.txt") == "alpha\nx\ny"
					&& range?["start"]?.GetValue<int>() == 2 && range?["end"]?.GetValue<int>() == 3, answer, Read("tail.txt"));
			}),
			
			// Members on adjacent lines: taking the first out removes 'int One() => 1;\n\t' as the
			// texts differ, which reaches into the indentation of the second, or '\tint One() =>
			// 1;\n', the line. Only as the line does it leave an insertion before the second alone.
			new("multi edit: a node deleted from the line above does not take the next line's indentation", async () => {
				
				var answer = await Batch(
					new { op = "roslyn_replace_in_code", filePath = "adjacent.cs", nodeKind = "method", textPattern = "One", replacement = "" },
					new { op = "roslyn_insert_lines", filePath = "adjacent.cs", atLine = 4, text = "\t// two" });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true
					&& Read("adjacent.cs") == "class Adjacent\n{\n\t// two\n\tint Two() => 2;\n}\n", answer, Read("adjacent.cs"));
			}),
			
			// A file and a link to it are one file of the batch, whichever name an edit uses:
			// planned against one text and written once. As two files each was written whole,
			// and the second write took the first's edit out again.
			new("multi edit: edits through a file's name and through a link to it land in one file", async () => {
				
				if(!linked)
					
					return Skip.SetupUnavailable("this machine does not allow creating symbolic links");
				
				var answer = await Batch(
					new { op = "roslyn_replace_in_file", filePath = "real_A.cs", pattern = "return 1;", replacement = "return 10;" },
					new { op = "roslyn_replace_in_file", filePath = "link_A.cs", pattern = "return 3;", replacement = "return 30;" });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true
					&& answer?["files"]?.AsArray() is { Count: 1 }
					&& Read("real_A.cs") == CodeOf("real").Replace("return 1;", "return 10;").Replace("return 3;", "return 30;"), answer, Read("real_A.cs"));
			}),
			
			// A text that opens with a blank line, as a new member after another does: the blank
			// line is a line the batch wrote, and is reported.
			new("multi edit: an inserted text that begins with a blank line reports that line too", async () => {
				
				var answer = await DryBatch(new { op = "roslyn_insert_lines", filePath = "dry_A.cs", atLine = 8, text = "\n\t// two" });
				var range  = answer?["files"]?[0]?["changed_line_ranges"]?.AsArray() is [var only] ? only : null;
				
				return Verdict(range?["start"]?.GetValue<int>() == 8 && range?["end"]?.GetValue<int>() == 9, answer, null);
			}),
			
			// #245: a project's XML is checked as the batch leaves it. An element opened by one
			// edit and closed by another is sound; each alone is refused by the single tool.
			new("multi edit: two edits that open and close an XML element pass together, and a lost closing tag is refused", async () => {
				
				var halves = await Batch(
					new { op = "roslyn_insert_lines", filePath = "halves.props", atLine = 2, text = "  <ItemGroup>" },
					new { op = "roslyn_insert_lines", filePath = "halves.props", atLine = 5, text = "  </ItemGroup>" });
				
				var lost = await Batch(
					new { op = "roslyn_replace_in_file", filePath = "lost.props", pattern = "<Foo>1</Foo>", replacement = "<Foo>2</Foo>" },
					new { op = "roslyn_replace_in_file", filePath = "lost.props", pattern = "  </PropertyGroup>\n", replacement = "" });
				
				return Verdict(halves?["applied"]?.GetValue<bool>() == true
					&& Read("halves.props") == "<Project>\n  <ItemGroup>\n  <PropertyGroup>\n    <Foo>1</Foo>\n  </PropertyGroup>\n  </ItemGroup>\n</Project>\n"
					&& lost?["applied"]?.GetValue<bool>() == false
					&& (lost?["files"]?[0]?["error"]?.GetValue<string>() ?? "").Contains("PropertyGroup")
					&& Read("lost.props") == Props, new JsonArray(halves?.DeepClone(), lost?.DeepClone()), Read("halves.props"));
			}),
			
			new("multi edit: an unknown op fails its edit and names the three there are", async () => {
				
				var answer = await Batch(new { op = "write_file", filePath = "op_A.cs", text = "x" });
				
				return Verdict(Ok(answer) is [false]
					&& (answer?["edits"]?[0]?["detail"]?.GetValue<string>() ?? "").Contains("roslyn_replace_in_code")
					&& Read("op_A.cs") == CodeOf("op"), answer, Read("op_A.cs"));
			}),
		};
		
		return new TestGroup($"Multi Edit ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); return Task.CompletedTask; });
	}
}
