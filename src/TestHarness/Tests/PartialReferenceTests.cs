using System.Diagnostics;
using System.Text.Json.Nodes;

/// <summary>
///     Covers a project that loaded with only part of its package references (#456). The load health
///     knew a project with no references and one whose project file failed; a project short of a few
///     assemblies was healthy to both, and a type of a missing assembly was "not found" as a misspelled
///     name is. The server now compares each project's metadata references with the compile-time
///     assemblies its assets file lists.
///     <para>
///         The state came from design-time builds racing each other, which cannot be staged. The fixture
///         produces what the server sees of it: a restored project whose own target takes one package's
///         assembly out of the references before the compiler gets them, so the assets file lists an
///         assembly the load has no reference to. A second project with the same package and no such
///         target is the control: the comparison must find nothing where nothing is missing.
///     </para>
///     <para>
///         Both are restored before the server first loads them. Restored afterwards, the reload would
///         turn a project the live workspace calls healthy into a short one, and be discarded for it.
///     </para>
/// </summary>
static class PartialReferenceTests
{
	const string Package = """<ItemGroup><PackageReference Include="Humanizer.Core" Version="2.14.1" /></ItemGroup>""";
	
	// Runs in a design-time build as in a real one: the package's Reference items exist once the
	// assets file was resolved, and ResolveAssemblyReferences is what hands them on.
	const string DropTarget = """
		<Target Name="DropHumanizer" BeforeTargets="ResolveAssemblyReferences">
		  <ItemGroup>
		    <Reference Remove="@(Reference)" Condition="'%(Reference.NuGetPackageId)' == 'Humanizer.Core'" />
		  </ItemGroup>
		</Target>
		""";
	
