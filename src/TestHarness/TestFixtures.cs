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
	
	/// <summary>
	///     A .csproj path that never exists — a deterministic workspace-resolution failure for tests that
	///     assert a tool returns a structured error rather than crashing. An empty projectPath no longer
	///     serves: it now resolves to the server's default workspace (#295).
	/// </summary>
	public static string MissingProjectPath { get; } = Path.Combine(TempRoot, "does-not-exist", "Missing.csproj");
	
	// Exact scratch names older harness binaries wrote into the dogfood project. Swept so a run of an
	// old build, or a killed run, cannot leave a broken .cs that fails the next server build.
	static readonly HashSet<string> LegacyDogfoodScratchFiles = new(StringComparer.OrdinalIgnoreCase) {
		"_BuildDiagnosticsTest_.cs",
		"_CodeFixCreated_.cs",
		"_CodeFixFixture_.cs",
		"_CodeFixFixture_.g.cs",
		"_CodeFixRemoved_.cs",
		"_CodeFixSecond_.cs",
		"_FileRenameFixture_.cs",
		"_FileRenameRenamed_.cs",
		"_Other_.cs",
		"_ReloadProbe_.cs",
		"_RenameFixture_.cs",
		"_TypeDependenciesOperatorFixture_.cs",
		".test_code_debug.cs",
		".test_code_temp.cs",
		".test_crlf_temp.cs",
		".test_ctor_temp.cs",
		".test_escaped_ctor_temp.cs",
		".test_lf_temp.cs",
		".test_mostly_lf_mixed_temp.cs",
		".test_multi_ctor_temp.cs",
		".test_replace_temp.cs",
		".test_verbatim_temp.cs",
		".test_write_temp.cs",
		"0_FindUnusedDesignerFixture.designer.cs",
		"0_FindUnusedFixture_.cs",
		"0_FindUnusedFixture_.generated.cs",
		"0_FindUnusedSuffixFixture.g.cs",
	};
	
	/// <summary>
	///     A minimal SDK-style project. <paramref name="targetFrameworks"/> null gives a singular
	///     <c>&lt;TargetFramework&gt;</c>; a value gives the plural element, which makes <c>dotnet build</c>
	///     emit <c>[proj::TargetFramework=…]</c> contexts even for a single framework (BuildTool parses
	///     those into <c>target_frameworks</c>). <paramref name="extraProjectXml"/> is inserted verbatim
	///     between the <c>PropertyGroup</c> and the closing <c>Project</c> tag — e.g. an
	///     <c>&lt;ItemGroup&gt;&lt;AdditionalFiles Include="Notes.txt" /&gt;&lt;/ItemGroup&gt;</c> for a
	///     fixture that needs a tracked additional document.
	/// </summary>
	public static FixtureProject NewMsBuildProject(string label, string? targetFrameworks = null, string? extraProjectXml = null)
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
			  {extraProjectXml}
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
		
		foreach(var file in Directory.EnumerateFiles(dogfood, "*.cs", SearchOption.TopDirectoryOnly)) {
			
			try {
				
				if(IsLegacyDogfoodScratch(file, cutoff))
					File.Delete(file);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
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
				candidates.AddRange(Directory.EnumerateDirectories(TempRoot)
					.Where(static dir => !Path.GetFileName(dir).Equals("logs", StringComparison.OrdinalIgnoreCase)));
			
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

	static bool IsLegacyDogfoodScratch(string path, DateTime cutoff)
	{
		var fileName = Path.GetFileName(path);
		
		if(!LegacyDogfoodScratchFiles.Contains(fileName))
			return false;
		
		DateTime written;
		
		try {
			written = File.GetLastWriteTimeUtc(path);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			return false;
		}
		
		return written < cutoff;
	}
	
	static string NewDir(string label)
	{
		var dir = Path.Combine(TempRoot, $"{label}.{Guid.NewGuid():N}");
		
		Directory.CreateDirectory(dir);
		
		return dir;
	}
}
