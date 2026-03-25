# Change Signature Tool — Planning Doc

**Status:** Planning (pre-v0.3.0)  
**Target:** v0.4.0 or later  
**Priority:** Medium (nice-to-have, not blocking)

---

## Overview

Add `roslyn_change_signature` tool to enable semantic method signature changes with automatic call site updates. Supports both non-breaking (add overload + deprecation) and breaking (update all call sites) modes.

---

## Motivation

Current tools for changing method signatures are inadequate:
- `roslyn_replace_in_code` — can replace method declarations but requires manual call site updates
- `roslyn_preview_rename` — only handles identifier changes, not signature structure
- `roslyn_replace_in_file` — text-based, no semantic understanding

**Signature changes are common and risky.** Roslyn already has the infrastructure (`SymbolFinder.FindCallersAsync`, semantic models, syntax rewriting) to do this safely. This is a natural fit for RoslynMcp's mission.

---

## Proposed API

### Tool: `roslyn_change_signature`

```csharp
[McpServerTool, Description("Changes a method signature and optionally updates call sites")]
public object ChangeSignature(
    [Description("Method name to change")] string methodName,
    [Description("Optional containing type to disambiguate")] string? containingType = null,
    [Description("Parameters to add: [{name, type, defaultValue}, ...]")] object[]? addParameters = null,
    [Description("Parameters to remove (array of parameter names)")] string[]? removeParameters = null,
    [Description("Parameter reordering (map of old index -> new index)")] Dictionary<int, int>? reorderParameters = null,
    [Description("Non-breaking mode: adds overload + [Obsolete] (default: true)")] bool nonBreaking = true,
    [Description("Preview only, don't apply (default: true)")] bool preview = true,
    [Description(ProjectPathDescription)] string? projectPath = null)
{
    // Returns: diff, approval token, diagnostics, call site count
}
```

### Tool: `roslyn_apply_signature_change`

```csharp
[McpServerTool, Description("Applies a previewed signature change")]
public object ApplySignatureChange(
    [Description("Approval token from roslyn_change_signature")] string token,
    [Description("'y' to apply, 'session' to apply + remember, 'n' to reject")] string approval)
{
    // Same approval pattern as roslyn_apply_rename
}
```

### Tool: `roslyn_remove_deprecated_overload`

```csharp
[McpServerTool, Description("Removes or cleans up [Obsolete] overloads from roslyn_change_signature")]
public object RemoveDeprecatedOverload(
    [Description("Method name")] string methodName,
    [Description("Optional containing type")] string? containingType = null,
    [Description("'remove' = delete method, 'clean' = strip RoslynMcp marker (default: 'remove')")] 
    string mode = "remove",
    [Description("Preview only (default: true)")] bool preview = true,
    [Description(ProjectPathDescription)] string? projectPath = null)
{
    // Finds methods with [Obsolete("RoslynMcp.ChangeSignature: ...")] 
    // Mode 'remove': deletes the method if all call sites are migrated
    // Mode 'clean': strips marker, keeps [Obsolete] and method body
}
```

**Mode: `remove` (default)**
- Deletes the entire deprecated method
- Validates all call sites are migrated (optional safety check)
- Returns error if call sites still exist

**Mode: `clean`**
- Strips `"RoslynMcp.ChangeSignature: "` prefix and `"Migration ID: <guid>"` suffix
- Leaves user-facing message intact: `"Use Foo(int, string) instead"`
- Keeps the `[Obsolete]` attribute and method body
- Use case: manual migration complete, want standard deprecation without marker

**Example transformation (clean mode):**
```csharp
// Before clean
[Obsolete("RoslynMcp.ChangeSignature: Use ProcessData(int, string) instead. Migration ID: abc123")]
public void ProcessData(int id) => ProcessData(id, "default");

// After clean
[Obsolete("Use ProcessData(int, string) instead")]
public void ProcessData(int id) => ProcessData(id, "default");
```

**Cleaning logic:**
```csharp
// Regex pattern to extract user message
var pattern = @"^RoslynMcp\.ChangeSignature:\s*(.*?)\.\s*Migration ID:\s*[a-f0-9\-]+$";
var match = Regex.Match(obsoleteMessage, pattern);
if (match.Success) {
    var cleanMessage = match.Groups[1].Value;
    // Update attribute with cleaned message
}
```

---

## Behavior Modes

### Non-Breaking Mode (Default: `nonBreaking: true`)

**Before:**
```csharp
public void ProcessData(int id)
{
    // implementation
}
```

**After adding parameter `string source = "default"`:**
```csharp
public void ProcessData(int id, string source = "default")
{
    // implementation
}

[Obsolete("RoslynMcp.ChangeSignature: Use ProcessData(int, string) instead. Migration ID: abc123")]
public void ProcessData(int id) => ProcessData(id, "default");
```

**Benefits:**
- Zero build errors at call sites
- Agent can continue working without blocking
- Clear deprecation path with recognizable marker
- Call sites can be migrated gradually

**Marker Format:**  
`[Obsolete("RoslynMcp.ChangeSignature: <message>. Migration ID: <guid>")]`

The `RoslynMcp.ChangeSignature:` prefix makes it trivial for `roslyn_remove_deprecated_overload` to find these via `roslyn_semantic_search` or `roslyn_search_files`.

### Breaking Mode (`nonBreaking: false`)

**Before:**
```csharp
public void ProcessData(int id) { ... }

// Call sites:
ProcessData(42);
ProcessData(someVar);
```

