namespace RoslynMcp;

/// <summary>
///     Finds the provider assemblies of a NuGet package in the local package cache (#389): the
///     <c>NUGET_PACKAGES</c> directory, or <c>~/.nuget/packages</c>. Only the cache is searched, never
///     a feed: a package gets there by a restore the user ran, which is what makes a configured
///     package entry safe to honour without a trust gate. The layouts tried are the ones Roslyn hosts
///     load providers from, analyzer assets first.
/// </summary>
internal static class NuGetPackageLocator
{
	static readonly string[] layouts = [
		Path.Combine("analyzers", "dotnet", "cs"),
		Path.Combine("analyzers", "dotnet"),
		"analyzers",
		Path.Combine("lib", "netstandard2.0"),
	];
	
	public static string Root
		=> Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } root
			? root
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
	
	/// <summary>
	///     The assemblies of <paramref name="id"/> at <paramref name="version"/> (the highest cached
	///     version when null), or a problem saying what is missing.
	/// </summary>
	public static bool TryLocate(string id, string? version, out string[] assemblies, out string problem)
	{
		assemblies = [];
		problem    = string.Empty;
		
		var root       = Root;
		var packageDir = Path.Combine(root, id.ToLowerInvariant());
		
		if(!Directory.Exists(packageDir)) {
			
			problem = $"package '{id}' is not in the NuGet cache ({root}); restore a project that references it first";
			
			return false;
		}
		
		var versions = Directory.GetDirectories(packageDir)
			.Select(Path.GetFileName)
			.Where(name => name is not null)
			.Select(name => name!)
			.ToArray()
		;
		string? versionDir;
		
		if(version is null)
			versionDir = versions.OrderByDescending(Parse).Select(name => Path.Combine(packageDir, name)).FirstOrDefault();
		
		else {
			
			versionDir = Path.Combine(packageDir, version.ToLowerInvariant());
			
			if(!Directory.Exists(versionDir)) {
				
				problem = $"version {version} of package '{id}' is not in the NuGet cache; cached: {(versions.Length == 0 ? "none" : string.Join(", ", versions))}";
				
				return false;
			}
		}
		
		if(versionDir is null) {
			
			problem = $"package '{id}' has no version in the NuGet cache";
			
			return false;
		}
		
		foreach(var layout in layouts) {
			
			var directory = Path.Combine(versionDir, layout);
			
			if(!Directory.Exists(directory))
				continue;
			
			assemblies = Directory.GetFiles(directory, "*.dll");
			
			if(assemblies.Length > 0)
				return true;
		}
		
		problem = $"package '{id}' {Path.GetFileName(versionDir)} ships no assemblies under {string.Join(", ", layouts)}";
		
		return false;
	}
	
	// Enough of NuGet's version order for a cache directory listing: numeric parts, then a release
	// above any prerelease of the same number, prereleases in ordinal order.
	static (Version Numeric, int Release, string Prerelease) Parse(string name)
	{
		var dash       = name.IndexOf('-');
		var numeric    = dash < 0 ? name : name[..dash];
		var prerelease = dash < 0 ? string.Empty : name[(dash + 1)..];
		var plus       = numeric.IndexOf('+');
		
		if(plus >= 0)
			numeric = numeric[..plus];
		
		if(!numeric.Contains('.'))
			numeric += ".0";
		
		return (Version.TryParse(numeric, out var parsed) ? parsed : new Version(0, 0), prerelease.Length == 0 ? 1 : 0, prerelease);
	}
}
