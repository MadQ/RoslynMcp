# DI Registration Analysis — `.AddTransient<>` vs `.WithToolsFromAssembly()`
**Date:** 2026-03-22  
**Context:** Tool registration in Program.cs

---

## Question

Do we need manual `.AddTransient<MyNewTool>()` registrations when we're already using `.WithToolsFromAssembly()`?

---

## TL;DR

**NO — the manual `.AddTransient<>()` calls are redundant.** `.WithToolsFromAssembly()` already discovers and registers all `[McpServerToolType]`-marked classes automatically.

---

## Current Code

```csharp
builder.Services
    .AddSingleton(_ => new WorkspaceManager(targetPath))
    .AddSingleton<ApprovalStore>()
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()  // ← Auto-discovers all [McpServerToolType] classes
;

// Register tool types so DI can inject WorkspaceManager and ApprovalStore.
builder.Services
    .AddTransient<TypeMembersTool>()         // ← REDUNDANT
    .AddTransient<DiagnosticsTool>()         // ← REDUNDANT
    .AddTransient<FindReferencesTool>()      // ← REDUNDANT
    // ... 20 more lines of the same pattern
;
```

---

## What `.WithToolsFromAssembly()` Does

From the [C# MCP SDK code samples](https://learn.microsoft.com/azure/container-apps/tutorial-mcp-server-dotnet):

```csharp
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly(); // Add all classes marked with [McpServerToolType]
```

**Behavior:**
1. Scans the assembly for all types decorated with `[McpServerToolType]`
2. Registers each one in the DI container
3. Resolves constructor dependencies automatically (via existing registrations)

**Our tools have these dependencies:**
- `WorkspaceManager` (registered as singleton)
- `ApprovalStore` (registered as singleton)

Both are already in the DI container when `.WithToolsFromAssembly()` runs, so it can inject them automatically.

---

## Why We Have Manual Registrations

**Hypothesis:** We added them thinking they were required for constructor injection to work.

**Reality:** The MCP SDK's `.WithToolsFromAssembly()` already handles DI registration. The manual registrations are leftovers from exploration/experimentation.

**Evidence:**
- All 23 tools work correctly (proven by test suite)
- Removing manual registrations should have zero effect on functionality

---

## Test: Remove Manual Registrations

### Expected Outcome
✅ All tools still work  
✅ All 22 tests still pass  
✅ Code is simpler (25 fewer lines)

### Risks
⚠️ If `.WithToolsFromAssembly()` doesn't actually register tools (maybe it only discovers them for the MCP SDK's internal use), tools would fail at runtime when the SDK tries to resolve them.

**Mitigation:** Run full test suite after removal. If tests pass, we're good.

---

## Recommendation

**Action:** Remove all manual `.AddTransient<>()` tool registrations as an experiment.

**Rationale:**
1. Official docs show `.WithToolsFromAssembly()` is sufficient
2. Redundant code increases maintenance burden (every new tool = 2 places to update)
3. If it fails, we can revert instantly

**Test plan:**
1. Remove manual registrations
2. Build
3. Run TestHarness (all 22 tests)
4. If pass → commit
5. If fail → revert, document why manual registration is needed

---

## Proposed Change

**Before (25 lines):**
```csharp
builder.Services
    .AddSingleton(_ => new WorkspaceManager(targetPath))
    .AddSingleton<ApprovalStore>()
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
;

// Register tool types so DI can inject WorkspaceManager and ApprovalStore.
builder.Services
    .AddTransient<TypeMembersTool>()
    .AddTransient<DiagnosticsTool>()
    // ... 21 more lines
;
```

**After (6 lines):**
```csharp
builder.Services
    .AddSingleton(_ => new WorkspaceManager(targetPath))
    .AddSingleton<ApprovalStore>()
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
;
```

**Diff:** -25 lines, simpler, more maintainable.

---

## If Manual Registration IS Required (Unexpected)

If tests fail after removal, it means `.WithToolsFromAssembly()` does NOT register tools in the DI container — it only discovers them for the MCP SDK.

**In that case:**
- Keep manual registrations
- Document WHY in a code comment:
  ```csharp
  // Manual registration required: .WithToolsFromAssembly() discovers tools for MCP SDK
  // but doesn't register them in the DI container for dependency injection.
  builder.Services
      .AddTransient<TypeMembersTool>()
      // ...
  ;
  ```

---

## Conclusion

**Status:** ⏳ **Experiment Required**

Remove manual `.AddTransient<>()` registrations and run tests. If they pass, commit the cleanup. If they fail, revert and document the requirement.

**Next step:** Try it and find out! 🏴‍☠️
