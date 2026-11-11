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
		
		if(loader.TransientFailures == failures)
			shadowedAnalyzers[key] = (analyzers, loader.UnresolvedDependencies == unresolved ? null : locations);
		
		return analyzers;
	}
}
