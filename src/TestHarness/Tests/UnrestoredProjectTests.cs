using System.Text.Json.Nodes;

/// <summary>
///     Covers a project that was never restored (#399). Design-time builds run in Roslyn's BuildHost on the
///     MSBuild of whatever SDK the project's directory resolves, and a newer SDK's (11.0.100-rc.3 was seen)
///     resolves no references at all for a project with no <c>project.assets.json</c> — and reports no
///     failure. The server called that contention and recommended a respawn, which repeats the same load;
///     and <c>roslyn_restore_packages</c>, the actual fix, left the broken load in place, because the assets
///     file it writes is under obj\, which the watcher ignores.
///     <para>
///         An SDK that shows the symptom cannot be installed on a CI runner, so the fixture produces the
///         state the server detects directly: a project with <c>DisableImplicitFrameworkReferences</c> and no
///         references of its own loads with zero metadata references on any SDK, and has no assets file until
///         it is restored. Restoring it needs no package and no network. After the restore it still has no
///         references, but it has an assets file, so it is no longer "not restored": that is how the test sees
///         that the restore flagged a reload and the reload ran.
///     </para>
///     <para>The tests hand their state on, in order: run the group whole.</para>
/// </summary>
static class UnrestoredProjectTests
{
	internal static async Task<TestGroup> BuildAsync(TestContext ctx)
	{
		var fx = TestFixtures.NewMsBuildProject("Unrestored", extraProjectXml:
			"<PropertyGroup><DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences></PropertyGroup>");
		
		fx.Write("Probe.cs", "class Probe { }\n");
		
		const string name = "UnrestoredFixture";
		
		// A second fixture that loads healthy: on the pinned SDK a plain project has its framework references
		// without a restore. An edit that leaves it with no references and still no assets file makes the
		// reload "not restored", and that reload must not replace the working workspace (#399 review).
		var healthy         = TestFixtures.NewMsBuildProject("UnrestoredHealthy");
		var healthyOriginal = File.ReadAllText(healthy.Csproj!);
		
		healthy.Write("Probe.cs", "class Probe { System.Text.StringBuilder s = new(); }\n");
		
		await fx.WarmAsync(ctx);
		await healthy.WarmAsync(ctx);
		
		Task<JsonNode?> Drift() => ctx.CallToolJsonAsync("roslyn_check_drift", new { projectPath = fx.Csproj });
		
		static string[] Names(JsonNode? array) =>
			array is JsonArray items ? [..items.Select(n => n?.GetValue<string>() ?? "")] : [];
		
		var tests = new List<TestCase> {
			
			// Detection, and the advice that follows from it: no references and no assets file is "not
			// restored", and the remedy named is a restore — not a respawn, which repeats the same load.
			new("unrestored project: a project with no references and no assets file is reported as not restored", async () =>
			{
				if(File.Exists(fx.PathOf("obj/project.assets.json")))
					
					return Skip.SetupUnavailable("the fixture has an assets file before any restore");
				
				var drift = await Drift();
				
				if(!Names(drift?["projects_without_references"]).Contains(name))
					
					return Skip.SetupUnavailable($"the fixture loaded with metadata references, so there is nothing to classify: {drift?.ToJsonString()}");
				
				if(!Names(drift?["projects_not_restored"]).Contains(name))
					
					return (false, $"FAIL  (projects_not_restored does not name {name}: {drift?.ToJsonString()})");
				
				var hint = drift?["hint"]?.GetValue<string>() ?? "";
				
				return hint.Contains("roslyn_restore_packages") && !hint.Contains("roslyn_respawn")
					? (true, "PASS  (not restored; the hint names roslyn_restore_packages, not a respawn)")
					: (false, $"FAIL  (the hint does not send the caller to a restore: '{hint}')");
			}),
			
			// The diagnostics say the same, instead of blaming contention.
			new("unrestored project: get_diagnostics sends the caller to a restore", async () =>
			{
				var hint = (await ctx.CallToolJsonAsync("roslyn_get_diagnostics", new { projectPath = fx.Csproj }))?["hint"]?.GetValue<string>() ?? "";
				
				return hint.Contains("roslyn_restore_packages") && !hint.Contains("contended")
					? (true, "PASS  (restore named, no contention)")
					: (false, $"FAIL  (the hint does not name the missing restore: '{hint}')");
			}),
			
			// The build tool answers with the advice rather than running a dotnet build --no-restore that can
			// only fail — holding the MSBuild gate for its whole length (#399 review).
			new("unrestored project: build_project skips a build that could only fail", async () =>
			{
				var build = await ctx.CallToolJsonAsync("roslyn_build_project", new { projectPath = fx.Csproj });
				var why   = build?["skip_reason"]?.GetValue<string>() ?? "";
				
				return build?["build_skipped"]?.GetValue<bool>() == true && why.Contains("roslyn_restore_packages")
					? (true, "PASS  (no build ran; the skip reason names roslyn_restore_packages)")
					: (false, $"FAIL  (expected a skipped build with the restore advice: {build?.ToJsonString()})");
			}),
			
			// The fix: a restore flags a reload of the workspace, and the reload sees the assets file. Before,
			// nothing reloaded — the file is written under obj\ — and the project stayed "not restored".
			new("unrestored project: roslyn_restore_packages reloads the workspace", async () =>
			{
				var restore = await ctx.CallToolJsonAsync("roslyn_restore_packages", new { projectPath = fx.Csproj });
				
				if(restore?["success"]?.GetValue<bool>() != true)
					
					return Skip.SetupUnavailable($"dotnet restore failed here: {restore?["message"]?.GetValue<string>()}");
				
				if(await Drift() is not { } pending || pending["reload_pending"]?.GetValue<bool>() != true)
					
					return (false, "FAIL  (no reload pending after the restore)");
				
				// A compilation call runs the reload.
				await ctx.CallToolJsonAsync("roslyn_get_diagnostics", new { projectPath = fx.Csproj, take = 0 });
				
				var drift = await Drift();
				
				if(drift?["reload_pending"] is not null)
					
					return (false, $"FAIL  (the reload did not run: {drift?.ToJsonString()})");
				
				return Names(drift?["projects_not_restored"]).Contains(name)
					? (false, $"FAIL  (still reported as not restored after the restore and reload: {drift?.ToJsonString()})")
					: (true, "PASS  (reload flagged by the restore, and the reloaded project is no longer 'not restored')");
			}),
			
			// A project that was never restored is not retried or healed, but it still counts when a reload is
			// weighed against the live workspace: one that leaves a working project without references and
			// without an assets file is discarded, like one that dropped them. Before, it was promoted.
			new("unrestored project: a reload that leaves a working project unrestored-looking is discarded", async () =>
			{
				var before = await ctx.CallToolJsonAsync("roslyn_check_drift", new { projectPath = healthy.Csproj });
				
				if(before?["workspace_healthy"]?.GetValue<bool>() != true)
					
					return Skip.SetupUnavailable($"the healthy fixture did not load with references: {before?.ToJsonString()}");
				
				var edited  = healthyOriginal.Replace("</PropertyGroup>", "<DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences></PropertyGroup>");
				var written = await ctx.CallToolJsonAsync("roslyn_write_file", new { filePath = Path.GetFileName(healthy.Csproj), content = edited, projectPath = healthy.Csproj });
				
				if(written is null || written["error"] is not null)
					
					return (false, $"FAIL  (roslyn_write_file failed: {written?.ToJsonString()})");
				
				await ctx.CallToolJsonAsync("roslyn_get_diagnostics", new { projectPath = healthy.Csproj, take = 0 });
				
				var drift = await ctx.CallToolJsonAsync("roslyn_check_drift", new { projectPath = healthy.Csproj });
				var last  = drift?["last_unhealthy_load"]?.GetValue<string>() ?? "";
				
				return drift?["workspace_healthy"]?.GetValue<bool>() == true && last.Contains("Reload discarded")
					? (true, "PASS  (reload discarded; the working workspace was kept)")
					: (false, $"FAIL  (the unrestored-looking reload replaced the working workspace: {drift?.ToJsonString()})");
			}),
		};
		
		return new TestGroup($"Unrestored Projects ({tests.Count} tests)", tests, Teardown: async () => {
			
			// Undo the edit and let the deferred retry it scheduled promote a healthy reload, so no retry
			// runs against a deleted tree.
			await ctx.CallToolJsonAsync("roslyn_write_file", new { filePath = Path.GetFileName(healthy.Csproj), content = healthyOriginal, projectPath = healthy.Csproj });
			
			var deadline = DateTime.UtcNow.AddSeconds(60);
			
			while(DateTime.UtcNow < deadline) {
				
				await ctx.CallToolJsonAsync("roslyn_get_diagnostics", new { projectPath = healthy.Csproj, take = 0 });
				
				if((await ctx.CallToolJsonAsync("roslyn_check_drift", new { projectPath = healthy.Csproj }))?["reload_pending"] is null)
					break;
				
				await Task.Delay(500);
			}
			
			fx.Dispose();
			healthy.Dispose();
		});
	}
}
