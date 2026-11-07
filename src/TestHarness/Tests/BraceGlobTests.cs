using System.Text.Json.Nodes;

/// <summary>
///     Brace alternatives in globs (#294). <c>roslyn_list_files</c> advertised <c>{a,b}</c>, with
///     <c>*.{cs,csproj}</c> as its own example, but handed the pattern to <c>FileSystemGlobbing.Matcher</c>,
///     which has no brace expansion: every brace pattern matched nothing, with no error and no hint, and an
///     agent concluded the files did not exist. The <c>filePattern</c> of the three content-search tools
///     (<c>GlobMatcher</c>) had the same gap. Both now expand the pattern first (<c>GlobBraces</c>) and match
///     its expansions as alternatives.
///     <para>
///         The fixture is a throwaway MSBuild project (#274), written before the first tool call. Five C#
///         files whose names the patterns choose between, each holding the string literal
///         <c>"brace-marker"</c>, so the three search tools find one match per file they search and the set of
///         files they report is exactly the set the <c>filePattern</c> selected. Next to them: a text file
///         (<c>notes.txt</c>, which <c>*.{cs,csproj}</c> must leave out), a file whose name holds braces
///         (<c>{x}.txt</c>, for the literal case), and two directories, <c>bin/</c> (ignored, so listed only
///         when a pattern names it) and <c>docs/</c>.
///     </para>
/// </summary>
static class BraceGlobTests
{
	static readonly string[] CsFiles = ["Program.cs", "Probe.cs", "Workspace.cs", "WorkspaceManager.cs", "Other.cs"];
	
