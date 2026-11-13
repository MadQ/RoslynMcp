// What a text search says about where each hit is (#460). roslyn_search_files answered file, line
// and text, and what an agent did next was semantic all the same: it wrote a keyword into the
// pattern to tell a declaration from a use, and read a guessed range or the whole file to see the
// member around a hit. Each hit now comes under the declaration it is in, with that declaration's
// lines and the start of its node.
//
// One file in which every kind of place occurs once, so each expectation below is one line of it:
//
//    1  using System;
//    3  namespace Fixture;
//    5  public class Basket
//    7      // A heading: counting.          (a comment of the class, a blank line above the method's)
//    9      /// <summary>Adds MARK one.</summary>
//   10      [Obsolete]
//   11      public int Add(int count)
//   13          return count + Step;
//   16      int Step = 1, Extra = 2;
static class SearchDeclarationTests
{
	const string Source =
		"using System;\n\nnamespace Fixture;\n\npublic class Basket\n{\n\t// A heading: counting.\n\n"
		+ "\t/// <summary>Adds MARK one.</summary>\n\t[Obsolete]\n\tpublic int Add(int count)\n\t{\n\t\treturn count + Step;\n\t}\n\n"
		+ "\tint Step = 1, Extra = 2;\n}\n";
	
	internal static TestGroup Build(TestContext ctx)
	{
		var fx = TestFixtures.NewAdhocDir("SearchDeclarations");
		
		fx.Write("Basket.cs", Source);
		
		var tests = new List<TestCase> {
			
			// One search with a hit in each kind of place. What must hold, per hit:
			//  - the using line is in no declaration;
			//  - the heading is the class's: a blank line parts it from the method's comment, so the
			//    method's lines would not hold it;
			//  - the doc comment, the signature and the body line are the method's, whose lines run
			//    from its doc comment (9) to its closing brace (14) and whose node starts at its
			//    attribute (10:2), the position roslyn_replace_in_code takes;
			//  - only the hit on the method's own name declares it, not the use of Step in its body;
			//  - the field declaration with two names is one declaration, and declares Step.
			// Case-sensitive, so that "Adds" in the comment is found by MARK and not by Add.
			new("search declarations: each hit is listed under the declaration it is in, with its lines and start", async () => {
				
				var answer = await ctx.CallToolJsonAsync("roslyn_search_files", new { pattern = @"Add\(|MARK|Step|System|heading", caseSensitive = true, projectPath = fx.Dir });
				var hits   = SearchHits.Of(answer);
				
				if(hits is null)
					return (false, $"FAIL  (no files in the answer: {answer?.ToJsonString() ?? "null"})");
				
				string Shown(int line) => hits.FirstOrDefault(hit => hit.Line == line) is { } hit
					? $"{hit.Declaration ?? "none"} {hit.Lines ?? "-"} {hit.At ?? "-"}{(hit.Declares ? " declares" : "")}"
					: "missing";
				
				(int Line, string Expected)[] expected = [
					(1,  "none - -"),
					(7,  "class Basket 5-17 5:1"),
					(9,  "method Add 9-14 10:2"),
					(11, "method Add 9-14 10:2 declares"),
					(13, "method Add 9-14 10:2"),
					(16, "field Step, Extra 16-16 16:2 declares"),
				];
				
				var wrong = expected.Where(item => Shown(item.Line) != item.Expected).Select(item => $"line {item.Line}: expected '{item.Expected}', got '{Shown(item.Line)}'").ToArray();
				
				if(wrong.Length > 0 || hits.Length != expected.Length)
					return (false, $"FAIL  ({hits.Length} hit(s); {string.Join("; ", wrong)})");
				
				// Grouping is what pays for the added fields: the three hits in the method must share
				// one group, and the file's path be written once.
				var groups = answer?["files"]?[0]?["in"]?.AsArray().Count;
				var files  = answer?["files"]?.AsArray().Count;
				
				return files == 1 && groups == 4
					? (true, "PASS  (6 hits in 4 groups of one file)")
					: (false, $"FAIL  ({files} file group(s), {groups} declaration group(s): {answer?.ToJsonString()})");
			}),
		};
		
		return new TestGroup($"Search Declarations ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); return Task.CompletedTask; });
	}
}
