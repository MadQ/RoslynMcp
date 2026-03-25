which of the RoslynMcp tools would you have reached for to change method signatures on exising methods across the code base? should we plan for a `change_signature` tool that can do this? should the result of such and edit include resulting errors, or should it offer a 2-step non-breaking way? i.e., add new parameter, then add a new overload that has the old signature, and that forwards to the new new signature using a default value? this would avoid creating build errors on all the calling sites. thoughts?

roslyn_change_signature tool for semantic method signature changes
  - Non-breaking mode (default): adds overload + [Obsolete] with recognizable marker
  - Breaking mode (opt-in): updates signature + all call sites
  - Preview + apply workflow (like rename)
  - See: docs/plans/change-signature-tool.md
  - Target: v0.4.0 or later (post-v0.3.0)

also, new tool for adding/removing attributes?
  - Related to change_signature (needs [Obsolete] marker)
  - Lower priority than change_signature
  - See: docs/plans/change-signature-tool.md (Related Future Work section)
	  
idea: semantic validation in editing tools: have the editing tools verify that where type names are specified, the types should exist and be accessible in the codebase. possibly sort-of spell check (`jnt` => "warning: did you mean `int`?")? for example, if we have a tool that adds an attribute to a method, and the attribute type doesn't exist anywhere, the tool should raise an error instead of just adding the attribute and creating a build error. this would be a more user-friendly way to catch mistakes before they cause build issues. thoughts?

we probably shouldn't crash the MCP process for something like `ModelContextProtocol.McpProtocolException: Unknown tool: 'wrong_tool_name'`.


don't forget to disable all that logging by default for final v0.3.0 release build.


