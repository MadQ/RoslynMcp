// Apply, build, clean and restore through a solution or another name of a project (#384): solution
// membership, projects the solution does not list, and project identity through links.
static partial class DefaultWorkspaceTests
{
	static List<TestCase> ApplyTests(Fixture f)
	{
		var rootedCtx   = f.Rooted;
		var fx          = f.Fx;
		var single      = f.Single;
		var adhocSln    = f.AdhocSln;
		var aliasLinked = f.AliasLinked;
		var dirLinked   = f.DirLinked;
		var libProject  = f.LibProject;
		
		return new List<TestCase> {
			
			// #384: a rename previewed in Lib.csproj and applied through the solution. The solution used to
			// answer for its first project (App), so the token's own workspace was refused as a mismatch.
			new("DefaultWorkspace: a rename previewed in one project applies through its solution", async () => {
				
				var (ok, preview) = await Call(rootedCtx, "roslyn_preview_rename", new { symbolName = "Before", newName = "After", containingType = "ApplyTarget", projectPath = libProject });
				var token         = preview?["token"]?.GetValue<string>();
				
				if(!ok || token is null)
					
					return (false, $"FAIL  (preview gave no token: {preview?.ToJsonString() ?? "null"})");
				
				var (applyOk, applied) = await Call(rootedCtx, "roslyn_apply_rename", new { token, approval = "y", projectPath = fx.PathOf("DefaultWs.slnx") });
				var renamed            = File.ReadAllText(fx.PathOf("Lib/ApplyTarget.cs")).Contains("After(");
				
				return Verdict(applyOk && applied?["error"] is null && renamed,
					$"expected Before renamed through the solution, got {applied?.ToJsonString() ?? "null"}");
			}),
			
			// #384: the same for a signature change.
			new("DefaultWorkspace: a signature change previewed in one project applies through its solution", async () => {
				
				var (ok, preview) = await Call(rootedCtx, "roslyn_change_signature", new {
					methodName     = "Shape",
					containingType = "ApplyTarget",
					addParameters  = """[{"name":"b","type":"int","defaultValue":"0"}]""",
					projectPath    = libProject
				});
				var token = preview?["token"]?.GetValue<string>();
				
				if(!ok || token is null)
					
					return (false, $"FAIL  (preview gave no token: {preview?.ToJsonString() ?? "null"})");
				
				var (applyOk, applied) = await Call(rootedCtx, "roslyn_apply_signature_change", new { token, approval = "y", projectPath = fx.PathOf("DefaultWs.slnx") });
				var changed            = File.ReadAllText(fx.PathOf("Lib/ApplyTarget.cs")).Contains("int b");
				
				return Verdict(applyOk && applied?["error"] is null && changed,
					$"expected Shape given a parameter through the solution, got {applied?.ToJsonString() ?? "null"}");
			}),
			
			// #384: membership, not any solution. Single.slnx does not list Lib.csproj, so a Lib token
			// applied through it is still refused, and the file is left alone.
			new("DefaultWorkspace: a token applied through a solution without its project is refused", async () => {
				
				var (ok, preview) = await Call(rootedCtx, "roslyn_preview_rename", new { symbolName = "Kept", newName = "Lost", containingType = "ApplyTarget", projectPath = libProject });
				var token         = preview?["token"]?.GetValue<string>();
				
				if(!ok || token is null)
					
					return (false, $"FAIL  (preview gave no token: {preview?.ToJsonString() ?? "null"})");
				
				var (applyOk, applied) = await Call(rootedCtx, "roslyn_apply_rename", new { token, approval = "y", projectPath = single.PathOf("Single.slnx") });
				var untouched          = !File.ReadAllText(fx.PathOf("Lib/ApplyTarget.cs")).Contains("Lost(");
				
				await Call(rootedCtx, "roslyn_apply_rename", new { token, approval = "n", projectPath = libProject });
				
				return Verdict(applyOk && applied?["error"]?.GetValue<string>() == "workspace mismatch" && untouched,
					$"expected workspace mismatch, got {applied?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review: a solution of one project names that project, for the preview as for every
			// lookup; the token must bind to Only.csproj, not to the root, or no apply can match it.
			new("DefaultWorkspace: a rename previewed and applied through a solution of one project round-trips", async () => {
				
				var solution      = single.PathOf("Single.slnx");
				var (ok, preview) = await Call(rootedCtx, "roslyn_preview_rename", new { symbolName = "Old", newName = "Renewed", containingType = "SoleTarget", projectPath = solution });
				var token         = preview?["token"]?.GetValue<string>();
				
				if(!ok || token is null)
					
					return (false, $"FAIL  (preview gave no token: {preview?.ToJsonString() ?? "null"})");
				
				var (applyOk, applied) = await Call(rootedCtx, "roslyn_apply_rename", new { token, approval = "y", projectPath = solution });
				var renamed            = File.ReadAllText(single.PathOf("Only/SoleTarget.cs")).Contains("Renewed(");
				
				return Verdict(applyOk && applied?["error"] is null && renamed,
					$"expected Old renamed through Single.slnx, got {applied?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review: a workspace pinned to adhoc has no project to build, whatever the path names.
			// A solution path is still a solution in adhoc mode, so it must not reach dotnet.
			new("DefaultWorkspace: build_project refuses a solution in an adhoc workspace", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_build_project", new { projectPath = adhocSln.PathOf("Pinned.slnx"), forceBuild = true });
				var error      = data?["error"]?.GetValue<string>() ?? "";
				
				return Verdict(ok && error.Contains("MSBuildWorkspace mode"),
					$"expected the MSBuild-only refusal, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review: Stray.csproj loads the solution above it, which does not list it. Restore must
			// target Stray.csproj — the project named — not the solution's first project (App) or nothing.
			new("DefaultWorkspace: restore_packages targets a .csproj its solution does not list", async () => {
				
				var assets     = fx.PathOf("Stray/obj/project.assets.json");
				var (ok, data) = await Call(rootedCtx, "roslyn_restore_packages", new { projectPath = fx.PathOf("Stray/Stray.csproj") });
				
				return Verdict(ok && data?["error"] is null && File.Exists(assets),
					$"expected Stray restored, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review, round 2: without forceBuild too. Stray has no compilation in the loaded
			// workspace for Roslyn to check first, so the check is skipped and dotnet builds it — it used
			// to stop at project_not_in_workspace. Needs the restore above.
			new("DefaultWorkspace: build_project builds a .csproj its solution does not list", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_build_project", new { projectPath = fx.PathOf("Stray/Stray.csproj") });
				
				return Verdict(ok && data?["succeeded"]?.GetValue<bool>() == true && data?["source"]?.GetValue<string>() != "roslyn",
					$"expected a dotnet build of Stray, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review, round 2: a .csproj that only shares its name with a listed project is not that
			// project — Greet is Lib's, and must not be found through Twin/Lib.csproj.
			new("DefaultWorkspace: a .csproj that only shares a listed project's name is not that project", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_member_body", new { symbolName = "Greet", projectPath = fx.PathOf("Twin/Lib.csproj") });
				
				return Verdict(ok && data?["error"]?.GetValue<string>() == "project_not_in_workspace",
					$"expected project_not_in_workspace, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review, round 4: a link to a listed project's file in another directory is the same file,
			// but MSBuild evaluates it from Alias/, with Alias/'s globs, so it is not that project. Greet is
			// Lib's and must not be found through it.
			new("DefaultWorkspace: a link to a listed project's .csproj in another directory is not that project", async () => {
				
				if(!aliasLinked)
					
					return Skip.SetupUnavailable("cannot create symbolic links here");
				
				var (ok, data) = await Call(rootedCtx, "roslyn_get_member_body", new { symbolName = "Greet", projectPath = fx.PathOf("Alias/Lib.csproj") });
				
				return Verdict(ok && data?["error"]?.GetValue<string>() == "project_not_in_workspace",
					$"expected project_not_in_workspace, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review, round 4: being a project of its own, the link is what dotnet is given — restore
			// evaluates it from Alias/ and writes Alias/obj, as for any project the solution does not list.
			new("DefaultWorkspace: restore_packages through a link to a .csproj in another directory restores that project", async () => {
				
				if(!aliasLinked)
					
					return Skip.SetupUnavailable("cannot create symbolic links here");
				
				var (ok, data) = await Call(rootedCtx, "roslyn_restore_packages", new { projectPath = fx.PathOf("Alias/Lib.csproj") });
				
				return Verdict(ok && data?["error"] is null && File.Exists(fx.PathOf("Alias/obj/project.assets.json")),
					$"expected Alias/Lib.csproj restored in Alias/, got {data?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review, round 4: Other.slnx lists Other/Lib.csproj, a link to Lib's file that MSBuild
			// evaluates from Other/ — another project. A Lib token applied through it is refused, and Lib's
			// file is left alone; matching by file alone accepted it.
			new("DefaultWorkspace: a token is refused through a solution listing a link to its project's file elsewhere", async () => {
				
				if(!aliasLinked)
					
					return Skip.SetupUnavailable("cannot create symbolic links here");
				
				var (ok, preview) = await Call(rootedCtx, "roslyn_preview_rename", new { symbolName = "Kept", newName = "Gone", containingType = "ApplyTarget", projectPath = libProject });
				var token         = preview?["token"]?.GetValue<string>();
				
				if(!ok || token is null)
					
					return (false, $"FAIL  (preview gave no token: {preview?.ToJsonString() ?? "null"})");
				
				var (applyOk, applied) = await Call(rootedCtx, "roslyn_apply_rename", new { token, approval = "y", projectPath = fx.PathOf("Other/Other.slnx") });
				var untouched          = !File.ReadAllText(fx.PathOf("Lib/ApplyTarget.cs")).Contains("Gone(");
				
				await Call(rootedCtx, "roslyn_apply_rename", new { token, approval = "n", projectPath = libProject });
				
				return Verdict(applyOk && applied?["error"]?.GetValue<string>() == "workspace mismatch" && untouched,
					$"expected workspace mismatch, got {applied?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review, round 3: a solution that lists the token's project through a directory link
			// holds that project. Membership compared path text, so the apply was refused as a mismatch.
			new("DefaultWorkspace: a token applies through a solution that lists its project through a link", async () => {
				
				if(!dirLinked)
					
					return Skip.SetupUnavailable("cannot create symbolic links here");
				
				var (ok, preview) = await Call(rootedCtx, "roslyn_preview_rename", new { symbolName = "Early", newName = "Late", containingType = "ViaSolutionTarget", projectPath = libProject });
				var token         = preview?["token"]?.GetValue<string>();
				
				if(!ok || token is null)
					
					return (false, $"FAIL  (preview gave no token: {preview?.ToJsonString() ?? "null"})");
				
				var (applyOk, applied) = await Call(rootedCtx, "roslyn_apply_rename", new { token, approval = "y", projectPath = fx.PathOf("Linked/Linked.slnx") });
				var renamed            = File.ReadAllText(fx.PathOf("Lib/ViaSolution.cs")).Contains("Late(");
				
				return Verdict(applyOk && applied?["error"] is null && renamed,
					$"expected Early renamed through Linked.slnx, got {applied?.ToJsonString() ?? "null"}");
			}),
			
			// #384 review, round 3: the same through the project's linked path itself — the binding
			// comparison, not the solution membership.
			new("DefaultWorkspace: a token applies through a link to its project's directory", async () => {
				
				if(!dirLinked)
					
					return Skip.SetupUnavailable("cannot create symbolic links here");
				
				var (ok, preview) = await Call(rootedCtx, "roslyn_preview_rename", new { symbolName = "Early", newName = "Late", containingType = "ViaProjectTarget", projectPath = libProject });
				var token         = preview?["token"]?.GetValue<string>();
				
				if(!ok || token is null)
					
					return (false, $"FAIL  (preview gave no token: {preview?.ToJsonString() ?? "null"})");
				
				var (applyOk, applied) = await Call(rootedCtx, "roslyn_apply_rename", new { token, approval = "y", projectPath = fx.PathOf("Linked/LibLink/Lib.csproj") });
				var renamed            = File.ReadAllText(fx.PathOf("Lib/ViaProject.cs")).Contains("Late(");
				
				return Verdict(applyOk && applied?["error"] is null && renamed,
					$"expected Early renamed through Linked/LibLink, got {applied?.ToJsonString() ?? "null"}");
			}),
		};
	}
}
