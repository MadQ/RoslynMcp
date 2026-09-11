/// <summary>
///     A throwaway project under <see cref="TestFixtures.TempRoot"/> that one test group owns for its
///     lifetime. Fixtures live here — never in <c>src/RoslynMcp</c>: a directory target resolves to the
///     repo's .slnx, so the FileSystemWatcher of every server that has the repo open (the harness's own
///     and the developer's live one) covers the whole tree, and each scratch .cs written there forced a
///     full workspace reload in both (#274). Write every fixture file before the group's first tool
///     call so the initial load includes it and no test depends on FSW timing.
/// </summary>
sealed class FixtureProject : IDisposable
{
	/// <summary>Root directory of the fixture tree.</summary>
	public string Dir { get; }
	
	/// <summary>The .csproj for MSBuild fixtures; null for an adhoc directory.</summary>
	public string? Csproj { get; }
	
	/// <summary>What to pass as <c>projectPath</c>: the .csproj when there is one, else the directory.</summary>
	public string ProjectPath => Csproj ?? Dir;
	
	internal FixtureProject(string dir, string? csproj)
	{
		Dir    = dir;
		Csproj = csproj;
	}
	
	/// <summary>Absolute path of a fixture-relative file.</summary>
	public string PathOf(string relative) => Path.Combine(Dir, relative);
	
	/// <summary>Writes a fixture file (creating parent directories) and returns its absolute path.</summary>
	public string Write(string relative, string content)
	{
		var path = PathOf(relative);
		
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
		
		return path;
	}
	
	/// <summary>Pays the workspace load now, so the group's first test measures the tool and not the load.</summary>
	public async Task WarmAsync(TestContext ctx)
	{
		await ctx.SendAsync(new { jsonrpc = "2.0", id = ctx.NextId(), method = "tools/call",
			@params = new { name = "roslyn_get_project_info", arguments = new { projectPath = ProjectPath } } });
		
		await ctx.ReceiveAsync();
	}
	
	/// <summary>
	///     Best-effort recursive delete. A leftover tree must never fail a run;
	///     <see cref="TestFixtures.SweepStale"/> picks it up next time.
	/// </summary>
	public void Dispose() => TestFixtures.DeleteTree(Dir);
}

/// <summary>Creates and sweeps the temp fixture trees the harness groups work in.</summary>
static class TestFixtures
{
	/// <summary>Every fixture tree lives under here, so one sweep covers them all.</summary>
	public static string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "RoslynMcp.TestHarness");
	
	// Scratch names older harness binaries wrote into the dogfood project. Swept so a run of an old
	// build, or a killed run, cannot leave a broken .cs that fails the next server build.
	static readonly string[] LegacyDogfoodScratchPatterns = ["_*_.cs", ".test_*", "0_FindUnused*"];
	
	/// <summary>
	///     A minimal SDK-style project. <paramref name="targetFrameworks"/> null gives a singular
	///     <c>&lt;TargetFramework&gt;</c>; a value gives the plural element, which makes <c>dotnet build</c>
	///     emit <c>[proj::TargetFramework=…]</c> contexts even for a single framework (BuildTool parses
	///     those into <c>target_frameworks</c>).
	/// </summary>
	public static FixtureProject NewMsBuildProject(string label, string? targetFrameworks = null)
	{
		var dir    = NewDir(label);
		var csproj = Path.Combine(dir, $"{label}Fixture.csproj");
		var tfm    = targetFrameworks is null
			? "<TargetFramework>net10.0</TargetFramework>"
			: $"<TargetFrameworks>{targetFrameworks}</TargetFrameworks>";
		
		File.WriteAllText(csproj, $"""
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup>
			    {tfm}
			  </PropertyGroup>
			</Project>
			""");
		
		return new FixtureProject(dir, csproj);
	}
	
	/// <summary>A bare directory: resolves to an AdhocWorkspace, no MSBuild.</summary>
	public static FixtureProject NewAdhocDir(string label) => new(NewDir(label), null);
	
	/// <summary>
	///     Removes what a killed run leaves behind: fixture trees under <see cref="TempRoot"/> (and the
	///     legacy <c>%TEMP%\RoslynMcp_CodeFix_*</c> directories) older than <paramref name="olderThan"/>,
	///     plus scratch files older harness binaries wrote into <c>src/RoslynMcp</c>. Every delete is
	///     best-effort — another harness may be mid-run in a sibling tree.
	/// </summary>
	public static void SweepStale(string repoRoot, TimeSpan olderThan)
	{
		var cutoff = DateTime.UtcNow - olderThan;
		
		foreach(var dir in EnumerateStaleTrees(cutoff)) {
			
			try {
				Directory.Delete(dir, recursive: true);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
		}
		
		var dogfood = Path.Combine(repoRoot, "src", "RoslynMcp");
		
		foreach(var pattern in LegacyDogfoodScratchPatterns) {
			
			foreach(var file in Directory.EnumerateFiles(dogfood, pattern, SearchOption.TopDirectoryOnly)) {
				
				try {
					File.Delete(file);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			}
		}
	}
	
	/// <summary>
	///     Best-effort recursive delete of one fixture tree. A leftover must never fail a run;
	///     <see cref="SweepStale"/> picks it up next time.
	/// </summary>
	public static void DeleteTree(string dir)
	{
		try {
			
			if(Directory.Exists(dir))
				Directory.Delete(dir, recursive: true);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
	}
	

	static IEnumerable<string> EnumerateStaleTrees(DateTime cutoff)
	{
		var candidates = new List<string>();
		
		try {
			
			if(Directory.Exists(TempRoot))
				candidates.AddRange(Directory.EnumerateDirectories(TempRoot));
			
			candidates.AddRange(Directory.EnumerateDirectories(Path.GetTempPath(), "RoslynMcp_CodeFix_*"));
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
		
		foreach(var dir in candidates) {
			
			DateTime created;
			
			try {
				created = Directory.GetCreationTimeUtc(dir);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				continue;
			}
			
			if(created < cutoff)
				yield return dir;
		}
	}
	
	static string NewDir(string label)
	{
		var dir = Path.Combine(TempRoot, $"{label}.{Guid.NewGuid():N}");
		
		Directory.CreateDirectory(dir);
		
		return dir;
	}
}
