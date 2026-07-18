using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;

namespace RoslynMcp;

/// <summary>
///     Loads analyzer assemblies from per-(path, mtime) shadow copies so the analyzed project's
///     output DLLs are never locked by this server — without shadowing, a running server blocks
///     'dotnet build' of any project whose analyzers it has loaded. The lock hazard and the
///     shadow-copy mitigation were documented empirically by MarcelRoozekrans/roslyn-codelens-mcp
///     (their issue #254); no code reused.
/// </summary>
internal sealed class ShadowCopyAnalyzerLoader : IAnalyzerAssemblyLoader
{
	internal static ShadowCopyAnalyzerLoader Instance { get; } = new();
	
	internal static string ShadowRoot { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"RoslynMcp", "analyzer-shadow");
	
	// Dependency locations registered by AnalyzerFileReference before LoadFromPath runs.
	private readonly ConcurrentDictionary<string, byte> dependencyLocations = new(StringComparer.OrdinalIgnoreCase);
	
	// Original path → loaded assembly, so repeated loads reuse the same shadow copy.
	private readonly ConcurrentDictionary<string, Assembly> loadedByPath = new(StringComparer.OrdinalIgnoreCase);
	
	private readonly AssemblyLoadContext context;
	
	private ShadowCopyAnalyzerLoader()
	{
		context = new AssemblyLoadContext("RoslynMcp.AnalyzerShadow", isCollectible: false);
		context.Resolving += ResolveDependency;
	}
	
	public void AddDependencyLocation(string fullPath)
		=> dependencyLocations.TryAdd(fullPath, 0);
	
	public Assembly LoadFromPath(string fullPath)
		=> loadedByPath.GetOrAdd(Path.GetFullPath(fullPath), p => context.LoadFromAssemblyPath(ShadowCopy(p)));
	
	// Analyzer dependencies (helper DLLs shipped next to the analyzer) resolve through the same
	// shadow mechanism, matched by simple assembly name across registered dependency locations.
	private Assembly? ResolveDependency(AssemblyLoadContext _, AssemblyName name)
	{
		foreach(var candidate in dependencyLocations.Keys) {
			
			if(!string.Equals(Path.GetFileNameWithoutExtension(candidate), name.Name, StringComparison.OrdinalIgnoreCase))
				continue;
			
			if(!File.Exists(candidate))
				continue;
			
			return LoadFromPath(candidate);
		}
		
		return null;
	}
	
	/// <summary>
	///     Copies the assembly into a shadow directory keyed by (path, mtime) and returns the
	///     copy's path. Rebuilt analyzers get a fresh key; unchanged ones reuse the existing copy.
	///     Stale directories are swept by FilePruner; in-use copies survive (Windows keeps loaded
	///     images locked, which is exactly the lock we are moving off the originals).
	/// </summary>
	private static string ShadowCopy(string fullPath)
	{
		var mtime  = File.GetLastWriteTimeUtc(fullPath);
		var key    = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{fullPath.ToLowerInvariant()}|{mtime.Ticks}")))[..16];
		var dir    = Path.Combine(ShadowRoot, key);
		var shadow = Path.Combine(dir, Path.GetFileName(fullPath));
		
		if(File.Exists(shadow))
			
			return shadow;
		
		Directory.CreateDirectory(dir);
		
		try {
			File.Copy(fullPath, shadow);
		}
		catch(IOException) when(File.Exists(shadow)) {
			// A concurrent copy of the same (path, mtime) won the race — the existing copy is identical.
		}
		
		return shadow;
	}
}
