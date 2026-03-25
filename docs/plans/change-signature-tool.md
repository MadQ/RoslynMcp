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
    [Description("Bypass safety checks (allow virtual/operator changes, default: false)")] bool force = false,
    [Description("Preview only, don't apply (default: true)")] bool preview = true,
    [Description(ProjectPathDescription)] string? projectPath = null)
{
    // Returns: diff, approval token, diagnostics, call site count
}
```

**Parameters:**
- `methodName` — method to change
- `containingType` — optional type qualifier for disambiguation
- `addParameters` — new parameters to add (with default values)
- `removeParameters` — parameters to remove by name
- `reorderParameters` — (Phase 2) index mapping for reordering
- `nonBreaking` — add overload + deprecation (default) vs. breaking change
- **`force`** — bypass safety checks for virtual/override/operator methods
- `preview` — return diff without applying (default)
- `projectPath` — target project (optional)

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

## Architecture & Implementation

This feature is **too complex for a single tool class**. We need clean separation of concerns with well-defined patterns.

### Design Principles

1. **Single Responsibility** — each component has one job
2. **Strategy Pattern** — different method kinds (regular, extern, operator) use specialized editors
3. **Atomic Application** — all edits applied in one shot to avoid transient IDE errors
4. **Testability** — each component can be unit tested in isolation
5. **Extensibility** — Phase 2 features (constructors, indexers) add new strategies without changing core logic

### Component Architecture

```
ChangeSignatureTool (thin MCP tool entry point)
    ↓
SignatureChangeOrchestrator (validation + workflow coordination)
    ↓
┌─────────────────────────────────────────────────┐
│  Abstract Class: SignatureEditor                │
│  (provides shared helpers + virtual validation) │
├─────────────────────────────────────────────────┤
│  - MethodSignatureEditor (regular methods)      │
│  - ExternMethodEditor (P/Invoke refactoring)    │
│  - OperatorSignatureEditor (operator overloads) │
│  - ConversionSignatureEditor (implicit/explicit)│
│  - (Phase 2) ConstructorEditor                  │
│  - (Phase 2) IndexerEditor                      │
└─────────────────────────────────────────────────┘
    ↓
SignatureChangeApplicator (atomic solution transformation)
    ↓
CallSiteUpdater (find & update invocations)
```

**Why abstract class instead of interface?**
- No external implementations planned (internal RoslynMcp-only)
- Shared utilities needed: `CreateObsoleteAttribute()`, `BuildNewParameterList()`, `CreateForwardingInvocation()`
- Enables `protected virtual` validation that subclasses can use, extend, or replace
- Avoids premature abstraction — abstract classes are for inheritance hierarchies, interfaces are for contracts
- Still testable — concrete subclasses tested directly without mocking

---

### 1. Tool Class (Thin Entry Point)

**File:** `Tools/Refactoring/ChangeSignatureTool.cs`

```csharp
[McpServerToolType]
internal sealed class ChangeSignatureTool : RoslynMcpTool
{
    public ChangeSignatureTool(WorkspaceResolver workspace) : base(workspace) { }

    [McpServerTool, Description("Changes a method signature...")]
    public object ChangeSignature(
        string methodName,
        string? containingType = null,
        object[]? addParameters = null,
        string[]? removeParameters = null,
        bool nonBreaking = true,
        bool force = false,  // Bypass safety checks
        bool preview = true,
        string? projectPath = null)
    {
        using var scope = BeginTool($"{containingType}.{methodName}");

        if(!TryGetCompilation(projectPath, out var compilation, out var error))
            return scope.Failed(error);

        try {
            var orchestrator = new SignatureChangeOrchestrator(
                workspace.GetSolution(projectPath),
                compilation
            );

            var result = orchestrator.PrepareSignatureChange(
                methodName,
                containingType,
                addParameters,
                removeParameters,
                nonBreaking,
                force
            );

            if(!result.Success)
                return scope.Failed(result.Error);

            if(preview) {
                var token = ApprovalStore.StorePendingChange(result);
                return scope.Outcome($"{result.FilesAffected} files", new {
                    diff = result.UnifiedDiff,
                    token,
                    warnings = result.Warnings,
                    diagnostics = result.Diagnostics,
                    summary = result.Summary
                });
            }

            var applied = orchestrator.ApplySignatureChange(result);
            return scope.Outcome($"Applied to {applied.FilesAffected} files", applied);
        }
        catch(Exception ex) {
            return scope.Failed(ex);
        }
    }
}
```

**Responsibilities:**
- MCP protocol integration
- Parameter validation
- Delegates to orchestrator
- Handles preview/apply workflow

---

### 2. Orchestrator (Validation + Workflow)

**File:** `Tools/Refactoring/SignatureChange/SignatureChangeOrchestrator.cs`

```csharp
internal sealed class SignatureChangeOrchestrator
{
    private readonly Solution _solution;
    private readonly Compilation _compilation;

    public SignatureChangeOrchestrator(Solution solution, Compilation compilation)
    {
        _solution = solution;
        _compilation = compilation;
    }

