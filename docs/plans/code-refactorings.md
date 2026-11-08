# Code Refactorings — Planning Doc (#389)

**Status:** Draft v2, awaiting approval. Branch `feat/issue-389-refactoring-tools` off `dev`, with the
Roslyn 5.9.0 upgrade commits cherry-picked from the earlier `feat/issue-389-code-refactorings` branch.

Decisions taken so far: deny-list rather than allow-list (agreed); **one PR for the whole issue**, no split;
a branch of this session's own.

---

## Goal

Run Roslyn code actions headlessly through the server's two-step workflow: a discovery tool that says which
refactorings apply to a line or span, a preview that turns one of them into a diff and a token, and an apply.
Providers come from every place a Roslyn host would take them: the built-in Features assemblies, a project's
analyzer references (NuGet analyzer packages and `<Analyzer Include>` items), and the server's own global and
project configuration. Code fixes move onto the same host, so `roslyn_preview_code_fix` offers the built-in
fixers too. Everything has an opt-out, and a capability that one workspace mode cannot provide is a structured
error, never a crash.

---

## What the research settled

Sources: dotnet/roslyn `main` (`MefHostServices.cs`, `MefHostServicesHelpers.cs`, `CodeRefactoringContext.cs`,
`CodeRefactoringService.cs`, `CodeActionWithOptions.cs`, `AbstractMoveTypeService*.cs`,
`AbstractExtractMethodCodeRefactoringProvider.cs`, `PredefinedCodeRefactoringProviderNames.cs`, the LSP
`CodeActionHelpers.cs`), the `CodeAction` API docs on learn.microsoft.com, OmniSharp's
`CompositionHostBuilder.cs` / `CodeActionHelper.cs`, the NuGet flat-container index and 5.9.0 nuspecs,
SharpLsp issue #201, and this repo's apply path.

1. **Composition is free.** `MefHostServices.DefaultAssemblies` loads six assemblies *by name*, swallowing
   the ones that are not present: `Microsoft.CodeAnalysis.Workspaces`, `.CSharp.Workspaces`,
   `.VisualBasic.Workspaces`, `.Features`, `.CSharp.Features`, `.VisualBasic.Features`.
   `MSBuildWorkspace.Create()` and `new AdhocWorkspace()` both use `MefHostServices.DefaultHost`, which is
   `Create(DefaultAssemblies)`. Features core is referenced today, so its exports are already composed on every
   load; referencing `CSharp.Features` adds the C# providers to the same composition. The cost is one-off at the
   first workspace load and must be measured.
2. **The public surface is enough; no reflection into Roslyn internals.** `MefHostServices.Create(CompositionContext)`
   is public, so the server can build the `CompositionHost` itself from `MefHostServices.DefaultAssemblies`, hand
   it to `MSBuildWorkspace.Create(properties, hostServices)` and `new AdhocWorkspace(hostServices)`, and keep the
   container to enumerate `Lazy<CodeRefactoringProvider, TMetadata>` and `Lazy<CodeFixProvider, TMetadata>`
   exports. `ExportCodeRefactoringProviderAttribute` / `ExportCodeFixProviderAttribute` are public metadata
   attributes (`Name`, `Languages`, `DocumentKinds`, `DocumentExtensions`), so a metadata view of the server's own
   is all that is needed. `CodeRefactoringContext(Document, TextSpan, Action<CodeAction>, CancellationToken)` and
   `CodeFixContext` are public; `ComputeRefactoringsAsync` / `RegisterCodeFixesAsync` are the public entry
   points. Roslyn's own services do nothing more than this per provider, wrapped in an `IExtensionManager`.
3. **Providers in NuGet packages are analyzer references.** Roslyn's `CodeRefactoringService` and
   `CodeFixService` take project-level providers from `AnalyzerFileReference` assemblies by reflecting
   `[ExportCodeRefactoringProvider]` / `[ExportCodeFixProvider]` types (`ProjectCodeRefactoringProvider`).
   A `<PackageReference>` with analyzer assets and an `<Analyzer Include="x.dll">` item both arrive as
   `Project.AnalyzerReferences`. The server already shadow-copies and loads those for
   `roslyn_get_diagnostics(includeAnalyzers)` (`ShadowCopyAnalyzerLoader`, `AnalyzerLoading`).
