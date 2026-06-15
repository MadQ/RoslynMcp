# Issue #19: Semantic Analysis Tools

## What To Trace First

This issue is a feature gap, not a bug reproduction. To understand it, trace how existing Roslyn-backed tools are built and then map the three proposed tools onto the same patterns.

Start with the design note:

- `docs/plans/tool-suggestions.md`
- `ROADMAP.md`, v0.9.0 Semantic Analysis section
- `ROADMAP.md`, For Contributors section, Issue #19 note

Then trace these implementation patterns:

- `src/RoslynMcp/Tools/Analysis/FindReferencesTool.cs`
  - Shows `SymbolFinder.FindReferencesAsync`
  - Shows how named symbols are resolved
  - Shows pagination and project-relative file paths
- `src/RoslynMcp/Tools/Analysis/FindCallersTool.cs`
  - Shows async Roslyn symbol APIs
  - Shows structured caller result entries
- `src/RoslynMcp/Tools/Analysis/GetCallGraphTool.cs`
  - Shows semantic operation walking with `IOperation`
  - Useful model for dependency-style analysis
- `src/RoslynMcp/Tools/Analysis/TypeMembersTool.cs`
  - Shows type lookup by simple or fully qualified name
  - Shows member filtering and signature formatting
- `src/RoslynMcp/Tools/RoslynMcpTool.cs`
  - Shared helpers: `TryGetCompilation`, `FindSymbol`, `FindSymbols`, `ProjectPathDescription`
- `src/RoslynMcp/Tools/ToolResults.cs`
  - Existing response record shapes

Key architecture pattern:

1. Add a new tool class under `src/RoslynMcp/Tools/Analysis/`.
2. Inherit from `RoslynMcpTool`.
3. Add `[McpServerToolType]`.
4. Add a method with `[McpServerTool(Name = "roslyn_...", ReadOnly = true, OpenWorld = false, Idempotent = true)]`.
5. Accept `projectPath`.
6. Call `TryGetCompilation`.
7. Use Roslyn semantic APIs, not text search.
8. Return structured, token-efficient results.
9. Page large result sets.

## Filled Template

### Environment Setup

Used the local fork:

```text
/Users/rubinashaik/RoslynMcp
```

Remote:

```text
https://github.com/rubinashaik2022/RoslynMcp.git
```

Working branch:

```text
issue-19-semantic-analysis-tools
```

Local machine has .NET 8 and .NET 9 SDKs, not .NET 10, so local build verification should target `net8.0`.

Useful setup commands:

```bash
dotnet restore src/RoslynMcp/RoslynMcp.csproj -p:TargetFrameworks=net8.0 -r osx-arm64
dotnet restore src/RoslynMcp.Analyzers/RoslynMcp.Analyzers.csproj
dotnet publish src/RoslynMcp/RoslynMcp.csproj -c Release -f net8.0 -o ./publish/net8.0 -p:TargetFrameworks=net8.0 --no-restore
```

### Steps To Reproduce / Observe

This is not a runtime bug with failing user-facing behavior. It is a missing semantic-analysis capability.

Current RoslynMcp already has tools for references, callers, call graphs, and type members, but it does not yet have dedicated tools for these questions:

1. Which private/internal symbols have no references?
2. What types does this type directly depend on?
3. What overloads exist for this method on this containing type?

The design is described in `docs/plans/tool-suggestions.md`, and the remaining tools are tracked in `ROADMAP.md` under the semantic analysis milestone.

### Expected / Desired

Agents should be able to answer these C# semantic questions without falling back to grep or full-file reads:

- `roslyn_find_unused`: find private/internal dead code candidates
- `roslyn_get_type_dependencies`: list direct type coupling for a type
- `roslyn_find_overloads`: list all overload signatures for a method

### Actual / Current

These specific tools do not exist yet. Agents must approximate the answers by combining broader tools like `roslyn_find_references`, `roslyn_get_type_members`, file reads, or text search.

## Solution Plan

