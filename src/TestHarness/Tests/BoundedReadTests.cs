using System.Text;

/// <summary>
///     #230: the server reads whole files through <c>FileReader</c>, which refuses a file past its
///     limit instead of loading it, and decodes text the way <c>File.ReadAllText</c> did. The
///     server's <c>FileReader</c> is internal to it, so these tests reach it through the tools
///     that read a file whole — <c>roslyn_read_file</c> for a file that is not a tracked document,
///     and the three edit tools.
/// </summary>
static class BoundedReadTests
{
	// FileReader.DocumentLimit, the limit every tool here reads with. Duplicated on purpose: the
	// harness checks the server's contract from outside, and a limit changed by accident must
	// fail here rather than be followed silently.
	const long DocumentLimit = 64L * 1024 * 1024;
	
	internal static TestGroup Build(TestContext ctx)
	{
		var fx = TestFixtures.NewAdhocDir("BoundedRead");
		
		// Every file is .txt, so none is a tracked document of the AdhocWorkspace (which tracks .cs
		// only): each read goes to disk through FileReader, never to Roslyn's in-memory text.
		// The two sizes straddle the limit by one byte: the limit is "at most", and an off-by-one
		// either way fails one of the tests that use them. The file at the limit is read, so it is
		// 65,536 lines of 1 KB — as one line of NUL bytes, line 1 would be a 64 MB string to
		// return. The file past it is never read when the server is right, so SetLength sizes it
		// in milliseconds.
		Lines(fx.PathOf("at-limit.txt"), DocumentLimit);
		
		var overLimit = Sized(fx.PathOf("over-limit.txt"), DocumentLimit + 1);
		
		fx.Write("empty.txt", "");
		fx.Write("no-bom.txt", "café au lait\n");
		
		File.WriteAllBytes(fx.PathOf("utf8-bom.txt"),  [..Encoding.UTF8.GetPreamble(),   ..Encoding.UTF8.GetBytes("first line\nsecond\n")]);
		File.WriteAllBytes(fx.PathOf("utf16-bom.txt"), [..Encoding.Unicode.GetPreamble(), ..Encoding.Unicode.GetBytes("say hello\n")]);
		
		var tests = new List<TestCase> {
			
			// The boundary from below: a file of exactly the limit is read, so the limit is
			// inclusive. Only the first line is asked for — the tool still reads the whole file, to
			// report total_lines, and that is the read that is bounded. 65,536 lines each ended by
			// a newline, plus the empty line after the last one, as SourceText counts them.
			new("roslyn_read_file: a file of exactly the limit is read",
				() => ctx.RunTestAsync(
					"roslyn_read_file",
					new { filePath = "at-limit.txt", startLine = 1, endLine = 1, projectPath = fx.ProjectPath },
					data => data?["error"] is null
						&& data?["source"]?.GetValue<string>() == "disk"
						&& data?["total_lines"]?.GetValue<int>() == (int) (DocumentLimit / 1024) + 1)),
			
			// The boundary from above: one byte more is refused, with the stable code an agent can
			// branch on and the limit in bytes. Before #230 the tool loaded the file whatever its
			// size; a 1 GB log took the server's memory with it.
			new("roslyn_read_file: one byte past the limit is file_too_large",
				() => ctx.RunTestAsync(
					"roslyn_read_file",
					new { filePath = "over-limit.txt", startLine = 1, endLine = 1, projectPath = fx.ProjectPath },
					data => IsTooLarge(data))),
			
			// The edit tools read the whole file before they change it. Refused, the file must be
			// exactly as it was: the length is the check, since an edit that went ahead regardless
			// would write it back shorter (decoded and re-encoded) or longer (with the insert).
			new("roslyn_replace_in_file: a file past the limit is file_too_large and left alone",
				async () => {
					
					var result = await ctx.RunTestAsync(
						"roslyn_replace_in_file",
						new { filePath = "over-limit.txt", pattern = "x", replacement = "y", projectPath = fx.ProjectPath },
						data => IsTooLarge(data));
					
					return Untouched(result, overLimit);
				}),
			
			new("roslyn_insert_lines: a file past the limit is file_too_large and left alone",
				async () => {
					
					var result = await ctx.RunTestAsync(
						"roslyn_insert_lines",
						new { filePath = "over-limit.txt", text = "added", atLine = 1, projectPath = fx.ProjectPath },
						data => IsTooLarge(data));
					
					return Untouched(result, overLimit);
				}),
			
			// roslyn_write_file reads the file it replaces for its line endings and its pre-write
			// backup. Before #230 any read failure there other than a missing file escaped the tool
			// as the SDK's opaque error; now it is refused before anything is written.
			new("roslyn_write_file: overwriting a file past the limit is file_too_large and left alone",
				async () => {
					
					var result = await ctx.RunTestAsync(
						"roslyn_write_file",
						new { filePath = "over-limit.txt", content = "small\n", createNew = true, projectPath = fx.ProjectPath },
						data => IsTooLarge(data));
					
					return Untouched(result, overLimit);
				}),
			
			// An empty file has no length to size a buffer with and ends on the first read: it must
			// read as one empty line, not as an error and not as too large.
			new("roslyn_read_file: an empty file reads as one empty line",
				() => ctx.RunTestAsync(
					"roslyn_read_file",
					new { filePath = "empty.txt", projectPath = fx.ProjectPath },
					data => data?["error"] is null
						&& data?["source"]?.GetValue<string>() == "disk"
						&& data?["total_lines"]?.GetValue<int>() == 1)),
			
			// A UTF-8 byte order mark is detected and dropped, as File.ReadAllText dropped it. Kept,
			// it would be a U+FEFF before "first", and the anchored pattern would find nothing. A
			// dry run: the decoding is what is checked, not the write.
			new("roslyn_replace_in_file: a UTF-8 byte order mark is not part of the text",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_file",
					new { filePath = "utf8-bom.txt", pattern = "^first", replacement = "1st", mode = "regex", dryRun = true, projectPath = fx.ProjectPath },
					data => data?["match_count"]?.GetValue<int>() == 1)),
			
			// A UTF-16 byte order mark selects UTF-16. Decoded as UTF-8 instead, every other byte
			// would be a NUL between the letters and "hello" would not be found.
			new("roslyn_replace_in_file: a UTF-16 byte order mark selects UTF-16",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_file",
					new { filePath = "utf16-bom.txt", pattern = "hello", replacement = "bye", dryRun = true, projectPath = fx.ProjectPath },
					data => data?["match_count"]?.GetValue<int>() == 1)),
			