	internal static async Task<TestGroup> BuildAsync(TestContext ctx, string logDir, string logGlob)
	{
		var partial = TestFixtures.NewMsBuildProject("PartialRefs", extraProjectXml: Package + DropTarget);
		var whole   = TestFixtures.NewMsBuildProject("WholeRefs", extraProjectXml: Package);
		
		partial.Write("Probe.cs", "class Probe { }\n");
		whole.Write("Probe.cs", "class Probe { string Text => Humanizer.StringHumanizeExtensions.Humanize(\"some_text\"); }\n");
		
		const string name = "PartialRefsFixture";
		
		var restoreProblem = await RestoreAsync(partial.Csproj!) ?? await RestoreAsync(whole.Csproj!);
		
		if(restoreProblem is null) {
			
			await partial.WarmAsync(ctx);
			await whole.WarmAsync(ctx);
		}
		
		Task<JsonNode?> Drift(FixtureProject project) => ctx.CallToolJsonAsync("roslyn_check_drift", new { projectPath = project.Csproj });
		
		var tests = new List<TestCase> {
			
			// The control comes first: a restored project with every assembly of its package referenced
			// is healthy, and its type from the package resolves. If the comparison mistook a whole
			// project for a short one, every restored project would be unhealthy.
			new("partial references: a project with all its package assemblies is healthy", async () =>
			{
				if(restoreProblem is not null)
					
					return Skip.SetupUnavailable(restoreProblem);
				
				var drift = await Drift(whole);
				
				if(drift?["workspace_healthy"]?.GetValue<bool>() != true || drift["projects_with_missing_references"] is not null)
					
					return (false, $"FAIL  (the control project is not healthy: {drift?.ToJsonString()})");
				
				var errors = await ctx.CallToolJsonAsync("roslyn_get_diagnostics", new { projectPath = whole.Csproj, severity = "errors", take = 0 });
				
				return errors?["error_count"]?.GetValue<int>() == 0
					? (true, "PASS  (healthy, nothing missing, and the package's type resolves)")
					: (false, $"FAIL  (the control project does not compile, so the fixture's package is not referenced: {errors?.ToJsonString()})");
			}),
			
			// Detection: the project and the assembly are named, with the counts.
			new("partial references: a project short of a package assembly is reported with the assembly", async () =>
			{
				if(restoreProblem is not null)
					
					return Skip.SetupUnavailable(restoreProblem);
				
				var drift = await Drift(partial);
				
				if(drift?["projects_with_missing_references"] is not JsonArray { Count: 1 } projects || projects[0] is not { } entry)
					
					return (false, $"FAIL  (projects_with_missing_references does not hold the one project: {drift?.ToJsonString()})");
				
				var missing = entry["missing"] is JsonArray names ? names.Select(n => n?.GetValue<string>() ?? "").ToArray() : [];
				
				if(entry["project"]?.GetValue<string>() != name || entry["missing_count"]?.GetValue<int>() != 1 || !missing.SequenceEqual(["Humanizer.dll"]))
					
					return (false, $"FAIL  (expected {name} with Humanizer.dll missing: {entry.ToJsonString()})");
				
				var hint = drift["hint"]?.GetValue<string>() ?? "";
				
				return drift["workspace_healthy"]?.GetValue<bool>() == false && hint.Contains("part of its package references") && hint.Contains("Humanizer.dll")
					? (true, "PASS  (unhealthy; the project, the count and the assembly are named, also in the hint)")
					: (false, $"FAIL  (the answer does not call the workspace unhealthy for it: {drift.ToJsonString()})");
			}),
			
			// What the finding is for (#447): "not found" for a type of the missing assembly says why.
			new("partial references: a type of the missing assembly is 'not found' with the reason", async () =>
			{
				if(restoreProblem is not null)
					
					return Skip.SetupUnavailable(restoreProblem);
				
				var answer = await ctx.CallToolJsonAsync("roslyn_get_type_members", new { typeName = "Humanizer.StringHumanizeExtensions", projectPath = partial.Csproj });
				var hint   = answer?["hint"]?.GetValue<string>() ?? "";
				
				return answer?["error"] is not null && hint.Contains("part of its package references") && hint.Contains("Humanizer.dll")
					? (true, "PASS  (not found, and the hint names the missing assembly)")
					: (false, $"FAIL  (expected 'not found' with the load problem in the hint: {answer?.ToJsonString()})");
			}),
			
			// The first load found the project short and loaded once more, as it does for a project
			// without references; the log says what it compared and what was missing.
			new("partial references: the load is retried once and the log names what was missing", () =>
			{
				if(restoreProblem is not null)
					
					return Task.FromResult(Skip.SetupUnavailable(restoreProblem));
				
				if(!ServerLog.TryRead(logDir, logGlob, 1, out var lines, out var failure))
					
					return Task.FromResult((false, $"FAIL  ({failure})"));
				
				var retried  = lines.Any(line => line.Contains("Unhealthy load (package references missing on: " + name, StringComparison.Ordinal) && line.Contains("reloading once", StringComparison.Ordinal));
				var compared = lines.Any(line => line.Contains("assets file(s) in", StringComparison.Ordinal) && line.Contains(name + " (1 of ", StringComparison.Ordinal));
				
				return Task.FromResult(retried && compared
					? (true, "PASS  (one retry, and the comparison is in the log)")
					: (false, $"FAIL  (retry logged: {retried}; comparison logged: {compared})"));
			}),
		};
		
		return new TestGroup($"Partial References ({tests.Count} tests)", tests, Teardown: () => {
			
			partial.Dispose();
			whole.Dispose();
			
			return Task.CompletedTask;
		});
	}
	
	// Restores a fixture outside the server, before its first load. Null when it worked, else why not:
	// the package comes from a feed, and a machine without one cannot build the fixture.
	static async Task<string?> RestoreAsync(string csproj)
	{
		try {
			
			var psi = new ProcessStartInfo("dotnet") {
				WorkingDirectory       = Path.GetDirectoryName(csproj)!,
				RedirectStandardOutput = true,
				RedirectStandardError  = true,
				UseShellExecute        = false,
			};
			
			psi.ArgumentList.Add("restore");
			psi.ArgumentList.Add(csproj);
			psi.ArgumentList.Add("/nologo");
			psi.ArgumentList.Add("-tl:off");
			
			using var process = Process.Start(psi)!;
			
			var output = process.StandardOutput.ReadToEndAsync();
			var errors = process.StandardError.ReadToEndAsync();
			
			await process.WaitForExitAsync();
			
			var text = (await output + await errors).Trim();
			
			return process.ExitCode == 0 ? null : $"dotnet restore failed for the fixture (exit code {process.ExitCode}): {(text.Length > 400 ? text[^400..] : text)}";
		}
		catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) {
			
			return $"dotnet restore could not be started for the fixture: {ex.Message}";
		}
	}
}