    public async Task<SignatureChangeResult> PrepareSignatureChange(
        string methodName,
        string? containingType,
        object[]? addParameters,
        string[]? removeParameters,
        bool nonBreaking,
        bool force)
    {
        // 1. Find target symbol
        var symbol = FindMethodSymbol(methodName, containingType);
        if(symbol == null)
            return SignatureChangeResult.Error($"Method '{methodName}' not found");

        // 2. Select appropriate editor strategy
        var editor = SelectEditor(symbol, force, out var warnings);
        if(editor == null)
            return SignatureChangeResult.Error(warnings.FirstOrDefault() 
                ?? "Method kind not supported");

        // 3. Build parameter change specification
        var paramSpec = new ParameterChangeSpec(addParameters, removeParameters);

        // 4. Validate parameter changes
        var validation = editor.ValidateParameterChanges(symbol, paramSpec);
        if(!validation.IsValid && !force)
            return SignatureChangeResult.Error(validation.ErrorMessage);

        warnings.AddRange(validation.Warnings);

        // 5. Find all call sites
        var callSites = await CallSiteUpdater.FindCallSitesAsync(symbol, _solution);

        // 6. Generate edits (non-breaking or breaking)
        var edits = nonBreaking
            ? editor.GenerateNonBreakingEdits(symbol, paramSpec, callSites)
            : editor.GenerateBreakingEdits(symbol, paramSpec, callSites);

        // 7. Apply edits to solution (in-memory)
        var newSolution = await SignatureChangeApplicator.ApplyEditsAsync(
            _solution, 
            edits
        );

        // 8. Generate unified diff
        var diff = SolutionDiff.GenerateUnifiedDiff(_solution, newSolution);

        // 9. Collect diagnostics from new solution
        var diagnostics = await GetDiagnosticsAsync(newSolution, edits.AffectedDocuments);

        return new SignatureChangeResult {
            Success = true,
            NewSolution = newSolution,
            UnifiedDiff = diff,
            Warnings = warnings,
            Diagnostics = diagnostics,
            Summary = new {
                methodsChanged = edits.MethodsChanged,
                callSitesUpdated = edits.CallSitesUpdated,
                filesAffected = edits.AffectedDocuments.Count,
                nonBreaking = nonBreaking
            }
        };
    }

    private SignatureEditor? SelectEditor(
        IMethodSymbol method,
        bool force,
        out List<string> warnings)
    {
        warnings = new List<string>();

        // Delegates (MethodKind.DelegateInvoke)
        if(method.MethodKind == MethodKind.DelegateInvoke)
        {
            warnings.Add("Delegate signature change is breaking — all subscribers must be updated");
            return new DelegateSignatureEditor();
        }

        // Regular methods
        if(method.MethodKind == MethodKind.Ordinary)
        {
            if(method.IsExtern)
            {
                warnings.Add("Extern method: no forwarding stub will be created");
                return new ExternMethodEditor();
            }

            if(method.IsOverride && !force)
            {
                warnings.Add("Method is an override. Use force: true or target base class.");
                return null;
            }

            return new MethodSignatureEditor();
        }

        // Operators
        if(method.MethodKind == MethodKind.UserDefinedOperator)
        {
            warnings.Add("Operator signature change may break symmetry (==, !=, etc.)");
            return force ? new OperatorSignatureEditor() : null;
        }

        // Conversions
        if(method.MethodKind == MethodKind.Conversion)
        {
            warnings.Add("WARNING: Changing conversion signature breaks cast semantics!");
            return force ? new ConversionSignatureEditor() : null;
        }

        // Unsupported (Phase 2)
        if(method.MethodKind == MethodKind.Constructor)
            warnings.Add("Constructor signature changes deferred to Phase 2");

        return null;
    }
}
```

---

### 3. Strategy Pattern: `SignatureEditor` Abstract Class

**File:** `Tools/Refactoring/SignatureChange/Editors/SignatureEditor.cs`

**Design Choice: Abstract Class vs. Interface**

We use an abstract class instead of an interface:
- No external implementations planned
- Shared helpers needed (`CreateObsoleteAttribute`, `BuildNewParameterList`, `CreateForwardingInvocation`)
- Enables `protected virtual` validation methods
- Avoids premature abstraction

```csharp
/// <summary>
/// Base class for signature change strategies.
/// Provides shared utilities and defines the workflow contract.
/// </summary>
internal abstract class SignatureEditor
{
    /// <summary>
    /// Validates parameter changes for this method kind.
    /// Base implementation checks duplicate names and default value syntax.
    /// </summary>
    protected virtual ValidationResult ValidateParameterChanges(
        IMethodSymbol method,
        ParameterChangeSpec paramSpec)
    {
        var warnings = new List<string>();

        // Common validation: check for duplicate parameter names
        var existingNames = method.Parameters.Select(p => p.Name).ToHashSet();
        foreach(var addParam in paramSpec.AddParameters) {
            if(existingNames.Contains(addParam.Name))
                return ValidationResult.Invalid($"Parameter '{addParam.Name}' already exists");
        }

        // Common validation: check default value syntax
        foreach(var addParam in paramSpec.AddParameters) {
            if(addParam.DefaultValue != null && !IsValidExpression(addParam.DefaultValue))
                warnings.Add($"Default value '{addParam.DefaultValue}' may not parse correctly");
        }

        return ValidationResult.Valid(warnings);
    }

    /// <summary>
    /// Generates edits for non-breaking mode (add overload + deprecate old).
    /// </summary>
    public abstract SignatureEdits GenerateNonBreakingEdits(
        IMethodSymbol method,
        ParameterChangeSpec paramSpec,
        IReadOnlyList<CallSiteInfo> callSites);

    /// <summary>
    /// Generates edits for breaking mode (change signature + update call sites).
    /// </summary>
    public abstract SignatureEdits GenerateBreakingEdits(
        IMethodSymbol method,
        ParameterChangeSpec paramSpec,
        IReadOnlyList<CallSiteInfo> callSites);

    // Protected shared utilities (concrete implementations)

