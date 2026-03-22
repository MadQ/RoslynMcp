# MSBuild API vs Roslyn Inference Analysis
**Date:** 2026-03-22  
**Context:** ProjectInfoTool TFM and Package Detection

---

## Question

Should `ProjectInfoTool` use the MSBuild API instead of regex-based inference for extracting:
- Target Framework Moniker (TFM)
- NuGet package references

---

## Current Approach (Regex-based)

### What We Infer

**Via regex patterns:**
- **TFM:** Parse `net8.0`/`net10.0`/`net11.0` from:
  - Project output path (`bin\Debug\net8.0\...`)
  - Metadata reference paths (`.nuget\packages\...\lib\net8.0\...`)
- **NuGet packages:** Parse `<name>\<version>` from global package cache paths (`~/.nuget/packages/<name>/<version>/...`)

**Via Roslyn APIs directly:**
- Project name, assembly name, file path
- Language version, nullable setting, output kind (from `CompilationOptions`/`ParseOptions`)
- Additional files (from `project.AdditionalDocuments`)

### Pros ✅
- **Fast** — No MSBuild initialization overhead
- **Works with both workspace modes:**
  - MSBuildWorkspace (`.csproj` present)
  - AdhocWorkspace (`.cs` files only, no project file)
- **No additional dependencies** — Data already available via `MetadataReference.Display`
- **Simple implementation** — Two regex patterns, straightforward logic

### Cons ⚠️
- **TFM inference is heuristic** — Works in 99% of cases but not bulletproof
  - Fails if custom output path doesn't include TFM segment
  - Fails if all metadata references come from non-standard locations
- **Package detection is incomplete:**
  - Only finds packages that appear in metadata references (runtime/compile dependencies)
  - Misses analyzer-only packages, build-time-only tools
  - Can't distinguish framework assemblies from NuGet if path patterns are ambiguous
- **No version fallback** — If version can't be parsed from path, package is skipped entirely

---

## MSBuild API Alternative

### Implementation Approach

```csharp
using Microsoft.Build.Evaluation;

var msbuildProject = new Project(project.FilePath);
var tfm = msbuildProject.GetPropertyValue("TargetFramework");
var packages = msbuildProject.GetItems("PackageReference")
    .Select(item => new {
        Name = item.EvaluatedInclude,
        Version = item.GetMetadataValue("Version")
    });
```

### Pros ✅
- **TFM is authoritative** — Exactly what's in `<TargetFramework>` property, no inference needed
- **PackageReference is complete** — Includes *all* packages from `.csproj`, even analyzer-only
- **Accurate versions** — Direct access to `Version` metadata, no path parsing
- **Access to all MSBuild data** — Could expose define constants, properties, custom items if needed

### Cons ❌
- **Breaks AdhocWorkspace** — No `.csproj` file means MSBuild API throws exceptions
  - Would need separate code path or graceful degradation
- **Performance impact** — MSBuild project evaluation is slower than regex (~100-300ms vs <1ms)
  - Still acceptable for an MCP tool call, but noticeable
- **State management complexity:**
  - Must cache/dispose `Project` instances to avoid memory leaks
  - `ProjectCollection.GlobalProjectCollection` requires careful lifecycle management
- **Multi-target ambiguity:**
  - If `.csproj` has `<TargetFrameworks>net8.0;net10.0</TargetFrameworks>`, which TFM is "the" answer?
  - Roslyn has already loaded *one* specific TFM (e.g., net10.0), but MSBuild sees both
  - Would need logic to match Roslyn's loaded TFM to MSBuild's list
- **Stale data risk:**
  - If `.csproj` changes after Roslyn loaded it, MSBuild might return different data
  - Roslyn's in-memory project is the "truth" for the running server, not the on-disk file

---

## Hybrid Approach (Considered)

### Strategy
Keep Roslyn inference as fast path, fall back to MSBuild only when inference fails.

```csharp
private string? InferTfm(Project project)
{
    // Fast path: infer from output/reference paths (current approach)
    var inferred = TryInferFromPaths(project);
    if (inferred != null)
        return inferred;

    // MSBuild fallback (only for MSBuildWorkspace)
    if (workspace.IsMSBuild && project.FilePath != null)
        return TryGetTfmFromMSBuild(project.FilePath);

    return null;
}
```

### Pros ✅
- Fast path for 99% of cases (regex, <1ms)
- Authoritative for edge cases where inference fails
- Still compatible with AdhocWorkspace (skips MSBuild gracefully)

