import { joinSession } from "@github/copilot-sdk/extension";

// Trigger phrases that kick off a full doc↔code sweep.
const TRIGGERS = [/\/doc-sweep\b/i, /\bdoc[\s\-–—]*sweep\b/i, /\bdoc[\s\-–—]*code[\s\-–—]*sync\b/i];

const SWEEP_PROMPT = `
You have been triggered to perform a full doc↔code sweep of the RoslynMcp repository.

## What this sweep is

A comprehensive cross-reference of every .md file against every .cs file in the solution.
Fixes go ONLY into .md files — source code is the authoritative truth.
The ScratchPad*.md files (docs/ScratchPad.md, docs/ScratchPad2.md) may be read as reference context but are NOT authoritative.

## All .md files to read and audit

Root level:
- README.md
- AGENTS.md
- CHANGELOG.md
- INSTALLATION.md
- CONTRIBUTING.md
- ROADMAP.md
- SECURITY.md
- CODE_OF_CONDUCT.md

docs/:
- docs/guides/TROUBLESHOOTING.md
- docs/reference/WORKSPACE_MODES.md
- docs/reference/tools-assessment.md
- docs/process/DOC_REVIEW_CHECKLIST.md
- docs/process/RELEASE_CHECKLIST.md
- docs/development/DISCOVERY_PATTERN.md
- docs/development/CODE_STYLE_ENFORCEMENT.md
- docs/troubleshooting/ISSUE_ONEDRIVE_CONTAMINATION.md
- docs/tools/roslyn_get_trivia.md
- docs/tools/roslyn_get_trivia_quickref.md
- docs/AGENT-INSTRUCTIONS.md
- docs/MSBUILD_API_ANALYSIS.md
- docs/battle-test-results.md
- docs/plans/apply-style-plan.md
- docs/plans/v040-implementation.md
- docs/plans/tool-suggestions.md
- docs/plans/style-preservation.md
- docs/plans/style-analysis-plan.md
- docs/plans/pagination-cache.md
- docs/plans/oop-dry-opportunities.md
- docs/plans/mvp-alpha-release.md
- docs/plans/multi-instance-architecture.md
- docs/plans/code-audit.md
- docs/plans/change-signature-tool.md
- docs/sessions/HANDOFF.md (if it exists)
- docs/ScratchPad.md (reference only, not authoritative)
- docs/ScratchPad2.md (reference only, not authoritative)

.github/:
- .github/copilot-instructions.md
- .github/PULL_REQUEST_TEMPLATE.md
- .github/ISSUE_TEMPLATE/feature_request.md
- .github/ISSUE_TEMPLATE/bug_report.md

src/:
- src/RoslynMcp.Analyzers/AnalyzerReleases.Shipped.md
- src/RoslynMcp.Analyzers/AnalyzerReleases.Unshipped.md

## All .cs files to read (source of truth)

src/RoslynMcp/ (core server):
- Program.cs, WorkspaceManager.cs, WorkspaceManager.Resolution.cs, WorkspaceManager.Instance.cs
- WorkspaceResolver.cs, WorkspaceMode.cs, MSBuildBootstrap.cs
- FileLogger.cs, PaginationCache.cs, ApprovalStore.cs, BackupStore.cs
- SolutionDiff.cs, SymbolFormatter.cs, SymbolVisitors.cs
- RoslynMcpJson.cs, Exceptions.cs, LogEntry.cs
- Tools/RoslynMcpTool.cs, Tools/RoslynMcpTool.ToolScope.cs, Tools/RoslynMcpTool.Discovery.cs
- Tools/ToolResults.cs, Tools/ErrorResult.cs, Tools/InfoTool.cs
- Tools/RespawnTool.cs, Tools/DebugAttachTool.cs
- Tools/Analysis/ (all files: DiagnosticsTool, FileOutlineTool, FindImplementationsTool, FindReferencesTool, GetLineCountTool, GetMemberBodyTool, GetSymbolDefinitionTool, GetSymbolDocumentationTool, GetSymbolsInScopeTool, GetTriviaTool, GetUsingsTool, ListTypesTool, ProjectInfoTool, ReadFileTool, SymbolInfoTool, TypeHierarchyTool, TypeMembersTool)
- Tools/Build/ (BuildTool, CleanSolutionTool, RestorePackagesTool)
- Tools/Editing/ (InsertLinesTool, LocalHistoryTool, ReplaceInCodeTool, ReplaceInFileTool, WriteFileTool)
- Tools/Rename/ (ApplyRenameTool, PreviewRenameTool)
- Tools/Refactoring/ (ApplySignatureChangeTool, ChangeSignatureTool, and SignatureChange/ subfolder)
- Tools/Search/ (ListFilesTool, SearchFilesTool, SemanticSearchTool)

src/RoslynMcp.Analyzers/:
- PreferNintOverIntPtrAnalyzer.cs, PreferNintOverIntPtrCodeFixProvider.cs, ToolScopeAnalyzer.cs

src/TestHarness/:
- TestHarnessProgram.cs

src/RoslynMcp.LogViewer/:
- ViewerHtml.cs, LogViewerProgram.cs, LogTailer.cs, LogEntry.cs

## What to check (from DOC_REVIEW_CHECKLIST.md)

- Tool count accurate everywhere (currently 35 tools: 33 public + 2 debug-only: roslyn_respawn, roslyn_debug_attach)
- Architecture tables list all tools and components correctly
- Component descriptions match actual implementation
- Version numbers, milestone assignments, feature status
- File paths (all should use src/ prefix)
- Planned/deferred features that have since shipped — remove the "planned" label
- Code examples that compile against current API shapes
- .mcp.json examples use published executable (not dotnet run)
- No broken relative links between docs

## Mandatory subagent constraints (include verbatim in every subagent you launch)

MANDATORY TOOL CONSTRAINTS — do NOT violate these:
- You MUST use roslyn_* MCP tools for ALL C# file operations.
- Do NOT use Bash find, grep, cat, sed, awk, xargs, or wc on .cs files.
- Do NOT use cd — the CWD is already correct.
- Do NOT use the Read tool for .cs files — use roslyn_read_file instead.
- Do NOT use the Grep tool for .cs files — use roslyn_search_files instead.
- Do NOT use the Glob tool — use roslyn_list_files instead.
- Build: roslyn_build_project (NEVER dotnet build in terminal)
- Diagnostics: roslyn_get_diagnostics
- Do NOT run Test-CodeStyle.ps1 — style passes are suspended. Violators get the dunce cap. 🎓
- Do NOT reformat, reorder, or restyle any code while fixing docs — you are a doc editor, not a formatter
- Do NOT commit without being explicitly asked

## Process

1. Use parallel background subagents — one per logical doc group
2. Each agent reads its assigned .md files AND reads the relevant .cs source
3. Each agent fixes drift in its .md files only
4. After all agents complete, run a final diagnostics check: roslyn_get_diagnostics (severity: errors)
5. Commit: \`docs: audit and fix documentation drift\`

Start now. Launch the subagent fleet.
`.trim();

let sweepFired = false;

const session = await joinSession({
    hooks: {
        onUserPromptSubmitted: async (input) => {
            if(sweepFired)
                return;

            const triggered = TRIGGERS.some((re) => re.test(input.prompt));
            if(!triggered)
                return;

            sweepFired = true;

            // Fire-and-forget: inject the full sweep briefing as a new message.
            // Delay so this message queues after the current turn completes.
            setTimeout(() => {
                sweepFired = false; // reset for next use in this session
                session.send({ prompt: SWEEP_PROMPT });
            }, 100);
        },
    },
    tools: [],
});
