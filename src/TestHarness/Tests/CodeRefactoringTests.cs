using System.Diagnostics;
using System.Text.Json.Nodes;

/// <summary>
///     The three-step refactoring workflow (#389): roslyn_list_refactorings at a caret or selection,
///     roslyn_preview_refactoring for one of the listed actions, roslyn_apply_refactoring with the token.
///     The actions come from Roslyn's own providers through the server's MEF composition, so the tests
///     pin behaviour — the provider that answered, what the diff and the disk show, clean diagnostics
///     after the apply — never an action title, which a Roslyn upgrade may reword. One MSBuild fixture
///     serves the group; every applying test has a file of its own, so no test depends on another's
///     edit. Move type to file is the case that creates a file (the one #387 wanted); the rename-file
///     action of the same provider is the document rename the apply path refuses.
/// </summary>
internal static class CodeRefactoringTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		var fx = TestFixtures.NewMsBuildProject("Refactor");
		
		fx.Write("Shapes.cs", "namespace Fixture;\n\npublic class Circle\n{\n\tpublic double Radius { get; set; }\n}\n\npublic class Square\n{\n\tpublic double Side { get; set; }\n}\n");
		fx.Write("Lonely.cs", "namespace Fixture;\n\npublic class Misnamed\n{\n}\n");
		fx.Write("Extract.cs", "namespace Fixture;\n\npublic static class Extract\n{\n\tpublic static double Area(double side)\n\t{\n\t\tvar squared = side * side;\n\n\t\treturn squared;\n\t}\n}\n");
		fx.Write("Introduce.cs", "namespace Fixture;\n\npublic static class Introduce\n{\n\tpublic static double Volume(double side)\n\t{\n\t\treturn side * side * side + 1;\n\t}\n}\n");
		fx.Write("Invert.cs", InvertSource);
		fx.Write("Stale.cs", InvertSource.Replace("class Invert", "class Stale", StringComparison.Ordinal));
		fx.Write("Wide.cs", WideSource);
		
		async Task<(JsonNode? Data, string Text)> Call(string tool, object args)
		{
			await ctx.SendAsync(new {
				jsonrpc = "2.0",
				id      = ctx.NextId(),
				method  = "tools/call",
				@params = new { name = tool, arguments = args }
			});
			
			var response = await ctx.ReceiveAsync();
			var text     = response?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
			JsonNode? data = null;
			
			try { data = JsonNode.Parse(text); }
			catch { }
			
			return (data, text);
		}
		
		Task<(JsonNode? Data, string Text)> List(string projectPath, string file, int line, int column, int? endLine = null, int? endColumn = null)
			=> Call("roslyn_list_refactorings", new { projectPath, filePath = file, line, column, endLine, endColumn });
		
		Task<(JsonNode? Data, string Text)> Preview(string projectPath, string file, int line, int column, int actionIndex, int? endLine = null, int? endColumn = null)
			=> Call("roslyn_preview_refactoring", new { projectPath, filePath = file, line, column, endLine, endColumn, actionIndex });
		
		Task<(JsonNode? Data, string Text)> Apply(string projectPath, string token, string approval = "y")
			=> Call("roslyn_apply_refactoring", new { projectPath, token, approval });
		
		// The index of the first listed action whose provider type name ends with the suffix, and
		// optionally whose title contains the text; -1 when none does.
		static int IndexOf(JsonNode? list, string providerSuffix, string? titlePart = null)
		{
			var actions = list?["actions"]?.AsArray();
			
			if(actions is null)
				return -1;
			
			foreach(var action in actions) {
				
				var provider = action?["provider"]?.GetValue<string>() ?? string.Empty;
				var title    = action?["title"]?.GetValue<string>() ?? string.Empty;
				
				if(provider.EndsWith(providerSuffix, StringComparison.Ordinal) && (titlePart is null || title.Contains(titlePart, StringComparison.Ordinal)))
					return action?["index"]?.GetValue<int>() ?? -1;
			}
			
			return -1;
		}
		
		static bool Names(JsonNode? data, string key, string fileSuffix)
			=> data?[key]?.AsArray().Any(item => item?.GetValue<string>().EndsWith(fileSuffix, StringComparison.Ordinal) == true) == true;
		
		async Task<bool> CleanAsync(string projectPath)
		{
			var (diagnostics, _) = await Call("roslyn_get_diagnostics", new { projectPath, severity = "errors", take = 0 });
			
			return diagnostics?["error_count"]?.GetValue<int>() == 0;
		}
		
		// list → preview → apply for one action, then the file and the diagnostics are checked.
		// requireCleanDiagnostics: an MSBuild fixture must compile after the apply; an AdhocWorkspace
		// compiles with whatever references it finds, so its error count says nothing about the edit.
		async Task<(bool pass, string msg)> RunRoundTrip(string projectPath, string file, int line, int column, int? endLine, int? endColumn, string providerSuffix, string? titlePart, string expectInFile, string? expectCreated, bool requireCleanDiagnostics)
		{
			var sw = Stopwatch.StartNew();
			var (list, listText) = await List(projectPath, file, line, column, endLine, endColumn);
			var index = IndexOf(list, providerSuffix, titlePart);
			
			if(index < 0)
				return (false, $"FAIL  (no action from {providerSuffix}: {listText}) [{sw.ElapsedMilliseconds}ms]");
			
			var (preview, previewText) = await Preview(projectPath, file, line, column, index, endLine, endColumn);
			var token = preview?["token"]?.GetValue<string>();
			
			if(token is null
				|| preview?["action"]?["provider"]?.GetValue<string>().EndsWith(providerSuffix, StringComparison.Ordinal) != true
				|| (expectCreated is not null && !Names(preview, "createdFiles", expectCreated)))
				return (false, $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
			
			var (apply, applyText) = await Apply(projectPath, token);
			var directory  = Directory.Exists(projectPath) ? projectPath : Path.GetDirectoryName(projectPath)!;
			var targetPath = Path.Combine(directory, expectCreated ?? file);
			var pass = apply?["error"] is null
				&& (expectCreated is null ? apply?["createdFiles"] is null : Names(apply, "createdFiles", expectCreated))
				&& File.Exists(targetPath)
				&& (await File.ReadAllTextAsync(targetPath)).Contains(expectInFile, StringComparison.Ordinal)
				&& (!requireCleanDiagnostics || await CleanAsync(projectPath));
			
			return (pass, pass
				? $"PASS  [{sw.ElapsedMilliseconds}ms]"
				: $"FAIL  (apply: {applyText}) [{sw.ElapsedMilliseconds}ms]");
		}
		
		async Task<(bool pass, string msg)> RunMoveType()
		{
			var result = await RunRoundTrip(fx.Csproj!, "Shapes.cs", 3, 14, null, null, ".MoveTypeCodeRefactoringProvider", null, "class Circle", "Circle.cs", requireCleanDiagnostics: true);
			
			if(!result.pass)
				return result;
			
			// The type left its old file: the new file has it, Shapes.cs does not.
			var shapes = await File.ReadAllTextAsync(fx.PathOf("Shapes.cs"));
			
			return shapes.Contains("class Circle", StringComparison.Ordinal)
				? (false, "FAIL  (Shapes.cs still declares Circle after the move)")
				: result;
		}
		
		Task<(bool pass, string msg)> RunExtractMethod()
			=> RunRoundTrip(fx.Csproj!, "Extract.cs", 7, 1, 7, null, ".ExtractMethodCodeRefactoringProvider", "method", "NewMethod", null, requireCleanDiagnostics: true);
		
		Task<(bool pass, string msg)> RunIntroduceLocal()
			=> RunRoundTrip(fx.Csproj!, "Introduce.cs", 7, 10, 7, 28, ".IntroduceVariableCodeRefactoringProvider", null, "= side * side * side;", null, requireCleanDiagnostics: true);
		
		Task<(bool pass, string msg)> RunInvertIf()
			=> RunRoundTrip(fx.Csproj!, "Invert.cs", 7, 3, null, null, "InvertIfCodeRefactoringProvider", null, "!big)", null, requireCleanDiagnostics: true);
		
		// Every action a dialog would drive has a title ending in "..."; none may reach the list.
		// The built-in source reports its size, which is how a composition that silently lost the
		// Features assemblies would show.
		async Task<(bool pass, string msg)> RunNoDialogActions()
		{
			var sw = Stopwatch.StartNew();
			var (list, listText) = await List(fx.Csproj!, "Shapes.cs", 8, 14);
			var actions = list?["actions"]?.AsArray();
			var leaked  = actions?.Any(action => action?["title"]?.GetValue<string>().TrimEnd().EndsWith("...", StringComparison.Ordinal) == true) == true;
			var sources = list?["providerSources"]?.AsArray();
			var pass = list?["error"] is null
				&& actions is { Count: > 0 }
				&& !leaked
				&& sources?.Any(source => source?["kind"]?.GetValue<string>() == "built-in" && source?["refactorings"]?.GetValue<int>() > 50) == true;
			
			return (pass, pass
				? $"PASS  [{sw.ElapsedMilliseconds}ms]"
				: $"FAIL  (list: {listText}) [{sw.ElapsedMilliseconds}ms]");
		}
		
		// Move type's "Rename file to match type" is a document rename, which the apply path does not do.
		async Task<(bool pass, string msg)> RunRenameFileRefused()
		{
			var sw = Stopwatch.StartNew();
			var (list, listText) = await List(fx.Csproj!, "Lonely.cs", 3, 14);
			var index = IndexOf(list, ".MoveTypeCodeRefactoringProvider", "file");
			
			if(index < 0)
				return (false, $"FAIL  (no rename-file action: {listText}) [{sw.ElapsedMilliseconds}ms]");
			
			var (preview, previewText) = await Preview(fx.Csproj!, "Lonely.cs", 3, 14, index);
			var pass = preview?["error"]?.GetValue<string>() == "unsupported refactoring change"
				&& preview?["token"] is null
				&& preview?["message"]?.GetValue<string>().Contains("move or rename", StringComparison.Ordinal) == true
				&& File.Exists(fx.PathOf("Lonely.cs"))
				&& !File.Exists(fx.PathOf("Misnamed.cs"));
			
			return (pass, pass
				? $"PASS  [{sw.ElapsedMilliseconds}ms]"
				: $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
		}
		
		async Task<(bool pass, string msg)> RunInvalidInputs()
		{
			var sw = Stopwatch.StartNew();
			var (outside, outsideText) = await List(fx.Csproj!, "Invert.cs", 99, 1);
			var (badIndex, badIndexText) = await Preview(fx.Csproj!, "Invert.cs", 7, 3, 99);
			var (missing, missingText) = await List(fx.Csproj!, "Nope.cs", 1, 1);
			var pass = outside?["error"]?.GetValue<string>() == "invalid_location"
				&& badIndex?["error"]?.GetValue<string>() == "invalid action index"
				&& badIndex?["actions"]?.AsArray().Count > 0
				&& missing?["error"]?.GetValue<string>() == "file_not_found";
			
			return (pass, pass
				? $"PASS  [{sw.ElapsedMilliseconds}ms]"
				: $"FAIL  (outside: {outsideText} | index: {badIndexText} | missing: {missingText}) [{sw.ElapsedMilliseconds}ms]");
		}
		
		// A refactoring token is a refactoring token: the code-fix apply does not know it, 'n' drops it
		// without touching the disk, and the dropped token is gone.
		async Task<(bool pass, string msg)> RunTokenDiscipline()
		{
			var sw = Stopwatch.StartNew();
			var (list, listText) = await List(fx.Csproj!, "Invert.cs", 7, 3);
			var index = IndexOf(list, "InvertIfCodeRefactoringProvider");
			
			if(index < 0)
				return (false, $"FAIL  (no invert-if action: {listText}) [{sw.ElapsedMilliseconds}ms]");
			
			var (preview, previewText) = await Preview(fx.Csproj!, "Invert.cs", 7, 3, index);
			var token = preview?["token"]?.GetValue<string>();
			
			if(token is null)
				return (false, $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
			
			var before = await File.ReadAllTextAsync(fx.PathOf("Invert.cs"));
			var (wrongTool, wrongToolText) = await Call("roslyn_apply_code_fix", new { projectPath = fx.Csproj, token, approval = "y" });
			var (rejected, rejectedText) = await Apply(fx.Csproj!, token, "n");
			var (gone, goneText) = await Apply(fx.Csproj!, token);
			var after = await File.ReadAllTextAsync(fx.PathOf("Invert.cs"));
			var pass = wrongTool?["error"]?.GetValue<string>() == "token unavailable"
				&& rejected?["error"]?.GetValue<string>() == "rejected"
				&& gone?["error"]?.GetValue<string>() == "token unavailable"
				&& before == after;
			
			return (pass, pass
				? $"PASS  [{sw.ElapsedMilliseconds}ms]"
				: $"FAIL  (code-fix apply: {wrongToolText} | reject: {rejectedText} | retry: {goneText}) [{sw.ElapsedMilliseconds}ms]");
		}
		
		// A file edited between preview and apply is caught by the first stale check; the token survives.
		async Task<(bool pass, string msg)> RunStalePreview()
		{
			var sw = Stopwatch.StartNew();
			var (list, listText) = await List(fx.Csproj!, "Stale.cs", 7, 3);
			var index = IndexOf(list, "InvertIfCodeRefactoringProvider");
			
			if(index < 0)
				return (false, $"FAIL  (no invert-if action: {listText}) [{sw.ElapsedMilliseconds}ms]");
			
			var (preview, previewText) = await Preview(fx.Csproj!, "Stale.cs", 7, 3, index);
			var token = preview?["token"]?.GetValue<string>();
			
			if(token is null)
				return (false, $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
			
			var path     = fx.PathOf("Stale.cs");
			var original = await File.ReadAllTextAsync(path);
			
			await File.WriteAllTextAsync(path, original + "// edited after the preview\n");
			
			var (stale, staleText) = await Apply(fx.Csproj!, token);
			
			// Put the file back; the token is still pending, and the apply must now go through.
			await File.WriteAllTextAsync(path, original);
			
			var (retry, retryText) = await Apply(fx.Csproj!, token);
			var pass = stale?["error"]?.GetValue<string>() == "stale preview"
				&& retry?["error"] is null
				&& (await File.ReadAllTextAsync(path)).Contains("!big)", StringComparison.Ordinal);
			
			return (pass, pass
				? $"PASS  [{sw.ElapsedMilliseconds}ms]"
				: $"FAIL  (stale: {staleText} | retry: {retryText}) [{sw.ElapsedMilliseconds}ms]");
		}
		
		// The same workflow on an AdhocWorkspace: the host's composition serves both workspace kinds.
		async Task<(bool pass, string msg)> RunAdhoc()
		{
			using var adhoc = TestFixtures.NewAdhocDir("RefactorAdhoc");
			
			adhoc.Write("Invert.cs", InvertSource);
			
			var result = await RunRoundTrip(adhoc.Dir, "Invert.cs", 7, 3, null, null, "InvertIfCodeRefactoringProvider", null, "!big)", null, requireCleanDiagnostics: false);
			
			if(!result.pass)
				return result;
			
			// No project file, so no analyzer references: the project source says so instead of pretending.
			var (list, listText) = await List(adhoc.Dir, "Invert.cs", 7, 3);
			var projectSource = list?["providerSources"]?.AsArray().FirstOrDefault(source => source?["kind"]?.GetValue<string>() == "project");
			var reported = projectSource?["problems"]?.AsArray().Any(problem => problem?.GetValue<string>().Contains("AdhocWorkspace", StringComparison.Ordinal) == true) == true;
			
			return reported
				? result
				: (false, $"FAIL  (adhoc project source not reported: {listText})");
		}
		
		// "enabled": false in the project file turns the tools off with a structured error.
		async Task<(bool pass, string msg)> RunDisabled()
		{
			var sw = Stopwatch.StartNew();
			using var off = TestFixtures.NewAdhocDir("RefactorOff");
			
			off.Write(".madq_roslynmcp.json", "{ \"codeActions\": { \"enabled\": false } }\n");
			off.Write("Invert.cs", InvertSource);
			
			var (list, listText) = await List(off.Dir, "Invert.cs", 7, 3);
			var (preview, previewText) = await Preview(off.Dir, "Invert.cs", 7, 3, 0);
			var pass = list?["error"]?.GetValue<string>() == "providers_disabled"
				&& preview?["error"]?.GetValue<string>() == "providers_disabled";
			
			return (pass, pass
				? $"PASS  [{sw.ElapsedMilliseconds}ms]"
				: $"FAIL  (list: {listText} | preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
		}
		
		// The preview's diff is the size of the change, not of the file (#411). Extract method adds a
		// method, and Roslyn separates it with an empty line; Wide.cs has no empty line but the one
		// after its last newline (its blank lines are indented), so the old greedy matcher paired that
		// new line with the end of the file and reported the thirty methods in between — untouched —
		// as removed and added again: a diff too large for a client to show. The bound is generous
		// (the change itself is under twenty lines) and far below the file's 150; a removed line that
		// is also added back unchanged is the same failure seen from the other side.
		async Task<(bool pass, string msg)> RunDiffIsLocal()
		{
			var sw = Stopwatch.StartNew();
			var (list, listText) = await List(fx.Csproj!, "Wide.cs", 7, 1, 7);
			var index = IndexOf(list, ".ExtractMethodCodeRefactoringProvider", "method");
			
			if(index < 0)
				return (false, $"FAIL  (no extract-method action: {listText}) [{sw.ElapsedMilliseconds}ms]");
			
			var (preview, previewText) = await Preview(fx.Csproj!, "Wide.cs", 7, 1, index, 7);
			var diff = preview?["diff"]?.GetValue<string>();
			
			if(diff is null)
				return (false, $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
			
			var lines     = diff.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => !line.StartsWith("---", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal)).ToArray();
			var removed   = lines.Where(line => line.StartsWith('-')).Select(line => line[1..]).ToArray();
			var added     = lines.Where(line => line.StartsWith('+')).Select(line => line[1..]).ToArray();
			var untouched = removed.Count(line => line.Contains("return value +", StringComparison.Ordinal));
			var pass      = added.Any(line => line.Contains("NewMethod", StringComparison.Ordinal))
				&& removed.Length + added.Length <= 40
				&& untouched == 0;
			
			if(preview?["token"]?.GetValue<string>() is { } token)
				await Apply(fx.Csproj!, token, "n");
			
			return (pass, pass
				? $"PASS  ({removed.Length} removed, {added.Length} added) [{sw.ElapsedMilliseconds}ms]"
				: $"FAIL  ({removed.Length} removed, {added.Length} added, {untouched} untouched methods reported as removed) [{sw.ElapsedMilliseconds}ms]");
		}
		
		List<TestCase> tests = [
			new("list offers Roslyn's refactorings and keeps dialog actions out", RunNoDialogActions),
			new("a preview's diff is the size of the change, not of the file", RunDiffIsLocal),
			new("move type to file creates the file and empties the old declaration", RunMoveType),
			new("extract method on a selected statement", RunExtractMethod),
			new("introduce local for a selected expression", RunIntroduceLocal),
			new("rename file to match type is refused as a document rename", RunRenameFileRefused),
			new("invalid location, index and file are structured errors", RunInvalidInputs),
			new("a refactoring token is refused by the code-fix apply and dropped by 'n'", RunTokenDiscipline),
			new("a file edited after the preview is stale, and the token survives", RunStalePreview),
			new("invert if round-trips", RunInvertIf),
			new("the same workflow on an AdhocWorkspace", RunAdhoc),
			new("codeActions.enabled false turns the tools off", RunDisabled),
		];
		
		return new TestGroup($"Code Refactorings ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); return Task.CompletedTask; });
	}
	
	const string InvertSource = "namespace Fixture;\n\npublic static class Invert\n{\n\tpublic static string Describe(bool big)\n\t{\n\t\tif(big)\n\t\t\treturn \"big\";\n\t\telse\n\t\t\treturn \"small\";\n\t}\n}\n";
	
	// A statement to extract, then thirty methods the refactoring does not touch. Every blank line is
	// indented, so the only empty line of the file is the one after its last newline.
	static readonly string WideSource =
		"namespace Fixture;\n\t\npublic static class Wide\n{\n\tpublic static double Area(double side)\n\t{\n\t\tvar squared = side * side;\n\t\t\n\t\treturn squared;\n\t}\n"
		+ string.Concat(Enumerable.Range(1, 30).Select(i => $"\t\n\tpublic static int M{i}(int value)\n\t{{\n\t\treturn value + {i};\n\t}}\n"))
		+ "}\n";
}
