import { joinSession } from "@github/copilot-sdk/extension";

const DOC_SWEEP_TRIGGERS   = [/\/doc-sweep\b/i,   /\bdoc[\s\-–—]*sweep\b/i,   /\bdoc[\s\-–—]*code[\s\-–—]*sync\b/i];
const ISSUE_SWEEP_TRIGGERS = [/\/issue-sweep\b/i, /\bissue[\s\-–—]*sweep\b/i];

// ---------------------------------------------------------------------------
// Issue sweep prompt (standalone and appended to doc sweep)
// ---------------------------------------------------------------------------

const ISSUE_SWEEP_PROMPT = `
You have been triggered to perform an issues sweep of the RoslynMcp GitHub repository (MadQ/RoslynMcp).

## What this sweep is

Cross-reference every GitHub issue body against the current codebase — tool names, file paths,
API shapes, class/method names, version numbers, shipped vs. planned status.
Source of truth: the .cs and .md files in this repo. Use roslyn_* tools to read them.

## What counts as an inaccuracy

- Wrong file path (file was moved, renamed, or deleted)
- Wrong tool name (tool was renamed, replaced, or removed)
- Wrong method / class / parameter name vs. current codebase
- Feature described as "planned" or "not yet implemented" that has since shipped
- Wrong version number for when something was or will be released
- Reproduction steps that reference files, APIs, or flags that no longer exist

## What does NOT count

- Intentional design discussion that predates the final implementation (that is history, leave it)
- Subjective opinions or preferences
- Features that are still genuinely future/planned
- Imprecise wording that is not factually wrong

## Step 1: Open issues

1. List all open issues:
     github-mcp-server-list_issues(owner: "MadQ", repo: "RoslynMcp", state: "OPEN")
2. For each issue, read its full body:
     github-mcp-server-issue_read(method: "get", owner: "MadQ", repo: "RoslynMcp", issue_number: N)
3. Cross-reference body against the codebase using roslyn_search_files / roslyn_get_member_body etc.
4. For each inaccuracy found:
   a. Tell me exactly what is wrong and what the correct value is
   b. WAIT for my explicit approval before editing
   c. To apply an approved edit: gh issue edit <number> --body "<corrected body>" --repo MadQ/RoslynMcp
      (write the corrected body to a temp file first: gh issue edit N --body-file path/to/temp.md)

## Step 2: Recently closed issues (last ~90 days)

1. List recently closed issues:
     github-mcp-server-list_issues(owner: "MadQ", repo: "RoslynMcp", state: "CLOSED")
   Filter in your head to those closed within the last 90 days (check the closedAt date).
2. Same cross-reference as Step 1.
3. Report ALL inaccuracies to me — closed issues are historical record.
   Do NOT edit any closed issue without explicit approval for each one individually.

## Mandatory tool constraints

MANDATORY TOOL CONSTRAINTS — do NOT violate these:
- You MUST use roslyn_* MCP tools for ALL C# file operations.
- Do NOT use Bash find, grep, cat, sed, awk, xargs, or wc on .cs files.
- Do NOT use cd — the CWD is already correct.
- Do NOT use the Read tool for .cs files — use roslyn_read_file instead.
- Do NOT use the Grep tool for .cs files — use roslyn_search_files instead.
- Do NOT use the Glob tool — use roslyn_list_files instead.
- Do NOT run Test-CodeStyle.ps1 — style passes are suspended. Violators get the dunce cap. 🎓
- Do NOT commit without being explicitly asked

Start now with Step 1.
`.trim();

// ---------------------------------------------------------------------------
// Doc sweep prompt
// ---------------------------------------------------------------------------

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

## Step 0: Resolve the authoritative tool count

Before auditing any docs, run this search to get the real tool count from the source:

  roslyn_search_files(pattern: '\[McpServerToolType\]', projectPath: "src/RoslynMcp/RoslynMcp.csproj")

Count the distinct matches. That is the authoritative total. Cross-reference:
- '[McpServerTool(... ReadOnly = false)]' → public tools
- 'roslyn_respawn' and 'roslyn_debug_attach' → the 2 debug-only tools

Use only this computed count when fixing doc files. Do NOT trust any hardcoded number, including this prompt.

## Step 0b: Verify the .md file list

This extension contains a hardcoded list of .md files. It may be stale. Before launching agents:

1. Run: roslyn_list_files(pattern: "**/*.md", projectPath: "src/RoslynMcp/RoslynMcp.csproj")
   Also run it on the repo root (use projectPath: ".") to catch root-level and docs/ .md files.
2. Compare the result against the list in this prompt.
3. Report to the user:
   - Any .md files found on disk that are NOT in the list above (newly added — sweep these too)
   - Any .md files in the list above that do NOT exist on disk (deleted — skip them)
4. Tell the user: "The doc-sweep extension file list may need updating — see above for the delta."
5. Proceed with the union of both lists (prompt list + discovered files) for the sweep itself.

## What to check (from DOC_REVIEW_CHECKLIST.md)

- Tool count accurate everywhere (use the count you computed in Step 0)
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
5. Once the doc fleet is done, also run the issues sweep — same rules as /issue-sweep:
   list all open issues, cross-reference against current codebase, ask before editing any.
   For recently closed issues (last 90 days): report inaccuracies but ask before touching anything.
6. Commit: \`docs: audit and fix documentation drift\`

Start now. Launch the subagent fleet.
`.trim();

// ---------------------------------------------------------------------------
// Session wiring — two independent triggers, each fire-once per session
// ---------------------------------------------------------------------------

let docSweepFired   = false;
let issueSweepFired = false;

const session = await joinSession({
    hooks: {
        onUserPromptSubmitted: async (input) => {

            if(!docSweepFired && DOC_SWEEP_TRIGGERS.some((re) => re.test(input.prompt))) {
                docSweepFired = true;
                setTimeout(() => {
                    docSweepFired = false;
                    session.send({ prompt: SWEEP_PROMPT });
                }, 100);
                return;
            }

            if(!issueSweepFired && ISSUE_SWEEP_TRIGGERS.some((re) => re.test(input.prompt))) {
                issueSweepFired = true;
                setTimeout(() => {
                    issueSweepFired = false;
                    session.send({ prompt: ISSUE_SWEEP_PROMPT });
                }, 100);
            }
        },
    },
    tools: [],
});
