import { joinSession } from "@github/copilot-sdk/extension";

// The repo root. cd to this is always redundant — CWD is already correct.
const REPO_ROOT = "J:\\Projects\\RoslynMcp";

// Matches cd / Set-Location / Push-Location with optional flags, capturing the target path.
// Covers: cd foo, cd "foo", Set-Location -Path foo, pushd foo, etc.
const CD_PATTERN = /^\s*(?:cd|Set-Location|sl|Push-Location|pushd)\b/i;

// Normalize a path string for comparison: trim quotes, trailing slashes, lowercase.
function normalize(p) {

    return p.replace(/^["']|["']$/g, "").replace(/[/\\]+$/, "").toLowerCase().trim();
}

// Extract the target directory from a cd-style command.
// Returns null if no path argument could be parsed (bare `cd` with no arg).
function extractTarget(command) {

    // Strip the command verb, then grab the first non-flag token.
    const rest = command.replace(/^\s*(?:cd|Set-Location|sl|Push-Location|pushd)\s*/i, "");
    const match = rest.match(/(?:-(?:Path|LiteralPath|PSProvider|StackName)\s+)?([^\s-][^\s]*)/i);

    return match ? normalize(match[1]) : null;
}

await joinSession({
    hooks: {
        onPreToolUse: async (input) => {

            if(input.toolName !== "powershell")
                return;

            const command = String(input.toolArgs?.command ?? "");

            if(!CD_PATTERN.test(command))
                return;

            const target = extractTarget(command);

            // Block if the target resolves to the repo root (or no target = bare `cd`).
            if(target === null || normalize(target) === normalize(REPO_ROOT)) {
                return {
                    permissionDecision: "deny",
                    permissionDecisionReason:
                        `Redundant \`cd\` blocked. CWD is already ${REPO_ROOT}. ` +
                        `Per AGENTS.md: "Never use \`cd\` in PowerShell commands when the CWD is already correct — ` +
                        `it causes unnecessary permission prompts." Run your command directly without changing directory.`,
                };
            }

            // cd to a subdirectory or elsewhere: warn but allow.
            // Agents should chain with &&, not cd then run separately.
            return {
                additionalContext:
                    `Note: \`cd\` detected. Per AGENTS.md, prefer chaining commands with \`&&\` ` +
                    `instead of changing directory. If this \`cd\` is intentional, proceed — but ` +
                    `consider whether a path argument to the actual command would work instead.`,
            };
        },
    },
    tools: [],
});
