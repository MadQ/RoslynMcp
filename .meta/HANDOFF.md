# Session Handoff — March 22, 2026

**Date:** 2026-03-22  
**Branch:** `feature/restructure-folders` (pushed, ready to merge)  
**Last Commit:** `c82db98` — Restructure: move code to src/, metadata to .meta/  
**Repository:** https://github.com/MadQ/RoslynMcp.git (private, ready for public)  
**Tool Count:** 20 tools

---

## Current Session (March 22, 2026 — Part 2)

### **Project Restructure: src/ and .meta/** 🏗️

**Goal:** Clean up root directory, separate project metadata from code, eliminate manual Solution Items management.

**Changes:**
- ✅ Created `.meta/` folder for project metadata (dot-prefix keeps VS from auto-adding to solution)
- ✅ Created `src/` folder for code projects (industry standard layout)
- ✅ Moved metadata files to `.meta/`:
  - AGENTS.md, CONTRIBUTING.md, HANDOFF.md
  - POST_PUSH_CHECKLIST.md, RELEASE_CHECKLIST.md, TEST_RESULTS.md
  - HumanNotes.txt
- ✅ Moved code to `src/`:
  - RoslynMcp/ (entire project)
  - TestHarness/ (entire project)
- ✅ Updated all cross-references:
  - .github/copilot-instructions.md
  - .meta/AGENTS.md
  - .meta/CONTRIBUTING.md
  - README.md
  - INSTALLATION.md
- ✅ Updated solution file (removed Solution Items folder, updated project paths)
- ✅ Updated CI workflow (.github/workflows/build.yml)
- ✅ Build verified (all targets compile successfully)

**Root directory now contains:**
- README.md, LICENSE, CHANGELOG.md, INSTALLATION.md (user-facing)
- .gitattributes, .gitignore (git config)
- .github/ (GitHub-specific)
- RoslynMcp.slnx (solution file)
- .meta/ (project metadata, hidden)
- src/ (all code)

**Benefits:**
- Clean, professional root directory
- Standard .NET OSS layout (src/ is industry convention)
- No manual Solution Items management in Visual Studio
- Clear separation: product docs vs project metadata vs code
- Scales well for future additions (docs/ remains available for user guides)

**Status:** Committed and pushed to feature branch, ready to merge to dev

---

## Previous Session (March 22, 2026 — Part 1)

### What Was Accomplished

### 1. **Public GitHub Release Preparation** ✅
- Comprehensive housekeeping completed
- **Added:** LICENSE (MIT, 2025), CONTRIBUTING.md, CHANGELOG.md
- **Added:** GitHub issue templates (bug, feature), PR template
- **Added:** CI workflow (`.github/workflows/build.yml`)
- **Added:** .gitattributes, POST_PUSH_CHECKLIST.md, RELEASE_CHECKLIST.md
- **Pushed to GitHub** (initially private)
- Fixed placeholders (YOUR_USERNAME → MadQ)
- Added contributor recognition section to CONTRIBUTING.md

### 2. **Documentation DRY + Simplification** ✅
- **Eliminated `dotnet run` approach** — Too fragile (multi-target confusion, process conflicts when dogfooding)
- **Simplified to:** `dotnet publish` → use executable
- **Added:** Central Configuration section in README.md (single source of truth)
- **Added:** "Building a Local Executable" section with benefits explained
- **Added:** Brief note about "interesting recursive behavior" experiment with dotnet run
- **Result:** -128 lines, +67 lines = **-61 lines of documentation complexity**
- Fixed DRY violations across README, INSTALLATION, CONTRIBUTING

### 3. **Fixed CI/CD Build Failures** ✅
- **Problem:** .NET 11 not available on GitHub Actions runners (NETSDK1045 error)
- **Solution:** Auto-detect .NET 11 SDK using MSBuild target
- **Added:** `DetectNet11SDK` target checks `$(NETCoreSdkVersion)` property
- **Behavior:**
  - Local dev: builds net8.0, net10.0, net11.0 (if SDK ≥ 11.0 installed)
  - CI/CD: builds net8.0, net10.0 only (no .NET 11 SDK on runners yet)
