using System.Text.Json.Nodes;

static class FileContentTests
{
	// Every character JSON would escape — tabs, quotes, backslashes — plus an indented blank line
	// and a trailing newline, which gives the file an empty last line.
	const string ExactSource =
		"class Probe\n" +
		"{\n" +
		"\tstring Path() => \"C:\\\\temp\\\\a.txt\";\n" +
		"\t\n" +
		"\tint Last() => 1;\n" +
		"}\n";
	
	internal static TestGroup Build(TestContext ctx)
	{
		
		var fx = TestFixtures.NewAdhocDir("ReadExact");
		
		fx.Write("Probe.cs", ExactSource);
		
		var tests = new List<TestCase> {
			
			new("roslyn_read_file: read .cs file from Roslyn in-memory",
				() => ctx.RunTestAsync(
					"roslyn_read_file",
					new { filePath = "WorkspaceManager.cs", projectPath = ctx.TargetPath },
					(data, source) => data?["source"]?.GetValue<string>() == "roslyn"
						&& data?["total_lines"]?.GetValue<int>() > 100
						&& source is [{ Length: > 0 }])),
			
			// The header's range and the text must agree: ten lines requested, ten lines returned,
			// each ended by a newline.
			new("roslyn_read_file: read with line range",
				() => ctx.RunTestAsync(
					"roslyn_read_file",
					new { filePath = "WorkspaceManager.cs", startLine = 1, endLine = 10, projectPath = ctx.TargetPath },
					(data, source) => source is [var text]
						&& text.Count(c => c == '\n') == 10
						&& data?["start_line"]?.GetValue<int>() == 1
						&& data?["end_line"]?.GetValue<int>() == 10)),
			
			new("roslyn_read_file: read non-.cs file from disk",
				() => ctx.RunTestAsync(
					"roslyn_read_file",
					new { filePath = "RoslynMcp.csproj", projectPath = ctx.TargetPath },
					data => data?["source"]?.GetValue<string>() == "disk"
						&& data?["total_lines"]?.GetValue<int>() > 0)),
			
			// ── #328: source is returned as raw text, not inside a JSON string ──────────────────────
			// The point of the raw block is that it can be copied verbatim into an exact-match edit.
			// The fixture holds the characters JSON escapes; the block must reproduce the file
			// character for character. The file ends in a newline, so its last line is empty: the
			// expected text is the file plus the one newline the server adds after the last line. A
			// server that treated the file's own final newline as that terminator would drop the
			// empty line and fail here.
			new("roslyn_read_file: source block reproduces the file exactly",
				() => ctx.RunTestAsync(
					"roslyn_read_file",
					new { filePath = "Probe.cs", projectPath = fx.ProjectPath },
					(data, source) => source is [var text]
						&& text == ExactSource + "\n"
						&& data?["total_lines"]?.GetValue<int>() == 7
						&& data?["lines"] is null)),
			
			// Lines 3-4 end on the indented blank line. A whitespace-only line is the easiest thing
			// for a "tidy" serializer to trim, and an edit anchored on it would then never match.
			new("roslyn_read_file: a range keeps tabs, quotes, backslashes and an indented blank line",
				() => ctx.RunTestAsync(
					"roslyn_read_file",
					new { filePath = "Probe.cs", startLine = 3, endLine = 4, projectPath = fx.ProjectPath },
					(data, source) => source is [var text]
						&& text == "\tstring Path() => \"C:\\\\temp\\\\a.txt\";\n\t\n"
						&& data?["start_line"]?.GetValue<int>() == 3
						&& data?["end_line"]?.GetValue<int>() == 4)),
			
			new("roslyn_get_line_count: single and multi-file",
				() => ctx.RunTestAsync(
					"roslyn_get_line_count",
					new { filePaths = "WorkspaceManager.cs,RoslynMcp.csproj", projectPath = ctx.TargetPath },
					data => data?["files"]?.AsArray().Count == 2
						&& data?["files"]?[0]?["line_count"]?.GetValue<int>() > 100)),
			
			// ── Regression: guarded workspace resolution (transient mid-reload hardening, #249 part 2) ──
			// read_file and get_line_count resolve root + security boundary through the new
			// TryResolveFileContext guard, ahead of any compilation access. Before the guard a mid-reload
			// throw during that resolution surfaced as an opaque "An error occurred invoking '<tool>'".
			// A projectPath that never exists deterministically forces the resolution failure; the tool must now
			// return a STRUCTURED JSON error. RunTestAsync fails on non-JSON, so these fail against the
			// pre-guard server — not tautological: they assert the resolver call is now wrapped.
			
			new("roslyn_read_file: structured error (not crash) on resolution failure",
				() => ctx.RunTestAsync(
					"roslyn_read_file",
					new { filePath = "WorkspaceManager.cs", projectPath = TestFixtures.MissingProjectPath },
					data => data?["error"]?.GetValue<string>() is { Length: > 0 })),
			
			new("roslyn_get_line_count: structured error (not crash) on resolution failure",
				() => ctx.RunTestAsync(
					"roslyn_get_line_count",
					new { filePaths = "WorkspaceManager.cs", projectPath = TestFixtures.MissingProjectPath },
					data => data?["error"]?.GetValue<string>() is { Length: > 0 })),
		};
		
		return new TestGroup($"File Content Tools ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); return Task.CompletedTask; });
	}
}