**After:**
```csharp
public void ProcessData(int id, string source) { ... }

// Call sites updated:
ProcessData(42, "default");
ProcessData(someVar, "default");
```

**Use cases:**
- Private methods (no external callers)
- Prototypes / early development
- When deprecation overhead isn't justified

---

## Preview Response Structure

```json
{
  "success": true,
  "diff": "unified diff of all changes",
  "token": "change_sig_abc123",
  "diagnostics": {
    "errors": [],
    "warnings": [
      "Dynamic invocation at Caller.cs:42 cannot be automatically updated",
      "Reflection usage at Factory.cs:15 may require manual update"
    ]
  },
  "summary": {
    "methodsChanged": 1,
    "callSitesUpdated": 23,
    "deprecatedOverloadAdded": true,
    "filesAffected": 8
  }
}
```

---

## Edge Cases to Handle

| Case | Strategy |
|------|----------|
| **Named arguments** | Preserve or warn if incompatible |
| **Params arrays** | Warn if reordering affects variadic calls |
| **Extension methods** | Handle `this` parameter specially |
| **Interface implementations** | Update interface + all implementers |
| **Virtual/override chains** | Update entire hierarchy atomically |
| **Dynamic invocations** | Warn — cannot auto-update |
| **Reflection** | Warn — may require manual fixup |
| **Async methods** | Handle `Task<T>` return type correctly |
| **Optional parameters** | Validate default value compatibility |

---

## Implementation Notes

### Phase 1: MVP (v0.4.0 target)
- Add/remove parameters only (no reordering)
- Non-breaking mode only
- Preview + apply workflow (like rename)
- Basic diagnostics (errors/warnings)

### Phase 2: Full Feature (post-v0.4.0)
- Parameter reordering
- Breaking mode support
- Interface/virtual hierarchy handling
- Advanced diagnostics

### Phase 3: Ecosystem (future)
- `roslyn_remove_deprecated_overload` cleanup tool
- `roslyn_add_attribute` / `roslyn_remove_attribute` (general-purpose attribute editing)
- Batch signature change support

---

## Roslyn API Surface

Key APIs to leverage:

```csharp
// Find method symbol
var symbol = semanticModel.GetDeclaredSymbol(methodSyntax);

// Find all call sites
var callers = await SymbolFinder.FindCallersAsync(symbol, solution);

// Update method signature
var newMethod = methodSyntax
    .AddParameterListParameters(newParam)
    .WithTrailingTrivia(...);

// Add [Obsolete] attribute
var obsoleteAttr = SyntaxFactory.Attribute(
    SyntaxFactory.ParseName("System.Obsolete"),
    SyntaxFactory.AttributeArgumentList(...)
);

// Update call sites
foreach(var caller in callers) {
    var invocation = caller.CallingSymbol;
    var newInvocation = invocation.AddArgumentListArguments(defaultArg);
    // Apply via DocumentEditor
}
```

---

## Open Questions

1. **Should we support method overload disambiguation?**
   - If multiple overloads exist, how does agent specify which one?
   - Option: require full signature match via parameter types?

2. **How to handle partial classes?**
   - Method declared in one file, call sites in others
   - Should work naturally via Roslyn's symbol resolution

3. **What about generic methods?**
   - Adding type parameters, constraints
   - Defer to Phase 2?

4. **Approval scope for batch changes?**
   - If changing 10 methods at once, single approval or per-method?
   - Lean toward single approval for simplicity

5. **Integration with existing ApprovalStore?**
   - Reuse for session-level "auto-approve signature changes" mode?
   - Probably yes — consistent UX with rename

6. **What if cleaned [Obsolete] message is empty?**
   - If original was `[Obsolete("RoslynMcp.ChangeSignature: Migration ID: abc123")]`
   - After cleaning: `[Obsolete("")]` — should we remove attribute entirely?
   - Lean toward: yes, remove attribute if no meaningful message remains

---

## Related Future Work

### `roslyn_add_attribute` / `roslyn_remove_attribute`
Generic attribute manipulation tool for adding/removing attributes on types, methods, parameters, etc.

```csharp
public object AddAttribute(
    string targetName,
    string attributeName,
    string? containingType = null,
    object[]? attributeArguments = null,
    string? projectPath = null)
```

Use cases:
- Add `[Obsolete]` manually
- Add `[JsonProperty]`, `[Required]`, etc.
- Remove stale attributes

**Priority:** Low (signature change tool has higher ROI)

---

## Timeline

- **Pre-v0.3.0:** This planning doc (DONE)
- **v0.3.0:** Focus on multi-project infrastructure, stabilize core tools
- **Post-v0.3.0:** Review this doc, refine API based on any new insights from v0.3.0 work
- **v0.4.0 target:** Implement Phase 1 (MVP)
- **Future:** Phase 2-3 as demand warrants

---

## Decision Log

| Date | Decision | Rationale |
|------|----------|-----------|
| 2025-01-XX | Non-breaking mode as default | Minimizes disruption; agents can continue work without fixing all call sites immediately |
| 2025-01-XX | Recognizable `[Obsolete]` marker | Makes cleanup tooling reliable and simple via text search |
| 2025-01-XX | Preview-first workflow | Consistent with rename; avoids surprise breaking changes |
| 2025-01-XX | Defer to post-v0.3.0 | v0.3.0 focused on multi-project infrastructure; signature changes are complex and non-blocking |
| 2025-01-XX | `mode` parameter for cleanup tool | Single tool with `remove`/`clean` modes more intuitive than separate tools; keeps tool list manageable |

---

**Next steps:** Revisit before v0.3.0 release to confirm priority and finalize API design.
