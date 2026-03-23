we should consider implementing the other [C# MCP Server Features](https://csharp.sdk.modelcontextprotocol.io/concepts/index.html#server-features).
same for the [Base Protocol Features](https://csharp.sdk.modelcontextprotocol.io/concepts/index.html#base-protocol).
and the [Client Features](]https://csharp.sdk.modelcontextprotocol.io/concepts/index.html#client-features)
thoughts? this will be a feat branch, and i'll want to approve the plan before we start coding.


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


investigate: .sln/.slnx file support for multi-project solutions
  - Currently only supports single .csproj files
  - Could resolve solution files to list of projects
  - Deferred to post-v0.3.0

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