4. **Headless pitfalls have a known shape.** Roslyn's LSP host filters exactly two things: a
   `CodeActionWithOptions` (an action that needs a dialog: Generate overrides…, Extract interface…, Pull members
   up…, Move to namespace…, Change signature…) whose option service is absent, and an action tagged
   `RequiresNonDocumentChange`. SharpLsp #201 is what happens without that filter: the action lists, then
   throws `Service of type IPickMembersService is required` on apply. OmniSharp additionally denies
   `ChangeSignatureCodeRefactoringProvider` and `ExtractInterfaceCodeRefactoringProvider` by type name.
5. **Move type adds a document with a file path.** `AbstractMoveTypeService.MoveTypeEditor` calls
   `Solution.AddDocument(id, FileName, root, document.Folders, filePath: <source dir>/<FileName>)`, copies the
   file banner, and makes the containing type chain `partial`. The same provider also offers *Rename file to
   match type* (a document rename) and *Rename type to match file* (a text change).
6. **The apply path already handles adds and removes.** `PreviewFileStateBuilder` records an added document as
   `ExpectedFileState.Absent` (refusing when the file already exists), `SolutionDiff` prints it, and
   `PhysicalSolutionApplyPlan` turns it into a `Create`. The only thing that forbids adds today is
   `PreviewCodeFixTool.ValidateSupportedChangesAsync` (Phase 1), which is code-fix specific.
7. **Options.** Refactorings and fixers read code-style options from the document's analyzer config
   (`.editorconfig`, `.globalconfig`) with `IFallbackAnalyzerConfigOptionsProvider` supplying host defaults;
   the default export returns `StructuredAnalyzerConfigOptions.Empty`, so a headless host gets Roslyn's
   defaults where the config is silent. **`.editorconfig` and `.globalconfig` cannot name a provider assembly**:
   their role here is options and diagnostic severities, which flow in unchanged. (This is my reading of
   "load providers from .editorconfigs / global configs" — flagged below.)
8. **Extract method** needs a non-empty selection, returns no action rather than throwing when the selection
   cannot be extracted, and offers *Extract method* and *Extract local function*.
9. **5.9.0 is the latest stable** (published 2026-08-17; 5.6.0 before it). `CSharp.Features` 5.9.0 adds no
   dependency Features core did not already bring. The lockfile diff confirms only that one package is new.
   Restore and compiler diagnostics are clean on this machine.
10. **Nested actions.** `CodeAction.NestedActions` and `IsInlinable` are public. Group actions have no
    operations of their own; only leaves are applicable.

---

## Design

### `CodeActionHost` (replaces `CodeFixHost`; `Tools/CodeActions/`; DI singleton)

One place that knows every provider the server may run, refactoring and code fix alike.

- **Composition.** Builds one `CompositionHost` per process from `MefHostServices.DefaultAssemblies`, wraps it
  in `MefHostServices.Create(container)`, and exposes `HostServices` for `WorkspaceManager` (both
  `MSBuildWorkspace.Create` call sites and `new AdhocWorkspace`). Lazy: the first workspace load pays it, as
  today. Logs `Composition — N assemblies, R refactoring / F fix providers, X ms`. One shared container rather
  than `DefaultHost` plus a second one: two would compose Features twice and give providers a different set of
  `[Shared]` singletons than the workspace services they run against; one is also the hook for the server's
  own exports later (a headless `IPickMembersService`, say).
