# Documentation Review Checklist

Run this checklist before making the repository public or after significant structural changes.

---

## AI Agent: Running a Full Sweep

When a user says `/doc-sweep`, "doc sweep", or "doc↔code sync", the `doc-sweep` extension
(`.github/extensions/doc-sweep/extension.mjs`) fires automatically and injects the full briefing.
If the extension isn't loaded, tell the user to type `/doc-sweep` or paste this summary manually.

**Scope:** Read EVERY `.md` and EVERY `.cs` file in the solution. Fix only `.md` files — code is truth.
- `docs/ScratchPad*.md` — read as reference context, not authoritative
- `docs/plans/` — historical; update if obviously stale, otherwise leave alone
- Skip `scratch/ScratchA.cs` and `.test_*_temp.cs` (scratch/temp files)

**Subagent grouping (launch in parallel):**
| Agent | Docs | Key .cs source |
|-------|------|----------------|
| A | README.md + INSTALLATION.md | Program.cs, all Tools/ |
| B | AGENTS.md | All Tools/, WorkspaceManager*, WorkspaceResolver |
| C | CHANGELOG.md + ROADMAP.md | git log, GitHub issues/milestones |
| D | CONTRIBUTING.md + SECURITY.md + CODE_OF_CONDUCT.md | CONTRIBUTING conventions |
| E | WORKSPACE_MODES.md + TROUBLESHOOTING.md + DISCOVERY_PATTERN.md | WorkspaceManager*, MSBuildBootstrap |
| F | CODE_STYLE_ENFORCEMENT.md + DOC_REVIEW_CHECKLIST.md + RELEASE_CHECKLIST.md | scripts/Test-CodeStyle.ps1 |
| G | docs/tools/*.md + docs/reference/tools-assessment.md | Relevant tool .cs files |
| H | HANDOFF.md + .github/copilot-instructions.md + AGENT-INSTRUCTIONS.md | Current state of everything |
| I | docs/plans/*.md + battle-test-results.md + MSBUILD_API_ANALYSIS.md | Historical; flag stale claims |
| J | .github/PULL_REQUEST_TEMPLATE.md + ISSUE_TEMPLATE/*.md | GitHub workflow accuracy |

**Every subagent must include this constraint block verbatim:**
```
MANDATORY TOOL CONSTRAINTS — do NOT violate these:
- Use roslyn_* MCP tools for ALL C# file operations.
- Do NOT use cd — the CWD is already correct.
- Do NOT use roslyn_read_file on non-.cs files — use the view tool instead.
- Do NOT run Test-CodeStyle.ps1 — style passes are suspended. Violators get the dunce cap. 🎓
- Do NOT reformat, reorder, or restyle any code while fixing docs — you are a doc editor, not a formatter.
- Do NOT commit without being explicitly asked.
- Build check: roslyn_get_diagnostics (severity: errors) only — never dotnet build.
```

**Commit when done:** `docs: audit and fix documentation drift`

---

## Documentation Structure (v0.3.0+)

### Primary User-Facing Docs
- **README.md** — Quick overview, selling points, tool list (keep concise, link to details)
- **INSTALLATION.md** — Client-specific setup (self-contained for UX)
- **CONTRIBUTING.md** — Contributor workflow, testing, PR guidelines

### Technical Reference
- **AGENTS.md** — AI agent working rules, architecture, code patterns
- **docs/reference/WORKSPACE_MODES.md** — MSBuildWorkspace vs AdhocWorkspace deep-dive
- **docs/guides/TROUBLESHOOTING.md** — Comprehensive issue resolution guide

### Process Docs
- **docs/process/DOC_REVIEW_CHECKLIST.md** — This file
- **`docs/process/DUPLICATION_ANALYSIS.md`** — referenced here but does not exist; removed reference
- **docs/process/RELEASE_CHECKLIST.md** — Pre-release verification

---

## Quick Validation Commands

These `rg` (ripgrep) commands catch common documentation drift issues:

```powershell
# 1. Check for old-style project paths (should be src/RoslynMcp/, not RoslynMcp/)
rg "RoslynMcp/RoslynMcp\.csproj" --type md --glob "!HANDOFF*.md"

# 2. Check for dotnet run in MCP configs (should use published executable)
rg "dotnet.*run.*--project.*\.mcp\.json" --type md -A 3 -B 3

# 3. Verify tool count is consistent (should be 37 tools, 35 public + 2 debug-only)
rg "23 tools|22 tools|21 tools" --type md

# 4. Check for stale "deferred" or "planned" features that shipped
rg -i "deferred|planned feature|TODO:" --type md --glob "README.md" --glob "AGENTS.md" --glob "INSTALLATION.md"

# 5. Check for placeholder URLs or usernames
rg "YOUR_USERNAME|example\.com|PLACEHOLDER" --type md

# 6. Check for TestHarness paths (should be src/TestHarness/)
rg "TestHarness/TestHarness\.csproj" --type md | rg -v "src/TestHarness"
```

---

## Manual Review Sections

### File/Directory Structure
- [ ] All code examples use `src/RoslynMcp/` not `RoslynMcp/`
- [ ] All code examples use `src/TestHarness/` not `TestHarness/`
- [ ] `.mcp.json` examples use published executable, not `dotnet run`
- [ ] Build commands reference correct paths
- [ ] No broken relative links between docs

### Tool Count & Architecture
- [ ] Tool count is accurate in:
  - [ ] README.md
  - [ ] AGENTS.md
  - [ ] `docs/sessions/HANDOFF.md` header
  - [ ] TestHarness header comment
- [ ] Architecture tables list all 35 tools consistently
- [ ] New tools added to all relevant docs

### Code Examples
- [ ] C# code examples compile against current API
- [ ] MCP protocol examples match current implementation
- [ ] Roslyn pattern examples are idiomatic
- [ ] JSON examples are valid and complete

### Installation & Setup
- [ ] README.md installation steps are current
- [ ] INSTALLATION.md matches README.md intent (some duplication acceptable for UX)
- [ ] Prerequisites are accurate (.NET versions, etc.)
- [ ] Published executable approach is documented
- [ ] No references to abandoned approaches

### Reference Documentation
- [ ] docs/reference/WORKSPACE_MODES.md is accurate and complete
- [ ] docs/guides/TROUBLESHOOTING.md covers common issues
- [ ] All links to reference docs from main docs work

### Feature Completeness
- [ ] Remove "planned feature" notes for shipped features
- [ ] Remove "deferred enhancements" for completed work
- [ ] Update status badges if applicable
- [ ] Verify CHANGELOG.md is up to date

### Public Repo Readiness
- [ ] No internal/private references
- [ ] No placeholder text (YOUR_USERNAME, etc.)
- [ ] LICENSE file exists and is correct
- [ ] CONTRIBUTING.md is welcoming and accurate
- [ ] Issue templates exist and are relevant
- [ ] No sensitive information in examples

---

## File-Specific Review

### README.md
- [ ] Project description is accurate
- [ ] Installation instructions work
- [ ] Tool count matches reality
- [ ] Tool selection guidance is clear
- [ ] Links to other docs work
- [ ] Examples are copy-paste ready

### AGENTS.md
- [ ] Architecture table has all 35 tools
- [ ] Code style rules are current
- [ ] MCP/Roslyn patterns are accurate
- [ ] Testing section references correct paths
- [ ] `.mcp.json` example uses executable approach

### INSTALLATION.md
- [ ] Matches README.md installation steps (some duplication OK)
- [ ] Paths are correct (src/ structure)
- [ ] Published executable approach documented
- [ ] Links to reference docs work

### AGENTS.md
- [ ] Architecture section has correct tool/component count
- [ ] Links to docs/reference/ work
- [ ] Workspace modes summary is current

### docs/reference/WORKSPACE_MODES.md
- [ ] MSBuildWorkspace section complete and accurate
- [ ] AdhocWorkspace section complete and accurate
- [ ] FAQ addresses common questions
- [ ] Performance comparison up to date

### docs/guides/TROUBLESHOOTING.md
- [ ] Covers common installation issues
- [ ] Workspace/type resolution issues documented
- [ ] Platform-specific solutions included
- [ ] Links to other docs work
- [ ] Platform-specific notes are accurate

### CONTRIBUTING.md
- [ ] References to AGENTS.md are correct
- [ ] Code style section is in sync with AGENTS.md
- [ ] Exception handling guidelines are current
- [ ] Adding a new tool instructions work

### `docs/sessions/HANDOFF.md`
- [ ] Tool count in header is current
- [ ] Test status is current
- [ ] Last commit message matches reality
- [ ] Commands Reference paths include src/
- [ ] Session summaries are complete

### CHANGELOG.md
- [ ] Latest release notes are accurate
- [ ] Version numbers match tags
- [ ] Breaking changes are documented
- [ ] Links to issues/PRs work

---

## Post-Review Actions

After fixing issues found:

1. **Commit changes:**
   ```bash
   git add -A
   git commit -m "docs: audit and fix documentation drift"
   ```

2. **Update `docs/sessions/HANDOFF.md`** with audit summary

3. **Build and test:**
   ```bash
   dotnet build src/RoslynMcp/RoslynMcp.csproj
   dotnet run --project src/TestHarness/TestHarness.csproj
   ```

4. **Final verification:**
   - Re-run quick validation commands above
   - Read README.md as if you're a new user
   - Follow installation instructions in a clean directory

---

## Integration with Development Workflow

### When to Run This Checklist

- ✅ **Before making repository public**
- ✅ **Before major releases** (v0.x.0, v1.0.0)
- ✅ **After structural changes** (file moves, project restructure)
- ✅ **After adding/removing tools**
- ⚠️ **When docs feel stale** (trust your gut)

### Quick Checks During Development

For routine changes, just verify:
- Tool count if you added/removed tools
- Architecture tables if you added components
- Code examples if you changed APIs
- Paths if you moved files

Full audit only needed for major milestones.

---

## Automation (Future)

**Phase 2 ideas (when we feel pain):**
- Add doc validation tests to TestHarness
- Run in CI as warning (not failure)
- Automated tool count verification
- Link checker for internal references

**Not implementing now** — manual checklist sufficient for MVP and public launch.