- **More robust** than environment variable approach

### 4. **Added Build Workflow Tools** ✅
- **New tool:** `clean_solution` — Runs `dotnet clean`, removes bin/obj directories
- **New tool:** `restore_packages` — Runs `dotnet restore`, downloads NuGet packages
- **Design choice:** Separate, focused tools (not flags on build_project)
- **Rationale:** Unix philosophy, composability, clear intent, enables workflows like clean → restore → build
- **Total tools:** 20 (was 18)
- **Branch workflow:** Created feature/clean-restore-tools, merged to dev, cleaned up

### 5. **Added HumanNotes.txt** 📝
- User's development notes/ideas synced to repo
- **Added instruction in AGENTS.md:** "Ignore files called HumanNotes.txt — for human reference only"
- **Contains future ideas:**
  - Global MCP config vs project-specific
  - Semantic search enhancements (syntax-tree filtering)
  - DI necessity questions (do we need AddTransient registrations?)
  - Roslyn-native build without dotnet process

### 6. **Code Quality Improvements** ✅
- Renamed `ApprovalStore.gate` → `syncRoot` (standard .NET naming)
- Added comment explaining why `Lock` type not used (need .NET 8 compatibility)
- Code style refinements in CONTRIBUTING.md (emphasize intent over rigid consistency)

---

## Repository State

### Git Status
```
Branch: dev (clean working tree)
Remote: https://github.com/MadQ/RoslynMcp.git
Visibility: Private (ready for public when decided)
Feature branches: None (clean)
```

### Build Status
- ✅ Compiles on net8.0, net10.0, net11.0 (if SDK installed locally)
- ✅ Zero compiler warnings
- ✅ 16/16 tests passing (TestHarness)
- ⚠️ New tools (clean_solution, restore_packages) **not yet tested in TestHarness**

### CI/CD
- ✅ Workflow configured (`.github/workflows/build.yml`)
- ✅ Auto-detects .NET 11 SDK (builds net8.0/net10.0 only on GitHub Actions)
- ✅ Dependabot configured for automated dependency updates
- ⚠️ **Not yet verified on actual GitHub Actions run** (private repo)

---

## Current Tool Count: 20

### Tools by Category

**Discovery (5):**
- search_files, list_types, get_file_outline, get_project_info, get_usings

**Type Understanding (4):**
- get_type_members, get_type_hierarchy, find_implementations, get_symbol_documentation

**Navigation (3):**
- get_symbol_info, find_references, get_symbol_definition

**Code Generation (1):**
- get_symbols_in_scope

**Validation (4):** ⭐ *Updated this session*
- get_diagnostics, build_project, **clean_solution** ⭐, **restore_packages** ⭐

**Refactoring (2):**
- preview_rename, apply_rename (two-phase with ApprovalStore)

**Debug (1):**
- respawn (DEBUG only)

---

## Outstanding Items (from HumanNotes.txt)

### High Priority
- [ ] **Global MCP config** — Make MCP configurable globally, not project-specific
  - Current: .mcp.json args point to specific project directory
  - Idea: Project parameter on tools instead? Hybrid approach?
  - Impact: Major architectural change

### Medium Priority
- [ ] **Semantic search** — Enhance search_files with syntax-tree-based filtering
  - Filter by: comments only, strings only, identifiers only, exclude generated code
  - Implementation: Use Roslyn SyntaxTree to inspect matched lines
  - Option 1: Add flags to search_files
  - Option 2: New tool `semantic_search`
  - Documented in copilot-instructions.md as "Deferred enhancements"

- [ ] **Add tests for new tools** — CleanSolutionTool, RestorePackagesTool in TestHarness

### Low Priority / Research
- [ ] **DI necessity** — Verify if `builder.Services.AddTransient<>` registrations actually needed
  - C# MCP SDK docs may say otherwise (need to find reference)
  - Potential simplification if not required

