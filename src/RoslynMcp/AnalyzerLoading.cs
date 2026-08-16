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
	// ones are reused across calls without touching the original file again.
	private static readonly ConcurrentDictionary<(string Path, long MtimeTicks, string Language), ImmutableArray<DiagnosticAnalyzer>> shadowedAnalyzers = new();
	
	public static ImmutableArray<DiagnosticAnalyzer> GetShadowedAnalyzers(AnalyzerReference reference, string language)
	{
		if(reference.FullPath is not { } path || !File.Exists(path))
			
			return reference.GetAnalyzers(language);
		
		return shadowedAnalyzers.GetOrAdd(
			(path, File.GetLastWriteTimeUtc(path).Ticks, language),
			key => new AnalyzerFileReference(key.Path, ShadowCopyAnalyzerLoader.Instance).GetAnalyzers(key.Language));
	}
}
