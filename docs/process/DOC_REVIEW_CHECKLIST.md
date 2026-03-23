# Documentation Review Checklist

Run this checklist before making the repository public or after significant structural changes.

---

## Quick Validation Commands

These `rg` (ripgrep) commands catch common documentation drift issues:

```powershell
# 1. Check for old-style project paths (should be src/RoslynMcp/, not RoslynMcp/)
rg "RoslynMcp/RoslynMcp\.csproj" --type md --glob "!HANDOFF*.md"

# 2. Check for dotnet run in MCP configs (should use published executable)
rg "dotnet.*run.*--project.*\.mcp\.json" --type md -A 3 -B 3

# 3. Verify tool count is consistent (should be 24 tools currently)
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
- [ ] Architecture tables list all 24 tools consistently
- [ ] New tools added to all relevant docs

### Code Examples
- [ ] C# code examples compile against current API
- [ ] MCP protocol examples match current implementation
- [ ] Roslyn pattern examples are idiomatic
- [ ] JSON examples are valid and complete

### Installation & Setup
- [ ] README.md installation steps are current
- [ ] INSTALLATION.md matches README.md (DRY check)
- [ ] Prerequisites are accurate (.NET versions, etc.)
- [ ] Published executable approach is documented
- [ ] No references to abandoned approaches (`dotnet run` confusion)

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
- [ ] Architecture table has all 24 tools
- [ ] Code style rules are current
- [ ] MCP/Roslyn patterns are accurate
- [ ] Testing section references correct paths
- [ ] `.mcp.json` example uses executable approach

### INSTALLATION.md
- [ ] Matches README.md installation steps
- [ ] Paths are correct (src/ structure)
- [ ] Published executable approach documented
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