	internal static TestGroup Build(TestContext ctx)
	{
		var fx     = TestFixtures.NewMsBuildProject("BraceGlob");
		var csproj = fx.Csproj!;
		
		foreach(var file in CsFiles)
			fx.Write(file, $"class {Path.GetFileNameWithoutExtension(file)} {{ const string Marker = \"brace-marker\"; }}\n");
		
		fx.Write("notes.txt",      "notes\n");
		fx.Write("{x}.txt",        "a file whose name holds braces\n");
		fx.Write("bin/keep.txt",   "in an ignored directory\n");
		fx.Write("docs/readme.md", "docs\n");
		
		// Calls a tool and returns the parsed JSON of its first content block, or null on a protocol error.
		async Task<JsonNode?> Call(string tool, object args)
		{
			
			await ctx.SendAsync(new { jsonrpc = "2.0", id = ctx.NextId(), method = "tools/call",
				@params = new { name = tool, arguments = args } });
			
			var resp = await ctx.ReceiveAsync();
			var text = resp?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
			
			if(resp?["error"] is not null || string.IsNullOrEmpty(text))
				
				return null;
			
			try {
				return JsonNode.Parse(text);
			}
			catch {
				return null;
			}
		}
		
		// The paths roslyn_list_files returns, forward-slashed; null when the call failed.
		async Task<string[]?> ListAsync(string pattern)
		{
			
			var data = await Call("roslyn_list_files", new { projectPath = csproj, pattern, take = 500 });
			
			return data?["files"] is JsonArray files ? [.. files.Select(f => f!.GetValue<string>().Replace('\\', '/'))] : null;
		}
		
		// The distinct file names a search tool reported matches in, sorted; null when the call failed.
		async Task<string[]?> SearchedFilesAsync(string tool, object args)
		{
			
			var data = await Call(tool, args);
			
			return data?["matches"] is JsonArray matches
				? [.. matches.Select(m => Path.GetFileName(m!["file"]!.GetValue<string>())).Distinct().Order(StringComparer.Ordinal)]
				: null;
		}
		
		// Exactly these paths, in any order — a missing one and an extra one are both failures.
		static (bool pass, string message) Exactly(string[]? actual, string[] expected, string what)
		{
			if(actual is null)
				
				return (false, $"FAIL  ({what}: the call failed or returned no list)");
			
			var missing = expected.Except(actual).ToArray();
			var extra   = actual.Except(expected).ToArray();
			
			return missing.Length == 0 && extra.Length == 0
				? (true,  $"PASS  ({what}: {string.Join(", ", actual.Order(StringComparer.Ordinal))})")
				: (false, $"FAIL  ({what}: missing [{string.Join(", ", missing)}], unexpected [{string.Join(", ", extra)}])");
		}
		
		var csprojName = Path.GetFileName(csproj);
		string[] programAndOther = ["Other.cs", "Program.cs"];
		
		var tests = new List<TestCase> {
			
			// The issue's own example, from the tool's description: two extensions in one group.
			// notes.txt and {x}.txt are the controls — neither extension.
			new("brace globs: list_files '*.{cs,csproj}' lists both extensions",
				async () => Exactly(await ListAsync("*.{cs,csproj}"), [.. CsFiles, csprojName], "*.{cs,csproj}")),
			
			// The issue's second repro: a wildcard inside one alternative, a plain name in the other.
			// Probe.cs and Other.cs are the controls.
			new("brace globs: list_files '{Workspace*,Program}.cs' picks among names",
				async () => Exactly(await ListAsync("{Workspace*,Program}.cs"), ["Program.cs", "Workspace.cs", "WorkspaceManager.cs"], "{Workspace*,Program}.cs")),
			
			// A group inside a group expands to all three names; Workspace*.cs stays out.
			new("brace globs: list_files expands a nested group",
				async () => Exactly(await ListAsync("{Other,{Program,Probe}}.cs"), ["Other.cs", "Probe.cs", "Program.cs"], "{Other,{Program,Probe}}.cs")),
			
			// A directory one alternative names is entered even when ignored, as 'bin/**' alone is
			// (#309): without expanding before NamedDirectories, '{bin,docs}' reads as a wildcard and
			// bin/ stays pruned, so only docs/readme.md would come back.
			new("brace globs: list_files enters an ignored directory one alternative names",
				async () => Exactly(await ListAsync("{bin,docs}/**/*"), ["bin/keep.txt", "docs/readme.md"], "{bin,docs}/**/*")),
			
			// A brace with no comma is a literal character, as in a shell, so a file whose name
			// holds braces can still be named exactly.
			new("brace globs: list_files '{x}.txt' matches the file named so",
				async () => Exactly(await ListAsync("{x}.txt"), ["{x}.txt"], "{x}.txt")),
			
			// An empty alternative is the shell's optional part: 'Probe{,.bak}.cs' is Probe.cs or
			// Probe.bak.cs. '{,}' expands to nothing but empty patterns, which name no file: an empty
			// listing, not an error and not every file.
			new("brace globs: an empty alternative is an optional part",
				async () => {
					
					var optional = Exactly(await ListAsync("Probe{,.bak}.cs"), ["Probe.cs"], "Probe{,.bak}.cs");
					
					if(!optional.pass)
						
						return optional;
					
					return Exactly(await ListAsync("{,}"), [], "{,}");
				}),
			
			// Nine two-way groups are 512 patterns, past the cap of 256: an error naming the cap,
			// not a silent match of part of what was asked.
			new("brace globs: a pattern past the expansion cap is an error",
				async () => {
					
					var data  = await Call("roslyn_list_files", new { projectPath = csproj, pattern = string.Concat(Enumerable.Repeat("{a,b}", 9)) });
					var error = data?["error"]?.GetValue<string>();
					
					return error is not null && error.Contains("256", StringComparison.Ordinal)
						? (true,  "PASS  (refused, naming the cap)")
						: (false, $"FAIL  (expected an error naming the cap of 256, got: {data?.ToJsonString() ?? "no response"})");
				}),
			
			// The filePattern of the three content searches (GlobMatcher). Each of the five files
			// holds one match, so the files reported are exactly the files the pattern selected.
			new("brace globs: search_files filePattern '{Program,Other}.cs'",
				async () => Exactly(
					await SearchedFilesAsync("roslyn_search_files", new { projectPath = csproj, pattern = "brace-marker", filePattern = "{Program,Other}.cs" }),
					programAndOther, "search_files")),
			
			new("brace globs: semantic_search filePattern '{Program,Other}.cs'",
				async () => Exactly(
					await SearchedFilesAsync("roslyn_semantic_search", new { projectPath = csproj, pattern = "brace-marker", context = "strings", filePattern = "{Program,Other}.cs" }),
					programAndOther, "semantic_search")),
			
			new("brace globs: find_string_literal filePattern '{Program,Other}.cs'",
				async () => Exactly(
					await SearchedFilesAsync("roslyn_find_string_literal", new { projectPath = csproj, pattern = "brace-marker", filePattern = "{Program,Other}.cs" }),
					programAndOther, "find_string_literal")),
		};
		
		return new TestGroup($"Brace Globs ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); return Task.CompletedTask; });
	}
}