			// No byte order mark means UTF-8, not the system code page: "é" is two bytes in the file
			// and must decode as one character for the literal to match.
			new("roslyn_replace_in_file: a file without a byte order mark is read as UTF-8",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_file",
					new { filePath = "no-bom.txt", pattern = "café", replacement = "tea", dryRun = true, projectPath = fx.ProjectPath },
					data => data?["match_count"]?.GetValue<int>() == 1)),
		};
		
		return new TestGroup($"Bounded Reads ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); return Task.CompletedTask; });
	}
	
	static string Sized(string path, long length)
	{
		using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
		
		stream.SetLength(length);
		
		return path;
	}
	
	// Lines of 1,023 letters and a newline; the length must be a multiple of 1 KB.
	static void Lines(string path, long length)
	{
		var line = new byte[1024];
		
		Array.Fill(line, (byte) 'a');
		line[^1] = (byte) '\n';
		
		using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
		
		for(var written = 0L; written < length; written += line.Length)
			stream.Write(line);
	}
	
	static bool IsTooLarge(System.Text.Json.Nodes.JsonNode? data) =>
		data?["error"]?.GetValue<string>() == "file_too_large"
		&& data?["limit_bytes"]?.GetValue<long>() == DocumentLimit
	;
	
	static (bool pass, string message) Untouched((bool pass, string message) result, string path)
	{
		if(!result.pass)
			
			return result;
		
		var length = new FileInfo(path).Length;
		
		return length == DocumentLimit + 1
			? result
			: (false, $"FAIL  (refused, but the file is now {length} bytes)");
	}
}