- [ ] **Roslyn-native build** — Can we build/clean/restore without spawning `dotnet.exe`?
  - Current tools spawn dotnet process (works but not ideal)
  - Investigate MSBuild API or Roslyn compilation APIs

- [ ] **Implement `undo_last_edit`** — Documented as planned feature in README
  - Revert most recent Roslyn-generated edit from in-memory snapshot
  - Single-level undo (git handles deeper history)

---

## Next Steps (Suggestions)

### Immediate
1. **Add tests for new tools** — CleanSolutionTool, RestorePackagesTool in TestHarness
2. **Verify CI workflow** — Push to main (or make repo public) and watch GitHub Actions run
3. **Test published executable** — Full `dotnet publish` and dogfooding workflow
4. **Update HANDOFF.md** — Replace old content with this session's accomplishments

### Short Term
5. **Go public** — Change repository visibility when ready
6. **Create first release** — Tag v0.2.0-alpha, create GitHub release with CHANGELOG
7. **Announce** — Reddit /r/dotnet, Twitter, MCP community Discord/forums

### Medium Term
8. **Implement `undo_last_edit`** — Documented as planned feature
9. **Global MCP config research** — Investigate feasibility and design
10. **Semantic search** — Implement syntax-tree filtering for search_files
11. **NuGet publishing** — Publish as global tool: `dotnet tool install --global RoslynMcp`

---

## Key Decisions Made This Session

### 1. **Removed `dotnet run` for MCP invocation** 🏴‍☠️
- **Why:** Too fragile (multi-target confusion, process conflicts, complex args)
- **Replacement:** `dotnet publish` → use executable directly
- **Impact:** Much simpler user experience, cleaner docs
- **Note added:** "Early experiments with dotnet run produced interesting recursive behavior"

### 2. **Separate tools for clean/restore (not flags on build_project)**
- **Why:** Unix philosophy (do one thing well), composability, clear intent
- **Alternative considered:** `build_project(clean: bool, restore: bool)`
- **Decision:** Separate tools better for agent workflows (clean → restore → build)

### 3. **Auto-detect .NET 11 via MSBuild (not CI env var)**
- **Why:** More robust, self-documenting, works everywhere
- **Alternative considered:** `Condition="'$(CI)' != 'true'"`
- **Decision:** MSBuild `NETCoreSdkVersion` check is proper idiom

### 4. **Keep HumanNotes.txt synced but ignored by agents**
- **Why:** User wants notes synced, but they may confuse AI agents
- **Solution:** Explicit instruction in AGENTS.md to ignore

### 5. **Rename workflow philosophy documented**
- **Two-phase is always used:** preview_rename → apply_rename
- **User controls agent behavior:** Conservative / Balanced / Aggressive
- **Semantic approval vs protocol approval:** Clarified in docs
- **Planned undo feature:** Documented for "wait, let me rethink that" moments

---

## Important Files Changed This Session

### New Files Created
- `HumanNotes.txt` — User development notes
- `RoslynMcp/Tools/CleanSolutionTool.cs` — New tool (64 lines)
- `RoslynMcp/Tools/RestorePackagesTool.cs` — New tool (64 lines)
- `POST_PUSH_CHECKLIST.md` — Post-release tasks checklist
- `RELEASE_CHECKLIST.md` — Pre-release verification checklist
- `CONTRIBUTING.md` — Contribution guidelines with recognition section
- `CHANGELOG.md` — Version history (0.1.0-alpha, 0.2.0-alpha, Unreleased)
- `.gitattributes` — Line ending consistency
- `.github/ISSUE_TEMPLATE/bug_report.md` — Bug report template
- `.github/ISSUE_TEMPLATE/feature_request.md` — Feature request template
- `.github/PULL_REQUEST_TEMPLATE.md` — Pull request template
- `.github/workflows/build.yml` — CI/CD workflow (multi-OS, multi-.NET)
- `.github/dependabot.yml` — Automated dependency updates (from upstream)

