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
		
		// Two fixtures with a private .editorconfig (#413), one per workspace kind, written like
		// every other fixture before the first tool call.
		var styled      = TestFixtures.NewMsBuildProject("Styled");
		var styledAdhoc = TestFixtures.NewAdhocDir("StyledAdhoc");
		
		foreach(var fixture in (FixtureProject[]) [styled, styledAdhoc]) {
			
			fixture.Write(".editorconfig", StyleConfig);
			fixture.Write(".gitignore", ".editorconfig\n");
			fixture.Write("Styled.cs", StyledSource);
			fixture.Write("Caller.cs", CallerSource);
		}
		
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
		
		// A source the list names: one problem as text, no list of them and no origin path.
		static bool IsNamedProblem(JsonNode? source)
			=> source?["problem"]?.GetValue<string>() is { Length: > 0 }
				&& source?["problems"] is null
				&& source?["origin"] is null;
		
		// Every action a dialog would drive has a title ending in "..."; none may reach the list.
		// The built-in source reports its size in roslyn_info, which is how a composition that
		// silently lost the Features assemblies would show; the list's own count is a sum over all
		// sources, which another source's providers could make up for.
		//
		// The summary is also what keeps the answer small (#414). The list used to carry every
		// source in full, some 1,300 tokens on a project with seventeen of them, on every call. Now
		// a source appears by name only when it has a problem, and never with its origin path; the
		// sources that loaded are counted. So the old field must be gone, more sources must be
		// counted than are named, and each one named must say what is wrong with it. (The fixture's
		// SDK analyzers happen to have a problem today; the AdhocWorkspace test checks the same shape
		// on a source that always has one.)
		async Task<(bool pass, string msg)> RunNoDialogActions()
		{
			var sw = Stopwatch.StartNew();
			var (list, listText) = await List(fx.Csproj!, "Shapes.cs", 8, 14);
			var actions = list?["actions"]?.AsArray();
			var leaked  = actions?.Any(action => action?["title"]?.GetValue<string>().TrimEnd().EndsWith("...", StringComparison.Ordinal) == true) == true;
			var providers = list?["providers"];
			var named     = providers?["problems"]?.AsArray() ?? [];
			var info      = await ctx.CallToolJsonAsync("roslyn_info", new { });
			var builtIn   = info?["code_actions"]?["sources"]?.AsArray().FirstOrDefault(source => source?["kind"]?.GetValue<string>() == "built-in");
			var pass = list?["error"] is null
				&& actions is { Count: > 0 }
				&& !leaked
				&& builtIn?["refactorings"]?.GetValue<int>() > 50
				&& providers?["refactorings"]?.GetValue<int>() >= builtIn?["refactorings"]?.GetValue<int>()
				&& list?["providerSources"] is null
				&& providers?["sources"]?.GetValue<int>() > named.Count
				&& named.All(IsNamedProblem);
			
			return (pass, pass
				? $"PASS  [{sw.ElapsedMilliseconds}ms]"
				: $"FAIL  (list: {listText}) [{sw.ElapsedMilliseconds}ms]");
		}
		
		// roslyn_info is the first call an agent makes to see which server it is talking to, and it
		// listed every analyzer assembly of every project as a provider source: forty-two entries on a
		// repository with a few analyzer packages, some thirty of them a source generator or a helper
		// assembly with no refactoring, no fix and no problem (#448). Those are now counted. The
		// fixture's SDK references include such assemblies (the System.Text.Json source generator
		// among them), and the list above has loaded them. So the default answer must list no project
		// source that says nothing and must count some; allSources must list exactly that many more,
		// with a silent one among them — a count that did not match the full list would be a number
		// nobody can check.
		async Task<(bool pass, string msg)> RunInfoSources()
		{
			var sw = Stopwatch.StartNew();
			
			static bool Silent(JsonNode? source)
				=> source?["kind"]?.GetValue<string>() == "project"
					&& source["refactorings"]?.GetValue<int>() == 0
					&& source["fixes"]?.GetValue<int>() == 0
					&& source["problems"] is null
			;
			
			var brief    = (await ctx.CallToolJsonAsync("roslyn_info", new { }))?["code_actions"];
			var full     = (await ctx.CallToolJsonAsync("roslyn_info", new { allSources = true }))?["code_actions"];
			var listed   = brief?["sources"]?.AsArray() ?? [];
			var all      = full?["sources"]?.AsArray() ?? [];
			var uncounted = brief?["sources_without_providers"]?.GetValue<int>() ?? 0;
			
			var pass = uncounted > 0
				&& !listed.Any(Silent)
				&& listed.Any(source => source?["kind"]?.GetValue<string>() == "built-in")
				&& all.Count == listed.Count + uncounted
				&& all.Count(Silent) == uncounted
				&& full?["sources_without_providers"] is null;
			
			return (pass, pass
				? $"PASS  ({listed.Count} listed, {uncounted} counted) [{sw.ElapsedMilliseconds}ms]"
				: $"FAIL  (brief: {brief?.ToJsonString()} | all: {all.Count} source(s), {all.Count(Silent)} silent) [{sw.ElapsedMilliseconds}ms]");
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
			// It is the one source that has a problem on every machine, so the shape of a named source
			// is pinned here (#414): its problem as one text, no origin path, and the sources that
			// loaded — built-in and bundled — counted but not named.
			var (list, listText) = await List(adhoc.Dir, "Invert.cs", 7, 3);
			var projectSource = list?["providers"]?["problems"]?.AsArray().FirstOrDefault(source => source?["kind"]?.GetValue<string>() == "project");
			var providers = list?["providers"];
			var reported  = projectSource?["problem"]?.GetValue<string>().Contains("AdhocWorkspace", StringComparison.Ordinal) == true
				&& IsNamedProblem(projectSource)
				&& providers?["sources"]?.GetValue<int>() > providers?["problems"]?.AsArray().Count;
			
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
		
		// What a styled file must look like after a code action: every expected piece is there, and
		// no line is indented with spaces. Line ends are normalized, since Roslyn writes the
		// platform's for the lines it adds. Null when the text is as expected.
		static string? StyleProblem(string text, params string[] expected)
		{
			var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
			var missing    = expected.FirstOrDefault(piece => !normalized.Contains(piece, StringComparison.Ordinal));
			
			if(missing is not null)
				return $"missing {JsonValue.Create(missing).ToJsonString()}";
			
			return normalized.Split('\n').Any(line => line.StartsWith(' '))
				? "a line is indented with spaces"
				: null;
		}
		
		// A private .editorconfig shapes what a refactoring writes (#413). Without one Roslyn formats
		// the code it adds in its own defaults: four spaces, every brace on a line of its own, and
		// "foreach (". The fixture's file asks for tabs, braces on a new line for methods and types
		// only, and no space after a control-flow keyword, and Extract method must write all three:
		// the new method and the call to it are tab-indented, the method's brace is on its own line,
		// and the loop and the condition it moved keep their brace and have no space. The fixture's
		// .gitignore names .editorconfig, as this repository's does: an ignored file is still read.
		// No type name is expected: an AdhocWorkspace has no references, so there "int" is written
		// as an unresolved "global::System.Int32".
		async Task<(bool pass, string msg)> RunStyledExtract(FixtureProject fixture)
		{
			var result = await RunRoundTrip(fixture.ProjectPath, "Styled.cs", 9, 1, 14, null, ".ExtractMethodCodeRefactoringProvider", "method", "NewMethod", null, requireCleanDiagnostics: false);
			
			if(!result.pass)
				return result;
			
			var after   = await File.ReadAllTextAsync(fixture.PathOf("Styled.cs"));
			var problem = StyleProblem(after,
				"\t\ttotal = NewMethod(",
				"\tprivate static ",
				" total)\n\t{\n\t\tforeach(var item in items) {\n",
				"\t\t\tif(item > 0) {\n"
			);
			
			return problem is null
				? result
				: (false, $"FAIL  ({problem}: {JsonValue.Create(after).ToJsonString()})");
		}
		
		// The same for a code fix that adds code (#413): Roslyn's fixer for CS0117 writes the method
		// the call is missing, tab-indented and with its brace on its own line. That diagnostic and
		// its one fix exist in both workspace kinds; most others need framework references, which an
		// AdhocWorkspace does not have.
		async Task<(bool pass, string msg)> RunStyledFix(FixtureProject fixture)
		{
			var sw = Stopwatch.StartNew();
			var projectPath = fixture.ProjectPath;
			var (preview, previewText) = await Call("roslyn_preview_code_fix", new { projectPath, filePath = "Caller.cs", line = 7, column = 10, diagnosticId = "CS0117" });
			var token = preview?["token"]?.GetValue<string>();
			
			if(token is null)
				return (false, $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
			
			var (apply, applyText) = await Call("roslyn_apply_code_fix", new { projectPath, token, approval = "y" });
			
			if(apply?["error"] is not null)
				return (false, $"FAIL  (apply: {applyText}) [{sw.ElapsedMilliseconds}ms]");
			
			var after   = await File.ReadAllTextAsync(fixture.PathOf("Caller.cs"));
			var problem = StyleProblem(after, "\tinternal static void Missing()\n\t{\n");
			
			return problem is null
				? (true, $"PASS  [{sw.ElapsedMilliseconds}ms]")
				: (false, $"FAIL  ({problem}: {JsonValue.Create(after).ToJsonString()}) [{sw.ElapsedMilliseconds}ms]");
		}
		
		// An AdhocWorkspace has no build to hand it its configs, so it keeps them current itself
		// (#413): an .editorconfig that appears after the load is used by the next code action. The
		// file is written through the server, which syncs the workspace before it answers, so the
		// first half does not wait on the watcher. Two-space indentation is neither Roslyn's default
		// nor what the file's own tabs suggest; only the new config explains it. The second half is
		// the watcher's: nothing else reports a file deleted from outside, so it polls.
		async Task<(bool pass, string msg)> RunConfigAfterLoad()
		{
			var sw = Stopwatch.StartNew();
			using var late = TestFixtures.NewAdhocDir("StyledLate");
			
			late.Write("Caller.cs", CallerSource);
			
			// The first call loads the workspace, without a config of its own.
			var (before, beforeText) = await Call("roslyn_preview_code_fix", new { projectPath = late.Dir, filePath = "Caller.cs", line = 7, column = 10, diagnosticId = "CS0117" });
			
			if(before?["token"]?.GetValue<string>() is not { } early)
				return (false, $"FAIL  (preview before the config: {beforeText}) [{sw.ElapsedMilliseconds}ms]");
			
			await Call("roslyn_apply_code_fix", new { projectPath = late.Dir, token = early, approval = "n" });
			
			var (written, writtenText) = await Call("roslyn_write_file", new {
				projectPath = late.Dir,
				filePath    = ".editorconfig",
				content     = "root = true\n\n[*.cs]\nindent_style = space\nindent_size = 2\n",
				createNew   = true
			});
			
			if(written?["error"] is not null)
				return (false, $"FAIL  (write: {writtenText}) [{sw.ElapsedMilliseconds}ms]");
			
			var (preview, previewText) = await Call("roslyn_preview_code_fix", new { projectPath = late.Dir, filePath = "Caller.cs", line = 7, column = 10, diagnosticId = "CS0117" });
			var diff = preview?["diff"]?.GetValue<string>().Replace("\r\n", "\n", StringComparison.Ordinal) ?? string.Empty;
			
			if(!diff.Contains("+  internal static void Missing()\n+  {\n", StringComparison.Ordinal))
				return (false, $"FAIL  (preview after the config: {previewText}) [{sw.ElapsedMilliseconds}ms]");
			
			// Deleted behind the server's back, the config is reported by the watcher, and the
			// style goes with it. What replaces it is not asserted: a config further up the
			// machine's directories may apply, or Roslyn's defaults.
			File.Delete(late.PathOf(".editorconfig"));
			
			var goneText = previewText;
			
			for(var attempt = 0; attempt < 50; attempt++) {
				
				await Task.Delay(200);
				
				(var gone, goneText) = await Call("roslyn_preview_code_fix", new { projectPath = late.Dir, filePath = "Caller.cs", line = 7, column = 10, diagnosticId = "CS0117" });
				
				if(gone?["diff"]?.GetValue<string>() is { } unstyled && !unstyled.Contains("+  internal static", StringComparison.Ordinal))
					return (true, $"PASS  [{sw.ElapsedMilliseconds}ms]");
			}
			
			return (false, $"FAIL  (the deleted config still applies: {goneText}) [{sw.ElapsedMilliseconds}ms]");
		}
		
		List<TestCase> tests = [
			new("list offers Roslyn's refactorings and keeps dialog actions out", RunNoDialogActions),
			new("roslyn_info counts the analyzer references without providers and lists them only on request", RunInfoSources),
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
			new("a private .editorconfig shapes extract method (MSBuild)", () => RunStyledExtract(styled)),
			new("a private .editorconfig shapes a code fix (MSBuild)", () => RunStyledFix(styled)),
			new("a private .editorconfig shapes extract method (AdhocWorkspace)", () => RunStyledExtract(styledAdhoc)),
			new("a private .editorconfig shapes a code fix (AdhocWorkspace)", () => RunStyledFix(styledAdhoc)),
			new("an .editorconfig written after an AdhocWorkspace loaded is used", RunConfigAfterLoad),
		];
		
		return new TestGroup($"Code Refactorings ({tests.Count} tests)", tests, Teardown: () => {
			
			fx.Dispose();
			styled.Dispose();
			styledAdhoc.Dispose();
			
			return Task.CompletedTask;
		});
	}
	
	const string InvertSource = "namespace Fixture;\n\npublic static class Invert\n{\n\tpublic static string Describe(bool big)\n\t{\n\t\tif(big)\n\t\t\treturn \"big\";\n\t\telse\n\t\t\treturn \"small\";\n\t}\n}\n";
	
	// The style of this repository, which is none of Roslyn's defaults (#413).
	const string StyleConfig = "root = true\n\n[*.cs]\nindent_style = tab\ncsharp_new_line_before_open_brace = methods, types\ncsharp_space_after_keywords_in_control_flow_statements = false\n";
	
	// Lines 9 to 14 are the loop Extract method moves.
	const string StyledSource = "namespace Fixture;\n\npublic static class Styled\n{\n\tpublic static int Sum(int[] items)\n\t{\n\t\tvar total = 0;\n\n\t\tforeach(var item in items) {\n\t\t\tif(item > 0) {\n\t\t\t\ttotal += item;\n\t\t\t\ttotal += 1;\n\t\t\t}\n\t\t}\n\n\t\treturn total;\n\t}\n}\n";
	
	// CS0117 on line 7: Target has no method named Missing.
	const string CallerSource = "namespace Fixture;\n\npublic static class Caller\n{\n\tpublic static void Run()\n\t{\n\t\tTarget.Missing();\n\t}\n}\n\npublic static class Target\n{\n}\n";
	
	// A statement to extract, then thirty methods the refactoring does not touch. Every blank line is
	// indented, so the only empty line of the file is the one after its last newline.
	static readonly string WideSource =
		"namespace Fixture;\n\t\npublic static class Wide\n{\n\tpublic static double Area(double side)\n\t{\n\t\tvar squared = side * side;\n\t\t\n\t\treturn squared;\n\t}\n"
		+ string.Concat(Enumerable.Range(1, 30).Select(i => $"\t\n\tpublic static int M{i}(int value)\n\t{{\n\t\treturn value + {i};\n\t}}\n"))
		+ "}\n";
}
