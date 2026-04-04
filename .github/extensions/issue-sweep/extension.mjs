import { joinSession } from "@github/copilot-sdk/extension";

const TRIGGERS = [/\/issue-sweep\b/i, /\bissue[\s\-–—]*sweep\b/i];

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
   c. To apply an approved edit: write the corrected body to a temp file, then:
        gh issue edit N --body-file path/to/temp.md --repo MadQ/RoslynMcp

## Step 2: Recently closed issues (last ~90 days)

1. List recently closed issues:
     github-mcp-server-list_issues(owner: "MadQ", repo: "RoslynMcp", state: "CLOSED")
   Filter to those closed within the last 90 days (check the closedAt date).
2. Same cross-reference as Step 1.
3. Report ALL inaccuracies to me — closed issues are historical record.
   Do NOT edit any closed issue without explicit per-issue approval.

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

let fired = false;

const session = await joinSession({
    hooks: {
        onUserPromptSubmitted: async (input) => {
            if(fired)
                return;

            if(!TRIGGERS.some((re) => re.test(input.prompt)))
                return;

            fired = true;
            setTimeout(() => {
                fired = false;
                session.send({ prompt: ISSUE_SWEEP_PROMPT });
            }, 100);
        },
    },
    tools: [],
});