    /// <summary>
    /// Creates [Obsolete] attribute with RoslynMcp marker.
    /// </summary>
    protected AttributeListSyntax CreateObsoleteAttribute(string message)
    {
        var obsoleteAttr = SyntaxFactory.Attribute(
            SyntaxFactory.ParseName("System.Obsolete"),
            SyntaxFactory.AttributeArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.AttributeArgument(
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal(message)
                        )
                    )
                )
            )
        );

        return SyntaxFactory.AttributeList(
            SyntaxFactory.SingletonSeparatedList(obsoleteAttr)
        );
    }

    /// <summary>
    /// Builds new parameter list by adding/removing parameters.
    /// </summary>
    protected ParameterListSyntax BuildNewParameterList(
        ParameterListSyntax existingParams,
        ParameterChangeSpec paramSpec)
    {
        var parameters = existingParams.Parameters.ToList();

        // Remove parameters
        foreach(var removeParam in paramSpec.RemoveParameters) {
            parameters.RemoveAll(p => p.Identifier.Text == removeParam);
        }

        // Add parameters
        foreach(var addParam in paramSpec.AddParameters) {
            var newParam = SyntaxFactory.Parameter(
                SyntaxFactory.Identifier(addParam.Name)
            )
            .WithType(SyntaxFactory.ParseTypeName(addParam.Type));

            if(addParam.DefaultValue != null) {
                newParam = newParam.WithDefault(
                    SyntaxFactory.EqualsValueClause(
                        SyntaxFactory.ParseExpression(addParam.DefaultValue)
                    )
                );
            }

            parameters.Add(newParam);
        }

        return SyntaxFactory.ParameterList(
            SyntaxFactory.SeparatedList(parameters)
        );
    }

    /// <summary>
    /// Creates forwarding invocation for deprecated stub.
    /// </summary>
    protected InvocationExpressionSyntax CreateForwardingInvocation(
        MethodDeclarationSyntax oldMethod,
        MethodDeclarationSyntax newMethod,
        ParameterChangeSpec paramSpec)
    {
        // Build argument list: existing params + defaults for new params
        var args = oldMethod.ParameterList.Parameters
            .Select(p => SyntaxFactory.Argument(
                SyntaxFactory.IdentifierName(p.Identifier)
            ))
            .ToList();

        // Add default values for new parameters
        foreach(var addParam in paramSpec.AddParameters) {
            args.Add(SyntaxFactory.Argument(
                SyntaxFactory.ParseExpression(addParam.DefaultValue ?? "default")
            ));
        }

        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.IdentifierName(newMethod.Identifier),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(args)
            )
        );
    }

    private static bool IsValidExpression(string expr)
    {
        try {
            SyntaxFactory.ParseExpression(expr);
            return true;
        }
        catch {
            return false;
        }
    }
}
```

**Usage Patterns:**

**Pattern 1: Use base validation as-is**
```csharp
internal sealed class MethodSignatureEditor : SignatureEditor
{
    // Inherits ValidateParameterChanges — common checks are sufficient
    public override SignatureEdits GenerateNonBreakingEdits(...) { ... }
    public override SignatureEdits GenerateBreakingEdits(...) { ... }
}
```

**Pattern 2: Extend base validation**
```csharp
internal sealed class ExternMethodEditor : SignatureEditor
{
    protected override ValidationResult ValidateParameterChanges(...)
    {
        // Call base validation first
        var baseResult = base.ValidateParameterChanges(method, paramSpec);
        if(!baseResult.IsValid)
            return baseResult;

        // Add P/Invoke-specific checks
        var warnings = baseResult.Warnings.ToList();
        foreach(var addParam in paramSpec.AddParameters) {
            if(addParam.Type.Contains("string"))
                warnings.Add($"P/Invoke string may need [MarshalAs]");
        }

        return ValidationResult.Valid(warnings);
    }
}
```

**Pattern 3: Replace validation entirely**
```csharp
internal sealed class OperatorSignatureEditor : SignatureEditor
{
    protected override ValidationResult ValidateParameterChanges(...)
    {
        // Operators have strict rules — base validation doesn't apply
        var finalCount = method.Parameters.Length 
            + paramSpec.AddParameters.Length 
            - paramSpec.RemoveParameters.Length;

        if(finalCount < 1 || finalCount > 2)
            return ValidationResult.Invalid("Operators must have 1-2 parameters");

        return ValidationResult.Valid();
    }
}
```

#### Implementation: `MethodSignatureEditor`

**File:** `Tools/Refactoring/SignatureChange/Editors/MethodSignatureEditor.cs`

```csharp
internal sealed class MethodSignatureEditor : SignatureEditor
{
    // Inherits base ValidateParameterChanges — adds method-specific checks
    protected override ValidationResult ValidateParameterChanges(
        IMethodSymbol method,
        ParameterChangeSpec paramSpec)
    {
        // Call base validation (duplicate names, default value syntax)
        var baseResult = base.ValidateParameterChanges(method, paramSpec);
        if(!baseResult.IsValid)
            return baseResult;

        var warnings = baseResult.Warnings.ToList();

        // Method-specific: check for 'this' parameter (would make it extension method)
        if(paramSpec.AddParameters.Any(p => p.Name == "this")) {
            return ValidationResult.Invalid("Cannot add 'this' parameter to existing method");
        }

        return ValidationResult.Valid(warnings);
    }

    public override SignatureEdits GenerateNonBreakingEdits(
        IMethodSymbol method,
        ParameterChangeSpec paramSpec,
        IReadOnlyList<CallSiteInfo> callSites)
    {
        var edits = new List<DocumentEdit>();

        var existingDecl = (MethodDeclarationSyntax)method.DeclaringSyntaxReferences.First().GetSyntax();
        var newMethod = CreateMethodWithNewSignature(existingDecl, paramSpec);
        var deprecatedMethod = CreateDeprecatedForwardingStub(existingDecl, newMethod, paramSpec);

        // Atomic edit: insert new method BEFORE old, replace old with stub
        // Order matters: avoids transient errors
        edits.Add(new DocumentEdit {
            DocumentId = method.ContainingDocument.Id,
            Changes = new[] {
                new NodeChange {
                    Operation = NodeOperation.InsertBefore,
                    OriginalNode = existingDecl,
                    NewNode = newMethod
                },
                new NodeChange {
                    Operation = NodeOperation.Replace,
                    OriginalNode = existingDecl,
                    NewNode = deprecatedMethod
                }
            }
        });

        return new SignatureEdits {
            MethodsChanged = 2,  // New method + deprecated stub
            CallSitesUpdated = 0,  // Non-breaking: call sites unchanged
            AffectedDocuments = new[] { method.ContainingDocument.Id },
            DocumentEdits = edits
        };
    }