- **Provider sources**, each a `ProviderSource` with a name, an origin and a load report:
  1. *Built-in*: the composition's exports (unless `builtIn: false`).
  2. *Project*: per project, reflected from `Project.AnalyzerReferences` — shadow-copied through
     `ShadowCopyAnalyzerLoader`, types carrying the export attribute with a parameterless constructor; a MEF
     importing constructor with parameters is skipped and reported. Cached per reference path + mtime, as
     `AnalyzerLoading.GetShadowedAnalyzers` is. Not available in an `AdhocWorkspace` (no project file): the
     tools say so in `provider_sources`, not as a failure.
  3. *Configured*: entries from the server's global config and the project's `.madq_roslynmcp.json`
     (see *Configuration*). An entry names an assembly path (absolute, or relative to the declaring file) or a
     NuGet package id + version, resolved to `<nuget cache>/<id>/<version>/analyzers/dotnet/cs/*.dll`
     (falling back to `analyzers/dotnet`, then `lib/<tfm>` — the package layouts Roslyn's own loader accepts).
     Each configured source loads into its own isolated, collectible `AssemblyLoadContext` that resolves
     `Microsoft.CodeAnalysis.*` and framework assemblies from the server's default context (so a provider
     compiled against Roslyn 4.x binds to the server's 5.9.0), after a shadow copy so the original is never
     locked. A source that fails to load is reported with its reason and does not disable the rest.
- **Selection for a document**: providers whose metadata lists the document's language (and document kind /
  extension when set), minus the deny-list and the configured `deny` names.
- **`GetRefactoringsAsync(Document, TextSpan, ct)`**: runs each provider with its own `CodeRefactoringContext`
  in a try/catch per provider. A provider that throws becomes a `provider_failures` entry; the others still
  run. Returns the flattened leaf actions (`Parent › Child` titles), each with provider type name, source,
  equivalence key and title, sorted by (provider, title) so an index is stable for one input.
- **`GetFixesAsync(Document, Diagnostic, ct)`**: the current `CodeFixHost` logic over the same provider sets,
  filtered by `FixableDiagnosticIds`. The two bundled `RoslynMcp.Analyzers` fixers stay (they are a
  *configured* source of kind "bundled", so the reporting is uniform).
- **Filtering** (both kinds): drop any `CodeActionWithOptions` and any action tagged
  `RequiresNonDocumentChange`. Built-in deny-list by provider name: `ChangeSignature` (the server has its own),
  `RenameTracking` (editor-only), `ExtractInterface`, `ExtractClass`, `PullMemberUp`, `MoveStaticMembers`,
  `MoveToNamespace` (dialog-driven; the action filter would catch them too, the list keeps them from running).
- **Workspace compatibility rule.** Whatever one mode cannot do is a structured error naming the mode:
  `incompatible_workspace` when a tool is asked for something the current workspace kind cannot provide
  (project providers in Adhoc; a document the project does not track), `providers_disabled` when the opt-out is
  on. Nothing is silently skipped.

### Configuration and opt-out

- **Project**: `.madq_roslynmcp.json` gains a `codeActions` section:
  `{ "enabled": true, "builtIn": true, "projectProviders": true, "providers": [ { "path": "tools/Refactorings.dll" }, { "package": "Roslynator.Analyzers", "version": "4.13.1" } ], "deny": [ "SomeProvider" ] }`.
- **Global (per user)**: `%LOCALAPPDATA%\RoslynMcp\config.json` (new; same directory as logs and backups),
  same `codeActions` shape, for providers wanted in every workspace.
- **Env var / CLI**: `ROSLYNMCP_CODE_ACTIONS=off` / `--code-actions off` disables the refactoring tools and the
  non-bundled fixers; `ROSLYNMCP_TRUST_PROJECT_PROVIDERS=1` / `--trust-project-providers` (also
  `trustProjectProviders: true` in the global file) lets a committed `.madq_roslynmcp.json` load assemblies
  **by path**.
- **Merge rules**: `providers` and `deny` are the *union* of global and project (unlike `ignore`, where the
  winning source replaces: a user's tools plus a team's is the point). Boolean switches follow the usual
  precedence, CLI > env > project file > global file > default; `enabled: false` from any source that wins
  turns the feature off.
- **Trust**: a path entry in the committed project file runs third-party code from a repository checkout, so
  it needs the per-machine opt-in above and is otherwise reported as `skipped: untrusted`. A package entry
  resolves only from the user's own NuGet cache, so it is honoured without the gate. The global file is the
  user's own. (Flagged as a decision below.)
- **Reporting**: `roslyn_info` lists every provider source with origin, provider counts, load time and
  failures; `roslyn_list_refactorings` repeats the sources consulted for that document in `provider_sources`.

