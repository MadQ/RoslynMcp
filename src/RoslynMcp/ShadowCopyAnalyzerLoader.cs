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
	/// <summary>The loader for a project's analyzer references: one load context for all of them, as Roslyn's own host has.</summary>
	internal static ShadowCopyAnalyzerLoader Instance { get; } = new("RoslynMcp.AnalyzerShadow");
	
	internal static string ShadowRoot { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"RoslynMcp", "analyzer-shadow");
	
	// Dependency locations registered by AnalyzerFileReference before LoadFromPath runs.
	private readonly ConcurrentDictionary<string, byte> dependencyLocations = new(SecurityBoundary.PathComparer);
	
	// Original path → loaded assembly, so repeated loads reuse the same shadow copy.
	private readonly ConcurrentDictionary<string, Assembly> loadedByPath = new(SecurityBoundary.PathComparer);
	
	private readonly AssemblyLoadContext context;
	
	/// <summary>
	///     A loader of its own, for a configured provider source (#389): two configured packages may
	///     ship different versions of the same dependency, which one load context could not hold.
	///     Dependencies resolve from the registered locations first and fall back to the default
	///     context, so Microsoft.CodeAnalysis and the framework are always the server's own.
	/// </summary>
	internal ShadowCopyAnalyzerLoader(string name)
	{
		context = new AssemblyLoadContext(name, isCollectible: false);
		context.Resolving += ResolveDependency;
	}
	
	public void AddDependencyLocation(string fullPath)
		=> dependencyLocations.TryAdd(fullPath, 0);
	
	/// <summary>
	///     How many loads have failed on the file system so far — a sharing violation, a file that
	///     went away, access denied — and not on the assembly itself. A caller that caches what a
	///     load produced reads it before and after: a result from a load during which it moved may be
	///     missing an assembly that the next attempt would get, so it is not cached (#412). Roslyn
	///     swallows a failed analyzer load and a dependency fails inside the resolve handler, so the
	///     count is the one place every such failure is seen. A count that another thread's failure
	///     moved costs one more attempt, nothing else.
	/// </summary>
	internal int TransientFailures => Volatile.Read(ref transientFailures);
	
	private int transientFailures;
	
	public Assembly LoadFromPath(string fullPath)
	{
		try {
			return loadedByPath.GetOrAdd(Path.GetFullPath(fullPath), p => context.LoadFromAssemblyPath(ShadowCopy(p)));
		}
		catch(Exception ex) when(IsTransient(ex)) {
			
			Interlocked.Increment(ref transientFailures);
			
			throw;
		}
	}
	
	// FileLoadException and FileNotFoundException are IOExceptions. A file past the size limit is
	// one too, but it will be as large the next time; a file that is no assembly is a
	// BadImageFormatException, which is not.
	private static bool IsTransient(Exception ex)
		=> ex is (IOException or UnauthorizedAccessException) and not FileTooLargeException;
	
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
	///     <para>
	///         The shadow root is shared by every server on the machine, and several start together
	///         when a client restarts them, so the copy must hold up against another process making
	///         the same one (#412). It is written beside its final name and renamed into place
	///         (<see cref="FileWriter.ReplaceAtomic"/>): the name never leads to a file still being
	///         written. A file already there is used only at the source's length, so one cut short
	///         by a process that died, or by a server from before this rule, is replaced. And the
	///         copy carries the time it was made, not the source's as <c>File.Copy</c> would give it:
	///         FilePruner deletes by age, and a copy of a year-old package assembly was old enough
	///         for another server's startup sweep to delete between this copy and its load.
	///     </para>
	/// </summary>
	private static string ShadowCopy(string fullPath)
	{
		var source = new FileInfo(fullPath);
		var key    = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{fullPath}|{source.LastWriteTimeUtc.Ticks}")))[..16];
		var shadow = Path.Combine(ShadowRoot, key, source.Name);
		
		if(IsComplete(shadow, source.Length))
			return shadow;
		
		var bytes = FileReader.ReadAllBytes(fullPath, SizeLimit);
		
		try {
			FileWriter.ReplaceAtomic(shadow, bytes, AtomicReplace.None);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException && IsComplete(shadow, bytes.LongLength)) {
			// Another server's copy of the same (path, mtime) got there first and is loaded by now,
			// which on Windows refuses the rename over it. Its file is the same bytes.
		}
		
		return shadow;
	}
	
	// Far above any analyzer assembly; a limit at all so that a reference to something that is not
	// one cannot be read into memory whole.
	private const long SizeLimit = 512L * 1024 * 1024;
	
	private static bool IsComplete(string shadow, long length)
	{
		var copy = new FileInfo(shadow);
		
		return copy.Exists && copy.Length == length;
	}
}
