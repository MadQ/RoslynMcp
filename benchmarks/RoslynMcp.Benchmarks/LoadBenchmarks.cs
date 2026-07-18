using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynSymbolFinder = Microsoft.CodeAnalysis.FindSymbols.SymbolFinder;

namespace RoslynMcp.Benchmarks;

/// <summary>
///     Cold-load timing: fresh WorkspaceManager per iteration, full MSBuild design-time build.
///     The manager is driven exactly like a tool call would: resolve the .csproj, and because an
///     enclosing solution exists, GetOrLoadInstance loads the whole RoslynMcp.slnx (ForSolution).
///     ColdStart with few iterations — each op costs seconds, and cold loads have no steady state
///     for BenchmarkDotNet's usual statistics to converge on.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.ColdStart, launchCount: 1, warmupCount: 0, iterationCount: 3)]
public class ColdLoadBenchmarks
{
	[Benchmark]
	public Compilation ColdSolutionLoad()
	{
		using var manager = new WorkspaceManager(BenchEnv.Logger);

		var (resolved, _) = manager.ResolveProjectPath(BenchEnv.ProjectPath);

		return manager.GetCompilation(resolved);
	}
}

/// <summary>
///     Steady-state timings against one loaded solution: cache hits, incremental text change plus
///     recompile, and a solution-wide reference search.
/// </summary>
[MemoryDiagnoser]
public class WarmBenchmarks
{
	private WorkspaceManager manager       = null!;
	private string           resolved      = null!;
	private string           programCsPath = null!;
	private string           programCsText = null!;
	private int              flip;

	[GlobalSetup]
	public void Setup()
	{
		manager  = new WorkspaceManager(BenchEnv.Logger);
		resolved = manager.ResolveProjectPath(BenchEnv.ProjectPath).Path;

		// Initial load happens here, outside the measured region.
		manager.GetCompilation(resolved);

		programCsPath = Path.Combine(BenchEnv.RepoRoot, "src", "RoslynMcp", "Program.cs");
		programCsText = File.ReadAllText(programCsPath);
	}

	[GlobalCleanup]
	public void Cleanup()
	{
		// TryApplyTextChange writes through to disk — restore the original file content so
		// benchmark runs leave the working tree clean.
		manager.TryApplyTextChange(resolved, programCsPath, SourceText.From(programCsText));
		manager.Dispose();
	}

	[Benchmark]
	public Compilation WarmGetCompilation() => manager.GetCompilation(resolved);

	[Benchmark]
	public Compilation IncrementalTextChangeAndRecompile()
	{
		// Alternate a trailing marker comment so every iteration is a genuine text change.
		flip ^= 1;

		var marker  = flip == 0 ? "// bench-a" : "// bench-b";
		var newText = programCsText + Environment.NewLine + marker + Environment.NewLine;

		manager.TryApplyTextChange(resolved, programCsPath, SourceText.From(newText));

		return manager.GetCompilation(resolved);
	}

	[Benchmark]
	public async Task<int> FindAllReferences()
	{
		var compilation = manager.GetCompilation(resolved);
		var solution    = manager.GetSolution(resolved);

		var symbol = compilation.GetTypeByMetadataName("RoslynMcp.WorkspaceManager")
			?? throw new InvalidOperationException("benchmark symbol not found");

		var refs = await RoslynSymbolFinder.FindReferencesAsync(symbol, solution);

		return refs.Sum(r => r.Locations.Count());
	}
}
