using System.Text.Json.Nodes;

// A solution as projectPath (#376, #378): one-project tools reject it and name the projects,
// diagnostics cover every project of it, and a code fix round-trips through it.
static partial class DefaultWorkspaceTests
{
	static List<TestCase> SolutionTests(Fixture f)
	{
		var rootedCtx = f.Rooted;
		var fx        = f.Fx;
		var single    = f.Single;
		var adhocSln    = f.AdhocSln;
		var splitLinked = f.SplitLinked;
		
		return new List<TestCase> {
			
			// #376, #296: a tool that works on one project, given the solution, used to answer for the
			// solution's first project (App) — and Greet, which is in Lib, was "not found". A tool
			// that names a symbol now finds the project that declares it. One that names nothing a
			// project could be found by (a snippet checked against "the project") must still say
			// that it needs one, and name them, so the agent can pass one back.
			new("DefaultWorkspace: a solution passed to a one-project tool finds the symbol's project, or lists the projects", async () => {
				
				var solution          = fx.PathOf("DefaultWs.slnx");
				var (ok, data)        = await Call(rootedCtx, "roslyn_get_member_body", new { symbolName = "Greet", containingType = "Greeter", projectPath = solution });
				var (checkOk, check)  = await Call(rootedCtx, "roslyn_check_syntax", new { code = "int x = 1;", includeSemantics = true, projectPath = solution });
				var found             = check?["found_projects"]?.AsArray().Select(p => p?.GetValue<string>() ?? "").ToArray() ?? [];
				
				return Verdict(ok && (data?["file"]?.GetValue<string>() ?? "").Contains("Greeter.cs")
					&& checkOk && check?["error"]?.GetValue<string>() == "solution_needs_project"
					&& found.Any(p => p.EndsWith("App.csproj")) && found.Any(p => p.EndsWith("Lib.csproj")),
					$"expected Greet from Lib/Greeter.cs, and solution_needs_project naming App and Lib; got {data?.ToJsonString() ?? "null"} / {check?.ToJsonString() ?? "null"}");
			}),
			
			// #376: Stray.csproj lies under the solution's directory, so it loads the solution — which
			// does not contain it. It used to answer for the solution's first project.
			new("DefaultWorkspace: a .csproj the loaded solution does not contain is an error", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_member_body", new { symbolName = "Greet", projectPath = fx.PathOf("Stray/Stray.csproj") });
				
				return Verdict(ok && data?["error"]?.GetValue<string>() == "project_not_in_workspace",
					$"expected project_not_in_workspace, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #376: diagnostics for a solution cover every project — the warning is in Lib, not in the
			// first project — and a file-scoped check finds the file in whichever project holds it.
			new("DefaultWorkspace: get_diagnostics for a solution checks every project", async () => {
				
				var solution         = fx.PathOf("DefaultWs.slnx");
				var (ok, data)       = await Call(rootedCtx, "roslyn_get_diagnostics", new { projectPath = solution, severity = "warnings", take = 200 });
				var (fileOk, onFile) = await Call(rootedCtx, "roslyn_get_diagnostics", new { projectPath = solution, filePath = "Lib/Warn.cs", severity = "warnings" });
				
				static bool HasUnused(JsonNode? result)
					=> result?["items"]?.AsArray().Any(item => item?["code"]?.GetValue<string>() == "CS0168" && (item?["file"]?.GetValue<string>() ?? "").Contains("Warn.cs")) == true;
				
				return Verdict(ok && fileOk && HasUnused(data) && HasUnused(onFile),
					$"expected CS0168 in Lib/Warn.cs both ways; got {data?.ToJsonString() ?? "null"} / {onFile?.ToJsonString() ?? "null"}");
			}),
			
			// #376 review W1: a file no project has, with projectPath omitted. The default solution
			// stays, and the tool that needs a compilation must say the file is not found — not that a
			// solution was given where one project was needed.
			new("DefaultWorkspace: a file in no project is 'not found', not a solution error", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_file_outline", new { filePath = "Lib/Gretter.cs" });
				var error      = data?["error"]?.GetValue<string>() ?? "";
				
				return Verdict(ok && error.Contains("not found in any project"),
					$"expected 'not found in any project', got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #376 review W2: one entry the workspace cannot find fails that entry, not the batch.
			new("DefaultWorkspace: get_line_count reports a file in no project per entry", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_line_count", new { filePaths = "App/Consumer.cs,Lib/Nowhere.cs" });
				var files      = data?["files"]?.AsArray() ?? [];
				var counted    = files.Any(f => (f?["file"]?.GetValue<string>() ?? "").Contains("Consumer.cs") && f?["line_count"] is not null);
				var missing    = files.Any(f => (f?["file"]?.GetValue<string>() ?? "").Contains("Nowhere.cs") && f?["error"] is not null);
				
				return Verdict(ok && data?["error"] is null && counted && missing,
					$"expected Consumer.cs counted and Nowhere.cs with an error; got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #376 review W3: an explicit solution with filePath is narrowed to the file's project,
			// as the default solution is.
			new("DefaultWorkspace: an explicit solution with filePath uses the file's project", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_file_outline", new { filePath = "Lib/Greeter.cs", projectPath = fx.PathOf("DefaultWs.slnx") });
				var types      = data?["types"]?.AsArray().Select(t => t?["name"]?.GetValue<string>()).ToArray() ?? [];
				
				return Verdict(ok && types.Contains("Greeter"), $"expected the outline of Greeter, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #376 review W4: Shared.cs is App/Shared.cs and Lib/Shared.cs. Diagnosing it across the
			// solution must be ambiguous_file, not the two files' diagnostics merged.
			new("DefaultWorkspace: get_diagnostics with a solution and an ambiguous file name is ambiguous_file", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_diagnostics", new { projectPath = fx.PathOf("DefaultWs.slnx"), filePath = "Shared.cs" });
				
				return Verdict(ok && data?["error"]?.GetValue<string>() == "ambiguous_file",
					$"expected ambiguous_file, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #376 review W5: Beside.csproj lies under a solution of one project that does not list it.
			// The solution's one project used to answer for it; the solution itself still names it.
			new("DefaultWorkspace: a solution of one project still rejects a .csproj it does not list", async () => {
				
				var (ok, data)   = await Call(rootedCtx, "roslyn_get_member_body", new { symbolName = "Value", projectPath = single.PathOf("Beside/Beside.csproj") });
				var (slnOk, sln) = await Call(rootedCtx, "roslyn_get_member_body", new { symbolName = "Value", projectPath = single.PathOf("Single.slnx") });
				
				return Verdict(ok && data?["error"]?.GetValue<string>() == "project_not_in_workspace" && slnOk && sln?["error"] is null,
					$"expected project_not_in_workspace for Beside.csproj and a body through Single.slnx; got {data?.ToJsonString() ?? "null"} / {sln?.ToJsonString() ?? "null"}");
			}),
			
			// Review of #378: a project outside the solution's directory is still checked, and a project
			// with two target frameworks is checked under both — one warning only exists under each.
			new("DefaultWorkspace: get_diagnostics for a solution covers projects outside its directory and every target framework", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_diagnostics", new { projectPath = single.PathOf("Sln/Apart.slnx"), severity = "warnings", take = 200 });
				var messages   = data?["items"]?.AsArray().Select(item => item?["message"]?.GetValue<string>() ?? "").ToArray() ?? [];
				
				bool Has(string name) => messages.Any(message => message.Contains(name));
				
				return Verdict(ok && Has("outsideTheSolutionDirectory") && Has("onlyOnWindows") && Has("onlyElsewhere"),
					$"expected the warnings of Apart and of both Multi frameworks; got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #383: the build's Roslyn check covers every target framework, as get_diagnostics does. It used
			// to check one framework per project file, find that one's error only — or, with the error only
			// under the other framework, nothing — and go on to dotnet build. Both errors stop it here.
			new("DefaultWorkspace: build_project's Roslyn check covers every target framework", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_build_project", new { projectPath = single.PathOf("Tfm/Tfm.slnx") });
				var messages   = data?["errors"]?.AsArray().Select(item => item?["message"]?.GetValue<string>() ?? "").ToArray() ?? [];
				
				bool Has(string name) => messages.Any(message => message.Contains(name));
				
				return Verdict(ok && data?["source"]?.GetValue<string>() == "roslyn" && Has("missingOnWindows") && Has("missingElsewhere"),
					$"expected Roslyn errors under both frameworks; got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #383: dotnet build -f builds one framework, so the Roslyn check covers that one only — an
			// error that exists only under the other framework must not block the build. Asked once per
			// framework, so the test does not pass by the luck of which framework a lookup lands on.
			new("DefaultWorkspace: build_project with a target framework checks only that framework", async () => {
				
				async Task<(bool Ok, string Got)> Only(string framework, string expected, string excluded)
				{
					var (ok, data) = await Call(rootedCtx, "roslyn_build_project", new { projectPath = single.PathOf("Tfm/Split/Split.csproj"), targetFramework = framework });
					var messages   = data?["errors"]?.AsArray().Select(item => item?["message"]?.GetValue<string>() ?? "").ToArray() ?? [];
					
					return (ok && data?["source"]?.GetValue<string>() == "roslyn"
						&& messages.Any(message => message.Contains(expected)) && !messages.Any(message => message.Contains(excluded)),
						data?.ToJsonString() ?? "null");
				}
				
				var windows   = await Only("net10.0-windows", "missingOnWindows", "missingElsewhere");
				var elsewhere = await Only("net10.0",         "missingElsewhere", "missingOnWindows");
				
				return Verdict(windows.Ok && elsewhere.Ok,
					$"expected each framework's own error only; got {windows.Got} / {elsewhere.Got}");
			}),
			
			// #393 review: through a link to the project's directory, the path given is not the one the
			// workspace holds the project under. Both tools used to match no project by it and fall back
			// to the one compilation the path resolves to — one framework, one error.
			new("DefaultWorkspace: build_project and get_diagnostics through a directory link check every target framework", async () => {
				
				if(!splitLinked)
					
					return Skip.SetupUnavailable("cannot create symbolic links here");
				
				var project      = single.PathOf("Tfm/SplitLink/Split.csproj");
				var (ok, built)  = await Call(rootedCtx, "roslyn_build_project", new { projectPath = project });
				var (dOk, diags) = await Call(rootedCtx, "roslyn_get_diagnostics", new { projectPath = project, severity = "errors" });
				
				static bool Both(JsonNode? items)
				{
					var messages = items?.AsArray().Select(item => item?["message"]?.GetValue<string>() ?? "").ToArray() ?? [];
					
					return messages.Any(message => message.Contains("missingOnWindows")) && messages.Any(message => message.Contains("missingElsewhere"));
				}
				
				return Verdict(ok && dOk && built?["source"]?.GetValue<string>() == "roslyn" && Both(built?["errors"]) && Both(diags?["items"]),
					$"expected both frameworks' errors from each tool; got {built?.ToJsonString() ?? "null"} / {diags?.ToJsonString() ?? "null"}");
			}),
			
			// Review of #378: in an adhoc workspace the solution's projects have no files; the one
			// compilation is checked rather than none — "0 errors" for broken code was the result.
			new("DefaultWorkspace: get_diagnostics for a solution in an adhoc workspace still checks the code", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_diagnostics", new { projectPath = adhocSln.PathOf("Pinned.slnx"), severity = "errors", take = 200 });
				
				return Verdict(ok && data?["items"]?.AsArray().Any(item => item?["code"]?.GetValue<string>() == "CS0103") == true,
					$"expected CS0103 from Broken.cs; got {data?.ToJsonString() ?? "null"}");
			}),
			
			// Review of #378, round 2: the shared file is diagnosed in every project that compiles it —
			// P1 lacks OnlyInP2 and P2 lacks OnlyInP1 — not only in the first project found to have it.
			new("DefaultWorkspace: get_diagnostics for a file shared by two projects reports both projects' errors", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_diagnostics", new { projectPath = single.PathOf("Shared/Shared.slnx"), filePath = "Common/SharedSource.cs", severity = "errors" });
				var messages   = data?["items"]?.AsArray().Select(item => item?["message"]?.GetValue<string>() ?? "").ToArray() ?? [];
				
				return Verdict(ok && messages.Any(m => m.Contains("OnlyInP1")) && messages.Any(m => m.Contains("OnlyInP2")),
					$"expected the missing OnlyInP1 and OnlyInP2 both; got {data?.ToJsonString() ?? "null"}");
			}),
			
			// Review of #378, round 2: a preview given the solution binds its token to the project of
			// the file (P2, not the first project); applying it through the same solution must be
			// accepted, not refused as another workspace.
			new("DefaultWorkspace: a code fix previewed and applied through a solution round-trips", async () => {
				
				var solution       = single.PathOf("Shared/Shared.slnx");
				var (ok, preview)  = await Call(rootedCtx, "roslyn_preview_code_fix", new { projectPath = solution, filePath = "P2/FixMe.cs", line = 1, column = 24, diagnosticId = "CS0246", actionIndex = 0 });
				var token          = preview?["token"]?.GetValue<string>();
				
				if(!ok || token is null)
					
					return (false, $"FAIL  (preview gave no token: {preview?.ToJsonString() ?? "null"})");
				
				var (applyOk, applied) = await Call(rootedCtx, "roslyn_apply_code_fix", new { token, approval = "y", projectPath = solution });
				var text               = applied?.ToJsonString() ?? "null";
				
				return Verdict(applyOk && !text.Contains("mismatch") && applied?["error"] is null,
					$"expected the fix applied through the solution, got {text}");
			}),
		};
	}
}
