// Tools called with projectPath omitted or partly given: owning-project inference, file lookup and
// its ambiguity (#362), case rules, and editing and drift through the default workspace.
static partial class DefaultWorkspaceTests
{
	static List<TestCase> FileTests(Fixture f)
	{
		var rootedCtx        = f.Rooted;
		var bareCtx          = f.Bare;
		var fx               = f.Fx;
		var caseFx           = f.CaseFx;
		var caseDir          = f.CaseDir;
		var caseSensitive    = f.CaseSensitive;
		var caseDirState     = f.CaseDirState;
		var appProject       = f.AppProject;
		var libProject       = f.LibProject;
		
		// Outlines filePath and checks it named the expected file: the type it declares, or ambiguous_file
		// with both candidates when expectType is null. projectPath null exercises the default workspace.
		async Task<(bool pass, string message)> AssertOutline(string filePath, string? projectPath, string? expectType)
		{
			
			var (ok, data) = projectPath is null
				? await Call(rootedCtx, "roslyn_get_file_outline", new { filePath })
				: await Call(rootedCtx, "roslyn_get_file_outline", new { filePath, projectPath });
			
			if(expectType is null)
				
				return Verdict(ok && data?["error"]?.GetValue<string>() == "ambiguous_file" && data?["found_in"]?.AsArray().Count == 2,
					$"expected ambiguous_file naming both files, got {data?.ToJsonString() ?? "null"}");
			
			string?[] types = [.. data?["types"]?.AsArray().Select(t => t?["name"]?.GetValue<string>()) ?? []];
			
			return Verdict(ok && types is [var only] && only == expectType, $"expected type {expectType}, got {data?.ToJsonString() ?? "null"}");
		}
		
		return new List<TestCase> {
			
			// Guard: without both servers the rest of the group can only produce noise.
			new("DefaultWorkspace: rooted and bare servers initialize",
				() => Task.FromResult(Verdict(rootedCtx is not null && bareCtx is not null, "a server did not answer initialize"))),
			
			// Solution-level: text search is solution-wide, so both projects' files must appear.
			new("DefaultWorkspace: search_files without projectPath spans the solution", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_search_files", new { pattern = "class (Greeter|Consumer)" });
				var files      = data?["matches"]?.AsArray().Select(m => m?["file"]?.GetValue<string>() ?? "").ToArray() ?? [];
				
				return Verdict(ok
					&& files.Any(f => f.Contains("Greeter.cs"))
					&& files.Any(f => f.Contains("Consumer.cs")),
					$"expected hits in both projects, got [{string.Join(", ", files)}]");
			}),
			
			// File-anchored: Lib is not the solution's default project (see the class remarks).
			new("DefaultWorkspace: get_file_outline infers the owning project", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_file_outline", new { filePath = "Lib/Greeter.cs" });
				var types      = data?["types"]?.AsArray().Select(t => t?["name"]?.GetValue<string>()).ToArray() ?? [];
				
				return Verdict(ok && types.Contains("Greeter"), $"expected type Greeter, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// A bare file name matching a different file in each project must not silently pick the first
			// project: that would outline App's Shared.cs for a caller that meant Lib's, or the reverse.
			new("DefaultWorkspace: an ambiguous file name is rejected with candidates", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_file_outline", new { filePath = "Shared.cs" });
				var found      = data?["found_in"]?.AsArray().Count ?? 0;
				
				return Verdict(ok && data?["error"]?.GetValue<string>() == "ambiguous_file" && found == 2,
					$"expected ambiguous_file naming both projects, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #362: the name of a file at the project root is exact, so it must not resolve to a nested
			// file with the same trailing path. With projectPath omitted, the default workspace narrows to
			// App first and the lookup then runs against App's root — both routes must agree.
			new("DefaultWorkspace: a root file name beats a nested file with the same name (default)",
				() => AssertOutline("Program.cs", null, "RootProgram")),
			
			new("DefaultWorkspace: a root file name beats a nested file with the same name (explicit)",
				() => AssertOutline("Program.cs", appProject, "RootProgram")),
			
			new("DefaultWorkspace: a nested path still selects the nested file",
				() => AssertOutline("Tools/Program.cs", appProject, "ToolsProgram")),
			
			// Two nested files with nothing at the root: no exact match, so a bare name is ambiguous
			// within one project — the cross-project check cannot catch it.
			new("DefaultWorkspace: a name matching two nested files is ambiguous (default)",
				() => AssertOutline("Dup.cs", null, null)),
			
			new("DefaultWorkspace: a name matching two nested files is ambiguous (explicit)",
				() => AssertOutline("Dup.cs", libProject, null)),
			
			new("DefaultWorkspace: a longer path picks one of two nested files (default)",
				() => AssertOutline("A/Dup.cs", null, "DupA")),
			
			new("DefaultWorkspace: a longer path picks one of two nested files (explicit)",
				() => AssertOutline("B/Dup.cs", libProject, "DupB")),
			
			// The project-relative exact path is canonical, so spellings that are no raw suffix of any file
			// still name the project's root file.
			new("DefaultWorkspace: './Program.cs' and 'Tools/../Program.cs' name the project's root file", async () => {
				
				var dot  = await AssertOutline("./Program.cs",        appProject, "RootProgram");
				var back = await AssertOutline("Tools/../Program.cs", appProject, "RootProgram");
				
				return dot.pass ? back : dot;
			}),
			
			// get_line_count takes a list that can span projects, so with projectPath omitted each path not
			// on disk finds its own project: Dup.cs lives in Lib, not in the solution's default project App.
			new("DefaultWorkspace: get_line_count resolves each entry's project when projectPath is omitted", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_line_count", new { filePaths = "Dup.cs,A/Dup.cs" });
				var files      = data?["files"]?.AsArray();
				var dup        = files?[0]?["error"]?.GetValue<string>() ?? "";
				var counted    = files?[1]?["line_count"] is not null;
				
				return Verdict(ok && dup.Contains("ambiguous") && counted,
					$"expected Dup.cs ambiguous and A/Dup.cs counted, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// A positional rename reports the ambiguous file with the standard structured error, not as text
			// inside a message — a client must be able to read error and found_in.
			new("DefaultWorkspace: preview_rename at a position in an ambiguous file returns ambiguous_file", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_preview_rename", new {
					symbolName  = "DupA",
					newName     = "DupRenamed",
					projectPath = libProject,
					filePath    = "Dup.cs",
					line        = 3,
					column      = 16
				});
				
				return Verdict(ok && data?["error"]?.GetValue<string>() == "ambiguous_file" && data?["found_in"]?.AsArray().Count == 2,
					$"expected a structured ambiguous_file, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// An exact name follows the file system's case rules: where Name.cs and name.cs are two files,
			// each name selects its own — a case-insensitive comparison would take whichever came first.
			new("DefaultWorkspace: an exact name is case-sensitive where the file system is", async () => {
				
				if(!caseSensitive)
					
					return Skip.NotApplicable("names that differ only in case are one file on this file system");
				
				var upper = await AssertOutline("Case/Name.cs", caseFx.Dir, "UpperName");
				var lower = await AssertOutline("Case/name.cs", caseFx.Dir, "LowerName");
				
				if(upper.pass && lower.pass)
					
					return upper;
				
				// Says whether the compilation holds both files at all, which the outline cannot tell apart
				// from a lookup that picked the wrong one.
				var (_, types) = await Call(rootedCtx, "roslyn_list_types", new { projectPath = caseFx.Dir, namespaceFilter = "Probe" });
				
				return (false, $"{(upper.pass ? lower : upper).message}; types in Probe: {types?.ToJsonString() ?? "null"}");
			}),
			
			// MSBuildWorkspace on Linux loads one of two files whose names differ only in case. The other
			// name must then be "not found", never an answer about the loaded file — which one is loaded is
			// Roslyn's choice, so each name may resolve or be missing, but never to the other class.
			new("DefaultWorkspace: a case variant missing from the compilation is not found, not swapped", async () => {
				
				if(!caseSensitive)
					
					return Skip.NotApplicable("names that differ only in case are one file on this file system");
				
				var found = 0;
				
				foreach(var (name, type) in new[] { ("Case/Name.cs", "UpperName"), ("Case/name.cs", "LowerName") }) {
					
					var (ok, data) = await Call(rootedCtx, "roslyn_get_file_outline", new { filePath = name, projectPath = appProject });
					string?[] types = [.. data?["types"]?.AsArray().Select(t => t?["name"]?.GetValue<string>()) ?? []];
					var notFound    = (data?["error"]?.GetValue<string>() ?? "").Contains("not found");
					
					if(!ok || !(notFound || types is [var only] && only == type))
						
						return (false, $"FAIL  ({name}: expected {type} or not found, got {data?.ToJsonString() ?? "null"})");
					
					if(!notFound)
						found++;
				}
				
				return found > 0 ? (true, "PASS") : (false, "FAIL  (neither name resolved; expected the loaded one to)");
			}),
			
			// #391: where names are case-sensitive, Boundary/ROOT is a directory beside the workspace root
			// Boundary/root, not the root itself. The boundary decided case rules per platform and counted
			// it as inside on Windows and macOS — on a case-sensitive directory or volume, a way out.
			new("DefaultWorkspace: a sibling whose name differs from the root only in case is outside the workspace", async () => {
				
				if(caseDirState != CaseSensitivity.Sensitive)
					
					return TestFixtures.CaseSkip(caseDirState);
				
				var (ok, data) = await Call(rootedCtx, "roslyn_read_file", new { filePath = caseDir.PathOf("Boundary/ROOT/Secret.txt"), projectPath = caseDir.PathOf("Boundary/root/Root.csproj") });
				var body       = data?.ToJsonString() ?? "null";
				
				return Verdict(ok && data?["error"] is not null && data?["file"] is null && !body.Contains(CaseSecret),
					$"expected a refusal, got {body}");
			}),
			
			// #391: the other half — where names ignore case, another spelling of a path inside the workspace
			// is that path. The boundary now asks the file system about a case-only difference instead of
			// assuming it away, and must still let this one through.
			new("DefaultWorkspace: another case of a path inside the workspace is inside where names ignore case", async () => {
				
				if(caseSensitive)
					
					return Skip.NotApplicable("names that differ only in case are two files on this file system");
				
				var (ok, data) = await Call(rootedCtx, "roslyn_read_file", new { filePath = fx.PathOf("LIB/Greeter.cs"), projectPath = libProject });
				
				return Verdict(ok && data?["error"] is null && data?["file"] is not null,
					$"expected the file, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// A position-based semantic tool needs the owning project's semantic model, not just its text.
			new("DefaultWorkspace: get_symbol_info resolves in a non-default project", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_symbol_info", new { filePath = "Lib/Greeter.cs", line = 5, column = 28 });
				var json       = data?.ToJsonString() ?? "";
				
				return Verdict(ok && json.Contains("Text") && data?["error"] is null, $"expected Text, got {json}");
			}),
			
			// Editing a C# file with only filePath: the write lands and the workspace sees it.
			new("DefaultWorkspace: replace_in_code edits with only filePath", async () => {
				
				var (ok, _) = await Call(rootedCtx, "roslyn_replace_in_code", new {
					filePath    = "Lib/Greeter.cs",
					nodeKind    = "method",
					textPattern = "Greet",
					replacement = "public string Greet() => \"hello\";"
				});
				
				// The workspace's view of the file, through a content search: roslyn_read_file returns its
				// source as a raw text block, which this group's JSON-only Call helper does not see.
				var (readOk, read) = await Call(rootedCtx, "roslyn_search_files", new { pattern = "\"hello\"", mode = "literal", filePattern = "Greeter.cs" });
				var lines          = read?["matches"]?.AsArray().Select(m => m?["text"]?.GetValue<string>() ?? "") ?? [];
				
				return Verdict(ok && readOk
					&& lines.Any(l => l.Contains("\"hello\""))
					&& File.ReadAllText(fx.PathOf("Lib/Greeter.cs")).Contains("\"hello\""),
					"edit not visible in the workspace or on disk");
			}),
			
			// A file no project tracks falls back to the default solution rather than failing.
			new("DefaultWorkspace: insert_lines on an untracked file", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_insert_lines", new { filePath = "Notes.md", text = "- added", atLine = 2 });
				
				return Verdict(ok && data?["error"] is null && File.ReadAllText(fx.PathOf("Notes.md")).Contains("- added"),
					$"expected the line on disk, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// check_drift resolves the workspace before peeking, so an omitted path is a normal call.
			new("DefaultWorkspace: check_drift without projectPath", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_check_drift", new { });
				
				return Verdict(ok && data?["drifted"] is not null && data?["error"] is null,
					$"expected a drift report, got {data?.ToJsonString() ?? "null"}");
			}),
		};
	}
}
