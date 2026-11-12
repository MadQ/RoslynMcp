using System.Text.Json.Nodes;

// No tool needs to be told its project (#296): a tool that names a symbol finds the project that
// declares it, in the default solution or one given, and a tool that covers a project covers every
// project of a solution. The fixture's solution lists App first and Lib second, and App references
// Lib, so an answer taken from the first project alone is told apart from the right one.
static partial class DefaultWorkspaceTests
{
	static List<TestCase> SymbolProjectTests(Fixture f)
	{
		var rootedCtx = f.Rooted;
		var fx        = f.Fx;
		var single    = f.Single;
		
		return new List<TestCase> {
			
			// The issue's first acceptance case. Greet is declared in Lib and called from App. With
			// no projectPath the references must be found, and the one in App's Consumer.cs among
			// them: the search starts from the declaring project and covers the solution.
			new("DefaultWorkspace: find_references without projectPath finds a symbol declared in the second project", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_find_references", new { symbolName = "Greet", containingType = "Greeter" });
				var text       = data?.ToJsonString() ?? "null";
				
				return Verdict(ok && data?["error"] is null && text.Contains("Consumer.cs"),
					$"expected the reference in App/Consumer.cs; got {text}");
			}),
			
			// The second acceptance case. TwiceDeclared is a type in App and another in Lib. The
			// server must never prefer one: the call fails with one candidate per project, each
			// naming its project, and passing a candidate's project back must then succeed with
			// that project's declaration.
			new("DefaultWorkspace: a name declared in two projects is a candidate list with the projects", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_symbol_definition", new { symbolName = "TwiceDeclared" });
				var projects   = data?["candidates"]?.AsArray().Select(c => c?["project"]?.GetValue<string>() ?? "").ToArray() ?? [];
				var lib        = projects.FirstOrDefault(p => p.EndsWith("Lib.csproj")) ?? "";
				
				var (retryOk, retry) = await Call(rootedCtx, "roslyn_get_symbol_definition", new { symbolName = "TwiceDeclared", projectPath = lib });
				
				return Verdict(ok
					&& projects.Length == 2 && projects.Any(p => p.EndsWith("App.csproj")) && lib.Length > 0
					&& retryOk && retry?["error"] is null && (retry?.ToJsonString() ?? "").Contains("Lib"),
					$"expected two candidates naming App.csproj and Lib.csproj, and Lib's declaration on the retry; got {data?.ToJsonString() ?? "null"} / {retry?.ToJsonString() ?? "null"}");
			}),
			
			// What a tool is after decides which declarations count. Shadowed is a class in Lib and
			// a method in App, and the method calls ShadowHelper. A type before any member, applied
			// across the solution, sent the call graph of the method to Lib, where the class has no
			// calls: an empty answer with no error. The call graph must show the call, so it was
			// taken in App; the type tool must land in Lib. A tool that searches by name takes
			// neither side: both are candidates, each with its project.
			new("DefaultWorkspace: the kind of symbol a tool is after picks the project, or neither", async () => {
				
				var (graphOk, graph)     = await Call(rootedCtx, "roslyn_get_call_graph", new { symbolName = "Shadowed" });
				var (membersOk, members) = await Call(rootedCtx, "roslyn_get_type_members", new { typeName = "Shadowed" });
				var (refsOk, refs)       = await Call(rootedCtx, "roslyn_find_references", new { symbolName = "Shadowed" });
				
				var projects = refs?["candidates"]?.AsArray().Select(c => c?["project"]?.GetValue<string>() ?? "").ToArray() ?? [];
				
				return Verdict(graphOk && (graph?.ToJsonString() ?? "").Contains("ShadowHelper")
					&& membersOk && members?["error"] is null
					&& refsOk && projects.Length == 2 && projects.Any(p => p.EndsWith("App.csproj")) && projects.Any(p => p.EndsWith("Lib.csproj")),
					$"expected the call to ShadowHelper, the type's members, and two candidates for the search; got {graph?.ToJsonString() ?? "null"} / {members?.ToJsonString() ?? "null"} / {refs?.ToJsonString() ?? "null"}");
			}),
			
