using System.Text.Json.Nodes;

static class FindStringLiteralTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		// Captured by the pagination-page-1 test; consumed by pagination-page-2.
		string? paginationToken = null;
		string? page1FirstFile  = null;
		int?    page1FirstLine  = null;

		var tests = new List<TestCase> {

			// -- Basic result shape --------------------------------------------------------

			new("roslyn_find_string_literal: returns matches with all required fields",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					new { pattern = "roslyn_info", take = 5, projectPath = ctx.TargetPath },
					data => data?["total_matches"]?.GetValue<int>() > 0
						&& data?["matches"]?[0]?["file"]   is not null
						&& data?["matches"]?[0]?["line"]   is not null
						&& data?["matches"]?[0]?["column"] is not null
						&& data?["matches"]?[0]?["text"]   is not null
						&& data?["matches"]?[0]?["value"]  is not null)),

			new("roslyn_find_string_literal: text is raw source (has quotes), value is decoded",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					// filePattern ensures we get a known regular string literal, not interpolated parts.
					new { pattern = "roslyn_info", filePattern = "*InfoTool.cs", take = 1, projectPath = ctx.TargetPath },
					data => {
						var text  = data?["matches"]?[0]?["text"]?.GetValue<string>();
						var value = data?["matches"]?[0]?["value"]?.GetValue<string>();

						return text is not null && value is not null
							&& text.Contains('"') && !value.Contains('"') && text != value;
					})),

			new("roslyn_find_string_literal: no matches returns empty array without error",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					// filePattern avoids matching "XYZZY..." as a literal in this test file itself.
				new { pattern = "XYZZY_IMPOSSIBLE_PATTERN_12345_ZXYWQV", filePattern = "*InfoTool.cs", projectPath = ctx.TargetPath },
					data => data?["total_matches"]?.GetValue<int>() == 0
						&& data?["matches"]?.AsArray().Count == 0)),

			// -- caseSensitive -------------------------------------------------------------

			new("roslyn_find_string_literal: caseSensitive=false (default) — uppercase pattern matches",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					new { pattern = "ROSLYN_INFO", caseSensitive = false, take = 5, projectPath = ctx.TargetPath },
					data => data?["total_matches"]?.GetValue<int>() > 0)),

			new("roslyn_find_string_literal: caseSensitive=true — uppercase pattern finds nothing",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					// filePattern avoids matching "ROSLYN_INFO" literals in this test file.
				new { pattern = "ROSLYN_INFO", caseSensitive = true, filePattern = "*InfoTool.cs", projectPath = ctx.TargetPath },
					data => data?["total_matches"]?.GetValue<int>() == 0)),

			new("roslyn_find_string_literal: caseSensitive=true — exact case matches",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					new { pattern = "roslyn_info", caseSensitive = true, take = 5, projectPath = ctx.TargetPath },
					data => data?["total_matches"]?.GetValue<int>() > 0)),

			// -- useGlob ------------------------------------------------------------------

			new("roslyn_find_string_literal: useGlob=true — star wildcard finds all tool name strings",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					new { pattern = "roslyn_*", useGlob = true, take = 10, projectPath = ctx.TargetPath },
					data => data?["total_matches"]?.GetValue<int>() > 30)),

			// ── Regression: unified match-mode enum (#253) ──
			// mode:"glob" is the preferred replacement for useGlob=true and must produce the SAME
			// wildcard behavior. Mirrors the useGlob star test above; equal result count proves parity.
			new("roslyn_find_string_literal: mode=glob — star wildcard matches (parity with useGlob)",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					new { pattern = "roslyn_*", mode = "glob", take = 10, projectPath = ctx.TargetPath },
					data => data?["error"] is null && data?["total_matches"]?.GetValue<int>() > 30)),

			// The deprecated useGlob alias still works but must surface a _caution pointing to mode.
			new("roslyn_find_string_literal: deprecated useGlob emits _caution",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					new { pattern = "roslyn_*", useGlob = true, take = 5, projectPath = ctx.TargetPath },
					data => data?["_caution"]?.GetValue<string>()?.Contains("deprecated") == true)),

			// mode:"literal" seeks the pattern verbatim — the trailing '*' is a real character, not a
			// wildcard. No tool source string literal is literally "roslyn_search_files*" (with an
			// asterisk), so literal mode finds NOTHING. filePattern scopes to *Tool.cs to exclude this
			// test file, which itself contains the pattern as a string literal. The paired glob test
			// below runs the SAME pattern over the SAME files and DOES match the "roslyn_search_files"
			// tool-name literal — the 0-vs->0 contrast proves literal and glob are honored distinctly.
			new("roslyn_find_string_literal: mode=literal treats '*' as a literal character (no match)",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					new { pattern = "roslyn_search_files*", mode = "literal", filePattern = "*Tool.cs", take = 50, projectPath = ctx.TargetPath },
					data => data?["error"] is null && data?["total_matches"]?.GetValue<int>() == 0)),

			new("roslyn_find_string_literal: mode=glob wildcard matches where literal did not",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					new { pattern = "roslyn_search_files*", mode = "glob", filePattern = "*Tool.cs", take = 50, projectPath = ctx.TargetPath },
					data => data?["error"] is null && data?["total_matches"]?.GetValue<int>() > 0)),

			new("roslyn_find_string_literal: useGlob=true — question-mark wildcard",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					new { pattern = "roslyn_inf?", useGlob = true, take = 5, projectPath = ctx.TargetPath },
					data => data?["total_matches"]?.GetValue<int>() > 0)),

			new("roslyn_find_string_literal: useGlob=true, caseSensitive=false — uppercase glob matches",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					new { pattern = "ROSLYN_*", useGlob = true, caseSensitive = false, take = 10, projectPath = ctx.TargetPath },
					data => data?["total_matches"]?.GetValue<int>() > 30)),

			new("roslyn_find_string_literal: useGlob=true, caseSensitive=true — uppercase glob finds nothing",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					// filePattern avoids matching "ROSLYN_*" literals in this test file.
				new { pattern = "ROSLYN_*", useGlob = true, caseSensitive = true, filePattern = "*InfoTool.cs", projectPath = ctx.TargetPath },
					data => data?["total_matches"]?.GetValue<int>() == 0)),

			// -- matchRaw -----------------------------------------------------------------

			new("roslyn_find_string_literal: matchRaw=false (default) — pattern with quotes finds no decoded values",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					// InfoTool.cs has "roslyn_info" whose decoded value is roslyn_info (no quotes), so
					// the regex "roslyn_info" (with literal quote chars) does not match the decoded value.
					// Scoped to avoid matching escaped-quote string literals like "\"roslyn_info\"" in this test file
					// (their decoded value is "roslyn_info" which would match).
					new { pattern = "\"roslyn_info\"", matchRaw = false, filePattern = "*InfoTool.cs", projectPath = ctx.TargetPath },
					data => data?["total_matches"]?.GetValue<int>() == 0)),

			new("roslyn_find_string_literal: matchRaw=true — pattern with quotes matches raw source text",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					// Raw Token.Text of "roslyn_info" is "\"roslyn_info\"" — the regex matches it.
					new { pattern = "\"roslyn_info\"", matchRaw = true, take = 5, projectPath = ctx.TargetPath },
					data => data?["total_matches"]?.GetValue<int>() > 0)),

			new("roslyn_find_string_literal: matchRaw=true, caseSensitive=false — uppercase quoted matches",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					new { pattern = "\"ROSLYN_INFO\"", matchRaw = true, caseSensitive = false, take = 5, projectPath = ctx.TargetPath },
					data => data?["total_matches"]?.GetValue<int>() > 0)),

			new("roslyn_find_string_literal: matchRaw=true, caseSensitive=true — uppercase quoted finds nothing",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					// filePattern avoids matching "\"ROSLYN_INFO\"" literals in this test file.
				new { pattern = "\"ROSLYN_INFO\"", matchRaw = true, caseSensitive = true, filePattern = "*InfoTool.cs", projectPath = ctx.TargetPath },
					data => data?["total_matches"]?.GetValue<int>() == 0)),

			new("roslyn_find_string_literal: matchRaw=true, useGlob=true — glob on raw text",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					// Glob "roslyn_*" against raw text "roslyn_info" (with outer quotes) → matches.
					new { pattern = "\"roslyn_*\"", matchRaw = true, useGlob = true, take = 10, projectPath = ctx.TargetPath },
					data => data?["total_matches"]?.GetValue<int>() > 0)),

			// -- filePattern --------------------------------------------------------------

			new("roslyn_find_string_literal: filePattern restricts matches to matching files only",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					new { pattern = "roslyn_info", filePattern = "*InfoTool.cs", take = 20, projectPath = ctx.TargetPath },
					data => data?["total_matches"]?.GetValue<int>() > 0
						&& data?["matches"]?.AsArray()
							.All(m => m?["file"]?.GetValue<string>()?.Contains("InfoTool") == true) == true)),

			new("roslyn_find_string_literal: filePattern with no matching filename returns empty",
				() => ctx.RunTestAsync(
					"roslyn_find_string_literal",
					new { pattern = "roslyn_info", filePattern = "*NONEXISTENT_IMPOSSIBLE_XYZ.cs", projectPath = ctx.TargetPath },
					data => data?["total_matches"]?.GetValue<int>() == 0)),

			// -- Pagination ---------------------------------------------------------------

			new("roslyn_find_string_literal: pagination — take=2 returns page_token and has_more=true",
				async () => {
					var (pass, msg) = await ctx.RunTestAsync(
						"roslyn_find_string_literal",
						new { pattern = "roslyn_*", useGlob = true, take = 2, projectPath = ctx.TargetPath },
						data => {
							
							if(data?["has_more"]?.GetValue<bool>() != true)          return false;
							if(data?["page_token"]?.GetValue<string>() is null)      return false;
							if(data?["matches"]?.AsArray().Count != 2)               return false;

							paginationToken = data?["page_token"]?.GetValue<string>();
							page1FirstFile  = data?["matches"]?[0]?["file"]?.GetValue<string>();
							page1FirstLine  = data?["matches"]?[0]?["line"]?.GetValue<int>();

							return true;
						}
					);

					return (pass, msg);
				}),

			new("roslyn_find_string_literal: pagination — page_token fetches next page with different matches",
				async () => {
					
					if(paginationToken is null)
						return (false, "FAIL  (no page_token captured from page-1 test)");

					var (pass, msg) = await ctx.RunTestAsync(
						"roslyn_find_string_literal",
						new { pattern = "roslyn_*", useGlob = true, take = 2, page_token = paginationToken, projectPath = ctx.TargetPath },
						data => {
							
							if(data?["matches"]?.AsArray().Count == 0) return false;

							// Page 2 must contain different results than page 1.
							var file2 = data?["matches"]?[0]?["file"]?.GetValue<string>();
							var line2 = data?["matches"]?[0]?["line"]?.GetValue<int>();

							return file2 != page1FirstFile || line2 != page1FirstLine;
						}
					);

					return (pass, msg);
				}),
		};

		return new TestGroup($"String Literal Search ({tests.Count} tests)", tests);
	}
}
