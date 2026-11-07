using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
///     Covers load health beyond dropped references (#300). A design-time build that cannot write a file
///     under obj\ — another process holds it — reports a <c>WorkspaceFailed</c> failure naming the project
///     file, keeps the project's references, and leaves its compilation options at defaults: a project
///     that enables <c>Nullable</c> loaded with it off, and <c>roslyn_get_diagnostics</c> reported hundreds
///     of phantom CS8632 warnings while every probe called the workspace healthy.
///     <para>
///         The contention is made deterministic by putting a directory where the fixture's
///         <c>obj\Debug\net10.0\&lt;name&gt;.AssemblyInfoInputs.cache</c> goes while the project's
///         <c>&lt;Version&gt;</c> changes: the design-time build then has to rewrite that file (its content is
///         a hash of the assembly attributes) and fails as it did under a concurrent <c>dotnet build</c> —
///         the same <c>WorkspaceFailed</c> failure naming the project file, the same defaulted options, on
///         every platform (see <see cref="BlockedFile"/>). Without a changed version the target is up to
///         date, writes nothing, and the block proves nothing — so every load the tests want to fail is
///         preceded by a version bump, or by a cache that no load has written yet.
///     </para>
///     <para>
///         Two fixtures, because the two paths differ. Fixture A is blocked before its first load, which
///         reloads once and then keeps what it got: an unhealthy workspace, which is what the reporting
///         tests need. Fixture B loads healthy and is blocked before a reload, which must then be discarded
///         in favour of the healthy workspace — and, if the failure outlasts four discards, promoted after
///         all. The tests of each fixture run in order and hand their state on: run the group whole.
///     </para>
/// </summary>
static class LoadHealthTests
{
	// The source every fixture compiles. With Nullable enabled it is clean; with the option defaulted
	// off, the annotation is CS8632 — the issue's phantom warning, in one line.
	const string Source = "public class Probe { public string? Name { get; set; } }\n";
	
	// Nullable and Version in the project file: the first is what a failed load loses, the second is
	// what a test changes to make the design-time build rewrite the blocked cache.
	const string ProjectXml = "<PropertyGroup><Nullable>enable</Nullable><Version>1.0.0</Version></PropertyGroup>";
	
