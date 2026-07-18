# Benchmark Baseline

First recorded run of `RoslynMcp.Benchmarks`, for comparison against future runs. Numbers are
machine-specific — re-record this table when the reference machine changes.

**Run:** 2026-07-18 · `dotnet run -c Release --project benchmarks/RoslynMcp.Benchmarks -- --filter * --join`
**Environment:** BenchmarkDotNet v0.14.0 · Windows 10 22H2 (19045) · Intel Core i5-4590 (Haswell, 4 cores) · .NET 10.0.5 (X64 RyuJIT AVX2) · SDK 11.0.100-preview.2
**Workload:** the RoslynMcp.slnx solution itself (4 projects), loaded via `src/RoslynMcp/RoslynMcp.csproj` resolution.

| Benchmark | Mean | StdDev | Allocated | Notes |
|---|---:|---:|---:|---|
| ColdSolutionLoad | 7.78 s | 2.04 s | 27.9 MB | Fresh `WorkspaceManager`, full MSBuild design-time build (ColdStart, 3 iterations — high variance is inherent) |
| WarmGetCompilation | 779 ns | 3.6 ns | 72 B | Compilation-cache hit |
| IncrementalTextChangeAndRecompile | 16.8 ms | 1.03 ms | 1.94 MB | `TryApplyTextChange` on Program.cs + recompile |
| FindAllReferences | 408 µs | 18.3 µs | 69 KB | `SymbolFinder.FindReferencesAsync` for `WorkspaceManager` across the solution |

## Interpretation guardrails

- **ColdSolutionLoad** swings by seconds between runs (MSBuild BuildHost startup, disk cache state). Treat only multi-second regressions as signal.
- **WarmGetCompilation** is the cache-hit fast path — any jump above microseconds means the compilation cache broke.
- Run benchmarks with nothing else building the repo: concurrent TestHarness runs create transient scratch `.cs` files in `src/RoslynMcp` that break the dependency build.
- The warm suite writes through to `Program.cs` during measurement and restores it in `GlobalCleanup`; a killed run can leave a trailing `// bench-a`/`// bench-b` marker to revert.
