using System.Text;
using System.Text.Json.Nodes;

/// <summary>
///     #327: <c>roslyn_search_files</c> reaches the files that are not C# source. Until then it
///     searched the workspace's documents only, and a search for <c>TargetFramework</c> in a
///     project file was the one thing an agent had to leave the server for. A <c>filePattern</c>
///     that is not limited to <c>.cs</c> names now also searches the other text files under the
///     root, from disk, line by line.
/// </summary>
/// <remarks>
///     One fixture project, each file in it one case:
///     <list type="bullet">
///         <item><c>Code.cs</c>: a document of the workspace, found with its declaration as before.</item>
///         <item><c>notes.md</c>, the <c>.csproj</c>: plain disk files.</item>
///         <item><c>tracked.txt</c>: a declared <c>AdditionalFiles</c> item, which the workspace holds.</item>
///         <item><c>obj/ignored.md</c>: in a directory no listing enters.</item>
///         <item><c>data.bin</c>: a NUL byte before the text; <c>mini.json</c>: one line past the line limit.</item>
///         <item><c>long.txt</c>: a line of 2,000 characters, the match at 1,500.</item>
///         <item><c>breaks.txt</c>: every line break <c>SourceText</c> knows, and a byte that is not UTF-8.</item>
///         <item><c>out.link</c>: a symbolic link to a file outside the workspace.</item>
///         <item><c>slow.txt</c>: a line a nested quantifier backtracks on without end.</item>
///     </list>
/// </remarks>
static class SearchTextFilesTests
{
	// TextLineReader.LineLimit, duplicated on purpose: the harness checks the contract from outside.
	const int LineLimit = 1_000_000;
	
