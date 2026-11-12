using System.Text.Json.Nodes;

// The follow-ups to the default workspace (#298): what roslyn_info says about it, a relative
// projectPath taken against its root, a positional root after an on/off flag, and a solution
// built for one target framework.
static partial class DefaultWorkspaceTests
{
	static List<TestCase> FollowupTests(Fixture f)
	{
		var ctx       = f.Ctx;
		var rootedCtx = f.Rooted;
		var bareCtx   = f.Bare;
		var fx        = f.Fx;
		var single    = f.Single;
		
		return new List<TestCase> {
			
			// An agent had no way to learn what an omitted projectPath stands for, short of
			// reading it off result paths. roslyn_info must name the default workspace and the
			// setting that chose its root: here the solution of the fixture, from --root, which
			// is how every server of this group is started.
			new("DefaultWorkspace: roslyn_info names the default workspace and where its root came from", async () => {
				
				var (ok, data) = await Call(rootedCtx, "roslyn_info", new { });
				var info       = data?["default_workspace"];
				
				return Verdict(ok
					&& (info?["path"]?.GetValue<string>() ?? "").EndsWith("DefaultWs.slnx")
					&& info?["root_source"]?.GetValue<string>() == "--root"
					&& info["unavailable"] is null,
					$"expected default_workspace with DefaultWs.slnx from --root; got {info?.ToJsonString() ?? "null"}");
			}),
			
			// The other answer: the bare server's root is an empty directory, so there is no
			// default. roslyn_info must say so and why, with no path, and must not fail: it is
			// the tool an agent calls to find out what is wrong.
			new("DefaultWorkspace: roslyn_info says why there is no default workspace", async () => {
				
				var (ok, data) = await Call(bareCtx, "roslyn_info", new { });
				var info       = data?["default_workspace"];
				
				return Verdict(ok
					&& info?["path"] is null
					&& !string.IsNullOrEmpty(info?["unavailable"]?.GetValue<string>())
					&& info?["root_source"]?.GetValue<string>() == "--root",
					$"expected default_workspace with a reason and no path; got {info?.ToJsonString() ?? "null"}");
			}),
			
			// Tools report paths relative to the workspace root, so that is what an agent passes
			// back, and this server's working directory is not that root (the launcher starts it
			// in the harness's temp directory). "Lib/Lib.csproj" exists under the root only, and
			// used to be an invalid path. A required projectPath is used on purpose: it has no
			// other way to the default workspace. An absolute path to another fixture must still
			// reach that fixture, since the rule is for relative paths alone.
			new("DefaultWorkspace: a relative projectPath is found under the default workspace root", async () => {
				
				var (ok, data)       = await Call(rootedCtx, "roslyn_get_member_body", new { symbolName = "Greet", containingType = "Greeter", projectPath = "Lib/Lib.csproj" });
				var (otherOk, other) = await Call(rootedCtx, "roslyn_get_member_body", new { symbolName = "Value", containingType = "OnlyType", projectPath = single.PathOf("Only/Only.csproj") });
				
				return Verdict(ok && data?["error"] is null && (data?["file"]?.GetValue<string>() ?? "").Contains("Greeter.cs")
					&& otherOk && other?["error"] is null,
					$"expected Greet through Lib/Lib.csproj and Value through an absolute path; got {data?.ToJsonString() ?? "null"} / {other?.ToJsonString() ?? "null"}");
			}),
			
			// With no default workspace there is no root to try, and the path is taken against
			// the working directory as before: the same relative path, which does not exist
			// there, stays an invalid path. Guards the fallback against becoming a guess.
			new("DefaultWorkspace: without a default workspace a relative projectPath resolves as before", async () => {
				
				var (ok, data) = await Call(bareCtx, "roslyn_get_member_body", new { symbolName = "Greet", projectPath = "Lib/Lib.csproj" });
				
				return Verdict(ok && data?["error"]?.GetValue<string>() == "invalid_project_path",
					$"expected invalid_project_path; got {data?.ToJsonString() ?? "null"}");
			}),
			
			// The argument reader took the token after any flag as that flag's value, so
			// "--elicit ." lost the positional root to a flag that takes none. A server is
			// started with both on/off flags of that kind before a positional root and no
			// --root; roslyn_info must report the fixture's solution, from the positional
			// argument. A swallowed root would leave the working directory, which holds no
			// solution.
			new("DefaultWorkspace: an on/off flag does not swallow the positional root after it", async () => {
				
				var logPath = Path.Combine(TestFixtures.TempRoot, "logs", $"defaultws-flags-{Guid.NewGuid():N}.log");
				
				var (process, server) = await StartServerWithArgsAsync(ctx, fx.Dir, logPath, new Dictionary<string, string>(), $"--no-buildhost-pin --elicit \"{fx.Dir}\"");
				
				try {
					
					var (ok, data) = await Call(server, "roslyn_info", new { });
					var info       = data?["default_workspace"];
					
					return Verdict(ok
						&& (info?["path"]?.GetValue<string>() ?? "").EndsWith("DefaultWs.slnx")
						&& info?["root_source"]?.GetValue<string>() == "positional argument",
						$"expected DefaultWs.slnx from the positional argument; got {info?.ToJsonString() ?? "null"}");
				}
				finally {
					await StopServerAsync(process, server);
				}
			}),
			
			// dotnet build of a solution with -f fails for every project that does not target
			// that framework. Sln/Apart.slnx lists Apart (net10.0 only) and Multi (net10.0 and
			// net10.0-windows); built for net10.0-windows, only Multi may be built, and the
			// build must succeed. Before, Apart was built too and failed it. The solution is
			// restored first, since builds run with --no-restore.
			new("DefaultWorkspace: a solution built for one target framework builds only the projects that target it", async () => {
				
				var solution = single.PathOf("Sln/Apart.slnx");
				
				var (restoreOk, restore) = await Call(rootedCtx, "roslyn_restore_packages", new { projectPath = solution });
				var (ok, data)           = await Call(rootedCtx, "roslyn_build_project", new { projectPath = solution, targetFramework = "net10.0-windows" });
				
				return Verdict(restoreOk && ok
					&& data?["succeeded"]?.GetValue<bool>() == true
					&& data["build_skipped"]?.GetValue<bool>() != true,
					$"expected a successful build of Multi alone; got {data?.ToJsonString() ?? "null"} after restore {restore?.ToJsonString() ?? "null"}");
			}),
		};
	}
}