    public override SignatureEdits GenerateBreakingEdits(
        IMethodSymbol method,
        ParameterChangeSpec paramSpec,
        IReadOnlyList<CallSiteInfo> callSites)
    {
        var edits = new List<DocumentEdit>();

        var existingDecl = (MethodDeclarationSyntax)method.DeclaringSyntaxReferences.First().GetSyntax();
        var newMethod = CreateMethodWithNewSignature(existingDecl, paramSpec);

        edits.Add(new DocumentEdit {
            DocumentId = method.ContainingDocument.Id,
            Changes = new[] {
                new NodeChange {
                    Operation = NodeOperation.Replace,
                    OriginalNode = existingDecl,
                    NewNode = newMethod
                }
            }
        });

        // Update all call sites with default arguments
        foreach(var callSite in callSites) {
            var invocation = callSite.InvocationSyntax;
            var updatedInvocation = UpdateInvocationArguments(invocation, paramSpec);

            edits.Add(new DocumentEdit {
                DocumentId = callSite.DocumentId,
                Changes = new[] {
                    new NodeChange {
                        Operation = NodeOperation.Replace,
                        OriginalNode = invocation,
                        NewNode = updatedInvocation
                    }
                }
            });
        }

        return new SignatureEdits {
            MethodsChanged = 1,
            CallSitesUpdated = callSites.Count,
            AffectedDocuments = edits.Select(e => e.DocumentId).Distinct().ToList(),
            DocumentEdits = edits
        };
    }

    private MethodDeclarationSyntax CreateMethodWithNewSignature(
        MethodDeclarationSyntax existingMethod,
        ParameterChangeSpec paramSpec)
    {
        var newParameterList = BuildNewParameterList(existingMethod.ParameterList, paramSpec);

        // Preserve trivia and body using .With* methods
        return existingMethod
            .WithParameterList(newParameterList)
            .WithLeadingTrivia(existingMethod.GetLeadingTrivia())
            .WithTrailingTrivia(existingMethod.GetTrailingTrivia())
            .WithAttributeLists(SyntaxFactory.List<AttributeListSyntax>());
    }

    private MethodDeclarationSyntax CreateDeprecatedForwardingStub(
        MethodDeclarationSyntax existingMethod,
        MethodDeclarationSyntax newMethod,
        ParameterChangeSpec paramSpec)
    {
        var obsoleteMessage = 
            $"RoslynMcp.ChangeSignature: Use {newMethod.Identifier}(...) instead. " +
            $"Migration ID: {Guid.NewGuid():N}";
        var obsoleteAttr = CreateObsoleteAttribute(obsoleteMessage);
        var forwardingBody = CreateForwardingInvocation(existingMethod, newMethod, paramSpec);

        // Convert to expression-bodied member with [Obsolete]
        return existingMethod
            .WithAttributeLists(existingMethod.AttributeLists.Add(obsoleteAttr))
            .WithBody(null)
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(forwardingBody))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    private InvocationExpressionSyntax UpdateInvocationArguments(
        InvocationExpressionSyntax invocation,
        ParameterChangeSpec paramSpec)
    {
        var args = invocation.ArgumentList.Arguments.ToList();

        // Add default arguments for new parameters
        foreach(var addParam in paramSpec.AddParameters) {
            var defaultArg = SyntaxFactory.Argument(
                SyntaxFactory.ParseExpression(addParam.DefaultValue ?? "default")
            );
            args.Add(defaultArg);
        }

        // Remove arguments for removed parameters
        foreach(var removeParam in paramSpec.RemoveParameters) {
            var index = FindParameterIndex(args, removeParam);
            if(index >= 0)
                args.RemoveAt(index);
        }

        return invocation.WithArgumentList(
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args))
        );
    }

    private static int FindParameterIndex(List<ArgumentSyntax> args, string paramName)
    {
        for(int i = 0; i < args.Count; i++) {
            if(args[i].NameColon?.Name.Identifier.Text == paramName)
                return i;
        }
        return -1;
    }
}
```

#### Implementation: `ExternMethodEditor`

**File:** `Tools/Refactoring/SignatureChange/Editors/ExternMethodEditor.cs`

```csharp
internal sealed class ExternMethodEditor : SignatureEditor
{
    protected override ValidationResult ValidateParameterChanges(
        IMethodSymbol method,
        ParameterChangeSpec paramSpec)
    {
        var baseResult = base.ValidateParameterChanges(method, paramSpec);
        if(!baseResult.IsValid)
            return baseResult;

        var warnings = baseResult.Warnings.ToList();

        foreach(var addParam in paramSpec.AddParameters) {
            if(addParam.Type.Contains("string") && !addParam.Type.Contains("MarshalAs")) {
                warnings.Add($"P/Invoke string parameter '{addParam.Name}' may need [MarshalAs] attribute");
            }
        }

        return ValidationResult.Valid(warnings);
    }

    public override SignatureEdits GenerateNonBreakingEdits(
        IMethodSymbol method,
        ParameterChangeSpec paramSpec,
        IReadOnlyList<CallSiteInfo> callSites)
    {
        // Can't create forwarding stub for extern — fall back to breaking mode
        return GenerateBreakingEdits(method, paramSpec, callSites);
    }