	internal static async Task<TestGroup> BuildAsync(TestContext ctx)
	{
		var a = TestFixtures.NewMsBuildProject("LoadHealthA", extraProjectXml: ProjectXml);
		var b = TestFixtures.NewMsBuildProject("LoadHealthB", extraProjectXml: ProjectXml);
		
		a.Write("Probe.cs", Source);
		b.Write("Probe.cs", Source);
		
		const string nameA = "LoadHealthAFixture";
		const string nameB = "LoadHealthBFixture";
		
		// Fixture A is blocked before any load has written its cache, so its first design-time build has
		// to write it — and cannot. Fixture B writes its own on the healthy load.
		BlockedFile? blockedA = Block(a, nameA);
		BlockedFile? blockedB = null;
		
		// Whether the block failed the load, per fixture, as the first test of each found out.
		// The tests after it need that failure, and skip with it rather than fail for want of it.
		var failedA = true;
		var failedB = true;
		
		const string NotFailed = "the directory in place of the assembly-info cache did not fail the design-time build";
		
		await a.WarmAsync(ctx);
		await b.WarmAsync(ctx);
		
		Task<JsonNode?> Drift(FixtureProject fx) => ctx.CallToolJsonAsync("roslyn_check_drift", new { projectPath = fx.Csproj });
		
		Task<JsonNode?> Diagnostics(FixtureProject fx) => ctx.CallToolJsonAsync("roslyn_get_diagnostics", new { projectPath = fx.Csproj });
		
		static bool HasCs8632(JsonNode? diagnostics) =>
			diagnostics?["items"]?.AsArray().Any(d => d?["code"]?.GetValue<string>() == "CS8632") == true;
		
		static string[] Names(JsonNode? array) =>
			array is JsonArray items ? [..items.Select(n => n?.GetValue<string>() ?? "")] : [];
		
		// Whether the most recent load reported failing on the blocked cache: what tells a load the
		// file did not stop apart from one whose failure went undetected.
		async Task<bool> LoadFailedAsync(FixtureProject fx)
		{
			var info = await ctx.CallToolJsonAsync("roslyn_get_project_info", new { projectPath = fx.Csproj });
			
			return Names(info?["load_warnings"]).Any(w => w.Contains("AssemblyInfoInputs.cache"));
		}
		
		// Bumps <Version> through roslyn_write_file, which flags the reload synchronously: the next
		// compilation-needing call runs it, with no watcher timing involved.
		async Task<string?> BumpVersionAsync(FixtureProject fx)
		{
			var xml     = File.ReadAllText(fx.Csproj!);
			var bumped  = Regex.Replace(xml, @"<Version>1\.0\.(\d+)</Version>", m => $"<Version>1.0.{int.Parse(m.Groups[1].Value) + 1}</Version>");
			var written = await ctx.CallToolJsonAsync("roslyn_write_file", new { filePath = Path.GetFileName(fx.Csproj), content = bumped, projectPath = fx.Csproj });
			
			return written is null || written["error"] is not null
				? $"roslyn_write_file failed: {written?["error"]?.GetValue<string>() ?? "no result"}"
				: null;
		}
		
		// Polls until the workspace is healthy with nothing pending, servicing reloads on the way. A
		// discarded reload's retry runs on a timer, so the wait covers its backoff.
		async Task<JsonNode?> SettleHealthyAsync(FixtureProject fx, TimeSpan limit)
		{
			var deadline = DateTime.UtcNow + limit;
			
			while(DateTime.UtcNow < deadline) {
				
				await Diagnostics(fx);
				
				var drift = await Drift(fx);
				
				if(drift?["workspace_healthy"]?.GetValue<bool>() == true && drift["reload_pending"] is null)
					
					return drift;
				
				await Task.Delay(500);
			}
			
			return null;
		}
		
		var tests = new List<TestCase> {
			
			// The headline case. Before #300 health was reference health alone, and this project kept
			// its references, so check_drift said workspace_healthy: true over a compilation with the
			// wrong options. The first load's single healing reload hits the same block, so the
			// unhealthy workspace is what the load ends with.
			new("load health: a load that MSBuild failed on the project file of is unhealthy", async () =>
			{
				var drift = await Drift(a);
				
				if(drift?["workspace_healthy"] is null)
					
					return (false, $"FAIL  (roslyn_check_drift returned no workspace_healthy: {drift?.ToJsonString()})");
				
				var failed = Names(drift["projects_with_load_failures"]);
				
				// Healthy with no warning at all: the build wrote the file, so there was nothing to detect.
				// Healthy with the failure in load_warnings is the bug itself, and fails below.
				if(drift["workspace_healthy"]!.GetValue<bool>() && !await LoadFailedAsync(a)) {
					
					failedA = false;
					
					return Skip.SetupUnavailable(NotFailed);
				}
				
				if(drift["workspace_healthy"]!.GetValue<bool>())
					
					return (false, $"FAIL  (workspace_healthy: true for a load MSBuild failed on: {drift.ToJsonString()})");
				
				if(!failed.Contains(nameA))
					
					return (false, $"FAIL  (projects_with_load_failures does not name {nameA}: {drift.ToJsonString()})");
				
				if(drift["projects_without_references"] is not null)
					
					return (false, $"FAIL  (the project kept its references, but projects_without_references is set: {drift.ToJsonString()})");
				
				var last = drift["last_unhealthy_load"]?.GetValue<string>() ?? "";
				
				return last.Contains("MSBuild failed on the project file of")
					? (true, $"PASS  (workspace_healthy: false; projects_with_load_failures: {string.Join(", ", failed)})")
					: (false, $"FAIL  (last_unhealthy_load does not say what failed: '{last}')");
			}),
			
			// The issue's symptom, now labelled: the option really is defaulted (CS8632 is reported), and
			// the diagnostics carry the phantom hint instead of passing as the project's own warnings.
			new("load health: get_diagnostics calls the defaulted-option warnings phantom", async () =>
			{
				if(!failedA)
					
					return Skip.SetupUnavailable(NotFailed);
				
				var diagnostics = await Diagnostics(a);
				var hint        = diagnostics?["hint"]?.GetValue<string>() ?? "";
				
				if(!HasCs8632(diagnostics))
					
					return (false, $"FAIL  (no CS8632: the failed load did not default Nullable, so this fixture shows nothing: {diagnostics?.ToJsonString()})");
				
				return hint.Contains("phantom") && hint.Contains("MSBuild failure on the project file of")
					? (true, "PASS  (CS8632 reported, with the phantom hint naming the MSBuild failure)")
					: (false, $"FAIL  (no phantom hint naming the MSBuild failure: '{hint}')");
			}),
			
			// The issue's acceptance: a Roslyn check of this compilation would be a check of the wrong
			// options, so the tool goes straight to dotnet build and says why. The build itself may well
			// fail — the fixture is not restored and the cache is still blocked — and is not what is tested.
			new("load health: build_project skips the Roslyn fast path for it", async () =>
			{
				if(!failedA)
					
					return Skip.SetupUnavailable(NotFailed);
				
				var build = await ctx.CallToolJsonAsync("roslyn_build_project", new { projectPath = a.Csproj });
				var hint  = build?["hint"]?.GetValue<string>() ?? "";
				
				if(build?["source"]?.GetValue<string>() != "msbuild")
					
					return (false, $"FAIL  (expected source: msbuild, the Roslyn check skipped: {build?.ToJsonString()})");
				
				return hint.Contains("fast-path was skipped") && hint.Contains("MSBuild failure on the project file of")
					? (true, "PASS  (source: msbuild, hint names the MSBuild failure)")
					: (false, $"FAIL  (hint does not say why the fast path was skipped: '{hint}')");
			}),
			
			// Recovery: once the file is free, a reload is healthy and replaces the unhealthy workspace —
			// with no retries spent, since the live workspace failed on the same project.
			new("load health: a healthy reload replaces the unhealthy workspace", async () =>
			{
				blockedA?.Dispose();
				blockedA = null;
				
				if(await BumpVersionAsync(a) is { } error)
					
					return (false, $"FAIL  ({error})");
				
				if(await SettleHealthyAsync(a, TimeSpan.FromSeconds(30)) is not { } drift)
					
					return (false, "FAIL  (the workspace did not become healthy within 30 s of the cache being freed)");
				
				if(drift["projects_with_load_failures"] is not null)
					
					return (false, $"FAIL  (projects_with_load_failures still set: {drift.ToJsonString()})");
				
				return HasCs8632(await Diagnostics(a))
					? (false, "FAIL  (healthy, but CS8632 is still reported: the options were not reloaded)")
					: (true, "PASS  (healthy, Nullable back on, no CS8632)");
			}),
			
			// The reload path. The live workspace is healthy, so a reload that fails on the project file
			// is worse than what it would replace: it is discarded and a retry scheduled. The caller is
			// served the previous workspace — options intact, so no CS8632 — and told a reload is pending.
			new("load health: an unhealthy reload is discarded and the healthy workspace kept", async () =>
			{
				var before = await Drift(b);
				
				if(before?["workspace_healthy"]?.GetValue<bool>() != true)
					
					return (false, $"FAIL  (precondition: fixture B did not load healthy: {before?.ToJsonString()})");
				
				blockedB = Block(b, nameB);
				
				if(await BumpVersionAsync(b) is { } error)
					
					return (false, $"FAIL  ({error})");
				
				var diagnostics = await Diagnostics(b);
				var drift       = await Drift(b);
				var last        = drift?["last_unhealthy_load"]?.GetValue<string>() ?? "";
				
				if(!last.Contains("Reload discarded") && !HasCs8632(diagnostics) && !await LoadFailedAsync(b)) {
					
					failedB = false;
					
					return Skip.SetupUnavailable(NotFailed);
				}
				
				if(HasCs8632(diagnostics))
					
					return (false, "FAIL  (CS8632 reported: the unhealthy reload was promoted over a healthy workspace)");
				
				if(drift?["workspace_healthy"]?.GetValue<bool>() != true)
					
					return (false, $"FAIL  (the kept workspace should still be healthy: {drift?.ToJsonString()})");
				
				if(drift["reload_pending"]?.GetValue<bool>() != true)
					
					return (false, $"FAIL  (reload_pending should stay set while the retry is scheduled: {drift.ToJsonString()})");
				
				return last.Contains("Reload discarded") && last.Contains("MSBuild failure on the project file of")
					? (true, "PASS  (reload discarded, healthy workspace kept, reload pending)")
					: (false, $"FAIL  (last_unhealthy_load does not record the discard and its cause: '{last}')");
			}),
			
			// The bound on discarding. A failure that outlasts four discarded reloads is taken to be the
			// project's own, so the fifth attempt is promoted, and the workspace says it is unhealthy.
			// Waiting out four deferrals — each as long as the attempt before it, doubling — takes about a
			// minute, hence slow. On CI it runs on Linux only: the cap is a count in ReloadAttempt, the same
			// on every platform, and the part that is not — the block failing the build — is covered on every
			// runner by the tests above. A local --slow run checks it on any platform.
			new("load health: an unhealthy reload is promoted after four discards in a row", async () =>
			{
				if(Skip.OnCiRunner && !OperatingSystem.IsLinux())
					
					return Skip.NotApplicable("platform-independent; on CI this slow test runs on the Linux runner only");
				
				if(!failedB)
					
					return Skip.SetupUnavailable(NotFailed);
				
				var deadline = DateTime.UtcNow.AddMinutes(3);
				
				while(DateTime.UtcNow < deadline) {
					
					var drift = await Drift(b);
					
					if(drift?["workspace_healthy"]?.GetValue<bool>() == false) {
						
						if(!Names(drift["projects_with_load_failures"]).Contains(nameB))
							
							return (false, $"FAIL  (promoted, but projects_with_load_failures does not name {nameB}: {drift.ToJsonString()})");
						
						var info     = await ctx.CallToolJsonAsync("roslyn_get_project_info", new { projectPath = b.Csproj });
						var warnings = Names(info?["load_warnings"]);
						
						return warnings.Any(w => w.Contains("Reload promoted although it had") && w.Contains("4 reloads in a row"))
							? (true, "PASS  (promoted after four discards; workspace_healthy: false)")
							: (false, $"FAIL  (load_warnings do not record the promotion after four discards: {string.Join(" | ", warnings)})");
					}
					
					await Task.Delay(1000);
				}
				
				return (false, "FAIL  (still discarding after 3 minutes: the cap did not promote the reload)");
			}) { Slow = true },
			
			// Recovery for fixture B, whichever state the tests above left: a retry still scheduled (the
			// slow test skipped), or an unhealthy workspace promoted (it ran). Either way a fresh version
			// and a free file must end in a healthy workspace with nothing pending.
			new("load health: the reload path recovers once the file is free", async () =>
			{
				blockedB?.Dispose();
				blockedB = null;
				
				if(await BumpVersionAsync(b) is { } error)
					
					return (false, $"FAIL  ({error})");
				
				if(await SettleHealthyAsync(b, TimeSpan.FromSeconds(90)) is not { } drift)
					
					return (false, "FAIL  (fixture B did not become healthy within 90 s of the cache being freed)");
				
				return HasCs8632(await Diagnostics(b))
					? (false, "FAIL  (healthy, but CS8632 is still reported)")
					: (true, $"PASS  (healthy, nothing pending: {drift["last_synced_utc"]})");
			}),
		};
		
		return new TestGroup($"Load Health ({tests.Count} tests)", tests, Teardown: async () =>
		{
			blockedA?.Dispose();
			blockedB?.Dispose();
			
			// A retry scheduled for B must not run against a deleted tree: let it settle first.
			await SettleHealthyAsync(b, TimeSpan.FromSeconds(30));
			
			a.Dispose();
			b.Dispose();
		});
	}
	
	// Puts a directory where the fixture's assembly-info cache goes. The design-time build of a changed
	// version then fails to write it.
	static BlockedFile Block(FixtureProject fx, string name) =>
		new(fx.PathOf($"obj/Debug/net10.0/{name}.AssemblyInfoInputs.cache"));
	
	/// <summary>
	///     A file path nothing can write until disposed, because a directory stands in its place. Neither of
	///     the obvious ways works on every platform: holding the file open binds another process on Windows
	///     only (.NET's locks on Linux and macOS are advisory, and MSBuild's write went straight through one),
	///     and a read-only file is replaced there by a rename onto it. A directory refuses a write and a
	///     rename alike, for every user, root included. Disposing removes it; the next build writes the file.
	/// </summary>
	sealed class BlockedFile : IDisposable
	{
		readonly string path;
		bool            disposed;
		
		public BlockedFile(string path)
		{
			this.path = path;
			
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			
			if(File.Exists(path))
				File.Delete(path);
			
			Directory.CreateDirectory(path);
		}
		
		public void Dispose()
		{
			if(disposed)
				
				return;
			
			disposed = true;
			
			Directory.Delete(path);
		}
	}
}
