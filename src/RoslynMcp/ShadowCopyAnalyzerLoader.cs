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
	/// <summary>The loader for every project's analyzer references.</summary>
	internal static ShadowCopyAnalyzerLoader Instance { get; } = new("RoslynMcp.AnalyzerShadow");
	
	internal static string ShadowRoot { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"RoslynMcp", "analyzer-shadow");
	
	// Dependency locations registered by AnalyzerFileReference before LoadFromPath runs.
	private readonly ConcurrentDictionary<string, byte> dependencyLocations = new(SecurityBoundary.PathComparer);
	
	private readonly string name;
	
	// The load context each directory's assemblies are in now; read and replaced under the lock.
	private readonly Dictionary<string, DirectoryContext> contexts = new(SecurityBoundary.PathComparer);
	private readonly Lock                                 contextsLock = new();
	private int                                           generations;
	
	/// <summary>
	///     One load context for the assemblies of one directory, as Roslyn's own hosts have. A
	///     context holds one assembly of a simple name, so with a single context for everything two
	///     versions of an analyzer package — two projects of a solution, two workspaces of the
	///     process — could not both load: the second failed as already loaded (#419). A package's
	///     assemblies sit in one directory, so they still share their types.
	///     <para>
	///         For the same reason an assembly rebuilt in place cannot be loaded a second time into
	///         the context that has it. The context remembers the modification time each of its
	///         files had when it was loaded, and a load that finds another one gets the directory a
	///         new context (<see cref="ContextFor"/>, #418). The old one stays in memory, with the
	///         sets built from it: the contexts are not collectible.
	///     </para>
	/// </summary>
	private sealed class DirectoryContext(AssemblyLoadContext context, string directory)
	{
		public AssemblyLoadContext Context   { get; } = context;
		public string              Directory { get; } = directory;
		
		// Original path → loaded assembly, so repeated loads reuse the same shadow copy.
		public ConcurrentDictionary<string, Assembly> Loaded { get; } = new(SecurityBoundary.PathComparer);
		
		// Original path → the modification time it had when this context first loaded it; under contextsLock.
		public Dictionary<string, long> Stamps { get; } = new(SecurityBoundary.PathComparer);
	}
	
	/// <summary>
	///     A loader of its own, for a configured provider source (#389): its assemblies never share
	///     a context with a project's. Dependencies resolve from the registered locations first and
	///     fall back to the default context, so Microsoft.CodeAnalysis and the framework are always
	///     the server's own.
	/// </summary>
	internal ShadowCopyAnalyzerLoader(string name)
		=> this.name = name;
	
	public void AddDependencyLocation(string fullPath)
		=> dependencyLocations.TryAdd(fullPath, 0);
	
	// The context a path is loaded into: its directory's current one, unless that one loaded the
	// path at another modification time. Then the file was rebuilt, and the directory gets a new
	// context; whatever else of the directory is asked for from now on is loaded into that one too.
	private DirectoryContext ContextFor(string fullPath)
	{
		var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
		
		lock(contextsLock) {
			
			// Read under the lock: two calls on either side of a rebuild, entering in the other
			// order, would each find the other's time in the context and replace it in turn.
			var stamp = File.GetLastWriteTimeUtc(fullPath).Ticks;
			
			if(contexts.TryGetValue(directory, out var current)
				&& (!current.Stamps.TryGetValue(fullPath, out var loadedAt) || loadedAt == stamp)) {
				
				current.Stamps[fullPath] = stamp;
				
				return current;
			}
			
			var context = new AssemblyLoadContext($"{name}#{++generations}: {directory}", isCollectible: false);
			var created = new DirectoryContext(context, directory);
			
			context.Resolving += (_, dependency) => ResolveDependency(created, dependency);
			created.Stamps[fullPath] = stamp;
			contexts[directory]      = created;
			
			return created;
		}
	}
	
	/// <summary>
	///     How many dependency locations are registered; locations are never removed, so it only grows.
	///     A cache of a load's result compares it: a load that lost types for want of a dependency may
	///     find it once a later project has named more locations (#421).
	/// </summary>
	public int DependencyLocationCount => dependencyLocations.Count;
	
	/// <summary>
	///     How many dependencies were asked for and found at no registered location. Roslyn does not
	///     say when an assembly lost analyzers for want of a dependency, so a caller that caches a
	///     load's result reads this before and after to know whether it may have come up short. A
	///     count that another thread's load moved costs one more load later, nothing else.
	/// </summary>
	internal int UnresolvedDependencies => Volatile.Read(ref unresolvedDependencies);
	
	private int unresolvedDependencies;
	
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
		// One spelling of the path for every dictionary, or each spelling would have a limit of its own.
		fullPath = Path.GetFullPath(fullPath);
		
		return LoadInto(ContextFor(fullPath), fullPath);
	}
	
	private Assembly LoadInto(DirectoryContext target, string fullPath)
	{
		try {
			return target.Loaded.GetOrAdd(fullPath, path => Load(target.Context, path));
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
	private static Assembly Load(AssemblyLoadContext context, string fullPath)
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
	
	/// <summary>
	///     Analyzer dependencies (helper DLLs shipped next to the analyzer) resolve through the same
	///     shadow mechanism, by simple assembly name among the registered dependency locations.
	///     Several locations may carry the name — two projects on different versions of a package —
	///     and which one is taken must not depend on the order they were registered in (#419). The
	///     one in the asking assembly's own directory wins, and is loaded into the asking context:
	///     it is the one the assembly was built and shipped with. Failing that, the highest version
	///     anywhere, from the context of its own directory, so that it is one assembly for all who
	///     ask; of two equal versions, the first by path.
	/// </summary>
	private Assembly? ResolveDependency(DirectoryContext asking, AssemblyName name)
	{
		string?  best        = null;
		Version? bestVersion = null;
		
		foreach(var candidate in dependencyLocations.Keys) {
			
			if(!string.Equals(Path.GetFileNameWithoutExtension(candidate), name.Name, StringComparison.OrdinalIgnoreCase))
				continue;
			
			if(!File.Exists(candidate))
				continue;
			
			if(SecurityBoundary.PathComparer.Equals(Path.GetDirectoryName(candidate), asking.Directory)) {
				
				// Remembered like any other file of the context, so that a rebuild of it is seen.
				// The file is taken as it is now, also by a context the directory has since
				// replaced: an assembly still in use from the old one that first needs this
				// dependency after it was rebuilt gets the new build of it. The old build is
				// gone from the directory, and no dependency at all would lose the types.
				lock(contextsLock)
					asking.Stamps.TryAdd(candidate, File.GetLastWriteTimeUtc(candidate).Ticks);
				
				return LoadInto(asking, candidate);
			}
			
			var version = VersionOf(candidate);
			
			if(best is null || version > bestVersion || (version == bestVersion && string.CompareOrdinal(candidate, best) < 0))
				(best, bestVersion) = (candidate, version);
		}
		
		if(best is not null)
			return LoadFromPath(best);
		
		// A satellite resource assembly is asked for with every culture and is missing as a rule;
		// no type is lost with it.
		if(name.Name?.EndsWith(".resources", StringComparison.OrdinalIgnoreCase) != true)
			Interlocked.Increment(ref unresolvedDependencies);
		
		return null;
	}
	
	// The assembly version a file declares; the lowest there is for a file that is no assembly or
	// cannot be read, which then loses to any that can.
	private static Version VersionOf(string path)
	{
		try {
			return AssemblyName.GetAssemblyName(path).Version ?? new Version(0, 0);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException) {
			return new Version(0, 0);
		}
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