### Heavily Modified
- `README.md` — Configuration section (DRY), Quick Start simplified, badges added, rename workflow documented
- `INSTALLATION.md` — Removed dotnet run, added troubleshooting, simplified to single method
- `AGENTS.md` — Tool docs, ignore HumanNotes.txt instruction, rename workflow guidance
- `.github/copilot-instructions.md` — Tool documentation, architecture updates
- `LICENSE` — Fixed year (2026 → 2025), correct copyright holder
- `RoslynMcp/RoslynMcp.csproj` — Auto-detect .NET 11 SDK with MSBuild target
- `RoslynMcp/Program.cs` — Registered CleanSolutionTool, RestorePackagesTool
- `RoslynMcp/ApprovalStore.cs` — Renamed gate → syncRoot, added Lock type comment
- `RoslynMcp.slnx` — Added HumanNotes.txt to solution

---

## Known Issues

### None Currently Blocking

All builds pass, tests pass, documentation is clean. No known bugs or blocking issues.

---

## Questions for User / Next Session

1. **Public repo timing** — Ready to go public, or waiting for something specific?
2. **NuGet publishing** — When to publish as global tool?
3. **Global MCP config** — High priority? Worth researching soon?
4. **Semantic search** — Should this be next feature after tests?
5. **v0.2.0-alpha release** — Ready to tag and create GitHub release?

---

## Session Metrics

- **Duration:** ~4 hours
- **Commits to dev:** 20+
- **Files changed:** 35+
- **Lines added:** ~1,000
- **Lines removed:** ~250
- **Net documentation reduction:** -61 lines (DRY improvements)
- **Tools added:** 2 (clean_solution, restore_packages)
- **Major refactors:** 1 (removed dotnet run approach)
- **Feature branches created/merged:** 1 (feature/clean-restore-tools)
- **Feature branches cleaned up:** 1 (local + remote)

---

## Commands Reference

### Build & Test
```sh
dotnet build RoslynMcp/RoslynMcp.csproj
dotnet run --project TestHarness/TestHarness.csproj
```

### Publish Executable
```sh
dotnet publish RoslynMcp/RoslynMcp.csproj -c Release -f net10.0 -o ./publish/net10.0
```

### MCP Config (Current Recommended Approach)
```json
{
  "servers": {
    "roslyn": {
      "type": "stdio",
      "command": "/absolute/path/to/RoslynMcp/publish/net10.0/RoslynMcp.exe",
      "args": ["."]
    }
  }
}
```

### Git Workflow (Feature Branches)
```sh
# Create feature branch
git checkout -b feature/name

# Make changes, commit
git add .
git commit -m "Description"

# Push to remote
git push origin feature/name

# Merge to dev
git checkout dev
git pull origin dev
git merge feature/name --no-ff -m "Merge feature/name: Description"
git push origin dev

# Clean up
git branch -d feature/name
git push origin --delete feature/name
```

---

## Documentation Philosophy (From This Session)

### Code Style (from CONTRIBUTING.md)
> "These are guidelines, not laws. The codebase values *clarity* and *intent* over rigid consistency. If you see a better way to express something — even if it deviates from the guide — do it, and explain why in a comment or commit message. Thoughtful departures help the style evolve."

### From copilot-instructions.md
> "Consistency is overrated. Embrace diversity. Deliberate departure from the guidelines above is fine — that's how better patterns get discovered."

### Documentation Changes
- **DRY principle applied:** Single source of truth for configuration (README.md Configuration section)
- **Simplicity over features:** Removed complex dotnet run approach, embraced simpler executable approach
- **User empowerment:** Document choices, let users decide (rename workflow: conservative/balanced/aggressive)

---

**End of session. Repository is stable, well-documented, and ready for public release.** 🚀

---

## Previous Session (Archived)

**Date:** 2025-01-XX  
**Summary:** Added SearchFilesTool, RespawnTool, optimized MCP config with --no-build  
**Last Commit:** 77707f3 (Merge feature/search-files-tool into dev)

*See git history for full details of previous sessions.*
