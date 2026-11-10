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
		// One spelling of the path for both dictionaries, or each spelling would have a limit of its own.
		fullPath = Path.GetFullPath(fullPath);
		
		try {
			return loadedByPath.GetOrAdd(fullPath, Load);
		}
		catch(Exception ex) when(IsTransient(ex)) {
			
			// Only so often for one path. A failure of a passing kind that does not pass — a shadow
			// copy something holds for good — would keep every cache above from keeping its result,
			// and each call would load, fail and log again. Past the limit it is cached like any other.
			if(transientByPath.AddOrUpdate(fullPath, 1, (_, failed) => failed + 1) <= MaxTransientFailures)
				Interlocked.Increment(ref transientFailures);
			
			throw;
		}
	}
	
	private readonly ConcurrentDictionary<string, int> transientByPath = new(SecurityBoundary.PathComparer);
	
	private const int MaxTransientFailures = 5;
	
	// A shadow copy of the right length that is no assembly — cut off under a writer that had
	// sized the file first, or by a power loss — would be reused by every server until the source
	// changes. It is copied again, once: a source that is itself no assembly fails a second time.
	private Assembly Load(string fullPath)
	{
		try {
			return context.LoadFromAssemblyPath(ShadowCopy(fullPath, replace: false));
		}
		catch(BadImageFormatException) {
			return context.LoadFromAssemblyPath(ShadowCopy(fullPath, replace: true));
		}
	}
	
	// Only what another process causes and will stop causing: a file held open (a sharing or lock
	// violation, which is how a load of a held shadow copy fails), one that went away between the
	// copy and the load, and a copy another process is in the middle of. Every other failure is
	// taken to be lasting — a full disk, a shadow directory that cannot be written, a second
	// assembly of a name the context already holds, which is a FileLoadException too — and a
	// lasting failure that is not cached is paid for again at every call.
	private static bool IsTransient(Exception ex)
		=> ex is FileNotFoundException or DirectoryNotFoundException or ContendedCopyException || IsHeld(ex);
	
	private static bool IsHeld(Exception ex)
		=> ex is IOException && (ex.HResult & 0xFFFF) is SharingViolation or LockViolation;
	
	// How Windows refuses to rename over a file another process has open: as held, or as access denied.
	private static bool IsRefused(Exception ex)
		=> ex is UnauthorizedAccessException || IsHeld(ex);
	
	private const int SharingViolation = 32;
	private const int LockViolation    = 33;
	
	/// <summary>The shadow copy could not be put in place because another process holds the file that is there.</summary>
	private sealed class ContendedCopyException(string message, Exception inner) : IOException(message, inner);
	
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
	///         by a process that died is replaced; <paramref name="replace"/> replaces it whatever
	///         its length, for one that turned out to be no assembly. And the copy carries the time
	///         it was made, not the source's as <c>File.Copy</c> would give it: FilePruner deletes
	///         by age, and a copy of a year-old package assembly was old enough for another
	///         server's startup sweep to delete between this copy and its load. A copy that is
	///         reused is given the current time again for the same reason.
	///     </para>
	/// </summary>
	private static string ShadowCopy(string fullPath, bool replace)
	{
		var source = new FileInfo(fullPath);
		var key    = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{fullPath}|{source.LastWriteTimeUtc.Ticks}")))[..16];
		var shadow = Path.Combine(ShadowRoot, key, source.Name);
		
		if(!replace && IsComplete(shadow, source.Length)) {
			
			KeepFromPruning(shadow);
			
			return shadow;
		}
		
		// A second copy helps once. A source that is itself no assembly — a native library among
		// the analyzer items — fails the same way after it, and whoever asks for it again without
		// having kept the failure must not have the file rewritten each time. Recorded once the
		// copy is in place, not here: a second thread arriving meanwhile must make the copy too,
		// not go back to the file that failed, and a copy that did not get written is not one.
		if(replace && replacedOnce.ContainsKey(shadow))
			return shadow;
		
		var bytes = FileReader.ReadAllBytes(fullPath, SizeLimit);
		
		// Another server may have finished the same copy while this one read the source. Writing
		// anyway would end in a rename that Windows refuses once the file is loaded there, after
		// the retries and the error entries of a write that failed.
		if(!replace && IsComplete(shadow, bytes.LongLength))
			return shadow;
		
		try {
			FileWriter.ReplaceAtomic(shadow, bytes, AtomicReplace.None);
		}
		catch(Exception ex) when(!replace && ex is IOException or UnauthorizedAccessException && IsComplete(shadow, bytes.LongLength)) {
			// Another server's copy of the same (path, mtime) got there first and is loaded by now,
			// which on Windows refuses the rename over it. Its file is the same bytes. Not so when
			// replacing: the file of that length is the one that would not load.
		}
		catch(Exception ex) when(IsRefused(ex) && File.Exists(shadow)) {
			
			// The rename was refused over a file that is not known to be good: one a server from
			// before the copy was atomic is still writing, or, when replacing, one that somebody
			// has open — the bad file, or the good one another thread or server has just put there
			// and loaded. The next attempt finds out which. A write that failed for another
			// reason — a full disk — is not this, whatever sits at the final name, and goes on
			// as it is.
			throw new ContendedCopyException($"The shadow copy '{shadow}' is held by another process.", ex);
		}
		
		if(replace)
			replacedOnce.TryAdd(shadow, 0);
		
		return shadow;
	}
	
	// Far above any analyzer assembly; a limit at all so that a reference to something that is not
	// one cannot be read into memory whole.
	private const long SizeLimit = 512L * 1024 * 1024;
	
	private static readonly TimeSpan PruneGuard = TimeSpan.FromDays(1);
	
	// Shadow copies this process has replaced for being no assembly; shared by every loader, as the copies are.
	private static readonly ConcurrentDictionary<string, byte> replacedOnce = new(SecurityBoundary.PathComparer);
	
	private static bool IsComplete(string shadow, long length)
	{
		var copy = new FileInfo(shadow);
		
		return copy.Exists && copy.Length == length;
	}
	
	// The startup sweep of every server deletes shadow copies by age, and a copy in daily use
	// grows as old as one nobody needs. Once a day is often enough to stay far from the limit
	// and keeps a load from writing to the file each time. Best-effort: the time of a file is
	// not worth failing a load over.
	private static void KeepFromPruning(string shadow)
	{
		try {
			
			var now = DateTime.UtcNow;
			
			if(now - File.GetLastWriteTimeUtc(shadow) > PruneGuard)
				File.SetLastWriteTimeUtc(shadow, now);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
	}
}
