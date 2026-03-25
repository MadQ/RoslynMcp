# Add `roslyn_change_signature` Tool for Semantic Signature Changes

## Summary

Add a new MCP tool that enables semantic method and delegate signature changes with automatic call site updates. Leverages Roslyn's refactoring infrastructure to safely modify signatures across the codebase.

## Motivation

Current workflow for signature changes is manual and error-prone:
- `roslyn_replace_in_code` can replace method declarations but requires manual call-site updates
- `roslyn_preview_rename` only handles identifier changes, not structural signature changes
- No way to safely add/remove parameters without breaking all call sites

**Common use cases:**
- Adding parameters (e.g., cancellation tokens, context objects)
- Modernizing P/Invoke signatures (`IntPtr` → `nint`, `int` → `MyEnum`)
- Refactoring delegates (adding sender/event args pattern)
- Removing deprecated parameters

Roslyn has the infrastructure (`SymbolFinder.FindCallersAsync`, semantic models, syntax rewriting) to automate this safely.

## Proposed Solution

### Three Tools

**1. `roslyn_change_signature`** — Preview signature change
- Add/remove/reorder parameters
- Non-breaking mode (default): adds new overload, deprecates old with `[Obsolete]` marker
- Breaking mode: updates signature + all call sites
- Returns unified diff + approval token

**2. `roslyn_apply_signature_change`** — Apply previewed change
- Same approval workflow as `roslyn_apply_rename` (`y`/`session`/`n`)

**3. `roslyn_remove_deprecated_overload`** — Cleanup deprecated stubs
- `remove` mode: deletes deprecated method after migration
- `clean` mode: strips RoslynMcp marker, keeps `[Obsolete]` attribute

### Supported Scenarios (Phase 1)

| Type | Non-Breaking | Breaking | Notes |
|------|--------------|----------|-------|
| Regular methods | ✅ | ✅ | Add overload + forwarding stub |
| Extern methods | ❌ | ✅ | P/Invoke refactoring; no stub possible |
| Delegates | ❌ | ✅ | Detects subscribers in diagnostics |
| Operators | ❌ | ✅ | With `force: true`; warns about pairs |
| Conversions | ❌ | ✅ | With `force: true`; big scary warning |

**Deferred to Phase 2:**
- Constructors (`: this(...)` forwarding)
- Indexers (`this[...]`)
- Partial methods (atomic dual-declaration update)
- Generic type parameter changes

### Key Features

**Non-Breaking Mode (Default):**
```csharp
// Before
public void ProcessData(int id) { ... }

// After (non-breaking)
public void ProcessData(int id, string source = "default") { ... }

[Obsolete("RoslynMcp.ChangeSignature: Use ProcessData(int, string) instead. Migration ID: abc123")]
public void ProcessData(int id) => ProcessData(id, "default");
```

**Benefits:**
- Zero build errors at call sites
- Agents can continue work without blocking
- Gradual migration path
- Recognizable marker for cleanup tooling

**Breaking Mode (Opt-in):**
- Changes signature + updates all call sites atomically
- Useful for private methods, prototypes, or when deprecation overhead isn't justified

**Atomic Application:**
- All edits applied in one solution transformation
- No transient IDE errors (Error List never flashes red)
- Operation ordering prevents intermediate broken states

**Rich Diagnostics:**
- Warnings for edge cases (dynamic calls, reflection, paired operators)
- For delegates: lists subscriber methods needing updates
- Enables agent orchestration of multi-step fixes

## Architecture

Detailed design document: [`docs/plans/change-signature-tool.md`](../plans/change-signature-tool.md)

**Component overview:**
```
ChangeSignatureTool (thin MCP entry point)
    ↓
SignatureChangeOrchestrator (validation + workflow)
    ↓
Abstract Class: SignatureEditor (shared helpers + virtual validation)
    ├─ MethodSignatureEditor
    ├─ ExternMethodEditor
    ├─ DelegateSignatureEditor
    ├─ OperatorSignatureEditor
    └─ ConversionSignatureEditor
    ↓
SignatureChangeApplicator (atomic solution transformation)
    ↓
CallSiteUpdater (find & update invocations)
```

**Design highlights:**
- Abstract class pattern (not interface) for shared helpers
- Strategy pattern for method kinds
- Virtual validation methods (use/extend/replace)
- Preview-first workflow (consistent with rename)

## Implementation Plan

**Phase 1: MVP (v0.4.0 target)**
- Add/remove parameters (no reordering)
- Non-breaking + breaking modes
- Preview + apply workflow
- Basic diagnostics
- Method kinds: regular, extern, delegates, operators, conversions

**Phase 2: Advanced (post-v0.4.0)**
- Parameter reordering
- Constructors, indexers, partial methods
- Generic type parameter changes
- Interface/virtual hierarchy auto-update

**Phase 3: Ecosystem (future)**
- General-purpose `roslyn_add_attribute` / `roslyn_remove_attribute` tools
- Batch signature changes
- Undo/redo for refactorings

## Example Usage (Agent Workflow)

### Scenario 1: Add parameter to method
```
Agent: roslyn_change_signature(
  methodName: "ProcessData",
  containingType: "DataService",
  addParameters: [{name: "cancellationToken", type: "CancellationToken", defaultValue: "default"}],
  nonBreaking: true,
  preview: true
)

Tool returns: diff + token + diagnostics

Agent reviews diff, approves:
Agent: roslyn_apply_signature_change(token: "...", approval: "y")
```

### Scenario 2: Change delegate signature
```
Agent: roslyn_change_signature(
  methodName: "DataHandler",  // Delegate name
  addParameters: [{name: "sender", type: "object"}, {name: "e", type: "DataEventArgs"}],
  preview: true
)

Tool returns:
  - Updated delegate declaration
  - Updated invocations
  - List of 5 subscriber methods needing updates

Agent orchestrates:
  - Applies delegate change
  - Calls roslyn_change_signature on each of the 5 subscribers
  - All signatures now consistent
```

## Open Questions

1. **Method overload disambiguation** — if multiple overloads exist, require signature match via parameter types?
2. **Empty [Obsolete] message after cleaning** — remove attribute entirely if only RoslynMcp marker remains?
3. **Method already has [Obsolete]** — append to existing message or use XML doc comment marker?
4. **Approval scope for batch changes** — single approval or per-method?

## Related Work

- **Rename workflow:** `roslyn_preview_rename` + `roslyn_apply_rename` (established two-phase pattern)
- **ApprovalStore:** Session-level approval state for UX consistency
- **SolutionDiff:** Unified diff generation for preview

## Status

📋 **Planned** — Comprehensive design complete, ready for implementation post-v0.3.0

**Documentation:**
- Planning doc: [`docs/plans/change-signature-tool.md`](../plans/change-signature-tool.md) (~1400 lines, fully detailed)
- Architecture, edge cases, code examples, and decision log all documented

**Timeline:**
- v0.3.0: Multi-project infrastructure (in progress)
- v0.4.0: Implement Phase 1 (this feature)

## Labels

- `enhancement`
- `feature`
- `v0.4.0`
- `planned`

## Milestone

v0.4.0