we should consider implementing the other [C# MCP Server Features](https://csharp.sdk.modelcontextprotocol.io/concepts/index.html#server-features).
same for the [Base Protocol Features](https://csharp.sdk.modelcontextprotocol.io/concepts/index.html#base-protocol).
and the [Client Features](]https://csharp.sdk.modelcontextprotocol.io/concepts/index.html#client-features)
thoughts? this will be a feat branch, and i'll want to approve the plan before we start coding.

---

check whether `Compilation` duplication might be an issue with the new global project context feature. i.e., one .csproj file referencing another .csproj file. thoughts?
  ANALYSED: Yes, duplication exists — here is the full picture. Priority: HIGH for post-merge dev work.

  ROOT CAUSE
  WorkspaceManager caches by .csproj path. When A.csproj references B.csproj via <ProjectReference>,
  MSBuildWorkspace.OpenProjectAsync("A.csproj") silently loads B into A's Solution as a full project
  node (not a metadata/DLL reference). So A's WorkspaceInstance already contains B's parsed syntax
  trees and a Compilation for B internally. If an agent later requests B.csproj directly, a second
  WorkspaceInstance is created — B's sources are parsed again from disk into a second Compilation.

  TWO DISTINCT PROBLEMS

  1. Memory duplication (low–medium severity)
     B's syntax trees exist twice in memory: once inside A's Solution graph, once as B's own
     WorkspaceInstance. For deep graphs (A→B→C→D) this multiplies. Correctness is not affected —
     both copies are semantically identical. Tolerable for now; becomes meaningful with large or
     deeply nested dependency trees.

  2. Cross-project semantic operations are silently incomplete (HIGH severity)
     find_references, preview_rename, get_type_hierarchy, get_symbols_in_scope etc. all operate on
     a Solution, not just a Compilation. Two separate WorkspaceInstance objects = two separate
     Solution graphs that have no knowledge of each other. An agent asking "find all references to
     symbol X in A.csproj" will never see call sites inside B.csproj, even if B references A. No
     error is raised — the tool just returns incomplete results. This is a silent correctness gap.

  RECOMMENDED FIX (post-merge, before v0.3.0)
  Option 1 (preferred): Solution-level caching — use OpenSolutionAsync(.sln/.slnx) as the cache
  key instead of individual .csproj paths. One Solution per .sln file; all project references share
  the same graph; zero duplication; full cross-project semantics. Pairs naturally with the .sln/.slnx
  support that is already planned for post-v0.3.0 (see note below).
  Option 2 (interim): Document the limitation explicitly in tool descriptions. Tools that operate on
  a Solution (find_references, preview_rename, etc.) should warn in their description that
  cross-project results require passing the root/referencing project, not a leaf project.
  Option 3 (not recommended): Scan existing cache entries for a Solution that already contains the
  requested project as a dependency. Too complex and fragile — skip this.

  RELATIONSHIP TO EXISTING ROADMAP
  The .sln/.slnx investigation note below ("Deferred to post-v0.3.0") is the natural vehicle for
  Option 1. These two items should be tackled together. Bumping .sln support priority is justified
  by this analysis — it isn't just a convenience feature, it's the fix for silent semantic gaps.

---

add a git tag to the current dev branch.
DONE: v0.2.0 tag created. (commit 9f8c7e2)

---

the `"args": ["/path/to/your/project"]` needs to go, or be optional.
 MCP should be configurable globally, not project specific.
 maybe have a project parameter on the tools instead? maybe both/hybrid? idk.
 this might require caching Roslyn Compilation objects: we'd need to figure the cache ejection strategy (LRU? TTL?) and invalidation triggers (file changes, explicit command, etc.). let's discuss.
 ==> IN PROGRESS: feature/global-project-context branch
     - Infrastructure complete: WorkspaceResolver, LRU cache, smart path resolution
     - TypeMembersTool migrated as reference implementation
     - 23 tools commented out, ready for systematic migration
     - NEXT SESSION: Test TypeMembersTool first, then migrate remaining tools

==>  let's add this to the projectPath array for local .mcp.json configs: allow file globbing patterns (e.g., `src/**/*.csproj`) to specify multiple projects. MCP can then lazily resolve and cache Compilations for all matched projects, and tools can specify which project context they need. This provides flexibility while keeping the config manageable. We can start with a simple implementation that resolves globs at startup and caches the results, then iterate on cache management as needed. big plus: provides a good estimate of required cache size upfront. for future feature, we'll want to warn users when the # of projects exceeds a certain threshold, or when cache memory usage is high. let's discuss.

---

investigate: .sln/.slnx file support for multi-project solutions
  - Currently only supports single .csproj files
  - Could resolve solution files to list of projects
  - Deferred to post-v0.3.0
  - PRIORITY BUMP: This is also the fix for Compilation duplication and cross-project semantic gaps
    (silent incomplete results in find_references, preview_rename, etc.) — see analysis above.
    Tackle together with that item; don't treat as purely cosmetic convenience.

---

AGENTS.md still has a `dotnet run` reference for an MCP config example. that need to be corrected. also, some of code examples in the meta docs are out of sync/stale. for example the `### Publish Executable` section in HANDOFF.md. this kind of verification should be part of our documentation review process. maybe we should have a checklist for that? like, every time we make a change to the codebase, we should also review the docs and make sure they're still accurate. we could even automate some of that with tests that check for certain strings in the docs, or something like that. maybe creat a skill for you? let's discuss.
DONE.

do this first: search_tool: enhance with syntax-tree-based semantic filtering.
  or maybe have a separate tool for that? like "semantic_search" or something. thoughs?
DONE.

---

Ω = ∞⁰ ... 'Nuff said. There's your answer.

---


we need to be better about exceptions in our tools. this happened: Unhandled exception. System.IO.DirectoryNotFoundException: Could not find a part of the path 'J:\Projects\RoslynMcp\src\RoslynMcp\RoslynMcp\.test_replace_temp.cs'.
   at Microsoft.Win32.SafeHandles.SafeFileHandle.CreateFile(String fullPath, FileMode mode, FileAccess access, FileShare share, FileOptions options)
let's make sure to catch explicit exception typesm though.
  DONE: All tools hardened with specific exception types. Guidelines in CONTRIBUTING.md. (commit 40172f6)

document this?: yeah, there are a lot of tools! Roslyn is pretty awesome.
  DONE: Added to README.md "Why" section — acknowledges tool count, explains it's Roslyn's power surface. (commit b715e2f+)

do we really need the DI stuff (`builder.Services.AddTransient<MyNewTool>()`? i think the C# MCP SDK docs say otherwise, but i can't find that now. the c# MCP SDK docs are here: https://csharp.sdk.modelcontextprotocol.io/
  RESOLVED: NO! Manual .AddTransient<> registrations are redundant. .WithToolsFromAssembly() auto-discovers
  and registers all [McpServerToolType] classes. Removed 25 lines of redundant code. See DI_REGISTRATION_ANALYSIS.md.
  All 22 tests still pass. 🏴‍☠️

is there a roslyn way to `dotnet build` (`clean`/`restore`) without `dotnet`?
  RESOLVED: No, and that's the right answer. See MSBUILD_API_ANALYSIS.md for detailed rationale.
  TL;DR: Roslyn handles diagnostics (fast, in-process). MSBuild handles build orchestration, NuGet, multi-project.
  Hybrid approach is optimal — Roslyn for speed, MSBuild for correctness. Same logic applies to ProjectInfoTool's
  regex-based TFM/package inference — works 99% of the time, MSBuild fallback adds complexity for marginal benefit.

add a better replace_string_in_file tool to replace the copilot built-in one? maybe have it take a regex pattern instead of just a string? that way we can do more complex replacements. also, maybe have it return the number of replacements made, or the lines that were changed, or something like that. could be useful for debugging. possibly also make it a 2-step. thoughts?
  DONE: `replace_in_file` tool with regex support, dry-run, changed line tracking. (commit ae4bc0c)

i'd prefer using Roslyn to do the work in our editing tools. i.e., have it use Roslyn to parse a file, find the matching nodes, and replace them with the new string. this would be more robust than just doing a string replace on the file contents. also, it's in-line with the idea of using Roslyn for code analysis and manipulation. let's discuss.
  DECISION: Keep both approaches. `replace_in_file` = text-level (any file), `replace_in_code` (future) = Roslyn syntax-level (C# only).
  ROADMAP: Add `replace_in_code` tool for semantic C# manipulation:
    - Match by node kind (MethodDeclaration, FieldDeclaration, etc.)
    - Optional text pattern within nodes
    - Preserve formatting/trivia automatically
    - Validate syntax after edit
    - Start simple (node kind + text), expand to query DSL if needed