	internal static TestGroup Build(TestContext ctx)
	{
		var fx      = TestFixtures.NewMsBuildProject("SearchText", extraProjectXml: """<ItemGroup><AdditionalFiles Include="tracked.txt" /></ItemGroup>""");
		var outside = TestFixtures.NewAdhocDir("SearchTextOutside");
		
		fx.Write("Code.cs", "class Code\n{\n\tstring needle = \"x\";\n}\n");
		fx.Write("notes.md", "# Title\n\nA needle here.\n");
		fx.Write("tracked.txt", "tracked needle\n");
		fx.Write("obj/ignored.md", "an ignored needle\n");
		fx.Write("long.txt", new string('a', 1500) + "needle" + new string('b', 494) + "\n");
		fx.Write("mini.json", "{\"needle\":\"" + new string('x', LineLimit) + "\"}");
		
		fx.Write("slow.txt", new string('a', 5000) + "!\n");
		
		File.WriteAllBytes(fx.PathOf("data.bin"), [0x4D, 0x5A, 0x00, 0x00, ..Encoding.UTF8.GetBytes("needle\n")]);
		
		// Six breaks, each of another kind, so the file has seven lines; StreamReader.ReadLine
		// would see four. 0xE9 alone is not UTF-8 ('é' in Windows-1252).
		var nextLine           = (char) 0x0085;
		var lineSeparator      = (char) 0x2028;
		var paragraphSeparator = (char) 0x2029;
		
		File.WriteAllBytes(fx.PathOf("breaks.txt"), [
			..Encoding.UTF8.GetBytes($"one\rtwo{lineSeparator}three{nextLine}four needle{paragraphSeparator}five caf"),
			0xE9,
			..Encoding.UTF8.GetBytes("\r\nsix\n")]);
		
		var outsideReal = outside.Write("Outside.md", "a needle outside\n");
		var linked      = false;
		
		try {
			
			File.CreateSymbolicLink(fx.PathOf("out.link"), outsideReal);
			
			linked = true;
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
		
		Task<JsonNode?> Search(string pattern, string? filePattern) => filePattern is null
			? ctx.CallToolJsonAsync("roslyn_search_files", new { pattern, projectPath = fx.Csproj })
			: ctx.CallToolJsonAsync("roslyn_search_files", new { pattern, filePattern, projectPath = fx.Csproj });
		
		// The file groups of an answer as "name source", source being "-" when the field is absent.
		static string[] Files(JsonNode? answer) => [
			..(answer?["files"]?.AsArray() ?? []).Select(file =>
				$"{Path.GetFileName(file?["file"]?.GetValue<string>())} {file?["source"]?.GetValue<string>() ?? "-"}")];
		
		var tests = new List<TestCase> {
			
			// The default is the search it always was. Four other files of the fixture hold the
			// word; found here, the default would have started walking the disk, which costs a
			// walk on every search and changes every existing answer.
			new("text search: the default pattern still searches C# documents only", async () => {
				
				var answer = await Search("needle", null);
				var files  = Files(answer);
				
				return files is ["Code.cs -"] && answer?["skipped"] is null
					? (true, "PASS")
					: (false, $"FAIL  ({answer?.ToJsonString() ?? "null"})");
			}),
			
			// The case of the issue: a property in a project file. The hit names its source, as
			// roslyn_read_file does, and has no declaration.
			new("text search: a project file is searched from disk", async () => {
				
				var answer = await Search("TargetFramework", "*.csproj");
				var hits   = SearchHits.Of(answer);
				
				return Files(answer) is [var only] && only.EndsWith(".csproj disk") && hits is [{ Declaration: null, Lines: null, At: null }, ..]
					? (true, "PASS")
					: (false, $"FAIL  ({answer?.ToJsonString() ?? "null"})");
			}),
			
			// Markdown under the root is found, with the line number a read of the file gives.
			// The same word under obj\ is not: the search enters the directories a listing
			// enters, and a search of '*.json' must not read every project.assets.json.
			new("text search: a Markdown file is found, one under obj is not", async () => {
				
				var answer = await Search("needle", "*.md");
				var hits   = SearchHits.Of(answer);
				
				return Files(answer) is ["notes.md disk"] && hits is [{ Line: 3, Text: "A needle here." }]
					? (true, "PASS")
					: (false, $"FAIL  ({answer?.ToJsonString() ?? "null"})");
			}),
			
			// A declared AdditionalFiles item is a document of the workspace: it is searched as
			// the workspace holds it, which is what roslyn_read_file returns for it, so its group
			// carries no 'disk'.
			new("text search: a tracked additional file is searched from the workspace", async () => {
				
				var answer = await Search("tracked needle", "tracked.*");
				
				return Files(answer) is ["tracked.txt -"]
					? (true, "PASS")
					: (false, $"FAIL  ({answer?.ToJsonString() ?? "null"})");
			}),
			
			// A hit is read by a model: of a line of 2,000 characters it shows 400 around the
			// match, cut on both sides here, and the match itself is within them.
			new("text search: a long line is cut around its match", async () => {
				
				var answer = await Search("needle", "long.txt");
				var text   = SearchHits.Of(answer) is [{ } hit] ? hit.Text : null;
				
				return text is { Length: 402 } && text.StartsWith('…') && text.EndsWith('…') && text.Contains("aneedleb")
					? (true, "PASS")
					: (false, $"FAIL  (text of {text?.Length.ToString() ?? "no"} characters: {answer?.ToJsonString()[..Math.Min(300, answer.ToJsonString().Length)] ?? "null"})");
			}),
			
			// Every file at once. The C# document keeps its declaration, beside the disk files.
			// The binary file and the file of one endless line are not searched, and the answer
			// counts them: a file left out unseen reads as a file without the word.
			new("text search: '*' covers source and disk files, and counts what it skipped", async () => {
				
				var answer  = await Search("needle", "*");
				var files   = Files(answer);
				var hits    = SearchHits.Of(answer) ?? [];
				var skipped = answer?["skipped"];
				
				var found = files.Contains("Code.cs -") && files.Contains("notes.md disk") && files.Contains("tracked.txt -")
					&& files.Contains("long.txt disk") && files.Contains("breaks.txt disk")
					&& !files.Any(file => file.StartsWith("data.bin") || file.StartsWith("mini.json") || file.StartsWith("ignored.md") || file.StartsWith("out.link"));
				
				var declared = hits.Any(hit => hit.File.EndsWith("Code.cs") && hit.Declaration == "field needle" && hit.Declares);
				
				var counted = skipped?["binary"]?.GetValue<int>() == 1
					&& skipped?["line_too_long"]?.GetValue<int>() == 1
					&& skipped?["unreadable"] is null
					&& (skipped?["outside_workspace"]?.GetValue<int>() ?? 0) == (linked ? 1 : 0);
				
				return found && declared && counted
					? (true, "PASS")
					: (false, $"FAIL  (found {found}, declared {declared}, counted {counted}: files [{string.Join(", ", files)}], skipped {skipped?.ToJsonString() ?? "null"})");
			}),
			
			// The line number of a hit is what an edit is anchored on, so a disk file must be
			// numbered as roslyn_read_file numbers it, which is as SourceText does: '\r' alone,
			// U+0085, U+2028 and U+2029 end a line too. And the text must be decoded alike: the
			// byte that is no UTF-8 reads the same in the hit and in the read.
			new("text search: a disk file's lines are numbered and decoded as roslyn_read_file does", async () => {
				
				var answer = await Search("needle|caf", "breaks.txt");
				var hits   = SearchHits.Of(answer);
				
				if(hits is not [{ Line: 4, Text: "four needle" }, { Line: 5 } fifth])
					
					return (false, $"FAIL  (expected hits on lines 4 and 5: {answer?.ToJsonString() ?? "null"})");
				
				return await ctx.RunTestAsync(
					"roslyn_read_file",
					new { filePath = "breaks.txt", startLine = 4, endLine = 5, projectPath = fx.Csproj },
					(data, texts) => data?["total_lines"]?.GetValue<int>() == 7
						&& texts is [var text]
						&& text == $"four needle\n{fifth.Text}\n");
			}),
			
			// The pattern is the agent's and a disk line can be long: '(a+)+$' on a line of a's
			// that ends otherwise tries every way to split them, which no one waits for and no
			// cancellation between two lines could stop. The match is given up after two seconds,
			// and the file counted as not searched; the call answers.
			new("text search: a pattern that backtracks without end is given up, and the file counted", async () => {
				
				var answer = await Search("(a+)+$", "slow.txt");
				
				return Files(answer) is [] && answer?["skipped"]?["pattern_timeout"]?.GetValue<int>() == 1
					? (true, "PASS")
					: (false, $"FAIL  ({answer?.ToJsonString() ?? "null"})");
			}),
			
			// The walk enters no linked directory, but a file can be a link itself. One that
			// leads out of the workspace is not read, as no other tool reads it, and is counted.
			new("text search: a link leading out of the workspace is not read", async () => {
				
				if(!linked)
					
					return Skip.SetupUnavailable("this machine does not allow creating symbolic links");
				
				var answer = await Search("needle", "*.link");
				
				return Files(answer) is [] && answer?["skipped"]?["outside_workspace"]?.GetValue<int>() == 1
					? (true, "PASS")
					: (false, $"FAIL  ({answer?.ToJsonString() ?? "null"})");
			}),
		};
		
		return new TestGroup($"Text Search ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); outside.Dispose(); return Task.CompletedTask; });
	}
}