### Tools (`Tools/Refactoring/`, alongside the signature-change pair)

**`roslyn_list_refactorings`** (ReadOnly) — `(filePath, line, column, endLine?, endColumn?, projectPath?)`.
Discovery: the refactorings that apply to a caret or a selection. Returns
`actions: [{index, title, provider, source, equivalence_key}]`, `provider_sources`, `provider_failures`, and a
hint (`Extract method needs a selection; Move type needs the caret on the type name`). An empty list is a
normal result with `actions: []`, not a failure — "nothing applies here" is an answer. A separate tool rather
than a mode of the preview, because discovery has no token semantics and an agent asks it far more often than
it previews.

**`roslyn_preview_refactoring`** (ReadOnly) — same location arguments plus `actionIndex` (required).
Recomputes the list for the same span, picks the action, and echoes its title, provider and source so a
mismatch is visible.

1. `GetOperationsAsync`; accept exactly one `ApplyChangesOperation`, ignoring navigation operations
   (`OpenDocumentOperation`, `*NavigationOperation` by type name); anything else is
   `unsupported operation shape`.
2. `CodeActionChangeValidator` (own file; shared with code fixes, with the Phase 1 restriction lifted): no
   project add/remove, no reference or option changes, every changed or added document has a `FilePath`, no
   document rename/move (old path ≠ new path ⇒ `unsupported: file rename`, which is what *Rename file to match
   type* hits), every added path inside the `SecurityBoundary` and absent on disk, no generated or linked-file
   edits. Removed documents are allowed through the existing Delete plan.
3. `SolutionDiff` → `PreviewFileStateBuilder` → `FindTargetConflictAsync` →
   `approvals.Register(…, ApprovalWorkflow.Refactoring, …)` → diff + token, as the code-fix preview does.

**`roslyn_apply_refactoring`** (Destructive) — same shape as `roslyn_apply_code_fix`: token + approval
(`y`/`n`/`session`), `PhysicalSolutionApplier`, `PhysicalApplyResultMapper`, `workspace.InvalidateFile` per
written path. A created `.cs` flags a reload (new compilation input), as `roslyn_write_file` already does.
Result adds `created_files` and `deleted_files`. When the project is not SDK-style (explicit
`<Compile Include>` items), a `_caution` says the new file is not compiled until it is added to the project —
Visual Studio adds it through `TryApplyChanges`, the physical apply does not.

**`roslyn_preview_code_fix` / `roslyn_apply_code_fix`** — switch to `CodeActionHost.GetFixesAsync` and the
shared validator. The built-in fixers for compiler diagnostics (remove unnecessary usings, add missing using
for an in-solution type, make method async, …) become available. Fixers for IDE0xxx diagnostics need the IDE
analyzers to have produced those diagnostics, which `roslyn_get_diagnostics` does not run today — noted as a
follow-up, not in this PR.

### Shared pieces touched

- `ApprovalWorkflow` gains `Refactoring`.
- `WorkspaceManager.Instance.Loading.cs`: the three workspace constructions take `CodeActionHost.HostServices`.
- `GetChangedSolutionAsync` moves out of `PreviewCodeFixTool` into the shared validator with the relaxed
  operation rule.
- `ProjectConfig` learns the `codeActions` section; a new `UserConfig` reads the global file; `Program.cs`
  learns the two switches; `SetupProjectCommand` offers the section.

---

## Steps

0. **Upgrade first, separately** (already two commits on the branch). Full harness on Windows and under WSL
   before any feature code — the upgrade touches every tool. Any regression is fixed in its own commit, so the
   upgrade and the feature stay separable in the history. The repo owner updates the installed server first.
1. `CodeActionHost` with the built-in source; workspace creation through it; composition cost logged; the
   code-fix tools moved onto it with behaviour unchanged (bundled providers only, Phase 1 validator still on);
   harness green.
2. `roslyn_list_refactorings`.
3. `CodeActionChangeValidator` (adds allowed), `roslyn_preview_refactoring`, `roslyn_apply_refactoring`,
   `ApprovalWorkflow.Refactoring`, `created_files`, non-SDK caution. Code fixes switch to the shared validator.