    public override SignatureEdits GenerateBreakingEdits(
        IMethodSymbol method,
        ParameterChangeSpec paramSpec,
        IReadOnlyList<CallSiteInfo> callSites)
    {
        var edits = new List<DocumentEdit>();

        var existingDecl = (MethodDeclarationSyntax)method.DeclaringSyntaxReferences.First().GetSyntax();
        var newMethod = existingDecl
            .WithParameterList(BuildNewParameterList(existingDecl.ParameterList, paramSpec))
            .WithLeadingTrivia(existingDecl.GetLeadingTrivia())
            .WithTrailingTrivia(existingDecl.GetTrailingTrivia());

        edits.Add(new DocumentEdit {
            DocumentId = method.ContainingDocument.Id,
            Changes = new[] {
                new NodeChange {
                    Operation = NodeOperation.Replace,
                    OriginalNode = existingDecl,
                    NewNode = newMethod
                }
            }
        });

        // Update call sites
        foreach(var callSite in callSites) {
            var invocation = callSite.InvocationSyntax;
            var updatedInvocation = UpdateCallSiteArguments(invocation, paramSpec);

            edits.Add(new DocumentEdit {
                DocumentId = callSite.DocumentId,
                Changes = new[] {
                    new NodeChange {
                        Operation = NodeOperation.Replace,
                        OriginalNode = invocation,
                        NewNode = updatedInvocation
                    }
                }
            });
        }

        return new SignatureEdits {
            MethodsChanged = 1,
            CallSitesUpdated = callSites.Count,
            AffectedDocuments = edits.Select(e => e.DocumentId).Distinct().ToList(),
            DocumentEdits = edits
        };
    }

    private InvocationExpressionSyntax UpdateCallSiteArguments(
        InvocationExpressionSyntax invocation,
        ParameterChangeSpec paramSpec)
    {
        var args = invocation.ArgumentList.Arguments.ToList();

        foreach(var addParam in paramSpec.AddParameters) {
            args.Add(SyntaxFactory.Argument(
                SyntaxFactory.ParseExpression(addParam.DefaultValue ?? "default")
            ));
        }

        foreach(var removeParam in paramSpec.RemoveParameters) {
            var index = args.FindIndex(a => a.NameColon?.Name.Identifier.Text == removeParam);
            if(index >= 0)
                args.RemoveAt(index);
        }

        return invocation.WithArgumentList(
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args))
        );
    }
}
```

**Responsibilities:**
- Handle P/Invoke signature changes (e.g., `int wParam` → `MyEnum wParam`)
- Add marshaling warnings for string parameters
- No forwarding stubs possible (extern methods can't have implementations)
- Falls back to breaking mode for non-breaking requests

#### Implementation: `DelegateSignatureEditor`

**File:** `Tools/Refactoring/SignatureChange/Editors/DelegateSignatureEditor.cs`

```csharp
internal sealed class DelegateSignatureEditor : SignatureEditor
{
    protected override ValidationResult ValidateParameterChanges(
        IMethodSymbol method,
        ParameterChangeSpec paramSpec)
    {
        var baseResult = base.ValidateParameterChanges(method, paramSpec);
        if(!baseResult.IsValid)
            return baseResult;

        var warnings = baseResult.Warnings.ToList();
        warnings.Add("Delegate signature change is BREAKING — all subscribers must be updated");

        return ValidationResult.Valid(warnings);
    }

    public override SignatureEdits GenerateNonBreakingEdits(
        IMethodSymbol method,
        ParameterChangeSpec paramSpec,
        IReadOnlyList<CallSiteInfo> callSites)
    {
        // Can't overload delegates — fall back to breaking mode
        return GenerateBreakingEdits(method, paramSpec, callSites);
    }

    public override SignatureEdits GenerateBreakingEdits(
        IMethodSymbol method,
        ParameterChangeSpec paramSpec,
        IReadOnlyList<CallSiteInfo> callSites)
    {
        var edits = new List<DocumentEdit>();
        var warnings = new List<string>();

        // 1. Find and update delegate declaration
        var delegateType = method.ContainingType;  // INamedTypeSymbol for the delegate
        var delegateDecl = FindDelegateDeclaration(delegateType);
        var newDelegate = UpdateDelegateSignature(delegateDecl, paramSpec);

        edits.Add(new DocumentEdit {
            DocumentId = delegateDecl.SyntaxTree.GetDocument().Id,
            Changes = new[] {
                new NodeChange {
                    Operation = NodeOperation.Replace,
                    OriginalNode = delegateDecl,
                    NewNode = newDelegate
                }
            }
        });

        // Update all delegate invocations
        foreach(var callSite in callSites) {
            var invocation = callSite.InvocationSyntax;
            var updated = UpdateInvocationArguments(invocation, paramSpec);

            edits.Add(new DocumentEdit {
                DocumentId = callSite.DocumentId,
                Changes = new[] {
                    new NodeChange {
                        Operation = NodeOperation.Replace,
                        OriginalNode = invocation,
                        NewNode = updated
                    }
                }
            });
        }

        // Find subscriber methods for diagnostics
        var subscribers = FindSubscriberMethods(delegateType);

        if(subscribers.Any()) {
            warnings.Add($"Found {subscribers.Count} subscriber methods that require signature updates:");
            foreach(var sub in subscribers.Take(10)) {
                warnings.Add($"  - {sub.ContainingType.Name}.{sub.Name} ({sub.Locations.First().GetLineSpan().Path}:{sub.Locations.First().GetLineSpan().StartLinePosition.Line + 1})");
            }
            if(subscribers.Count > 10)
                warnings.Add($"  ... and {subscribers.Count - 10} more");
        }

        // Find event declarations using this delegate
        var events = FindEventDeclarations(delegateType);

        return new SignatureEdits {
            MethodsChanged = 1,  // Delegate declaration
            CallSitesUpdated = callSites.Count,  // Invocations only
            AffectedDocuments = edits.Select(e => e.DocumentId).Distinct().ToList(),
            DocumentEdits = edits,
            Warnings = warnings,
            DiagnosticData = new {
                subscriberMethods = subscribers.Select(s => new {
                    containingType = s.ContainingType.Name,
                    methodName = s.Name,
                    currentSignature = s.ToDisplayString(),
                    file = s.Locations.First().GetLineSpan().Path,
                    line = s.Locations.First().GetLineSpan().StartLinePosition.Line + 1
                }),
                events = events.Select(e => new {
                    name = e.Name,
                    file = e.Locations.First().GetLineSpan().Path,
                    line = e.Locations.First().GetLineSpan().StartLinePosition.Line + 1
                })
            }
        };
    }