### Understand

Add independent Roslyn-backed tools that follow existing tool conventions. Each tool should be read-only, idempotent, structured, and token-efficient.

### Match

Use these existing tools as implementation references:

- `FindReferencesTool.cs` for symbol reference lookup
- `FindCallersTool.cs` for async semantic lookup and result paging
- `GetCallGraphTool.cs` for dependency-style semantic traversal
- `TypeMembersTool.cs` for type resolution and member enumeration

### Plan

1. Implement `roslyn_find_unused`

   Find private/internal symbols with zero references.

   Suggested logic:

   - Enumerate source-declared private/internal types and members.
   - Exclude implicitly declared members, generated accessors, constructors unless intentionally supported, overrides, interface implementations, and public API.
   - Use `SymbolFinder.FindReferencesAsync` for each candidate.
   - Return only candidates with no reference locations outside the declaration.

   Suggested result fields:

   - `name`
   - `kind`
   - `signature`
   - `accessibility`
   - `file`
   - `line`

2. Implement `roslyn_get_type_dependencies`

   Given a type, return directly referenced types.

   Suggested logic:

   - Resolve the type using the same pattern as `TypeMembersTool`.
   - Collect dependencies from base type, interfaces, fields, properties, method return types, method parameters, generic arguments, and generic constraints.
   - Deduplicate with `SymbolEqualityComparer.Default`.
   - Exclude the containing type itself.

   Suggested result fields:

   - `typeName`
   - `totalDependencies`
   - `dependencies`
   - each dependency: `name`, `namespace`, `kind`, `source`, `reason`

3. Implement `roslyn_find_overloads`

   Given a method name and containing type, return all overloads.

   Suggested logic:

   - Require `containingType`.
   - Resolve the containing type.
   - Filter `GetMembers(methodName)` to `IMethodSymbol`.
   - Exclude property/event accessors and implicitly declared methods.
   - Format signatures with `SymbolFormatter.FormatMethod`.

   Suggested result fields:

   - `containingType`
   - `methodName`
   - `totalOverloads`
   - `overloads`
   - each overload: `signature`, `returnType`, `parameters`, `accessibility`, `isStatic`, `isGeneric`, `file`, `line`

4. Add tests

   Cover these minimum cases:

   - `roslyn_find_unused` reports unused private members.
   - `roslyn_find_unused` does not report referenced private members.
   - `roslyn_get_type_dependencies` reports dependencies from fields, parameters, return types, base type, and interfaces.
   - `roslyn_find_overloads` returns all overloads for a method and excludes unrelated methods.

5. Update docs

   After implementation, update:

   - `README.md` Tool Catalog
   - `docs/AGENT-INSTRUCTIONS.md`
   - `ROADMAP.md`
   - `docs/plans/tool-suggestions.md`

### Review

Check that the new tools:

- Use the `roslyn_*` naming convention.
- Are marked read-only and idempotent.
- Use `TryGetCompilation`.
- Return structured result records.
- Use project-relative file paths.
- Page large result sets.
- Avoid text search for semantic questions.
- Include useful tool descriptions that tell agents when to use them.

### Evaluate

Run:

```bash
dotnet publish src/RoslynMcp/RoslynMcp.csproj -c Release -f net8.0 -o ./publish/net8.0 -p:TargetFrameworks=net8.0
```

Then manually smoke-test against a small C# sample project:

- A deliberately unused private method appears in `roslyn_find_unused`.
- A referenced private method does not appear in `roslyn_find_unused`.
- A type with fields, parameters, return types, base type, and interfaces returns expected dependencies.
- A method with multiple overloads returns every overload signature.

## Clarification

Some older text refers to "four new analysis tools." In the current repository state, three related tools remain unshipped:

- `roslyn_find_unused`
- `roslyn_get_type_dependencies`
- `roslyn_find_overloads`

`roslyn_check_syntax` appears to have been part of the earlier suggestion set, but it has already shipped in v0.8.0-beta.