4. Project source: providers from analyzer references; `incompatible_workspace` for Adhoc.
5. Configured sources: `ProjectConfig` / `UserConfig` sections, path and package entries, shadow copy, isolated
   load contexts, trust gate, env var and CLI switches, `roslyn_info` reporting.
6. Harness group *Refactoring Tools* (`RefactoringTests`, fixtures per #274):
   - list at a type name includes an action from `MoveTypeCodeRefactoringProvider`; preview shows a new file in
     the diff; apply creates it, the source file no longer holds the type, diagnostics clean after the reload;
     `created_files` names it.
   - Extract method on a selected statement, Introduce local on an expression, Invert if: preview + apply,
     diagnostics clean.
   - a `CodeActionWithOptions` never lists (class with overridable members: nothing from
     `GenerateOverridesCodeRefactoringProvider`); a throwing provider lands in `provider_failures` without
     failing the call (DEBUG-only test provider behind an env var, like `CodeFixTestProvider`).
   - invalid `actionIndex`; empty location ⇒ `actions: []`; approval `n` leaves the disk untouched; a code-fix
     token is refused by `roslyn_apply_refactoring`; a file edited between preview and apply ⇒ stale; an added
     target that already exists ⇒ preview refuses; *Rename file to match type* ⇒ `unsupported: file rename`.
   - the same list/preview/apply on an `AdhocWorkspace`; project providers reported as unavailable there.
   - project source: a fixture project with `<Analyzer Include>` pointing at a test provider assembly built
     by the harness (the DEBUG test provider compiled into a small DLL) lists its action; a package entry
     resolves from a fake NuGet cache directory (`NUGET_PACKAGES` pointed at a fixture); a path entry in the
     project file is `skipped: untrusted` until the env var is set; `ROSLYNMCP_CODE_ACTIONS=off` ⇒
     `providers_disabled`; a corrupt DLL ⇒ reported, others still load.
   - code fixes: a compiler diagnostic (CS8019 unnecessary using) now previews a built-in fix; the bundled
     fixers still work; the existing *Code Fix Tools* group stays green.
7. Docs: AGENTS.md (architecture rows for `CodeActionHost` and the three tools; tool count 46 → 49;
   *Tools/Refactoring* now five tools; *Tools/CodeFix* Phase 1 wording), README tool list, CHANGELOG,
   `docs/reference/WORKSPACE_MODES.md` (what Adhoc cannot do here), this plan's status line.
8. Two critic passes, commit, push, PR `Fixes #389`.

---

## Flagged for the owner

- **`.editorconfig` / `.globalconfig` as provider sources.** Neither can name an assembly; they configure
  options and severities, which already reach the providers. Reading "load providers from them" as "honour
  them" — say so if something else was meant.
- **Plain `<Reference Include>` assemblies.** Not scanned. A refactoring provider ships as an analyzer asset,
  not a compile reference; reflecting every referenced DLL would load the world for nothing. `<Analyzer Include>`
  items are covered.
- **Trust gate for path entries in the committed project file.** Recommended as described (per-machine opt-in);
  drop it if the team-shared case matters more than the checkout-runs-code risk.
- **Code fixes on the same host** read as part of "all of it". Cheap to leave out if not.

## Risks and open questions

- **Composition cost.** `CSharp.Features` is the largest Roslyn assembly. Measured in step 1; if the first load
  gets noticeably slower, the container can be built on a background thread at startup while MSBuild is still
  being located.
- **5.9.0 behaviour changes.** Unknown until the full harness runs (step 0).
- **Third-party providers across Roslyn versions.** A provider built against Roslyn 4.x binds upward to 5.9.0;
  one that uses an API removed since then fails at call time and is reported per provider, not per server.
- **Index stability.** `actionIndex` is only meaningful for the same input; the preview echoes the chosen
  action's title and provider so a mismatch is visible.
- Aside noticed on the way, to re-check once the installed server is updated: `roslyn_get_type_members`
  reports `not found` for metadata types (`CodeAction`, `MefHostServices`), and `roslyn_restore_packages`
  rejected the `.slnx` path with `project_not_found`.