			// A qualified containing type must narrow. Mark is a member of TwiceDeclared in both
			// projects; by the type's simple name that is two projects, and qualifying it did not
			// help, since only the last part was compared. Lib.TwiceDeclared is one of them: the
			// body must be Lib's, the one that returns 2.
			new("DefaultWorkspace: a qualified containing type picks one of two projects", async () => {
				
				var (bareOk, bare) = await Call(rootedCtx, "roslyn_get_member_body", new { symbolName = "Mark", containingType = "TwiceDeclared" });
				
				await rootedCtx!.SendAsync(new { jsonrpc = "2.0", id = rootedCtx.NextId(), method = "tools/call", @params = new { name = "roslyn_get_member_body", arguments = new { symbolName = "Mark", containingType = "Lib.TwiceDeclared" } } });
				
				var blocks = (await rootedCtx.ReceiveAsync())?["result"]?["content"]?.AsArray();
				var body   = blocks is { Count: > 1 } ? blocks[1]?["text"]?.GetValue<string>() ?? "" : "";
				
				return Verdict(bareOk && bare?["candidates"]?.AsArray().Count == 2 && body.Contains("=> 2"),
					$"expected two candidates by the simple name and Lib's body by the qualified one; got {bare?.ToJsonString() ?? "null"} / {body}");
			}),
			
