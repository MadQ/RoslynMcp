using System.Text.Json.Nodes;

static class MemberBodyTests
{
	// One partial type split over two files. Each part's text is distinct, so a part paired with
	// the wrong header is detectable.
	const string SplitA = "partial class Split\n{\n\tstring Left() => \"a\\\\b\";\n}";
	const string SplitB = "partial class Split\n{\n\t\n\tint Right() => 2;\n}";
	
	internal static TestGroup Build(TestContext ctx)
	{
		
		var fx = TestFixtures.NewAdhocDir("MemberBodyExact");
		
		fx.Write("SplitA.cs", SplitA + "\n");
		fx.Write("SplitB.cs", SplitB + "\n");
		
		var tests = new List<TestCase> {
			
			new("roslyn_get_member_body: single method",
				() => ctx.RunTestAsync(
					"roslyn_get_member_body",
					new { symbolName = "GetCompilation", containingType = "WorkspaceManager", projectPath = ctx.TargetPath },
					(data, source) => source is [var text]
						&& text.Contains("GetCompilation")
						&& data?["start_line"]?.GetValue<int>() > 0
						&& data?["symbol_kind"]?.GetValue<string>() == "method")),
			
			// After the response header, a partial declaration is a header block and a source block
			// per part — so the blocks must come in pairs and match the count the header announces.
			new("roslyn_get_member_body: partial class (multiple parts)",
				() => ctx.RunTestAsync(
					"roslyn_get_member_body",
					new { symbolName = "WorkspaceManager", projectPath = ctx.TargetPath },
					(data, blocks) => data?["parts"]?.GetValue<int>() is int parts
						&& parts > 1
						&& blocks.Length == parts * 2
						&& data?["note"]?.GetValue<string>().Contains("Partial") == true)),
			
			// ── #328: source is returned as raw text, not inside a JSON string ──────────────────────
			// A single declaration is one raw block holding whole lines, so the member keeps its
			// indentation and its quotes and backslashes arrive unescaped. Line 3 of SplitA.cs is the
			// method; the header must locate it and the block must equal that line plus the one
			// newline the server adds.
			new("roslyn_get_member_body: source block reproduces a single declaration exactly",
				() => ctx.RunTestAsync(
					"roslyn_get_member_body",
					new { symbolName = "Left", projectPath = fx.ProjectPath },
					(data, source) => source is [var text]
						&& text == "\tstring Left() => \"a\\\\b\";\n"
						&& data?["start_line"]?.GetValue<int>() == 3
						&& data?["end_line"]?.GetValue<int>() == 3
						&& data?["body"] is null)),
			
			// The client may join blocks with nothing between them, so the only thing that tells one
			// part from the next is its header line. Each header must therefore name the file whose
			// text follows it and the right line range; the order of the parts is the compiler's and
			// is not asserted. SplitB's part contains an indented blank line.
			new("roslyn_get_member_body: each part of a partial type follows its own header",
				() => ctx.RunTestAsync(
					"roslyn_get_member_body",
					new { symbolName = "Split", projectPath = fx.ProjectPath },
					(data, blocks) => {
						
						if(data?["parts"]?.GetValue<int>() != 2 || blocks.Length != 4)
							
							return false;
						
						var seen = new HashSet<string>();
						
						for(var i = 0; i < blocks.Length; i += 2) {
							
							// A header block is one line of JSON ending in a newline.
							if(!blocks[i].EndsWith('\n') || blocks[i].AsSpan(0, blocks[i].Length - 1).Contains('\n'))
								
								return false;
							
							var header   = JsonNode.Parse(blocks[i]);
							var file     = header?["file"]?.GetValue<string>() ?? "";
							var expected = file.EndsWith("SplitA.cs") ? SplitA : file.EndsWith("SplitB.cs") ? SplitB : null;
							
							if(expected is null
								|| blocks[i + 1] != expected + "\n"
								|| header?["part"]?.GetValue<int>() != i / 2 + 1
								|| header?["start_line"]?.GetValue<int>() != 1
								|| header?["end_line"]?.GetValue<int>() != expected.Count(c => c == '\n') + 1)
								
								return false;
							
							seen.Add(file);
						}
						
						return seen.Count == 2;
					})),
		};
		
		return new TestGroup($"Member Body Tools ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); return Task.CompletedTask; });
	}
}