    private DelegateDeclarationSyntax FindDelegateDeclaration(INamedTypeSymbol delegateType)
    {
        var syntaxRef = delegateType.DeclaringSyntaxReferences.First();
        return (DelegateDeclarationSyntax)syntaxRef.GetSyntax();
    }

    private DelegateDeclarationSyntax UpdateDelegateSignature(
        DelegateDeclarationSyntax delegateDecl,
        ParameterChangeSpec paramSpec)
    {
        var newParameterList = BuildNewParameterList(delegateDecl.ParameterList, paramSpec);

        return delegateDecl
            .WithParameterList(newParameterList)
            .WithLeadingTrivia(delegateDecl.GetLeadingTrivia())
            .WithTrailingTrivia(delegateDecl.GetTrailingTrivia());
    }

    private IReadOnlyList<IMethodSymbol> FindSubscriberMethods(INamedTypeSymbol delegateType)
    {
        // Find methods whose signature matches the delegate
        // AND are assigned to delegate-typed variables/events
        // This is complex — may need SymbolFinder.FindReferencesAsync on the delegate type
        // then filter for method assignments

        // Simplified implementation for planning doc
        var compilation = delegateType.ContainingCompilation;
        var subscribers = new List<IMethodSymbol>();

        // TODO: Use SymbolFinder to find all references to delegate type
        // Filter for method group assignments, event subscriptions, etc.

        return subscribers;
    }

    private IReadOnlyList<IEventSymbol> FindEventDeclarations(INamedTypeSymbol delegateType)
    {
        var compilation = delegateType.ContainingCompilation;
        var events = new List<IEventSymbol>();

        // Find all events in the solution that use this delegate type
        foreach(var tree in compilation.SyntaxTrees) {
            var semanticModel = compilation.GetSemanticModel(tree);
            var eventDecls = tree.GetRoot()
                .DescendantNodes()
                .OfType<EventFieldDeclarationSyntax>();

            foreach(var eventDecl in eventDecls) {
                var symbol = semanticModel.GetDeclaredSymbol(eventDecl.Declaration.Variables.First());
                if(symbol is IEventSymbol eventSymbol && 
                   SymbolEqualityComparer.Default.Equals(eventSymbol.Type, delegateType)) {
                    events.Add(eventSymbol);
                }
            }
        }

        return events;
    }

    private InvocationExpressionSyntax UpdateInvocationArguments(
        InvocationExpressionSyntax invocation,
        ParameterChangeSpec paramSpec)
    {
        // Similar to MethodSignatureEditor — add/remove arguments
        var args = invocation.ArgumentList.Arguments.ToList();

        foreach(var addParam in paramSpec.AddParameters) {
            args.Add(SyntaxFactory.Argument(
                SyntaxFactory.ParseExpression(addParam.DefaultValue ?? "default")
            ));
        }

        foreach(var removeParam in paramSpec.RemoveParameters) {
            var index = args.FindIndex(a => a.NameColon?.Name.Identifier.Text == removeParam);
            if(index >= 0)
                args.RemoveAt(index);
        }

        return invocation.WithArgumentList(
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args))
        );
    }
}
```

**Agent Workflow:**
Agent calls `roslyn_change_signature` on delegate → tool returns subscriber methods in diagnostics → agent calls `roslyn_change_signature` on each subscriber → done.

**Why no non-breaking mode?**
Delegates can't be overloaded. Breaking mode with comprehensive diagnostics enables agent orchestration.

---

#### Implementation: `OperatorSignatureEditor`

**File:** `Tools/Refactoring/SignatureChange/Editors/OperatorSignatureEditor.cs`

```csharp
internal sealed class OperatorSignatureEditor : SignatureEditor
{
    protected override ValidationResult ValidateParameterChanges(
        IMethodSymbol method,
        ParameterChangeSpec paramSpec)
    {
        var warnings = new List<string>();

        var finalParamCount = method.Parameters.Length
            + paramSpec.AddParameters.Length
            - paramSpec.RemoveParameters.Length;

        if(finalParamCount < 1 || finalParamCount > 2) {
            return ValidationResult.Invalid("Operators must have 1 or 2 parameters");
        }

        // Warn about paired operators
        if(method.Name == "op_Equality") {
            warnings.Add("Changing == operator: ensure != operator is also updated for symmetry");
        }
        else if(method.Name == "op_Inequality") {
            warnings.Add("Changing != operator: ensure == operator is also updated for symmetry");
        }
        else if(method.Name == "op_LessThan" || method.Name == "op_GreaterThan") {
            warnings.Add("Changing comparison operator: ensure paired operators (<, >, <=, >=) are consistent");
        }

        return ValidationResult.Valid(warnings);
    }

    public override SignatureEdits GenerateNonBreakingEdits(
        IMethodSymbol method,
        ParameterChangeSpec paramSpec,
        IReadOnlyList<CallSiteInfo> callSites)
    {
        // Can't overload operators — fall back to breaking mode
        return GenerateBreakingEdits(method, paramSpec, callSites);
    }