			// The frameworks of a multi-targeted project are one project. Split has two target
			// frameworks, so Roslyn holds it twice, and its class is declared "twice"; a lookup
			// through its solution must find one project and return the member, not a candidate
			// list of the project against itself.
			new("DefaultWorkspace: the target frameworks of one project are not two declaring projects", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_member_body", new { symbolName = "M", containingType = "Split", projectPath = single.PathOf("Tfm/Tfm.slnx") });
				
				return Verdict(ok && data?["candidates"] is null && (data?["file"]?.GetValue<string>() ?? "").Contains("Split.cs"),
					$"expected M from Split.cs; got {data?.ToJsonString() ?? "null"}");
			}),
			
			// A solution may list a project that lies outside its own directory, under another
			// solution: Sln/Apart.slnx lists ../Apart/Apart.csproj, and the nearest solution above
			// Apart is Single.slnx, which does not list it. The project found for the symbol must be
			// read in the solution it was found in, not in whichever solution its path leads to.
			new("DefaultWorkspace: a symbol's project outside its solution's directory is read in that solution", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_member_body", new { symbolName = "Warned", projectPath = single.PathOf("Sln/Apart.slnx") });
				
				return Verdict(ok && (data?["file"]?.GetValue<string>() ?? "").Contains("Warned.cs"),
					$"expected Warned from Apart/Warned.cs; got {data?.ToJsonString() ?? "null"}");
			}),
			
			// A name with no declaration in source is not "not found" when it is a symbol of a
			// referenced assembly: IDisposable is declared in no project and seen by all. The call
			// must go on with a project that sees it and answer, not fail for want of a declaration.
			new("DefaultWorkspace: a symbol of a referenced assembly is found without projectPath", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_find_implementations", new { symbolName = "IDisposable" });
				
				return Verdict(ok && data?["error"] is null,
					$"expected an answer for IDisposable; got {data?.ToJsonString() ?? "null"}");
			}),
			
			// The third acceptance case. The warning (CS0168) is in Lib, the second project. With no
			// projectPath the diagnostics must cover it, and say which project it is in, since the
			// answer now mixes projects.
			new("DefaultWorkspace: get_diagnostics without projectPath covers every project and names each item's", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_diagnostics", new { severity = "warnings", take = 200 });
				
				var inLib = data?["items"]?.AsArray().FirstOrDefault(item =>
					item?["code"]?.GetValue<string>() == "CS0168" && (item["file"]?.GetValue<string>() ?? "").Contains("Warn.cs"));
				
				return Verdict(ok && inLib?["project"]?.GetValue<string>() == "Lib",
					$"expected CS0168 in Lib/Warn.cs with project Lib; got {data?.ToJsonString() ?? "null"}");
			}),
			
			// list_types answered for one project. Without projectPath it lists both projects' types,
			// each once and under its own project: Greeter is visible from App too, through the
			// reference, and must not be listed a second time as App's.
			new("DefaultWorkspace: list_types without projectPath lists every project's types under their project", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_list_types", new { take = 500 });
				var types      = data?["types"]?.AsArray().Select(t => t?.GetValue<string>() ?? "").ToArray() ?? [];
				
				return Verdict(ok
					&& types.Any(t => t.Contains("Greeter") && t.EndsWith("[Lib]"))
					&& types.Any(t => t.Contains("Consumer") && t.EndsWith("[App]"))
					&& types.Count(t => t.Contains("Greeter")) == 1,
					$"expected Greeter once under [Lib] and Consumer under [App]; got {string.Join(", ", types)}");
			}),
			
			// get_project_info for a solution is every project of it, each in the single-project
			// shape, and a single project keeps that shape as it was.
			new("DefaultWorkspace: get_project_info without projectPath describes every project of the solution", async () => {
				
				var (ok, data)   = await Call(rootedCtx, "roslyn_get_project_info", new { });
				var names        = data?["projects"]?.AsArray().Select(p => p?["name"]?.GetValue<string>() ?? "").ToArray() ?? [];
				var (oneOk, one) = await Call(rootedCtx, "roslyn_get_project_info", new { projectPath = fx.PathOf("Lib/Lib.csproj") });
				
				return Verdict(ok && names.Contains("App") && names.Contains("Lib")
					&& oneOk && one?["name"]?.GetValue<string>() == "Lib" && one["projects"] is null,
					$"expected projects App and Lib, and the flat shape for Lib alone; got {data?.ToJsonString() ?? "null"} / {one?.ToJsonString() ?? "null"}");
			}),
			
			// find_unused looked at one project's symbols. Unreferenced is an internal class of Lib
			// that nothing uses; without projectPath it must be reported, with its project.
			new("DefaultWorkspace: find_unused without projectPath reports the second project's unused symbols", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_find_unused", new { take = 200 });
				
				// On the one entry: another entry's project would not do.
				var entry = data?["unused"]?.AsArray().FirstOrDefault(item => (item?["name"]?.GetValue<string>() ?? "").Contains("Unreferenced"));
				
				return Verdict(ok && entry?["project"]?.GetValue<string>() == "Lib",
					$"expected Unreferenced with project Lib; got {data?.ToJsonString() ?? "null"}");
			}),
			
			// A snippet names no symbol and no file, so nothing says which project's types it is to be
			// checked against: with includeSemantics and neither path, on a solution, the call must
			// say that it needs a project and that a filePath names one. With a file of Lib it must
			// resolve Lib's type, which App's compilation would not: the snippet uses an internal
			// type of Lib that only Lib itself can name.
			new("DefaultWorkspace: check_syntax with semantics asks for a project or a file, and takes the file's project", async () => {
				
				var (ok, data)         = await Call(rootedCtx, "roslyn_check_syntax", new { code = "Lib.WarnHolder holder = null!;", includeSemantics = true });
				var (fileOk, withFile) = await Call(rootedCtx, "roslyn_check_syntax", new { code = "Lib.WarnHolder holder = null!;", includeSemantics = true, filePath = "Lib/Greeter.cs" });
				
				return Verdict(ok && data?["error"]?.GetValue<string>() == "solution_needs_project" && (data["hint"]?.GetValue<string>() ?? "").Contains("filePath")
					&& fileOk && withFile?["error_count"]?.GetValue<int>() == 0,
					$"expected a hint naming filePath, and no errors through Lib/Greeter.cs; got {data?.ToJsonString() ?? "null"} / {withFile?.ToJsonString() ?? "null"}");
			}),
			
			// The rename workflow with no projectPath on either call: the preview finds Lib by the
			// symbol, and the apply takes the workspace from the token. The file on disk must hold
			// the new name afterwards.
			new("DefaultWorkspace: a rename previews and applies without projectPath", async () => {
				
				var (ok, preview) = await Call(rootedCtx, "roslyn_preview_rename", new { symbolName = "OldName", newName = "NewName", containingType = "NoProjectRename" });
				
				if(!ok || preview?["token"]?.GetValue<string>() is not { } token)
					
					return (false, $"FAIL  (the preview: {preview?.ToJsonString() ?? "null"})");
				
				var (applyOk, applied) = await Call(rootedCtx, "roslyn_apply_rename", new { token, approval = "y" });
				var onDisk             = File.ReadAllText(fx.PathOf("Lib/NoProjectRename.cs"));
				
				return Verdict(applyOk && onDisk.Contains("NewName") && !onDisk.Contains("OldName"),
					$"expected NewName in Lib/NoProjectRename.cs; got {applied?.ToJsonString() ?? "null"} and the file: {onDisk}");
			}),
		};
	}
}