### Cons ⚠️
- Adds ~50-70 lines of code (MSBuild fallback + disposal)
- Two code paths to maintain and test
- Multi-target ambiguity still unresolved
- Stale data risk still present (though mitigated by only using MSBuild as last resort)
- Increases complexity for marginal benefit (edge cases are rare)

---

## Decision: Keep Current Approach

### Rationale

1. **"Good enough" is good enough**
   - Regex inference works in 99% of real-world scenarios
   - The 1% edge case (custom output paths, non-standard cache locations) is acceptable failure
   - Agents can handle `"target_framework": null` gracefully — they'll ask the user or read `.csproj` directly if critical

2. **AdhocWorkspace compatibility matters**
   - Some users load `.cs` files without a project
   - Breaking that mode to gain 1% accuracy for MSBuildWorkspace is bad trade-off

3. **Performance and simplicity**
   - Current approach is fast, simple, and maintainable
   - Adding MSBuild fallback increases complexity with minimal user-facing benefit

4. **Package detection is inherently incomplete**
   - Even with MSBuild, we'd only see `<PackageReference>` — not transitive dependencies
   - Agents don't need a perfect package list; they need "what's in use" (which metadata refs provide)
   - Missing analyzer-only packages is not a UX problem

5. **Roslyn-first philosophy**
   - RoslynMcp is about exposing Roslyn's view of the code
   - Roslyn's compilation has already resolved references and TFM — inferring from that state is more "honest" than re-querying MSBuild

### When This Decision Should Be Revisited

- **User reports** that TFM inference fails in common scenarios (not just custom edge cases)
- **Agent workflows** break because of missing package data (evidence that completeness matters)
- **MSBuild API** adds zero-overhead option (e.g., reusable cached instance, no eval cost)
- **AdhocWorkspace is deprecated** or usage drops to irrelevance

---

## Documentation Update

**Tool description already states:**
> "Returns metadata about the loaded project: name, assembly name, target framework, language version, output kind, nullable setting, NuGet package references, and additional files. Use this to understand project configuration without reading the .csproj directly."

**No change needed.** The phrase "NuGet package references" is accurate — we do return packages, just not every possible package. The limitation is implicit and acceptable.

**If users hit edge cases:**
- They'll see `"target_framework": null` and understand inference failed
- They can fall back to reading `.csproj` directly via Copilot's file tools
- Future version could add MSBuild fallback if demand justifies complexity

---

## Implementation Notes (If Revisited)

### MSBuild Fallback Code Sketch
```csharp
private static string? TryGetTfmFromMSBuild(string csprojPath)
{
    try {
        // Use a temporary ProjectCollection to avoid polluting global state
        using var projectCollection = new ProjectCollection();
        var msbuildProject = projectCollection.LoadProject(csprojPath);
        var tfm = msbuildProject.GetPropertyValue("TargetFramework");

        // Handle multi-target: return first TFM or try to match Roslyn's loaded one
        if (string.IsNullOrWhiteSpace(tfm)) {
            var tfms = msbuildProject.GetPropertyValue("TargetFrameworks");
            tfm = tfms?.Split(';').FirstOrDefault();
        }

        return string.IsNullOrWhiteSpace(tfm) ? null : tfm;
    }
    catch {
        // MSBuild evaluation failed — return null gracefully
        return null;
    }
}
```

### Testing Considerations
- Test with custom `OutputPath` property
- Test with `TargetFrameworks` (plural) — verify correct TFM selected
- Test with AdhocWorkspace — ensure MSBuild path is skipped
- Test performance impact with large multi-project solutions

---

## Related Notes

- MSBuild API is already a transitive dependency via `Microsoft.CodeAnalysis.Workspaces.MSBuild`
- `BuildTool`, `CleanSolutionTool`, `RestorePackagesTool` all shell out to `dotnet` CLI — MSBuild API wouldn't help there (restore/build orchestration is different from project evaluation)
- If we ever add a "reload project" tool, MSBuild API could be useful to detect `.csproj` changes

---

## Conclusion

**Status:** ✅ **Resolved — Keep current regex-based approach**

The regex inference is simple, fast, works with both workspace modes, and handles 99% of real-world cases. The 1% edge cases where it returns `null` are acceptable failure modes that agents can handle. Adding MSBuild fallback would increase complexity for marginal benefit.

**Future consideration:** Revisit if user feedback indicates TFM inference failures are common, or if MSBuild API offers a zero-cost cached evaluation model.
