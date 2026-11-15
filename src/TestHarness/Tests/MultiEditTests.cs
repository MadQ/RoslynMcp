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
	
	internal static TestGroup Build(TestContext ctx)
	{
		var fx = TestFixtures.NewMsBuildProject("MultiEdit");
		
		string CodeOf(string name)  => Code.Replace("class A", $"class A_{name}");
		string OtherOf(string name) => Other.Replace("class B", $"class B_{name}");
		
		foreach(var name in new[] { "mixed", "order", "overlap", "miss", "syntax", "halves", "dry", "several", "same", "edge", "op" })
			fx.Write($"{name}_A.cs", CodeOf(name));
		
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
					new { op = "insert_lines", filePath = "mixed_A.cs", atLine = 13, text = "\t// before Three" },
					new { op = "replace_in_code", filePath = "mixed_A.cs", nodeKind = "method", textPattern = "Two", replacement = "int Two()\n\t{\n\t\treturn 20;\n\t}" },
					new { op = "replace_in_file", filePath = "mixed_A.cs", pattern = "return 1;", replacement = "return 10;" },
					new { op = "replace_in_file", filePath = "mixed_B.cs", pattern = "value = 1", replacement = "value = 2" },
					new { op = "insert_lines", filePath = "mixed_notes.txt", insertAfter = "beta", text = "delta" });
				
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
					new { op = "insert_lines", filePath = "order_A.cs", atLine = 3,  text = "\t// one" },
					new { op = "insert_lines", filePath = "order_A.cs", atLine = 8,  text = "\t// two" },
					new { op = "insert_lines", filePath = "order_A.cs", atLine = 13, text = "\t// three" });
				
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
					new { op = "replace_in_code", filePath = "overlap_A.cs", nodeKind = "method", textPattern = "Two", replacement = "int Two() => 2;" },
					new { op = "replace_in_file", filePath = "overlap_A.cs", pattern = "return 2;", replacement = "return 22;" });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == false
					&& Ok(answer) is [false, false]
					&& (answer?["edits"]?[0]?["detail"]?.GetValue<string>() ?? "").Contains("same text as edit 2")
					&& Read("overlap_A.cs") == CodeOf("overlap"), answer, Read("overlap_A.cs"));
			}),
			
			// One edit of three matches nothing. Nothing is written, in either file, the good edits
			// say ok, and the bad one carries the answer its own tool gives.
			new("multi edit: one edit that cannot be made stops the batch and is named", async () => {
				
				var answer = await Batch(
					new { op = "replace_in_file", filePath = "miss_A.cs", pattern = "return 1;", replacement = "return 10;" },
					new { op = "replace_in_code", filePath = "miss_A.cs", nodeKind = "method", textPattern = "Four", replacement = "int Four() => 4;" },
					new { op = "replace_in_file", filePath = "miss_B.cs", pattern = "value = 1", replacement = "value = 2" });
				
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
					new { op = "replace_in_file", filePath = "syntax_A.cs", pattern = "return 1;", replacement = "return 10;" },
					new { op = "replace_in_file", filePath = "syntax_A.cs", pattern = "\t\treturn 2;\n\t}", replacement = "\t\treturn 2;" });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == false
					&& (answer?["files"]?[0]?["error"]?.GetValue<string>() ?? "").Contains("syntax error")
					&& Read("syntax_A.cs") == CodeOf("syntax"), answer, Read("syntax_A.cs"));
			}),
			
			// The check is on the file as the whole batch leaves it: one edit opens a block and
			// another closes it. Each alone is refused by roslyn_replace_in_file; together they
			// are one sound change.
			new("multi edit: two edits that are each half of a change pass together", async () => {
				
				var answer = await Batch(
					new { op = "replace_in_file", filePath = "halves_A.cs", pattern = "int One()\n\t{", replacement = "int One()\n\t{\n\t\tif(true) {" },
					new { op = "replace_in_file", filePath = "halves_A.cs", pattern = "\t\treturn 1;\n\t}", replacement = "\t\t\treturn 1;\n\t\t}\n\n\t\treturn 0;\n\t}" });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true
					&& Read("halves_A.cs") == CodeOf("halves").Replace("int One()\n\t{\n\t\treturn 1;\n\t}", "int One()\n\t{\n\t\tif(true) {\n\t\t\treturn 1;\n\t\t}\n\n\t\treturn 0;\n\t}"), answer, Read("halves_A.cs"));
			}),
			
			new("multi edit: a dry run resolves and checks everything and writes nothing", async () => {
				
				var answer = await DryBatch(
					new { op = "replace_in_file", filePath = "dry_A.cs", pattern = "return 1;", replacement = "return 10;" },
					new { op = "insert_lines", filePath = "dry_A.cs", atLine = 8, text = "\t// two" });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == false
					&& Ok(answer) is [true, true]
					&& answer?["files"]?[0]?["backup_token"] is null
					&& (answer?["message"]?.GetValue<string>() ?? "").StartsWith("Dry run")
					&& Read("dry_A.cs") == CodeOf("dry"), answer, Read("dry_A.cs"));
			}),
			
			// An edit of a batch is held to its tool's rules (#476): a literal pattern that matches
			// three times is not made, and its answer lists the places.
			new("multi edit: a literal pattern that matches several times fails its edit, with the places", async () => {
				
				var answer = await Batch(new { op = "replace_in_file", filePath = "several_A.cs", pattern = "return", replacement = "yield return" });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == false
					&& Ok(answer) is [false]
					&& answer?["edits"]?[0]?["error"]?["matches"]?.AsArray() is { Count: 3 }
					&& Read("several_A.cs") == CodeOf("several"), answer, Read("several_A.cs"));
			}),
			
			// Two insertions at one place are no overlap, and are written in the order given.
			new("multi edit: two insertions at one line keep the order they were given in", async () => {
				
				var answer = await Batch(
					new { op = "insert_lines", filePath = "same_A.cs", atLine = 8, text = "\t// first" },
					new { op = "insert_lines", filePath = "same_A.cs", atLine = 8, text = "\t// second" });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true
					&& Read("same_A.cs") == CodeOf("same").Replace("\tint Two()", "\t// first\n\t// second\n\tint Two()"), answer, Read("same_A.cs"));
			}),
			
			// A comment inserted before a method that the same batch replaces: the insertion is
			// at the edge of the replaced text, not inside it.
			new("multi edit: an insertion before a node the batch replaces is not an overlap", async () => {
				
				var answer = await Batch(
					new { op = "insert_lines", filePath = "edge_A.cs", atLine = 8, text = "\t// about Two" },
					new { op = "replace_in_code", filePath = "edge_A.cs", nodeKind = "method", textPattern = "Two", replacement = "int Two() => 2;" });
				
				return Verdict(answer?["applied"]?.GetValue<bool>() == true
					&& Read("edge_A.cs") == CodeOf("edge").Replace("\tint Two()\n\t{\n\t\treturn 2;\n\t}", "\t// about Two\n\tint Two() => 2;"), answer, Read("edge_A.cs"));
			}),
			
			new("multi edit: an unknown op fails its edit and names the three there are", async () => {
				
				var answer = await Batch(new { op = "write_file", filePath = "op_A.cs", text = "x" });
				
				return Verdict(Ok(answer) is [false]
					&& (answer?["edits"]?[0]?["detail"]?.GetValue<string>() ?? "").Contains("replace_in_code")
					&& Read("op_A.cs") == CodeOf("op"), answer, Read("op_A.cs"));
			}),
		};
		
		return new TestGroup($"Multi Edit ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); return Task.CompletedTask; });
	}
}