    public override SignatureEdits GenerateBreakingEdits(
        IMethodSymbol method,
        ParameterChangeSpec paramSpec,
        IReadOnlyList<CallSiteInfo> callSites)
    {
        var edits = new List<DocumentEdit>();

        var existingDecl = (OperatorDeclarationSyntax)method.DeclaringSyntaxReferences.First().GetSyntax();
        var newOperator = existingDecl
            .WithParameterList(BuildNewParameterList(existingDecl.ParameterList, paramSpec))
            .WithLeadingTrivia(existingDecl.GetLeadingTrivia())
            .WithTrailingTrivia(existingDecl.GetTrailingTrivia());

        edits.Add(new DocumentEdit {
            DocumentId = method.ContainingDocument.Id,
            Changes = new[] {
                new NodeChange {
                    Operation = NodeOperation.Replace,
                    OriginalNode = existingDecl,
                    NewNode = newOperator
                }
            }
        });

        return new SignatureEdits {
            MethodsChanged = 1,
            CallSitesUpdated = 0,
            AffectedDocuments = new[] { method.ContainingDocument.Id },
            DocumentEdits = edits
        };
    }
}
```

**Responsibilities:**
- Validate operator constraints (1-2 parameters)
- Warn about paired operators
- No non-breaking mode

---

### 4. `SignatureChangeApplicator` (Atomic Application)

**File:** `Tools/Refactoring/SignatureChange/SignatureChangeApplicator.cs`

```csharp
internal static class SignatureChangeApplicator
{
    public static async Task<Solution> ApplyEditsAsync(
        Solution solution,
        SignatureEdits edits)
    {
        var newSolution = solution;

        // Group edits by document for atomic per-document application
        var editsByDocument = edits.DocumentEdits
            .GroupBy(e => e.DocumentId)
            .ToList();

        foreach(var group in editsByDocument) {
            var document = newSolution.GetDocument(group.Key);
            var root = await document.GetSyntaxRootAsync();

            // Track all nodes that will be modified
            var nodesToTrack = group.SelectMany(e => e.Changes.Select(c => c.OriginalNode));
            var trackedRoot = root.TrackNodes(nodesToTrack);

            // Apply all changes to this document in one pass
            foreach(var edit in group) {
                foreach(var change in edit.Changes) {
                    var currentNode = trackedRoot.GetCurrentNode(change.OriginalNode);

                    trackedRoot = change.Operation switch {
                        NodeOperation.Replace => 
                            trackedRoot.ReplaceNode(currentNode, change.NewNode),

                        NodeOperation.InsertBefore => 
                            trackedRoot.InsertNodesBefore(currentNode, new[] { change.NewNode }),

                        NodeOperation.Remove => 
                            trackedRoot.RemoveNode(currentNode, SyntaxRemoveOptions.KeepNoTrivia),

                        _ => trackedRoot
                    };
                }
            }

            // Update document with all changes applied atomically
            newSolution = newSolution.WithDocumentSyntaxRoot(group.Key, trackedRoot);
        }

        return newSolution;
    }
}
```

**Responsibilities:**
- Apply all edits atomically per document
- Use `TrackNodes` / `GetCurrentNode` for immutable tree navigation
- Prevent transient errors in IDE (all changes visible together)

**Key insight:** Roslyn's immutable syntax trees require tracking nodes across transformations. `TrackNodes()` marks nodes before mutation, `GetCurrentNode()` retrieves them after intermediate changes.

---

### 5. `CallSiteUpdater` (Find & Update Invocations)

**File:** `Tools/Refactoring/SignatureChange/CallSiteUpdater.cs`

```csharp
internal static class CallSiteUpdater
{
    public static async Task<IReadOnlyList<CallSiteInfo>> FindCallSitesAsync(
        IMethodSymbol method,
        Solution solution)
    {
        var callers = await SymbolFinder.FindCallersAsync(method, solution);

        return callers
            .SelectMany(c => c.Locations)
            .Select(loc => new CallSiteInfo {
                Location = loc,
                DocumentId = solution.GetDocument(loc.SourceTree).Id,
                InvocationSyntax = FindInvocationSyntax(loc)
            })
            .ToList();
    }

    public static InvocationExpressionSyntax UpdateInvocationArguments(
        InvocationExpressionSyntax invocation,
        ParameterChangeSpec paramSpec)
    {
        var args = invocation.ArgumentList;

        // Add default arguments for new parameters
        foreach(var param in paramSpec.AddParameters) {
            var defaultArg = SyntaxFactory.Argument(
                SyntaxFactory.ParseExpression(param.DefaultValue)
            );
            args = args.AddArguments(defaultArg);
        }

        // Remove arguments for removed parameters
        foreach(var paramName in paramSpec.RemoveParameters) {
            var index = FindParameterIndex(args, paramName);
            if(index >= 0) {
                args = args.RemoveNode(args.Arguments[index], SyntaxRemoveOptions.KeepNoTrivia);
            }
        }

        return invocation.WithArgumentList(args);
    }

    private static SyntaxNode FindInvocationSyntax(Location location)
    {
        var root = location.SourceTree.GetRoot();
        var node = root.FindNode(location.SourceSpan);

        // Walk up to find InvocationExpressionSyntax
        while(node != null && node is not InvocationExpressionSyntax) {
            node = node.Parent;
        }

        return node;
    }
}
```

**Responsibilities:**
- Find all invocations via `SymbolFinder.FindCallersAsync`
- Update argument lists with new/removed parameters
- Handle named arguments (preserve or warn)

---

### File Organization

```
Tools/
  Refactoring/
    ChangeSignatureTool.cs                    (thin MCP tool)
    ApplySignatureChangeTool.cs               (apply preview)
    RemoveDeprecatedOverloadTool.cs           (cleanup)

    SignatureChange/
      SignatureChangeOrchestrator.cs          (validation + workflow)
      SignatureChangeApplicator.cs            (atomic application)
      CallSiteUpdater.cs                      (find & update call sites)

      Editors/
        SignatureEditor.cs                    (abstract base class)
        MethodSignatureEditor.cs              (regular methods)
        ExternMethodEditor.cs                 (P/Invoke)
        DelegateSignatureEditor.cs            (delegate declarations)
        OperatorSignatureEditor.cs            (operator overloads)
        ConversionSignatureEditor.cs          (implicit/explicit)

      Models/
        SignatureChangeResult.cs              (orchestrator result)
        SignatureEdits.cs                     (edit collection)
        ParameterChangeSpec.cs                (add/remove spec)
        CallSiteInfo.cs                       (call site metadata)
        ValidationResult.cs                   (validation outcome)
        DocumentEdit.cs                       (document-level edits)
        NodeChange.cs                         (node-level operation)
