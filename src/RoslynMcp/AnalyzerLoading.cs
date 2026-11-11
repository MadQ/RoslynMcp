using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoslynMcp;

/// <summary>
///     Shared entry point for materializing analyzer references without locking their assemblies.
///     Path-backed references load through <see cref="ShadowCopyAnalyzerLoader"/> so a running
///     server never blocks 'dotnet build' of a project whose analyzers it has loaded; pathless
///     (in-memory) references load directly. Every consumer that runs project analyzers routes
///     through here so there is exactly one shadow-copy cache for the process.
/// </summary>
internal static class AnalyzerLoading
{
	// Analyzer sets keyed by (path, mtime, language): rebuilt analyzers reload fresh, unchanged
	// ones are reused across calls without touching the original file again. A set during whose
	// load a dependency was found nowhere may have come up short — Roslyn does not say — and is
	// kept with the number of dependency locations the loader knew then: it is loaded again once
	// the loader knows more, since a later project may list the sibling an earlier one did not
	// (#421). ShortAt is null for a set that had every dependency it asked for.
	private static readonly ConcurrentDictionary<(string Path, long MtimeTicks, string Language), (ImmutableArray<DiagnosticAnalyzer> Analyzers, int? ShortAt)> shadowedAnalyzers = new();
	
	/// <summary>
	///     Every analyzer of the project's analyzer references, each reference loaded once per
	///     (path, mtime, language).
	/// </summary>
	public static ImmutableArray<DiagnosticAnalyzer> GetShadowedAnalyzers(Project project)
	{
		RegisterDependencies(project);
		
		var locations = ShadowCopyAnalyzerLoader.Instance.DependencyLocationCount;
		
		return [..project.AnalyzerReferences.SelectMany(reference => GetShadowedAnalyzers(reference, project.Language, locations))];
	}
	
	/// <summary>
	///     Tells the loader where every analyzer reference of the project is, before the first of
	///     them is loaded (#421). An analyzer assembly may depend on another reference of the same
	///     project — the C# NetAnalyzers on Microsoft.CodeAnalysis.NetAnalyzers, the interop
	///     generators on Microsoft.Interop.SourceGeneration — and the project may list it first. A
	///     reference registers its own path only when it is created, so loaded one at a time the
	///     dependent assembly found no dependency and lost every type that needed it. A compiler
	///     host knows all its analyzer paths before it loads one; this gives the loader the same.
	/// </summary>
	public static void RegisterDependencies(Project project)
	{
		foreach(var reference in project.AnalyzerReferences)
			if(reference.FullPath is { } path)
				ShadowCopyAnalyzerLoader.Instance.AddDependencyLocation(path);
	}
	
	/// <summary>
	///     The analyzer references of the project that name a file which is not there (#424): an
	///     analyzer package that was never restored, or one removed from the NuGet cache since. Roslyn
	///     answers such a reference with no analyzers and no word, so whoever runs a project's analyzers
	///     or lists its providers asks here to be able to say which ones are absent.
	/// </summary>
	public static ImmutableArray<string> MissingReferences(Project project)
		=> [..project.AnalyzerReferences
			.Select(reference => reference.FullPath)
			.OfType<string>()
			.Where(path => !File.Exists(path))]
	;
	
	/// <summary>
	///     What to do about analyzer references that were not found, by where they should be. A file
	///     under the NuGet package cache gets there by a restore. Anything else is most likely the
	///     output of a project referenced as an analyzer, which is missing before the first build and
	///     after a clean, and which no restore brings back.
	/// </summary>
	public static string MissingReferenceRemedy(IReadOnlyCollection<string> paths)
	{
		var packaged = paths.Count(IsUnderPackageCache);
		
		// Worded for one path and for several alike: the caller has named them.
		if(packaged == paths.Count)
			
			return "Under the NuGet package cache the server knows: restore the project's packages with roslyn_restore_packages, then ask again.";
		
		if(packaged == 0)
			
			return "Not under the NuGet package cache the server knows, so most likely the output of a project referenced as an analyzer: build it with roslyn_build_project, then ask again.";
		
		return "Those under the NuGet package cache the server knows come back with roslyn_restore_packages; the others are most likely the output of a project referenced as an analyzer, which roslyn_build_project builds. Then ask again.";
	}
	
	// Under the cache directory itself, not beside it: a sibling whose name begins the same way
	// ("packages-old") is not the cache. The root is whatever the environment names, so it is made a
	// full path first, and one that cannot be is no cache anything is under. A cache the server does
	// not know of (a folder nuget.config names) is taken for a build output; the remedy named for
	// that, a build, restores as well.
	private static bool IsUnderPackageCache(string path)
	{
		string root;
		
		try {
			root = Path.GetFullPath(NuGetPackageLocator.Root);
		}
		catch(Exception failure) when(failure is ArgumentException or NotSupportedException or PathTooLongException) {
			return false;
		}
		
		// A root that is a drive or the file system root keeps its separator; any other gets one.
		return SecurityBoundary.StartsWithPath(path, Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar);
	}
	
	private static ImmutableArray<DiagnosticAnalyzer> GetShadowedAnalyzers(AnalyzerReference reference, string language, int locations)
	{
		if(reference.FullPath is not { } path || !File.Exists(path))
			
			return reference.GetAnalyzers(language);
		
		var key = (path, File.GetLastWriteTimeUtc(path).Ticks, language);
		
		if(shadowedAnalyzers.TryGetValue(key, out var cached) && (cached.ShortAt is null || cached.ShortAt == locations))
			return cached.Analyzers;
		
		// Roslyn swallows an assembly that does not load and answers with no analyzers. Cached, that
		// answer would outlive a failure that was only another server holding the shadow copy for a
		// moment, and the project's analyzers would be gone for the life of the process (#412).
		var loader     = ShadowCopyAnalyzerLoader.Instance;
		var failures   = loader.TransientFailures;
		var unresolved = loader.UnresolvedDependencies;
		var analyzers  = new AnalyzerFileReference(path, loader).GetAnalyzers(language);
		
		if(loader.TransientFailures == failures) {
			
			shadowedAnalyzers[key] = (analyzers, loader.UnresolvedDependencies == unresolved ? null : locations);
			
			// One set per file and language: nothing asks for an earlier build's again (#425).
			foreach(var earlier in shadowedAnalyzers.Keys)
				if(earlier.MtimeTicks != key.Ticks && earlier.Language == language && SecurityBoundary.PathComparer.Equals(earlier.Path, path))
					shadowedAnalyzers.TryRemove(earlier, out _);
		}
		
		return analyzers;
	}
}
