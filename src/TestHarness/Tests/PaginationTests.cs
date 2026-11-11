using System.Text.Json.Nodes;

/// <summary>
///     Covers <c>page_token</c> as a cursor (#305). Before the fix a token named only the cached
///     result set: passing it back alone returned page 1 again, because the position still came
///     from <c>skip</c>, and the same token came back on every page — so an agent following the
///     documented "pass page_token to get the next page" looped on the first page forever. A
///     cached page also had a generic <c>items</c> shape instead of the tool's own.
///     <para>
///         These tests pin the contract an agent relies on, not the cache: a token alone advances
///         one page at the same page size; each page hands out a different token; the last page
///         hands out none; an explicit <c>skip</c> or <c>take</c> still wins; a token keeps
///         working after an edit has cleared the cache (the query is re-run from the token's
///         position); and an unreadable token behaves like no token.
///     </para>
///     <para>
///         The fixture is a throwaway MSBuild project under %TEMP% (#274) with five files that
///         each hold one <c>PAGEMARK</c> line, so <c>roslyn_search_files</c> has exactly five
///         matches: pages of two give 2 + 2 + 1, which exercises a first, a middle, and a last
///         page. Pages are compared by file name rather than by an assumed order — the order of
///         documents in a project is the workspace's business — so the assertions are that pages
///         are disjoint and together cover all five files. <c>Other.cs</c> has no marker; it is
///         the file the edit test touches, so the edit clears the cache without changing the
///         result set.
///     </para>
///     <para>
///         Query binding (#316) is checked with a changed primary pattern, an auxiliary file filter,
///         and a different project containing the same marker in differently named files. Each must
///         equal a fresh query at skip=2/take=2, both before and after a cache-clearing edit. A
///         list-files token is also passed to list-types: both cache string arrays, but must retain
///         their own result shape and values. Reusing the original token afterwards proves a
///         mismatch did not consume or replace its entry. An explicit default take=50 deliberately
///         keeps the token's size of two, documenting the omitted/default argument ambiguity.
///     </para>
/// </summary>
static class PaginationTests
{
	/// <param name="ctx">The harness's connection to the server.</param>
	/// <param name="serverLogDir">Directory the harness pointed ROSLYNMCP_LOG_PATH at.</param>
	/// <param name="serverLogGlob">File pattern of the server's log in that directory.</param>
	internal static TestGroup Build(TestContext ctx, string serverLogDir, string serverLogGlob)
	{
		
		var fx     = TestFixtures.NewMsBuildProject("Pagination");
		var csproj = fx.Csproj!;
		var otherProject = TestFixtures.NewMsBuildProject("PaginationOther");
		
		for(int i = 1; i <= 5; i++)
			fx.Write($"Page{i}.cs", $"// PAGEMARK {i}\nclass Page{i} {{ }}\n");
		
		for(int i = 1; i <= 5; i++)
			otherProject.Write($"Alternate{i}.cs", $"// PAGEMARK {i}\nclass Alternate{i} {{ }}\n");
		
		fx.Write("Other.cs", "class Other { }\n");
		
		// A project of its own for the diagnostics test: seven locals nobody uses, seven warnings.
		var diagnosed = TestFixtures.NewMsBuildProject("PaginationDiag");
		
		diagnosed.Write("Warned.cs", "class Warned\n{\n\tvoid Run()\n\t{\n" + string.Concat(Enumerable.Range(1, 7).Select(n => $"\t\tint unused{n};\n")) + "\t}\n}\n");
		
		// Captured by the earlier tests, consumed by the later ones — the tests run in order.
		string?   token1 = null;
		string?   token2 = null;
		string[]? page1  = null;
		string[]? page2  = null;
		
		// Calls a tool and returns the parsed JSON result, or null on a protocol error.
		Task<JsonNode?> Call(string tool, object args) => ctx.CallToolJsonAsync(tool, args);
		
		// The file names on one page, in the order returned. Null when the response does not have
		// the tool's own shape — a cached page used to come back as { items, total } instead.
		static string[]? Files(JsonNode? data)
		{
			
			if(data?["matches"] is not JsonArray matches || data["total_matches"]?.GetValue<int>() != 5)
				
				return null;
			
			return [..matches.Select(match => Path.GetFileName(match!["file"]!.GetValue<string>()))];
		}
		
		static string? Token(JsonNode? data) => data?["page_token"]?.GetValue<string>();
		
		static bool HasMore(JsonNode? data) => data?["has_more"]?.GetValue<bool>() == true;
		
		static (bool, string) Verdict(bool pass, string failure)
			=> pass ? (true, "PASS") : (false, $"FAIL  ({failure})");
		
		static string Show(string[]? files) => files is null ? "wrong shape" : string.Join(",", files);
		
		async Task<(bool, string)> CheckQueryBinding()
		{
			if(token1 is null)
				
				return (false, "FAIL  (no token captured from the first-page test)");
			
			foreach(var query in new[] {
				new { pattern = "^class Page", filePattern = (string?) null, projectPath = csproj },
				new { pattern = "PAGEMARK", filePattern = (string?) "Other.cs", projectPath = csproj },
				new { pattern = "PAGEMARK", filePattern = (string?) null, projectPath = otherProject.Csproj! }
			}) {
				
				var expected = await Call("roslyn_search_files", new { query.pattern, query.filePattern, query.projectPath, skip = 2, take = 2 });
				var actual = await Call("roslyn_search_files", new { query.pattern, query.filePattern, query.projectPath, page_token = token1 });
				
				if(expected?["matches"] is not JsonArray || actual?["matches"] is not JsonArray
					|| !JsonNode.DeepEquals(expected["matches"], actual["matches"])
					|| !JsonNode.DeepEquals(expected["total_matches"], actual["total_matches"]))
					
					return (false, $"FAIL  (query {query}: expected {expected}, got {actual})");
			}
			
			return (true, "PASS");
		}
		
		var tests = new List<TestCase> {
			
			new("pagination: first page of 2 returns a token and has_more",
				async () => {
					
					var data = await Call("roslyn_search_files", new { pattern = "PAGEMARK", take = 2, projectPath = csproj });
					
					page1  = Files(data);
					token1 = Token(data);
					
					return Verdict(page1?.Length == 2 && token1 is not null && HasMore(data),
						$"files={Show(page1)}, token={token1 ?? "null"}, has_more={HasMore(data)}");
				}),
			
			// The core of #305: no skip, no take — the token alone must advance, keep the page
			// size of 2, come back in the tool's own shape, and hand out a different token.
			new("pagination: token alone returns the next page at the same size, with a new token",
				async () => {
					
					if(token1 is null || page1 is null)
						
						return (false, "FAIL  (no token captured from the first-page test)");
					
					var data = await Call("roslyn_search_files", new { pattern = "PAGEMARK", page_token = token1, projectPath = csproj });
					
					page2  = Files(data);
					token2 = Token(data);
					
					return Verdict(
						page2?.Length == 2 && !page2.Intersect(page1).Any() && HasMore(data) && token2 is not null && token2 != token1,
						$"page1={Show(page1)}, page2={Show(page2)}, token1={token1}, token2={token2 ?? "null"}");
				}),
			
			// Five matches in pages of two leave one for the third page. Nothing follows it, so
			// there must be no token to follow — an agent stops on has_more=false or a null token.
			new("pagination: last page returns the remainder and no token",
				async () => {
					
					if(token2 is null || page1 is null || page2 is null)
						
						return (false, "FAIL  (no token captured from the second-page test)");
					
					var data  = await Call("roslyn_search_files", new { pattern = "PAGEMARK", page_token = token2, projectPath = csproj });
					var page3 = Files(data);
					var all   = page3 is null ? [] : page1.Concat(page2).Concat(page3).Distinct().ToArray();
					
					return Verdict(page3?.Length == 1 && all.Length == 5 && !HasMore(data) && Token(data) is null,
						$"page3={Show(page3)}, distinct files={all.Length}, token={Token(data) ?? "null"}");
				}),
			
			new("pagination: a result set that fits one page returns no token",
				async () => {
					
					var data  = await Call("roslyn_search_files", new { pattern = "PAGEMARK", take = 10, projectPath = csproj });
					var files = Files(data);
					
					return Verdict(files?.Length == 5 && !HasMore(data) && Token(data) is null,
						$"files={Show(files)}, token={Token(data) ?? "null"}");
				}),
			
			// An explicit skip is a deliberate jump and must win over the token's position; the
			// page size still comes from the token. skip=1 from the top yields items 2 and 3 —
			// the last of page 1 and the first of page 2.
			new("pagination: explicit skip overrides the token's position",
				async () => {
					
					if(token1 is null || page1 is null || page2 is null)
						
						return (false, "FAIL  (no pages captured from the earlier tests)");
					
					var data  = await Call("roslyn_search_files", new { pattern = "PAGEMARK", skip = 1, page_token = token1, projectPath = csproj });
					var files = Files(data);
					
					return Verdict(files is [var a, var b] && a == page1[1] && b == page2[0],
						$"expected {page1[1]},{page2[0]}; got {Show(files)}");
				}),
			
			new("pagination: explicit take overrides the token's page size",
				async () => {
					
					if(token1 is null || page2 is null)
						
						return (false, "FAIL  (no pages captured from the earlier tests)");
					
					var data  = await Call("roslyn_search_files", new { pattern = "PAGEMARK", take = 1, page_token = token1, projectPath = csproj });
					var files = Files(data);
					
					return Verdict(files is [var only] && only == page2[0] && HasMore(data),
						$"expected {page2[0]}; got {Show(files)}");
				}),
			
			new("pagination: explicit default take retains the token's page size",
				async () => {
					
					var data = await Call("roslyn_search_files", new { pattern = "PAGEMARK", take = 50, page_token = token1, projectPath = csproj });
					
					return Verdict(page2 is not null && Files(data)?.SequenceEqual(page2) == true,
						$"expected {Show(page2)}; got {Show(Files(data))}");
				}),
			
			new("pagination: cached token with changed pattern, filter or project re-runs the current query",
				CheckQueryBinding),
			
			new("pagination: string-array token from another tool keeps the current tool's shape and cursor",
				async () => {
					
					var first = await Call("roslyn_list_files", new { pattern = "*.cs", take = 2, projectPath = csproj });
					var token = Token(first);
					var expected = await Call("roslyn_list_types", new { skip = 2, take = 2, projectPath = csproj });
					var actual = await Call("roslyn_list_types", new { page_token = token, projectPath = csproj });
					
					return Verdict(token is not null && actual?["types"] is JsonArray { Count: 2 }
						&& JsonNode.DeepEquals(expected?["types"], actual["types"])
						&& actual["files"] is null && actual["skip"]?.GetValue<int>() == 2
						&& actual["take"]?.GetValue<int>() == 2,
						$"expected {expected}, got {actual}");
				}),
			
			new("pagination: mismatched calls leave the original cached query usable",
				async () => {
					
					var data = await Call("roslyn_search_files", new { pattern = "PAGEMARK", page_token = token1, projectPath = csproj });
					var next = Token(data);
					
					return Verdict(page2 is not null && Files(data)?.SequenceEqual(page2) == true && next == token2,
						$"expected {Show(page2)} and token {token2}; got {Show(Files(data))} and {next}");
				}),
			
			// Every edit clears the pagination cache. The token carries its position, so the query
			// is re-run and the same second page comes back instead of an error or page 1.
			new("pagination: token still returns the next page after an edit cleared the cache",
				async () => {
					
					if(token1 is null || page2 is null)
						
						return (false, "FAIL  (no pages captured from the earlier tests)");
					
					var edit = await Call("roslyn_insert_lines", new { filePath = "Other.cs", atLine = 1, text = "// touched", projectPath = csproj });
					
					if(edit?["applied"]?.GetValue<bool>() != true)
						
						return (false, "FAIL  (the cache-clearing edit was not applied)");
					
					var data  = await Call("roslyn_search_files", new { pattern = "PAGEMARK", page_token = token1, projectPath = csproj });
					var files = Files(data);
					
					return Verdict(files is not null && files.SequenceEqual(page2) && Token(data) is not null,
						$"expected {Show(page2)}; got {Show(files)}");
				}),
			
			// A token that is not a cursor at all is treated as absent: first page, default size.
			new("pagination: unreadable token behaves like no token",
				async () => {
					
					var data  = await Call("roslyn_search_files", new { pattern = "PAGEMARK", page_token = "not-a-cursor", projectPath = csproj });
					var files = Files(data);
					
					return Verdict(files?.Length == 5 && !HasMore(data), $"files={Show(files)}");
				}),
			
			new("pagination: evicted token with changed pattern, filter or project matches the cached-mismatch behavior",
				CheckQueryBinding),
			
			// roslyn_get_diagnostics pages like every other tool (#435). Its token was a bare
			// position of its own: every page computed the whole result again, and with analyzers
			// that is every analyzer again, twenty seconds a page on a solution with analyzer
			// packages. The token did not carry the page size either, so a token passed alone fell
			// back to pages of fifty. The fixture has seven warnings (seven locals never used) and
			// is paged in threes with analyzers on: 3 + 3 + 1. Every page after the first is asked
			// for with the token alone, must keep the size of three, and must be served from the
			// cache, which the server's log says for each call; together the pages must hold every
			// warning once.
			new("pagination: roslyn_get_diagnostics pages from one analyzer run, and its token keeps the page size",
				async () => {
					
					static int CacheHits(string[] log)
						=> log.Count(line => line.Contains("\"get_diagnostics\"", StringComparison.Ordinal) && line.Contains("\"HIT\"", StringComparison.Ordinal));
					
					if(!ServerLog.TryRead(serverLogDir, serverLogGlob, 1, out var before, out var logFailure))
						
						return (false, logFailure);
					
					var query = new { projectPath = diagnosed.Csproj, includeAnalyzers = true, severity = "warnings" };
					var first = await Call("roslyn_get_diagnostics", new { query.projectPath, query.includeAnalyzers, query.severity, take = 3 });
					var total = first?["total"]?.GetValue<int>() ?? -1;
					var seen  = new List<string>();
					var sizes = new List<int>();
					var pages = 0;
					
					for(var page = first; page is not null && pages < 10; pages++) {
						
						var items = page["items"]?.AsArray() ?? [];
						
						sizes.Add(items.Count);
						seen.AddRange(items.Select(item => $"{item?["code"]}@{item?["line"]}"));
						
						if(Token(page) is not { } token)
							break;
						
						page = await Call("roslyn_get_diagnostics", new { query.projectPath, query.includeAnalyzers, query.severity, page_token = token });
					}
					
					if(!ServerLog.TryRead(serverLogDir, serverLogGlob, 1, out var after, out logFailure))
						
						return (false, logFailure);
					
					var hits = CacheHits(after) - CacheHits(before);
					
					return Verdict(
						total == 7
							&& sizes.SequenceEqual([3, 3, 1])
							&& seen.Distinct().Count() == 7
							&& hits == 2,
						$"total {total}, expected 7; page sizes [{string.Join(", ", sizes)}], expected [3, 3, 1]; distinct findings {seen.Distinct().Count()} of {seen.Count}; pages served from the cache: {hits}, expected 2");
				}),
		};
		
		return new TestGroup($"Pagination Cursor ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); otherProject.Dispose(); diagnosed.Dispose(); return Task.CompletedTask; });
	}
}