```

---

### Operation Order: Avoiding Transient Errors

**Problem:** Naive ordering causes IDE to flash errors between steps.

**Bad order (causes transient errors):**
1. Change method signature → **all call sites break**
2. Add deprecated overload → errors disappear

**Good order (zero transient errors):**
1. Add new method with new signature → no errors (old method still exists)
2. Convert old method to deprecated forwarder → still no errors (just warnings)

**Implementation:**
```csharp
// Non-breaking mode: atomic insertion + replacement
var newRoot = root
    .InsertNodesBefore(existingMethod, new[] { newMethodWithNewSignature })  // Step 1
    .ReplaceNode(
        root.GetCurrentNode(existingMethod),                                  // Step 2
        deprecatedForwardingStub
    );

// Apply both changes in ONE document update
document = document.WithSyntaxRoot(newRoot);
```

**Result:** IDE sees before-state → after-state with no intermediate broken state. Error List never flashes red.

---

### Trivia Preservation

**Challenge:** Copying method bodies might lose formatting/comments.

**Solution:** Use `.With*` methods on existing nodes to preserve trivia:

```csharp
// GOOD: Full trivia preservation
var newMethod = existingMethod
    .WithParameterList(newParameterList)           // Change signature
    .WithAttributeLists(default)                   // Clear old attributes
    .WithLeadingTrivia(existingMethod.GetLeadingTrivia())   // Preserve XML docs
    .WithTrailingTrivia(existingMethod.GetTrailingTrivia()); // Preserve comments

// Body is copied as-is with all internal formatting intact
```

**Key insight:** Roslyn's `.With*` methods preserve all trivia that isn't explicitly replaced.

---

### Edge Case Handling Strategy

| Edge Case | Phase 1 (MVP) | Phase 2 (Future) |
|-----------|---------------|------------------|
| **Regular methods** | ✅ Full support | — |
| **Extern methods** | ✅ Signature change only (no stub) | — |
| **Delegates** | ✅ Breaking mode only (detect subscribers) | Consider adapter patterns |
| **Virtual methods** | ✅ With `force: true` (warn about hierarchy) | Auto-update hierarchy |
| **Override methods** | ❌ Error: suggest base class | Auto-detect base |
| **Operators** | ✅ With `force: true` (warn about pairs) | Auto-check pairs |
| **Conversions** | ✅ With `force: true` (big warning) | — |
| **Async methods** | ✅ Forward `Task<T>` directly | — |
| **Generic methods** | ✅ If only value params change | Add/remove type params |
| **Partial methods** | ❌ Error: deferred | Atomic update both |
| **Constructors** | ❌ Error: deferred | `: this(...)` forwarding |
| **Indexers** | ❌ Error: deferred | `this[...]` support |
| **Extension methods** | ❌ Error: can't change `this` | — |
| **Interface implementations** | ❌ Error: update interface first | Auto-update interface |

**Philosophy:** Phase 1 handles common cases. Phase 2 adds advanced scenarios. Always show preview and let user decide.

---

## Open Questions

1. **Should we support method overload disambiguation?**
   - If multiple overloads exist, how does agent specify which one?
   - Option: require full signature match via parameter types?
   - **Architecture note:** `SignatureChangeOrchestrator.FindMethodSymbol()` should handle this

2. **How to handle partial classes?**
   - Method declared in one file, call sites in others
   - Should work naturally via Roslyn's symbol resolution
   - **Phase 2:** Both partial declarations must change atomically

3. **What about generic methods?**
   - Adding type parameters, constraints
   - **Phase 1:** Support if only value parameters change
   - **Phase 2:** Support adding/removing type parameters

4. **Approval scope for batch changes?**
   - If changing 10 methods at once, single approval or per-method?
   - Lean toward single approval for simplicity
   - **Architecture note:** `ApprovalStore` can handle batch tokens

5. **Integration with existing ApprovalStore?**
   - Reuse for session-level "auto-approve signature changes" mode?
   - **Yes** — consistent UX with rename
   - Store `SignatureChangeResult` with token, retrieve for apply

6. **What if cleaned [Obsolete] message is empty?**
   - If original was `[Obsolete("RoslynMcp.ChangeSignature: Migration ID: abc123")]`
   - After cleaning: `[Obsolete("")]` — should we remove attribute entirely?
   - **Decision:** Yes, remove attribute if no meaningful message remains AND we can confirm it's our marker (starts with `RoslynMcp.ChangeSignature:`). Otherwise preserve it.

7. **What if method already has [Obsolete]?**
   - Appending to existing message could work: `[Obsolete("Legacy API. RoslynMcp.ChangeSignature: Use Foo(int, string) instead. Migration ID: abc123")]`
   - Or fail with error: "Method already obsolete — manual intervention required"
   - Or use XML doc comment marker (less intrusive): `/// <roslynmcp-migration id="abc123" />`
   - **Lean toward:** Append to existing message for Phase 1; explore comment markers if this becomes problematic

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
| 2025-01-XX | **Abstract class over interface** | No external implementations; enables shared helpers + `virtual` validation; avoids premature abstraction |
| 2025-01-XX | Atomic application per document | Avoid transient IDE errors; all changes visible together |
| 2025-01-XX | `force` parameter for edge cases | Allow virtual/operator/conversion changes with explicit opt-in; show warnings, let user decide |
| 2025-01-XX | Extern methods: allow signature changes | P/Invoke refactoring is legit (e.g., `int` → `MyEnum`); no stub, just signature + call sites |
| 2025-01-XX | Operators: allow with `force` + warning | Type changes legal (e.g., `Foo == Foo` → `Foo == Bar`); warn about paired operators |
| 2025-01-XX | Conversions: allow with `force` + big scary warning | Breaks cast semantics but sometimes needed; preview shows consequences |
| 2025-01-XX | **Delegates: breaking mode only** | Can't overload delegates; dual-delegate pattern too complex; provide rich diagnostics for agent orchestration |
| 2025-01-XX | Defer constructors/indexers/partials to Phase 2 | Different semantics warrant dedicated design; Phase 1 covers 80% of use cases |

---

**Next steps:** Revisit before v0.3.0 release to confirm priority and finalize API design.
