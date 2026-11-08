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
		
		await fx.WarmAsync(ctx);
		
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
		};
		
		return new TestGroup($"Unrestored Projects ({tests.Count} tests)", tests, Teardown: () => {
			
			fx.Dispose();
			
			return Task.CompletedTask;
		});
	}
}
