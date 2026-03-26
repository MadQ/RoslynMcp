# Documentation Duplication Analysis

**Date:** 2025-01-XX  
**Context:** v0.3.0 documentation review  
**Status:** Analysis complete, recommendations provided

---

## Executive Summary

Significant duplication exists across the markdown documentation files, particularly in:
- Build instructions (3+ locations)
- MCP configuration examples (4+ locations)
- Framework choices explanation (3 locations)
- Workspace modes description (3 locations)
- Tool tables and descriptions (2 major locations)

**Recommendation:** Hybrid approach — trim README.md, keep INSTALLATION.md self-contained, consolidate technical content in AGENTS.md, create focused reference docs.

---

## Duplication Map

### 1. Build Instructions

**Content:** `dotnet publish` commands with framework selection

| File | Lines | Detail Level |
|------|-------|--------------|
| README.md | "Building the Executable" section | Full explanation + examples |
| INSTALLATION.md | Step 1 | Full explanation + examples |
| CONTRIBUTING.md | "Building" section | Full explanation + examples |

**Assessment:** High duplication. All three contain identical content.

**Recommendation:** 
- README.md → Keep minimal quick start only
- INSTALLATION.md → Keep full (users need during setup)
- CONTRIBUTING.md → Link to docs/guides/building.md OR keep minimal variant for contributors

---

### 2. Framework Choices

**Content:** Explanation of net8.0/net10.0/net11.0 selection

| File | Occurrence |
|------|------------|
| README.md | Configuration section |
| INSTALLATION.md | Step 1 |
| AGENTS.md | Project table |

**Assessment:** Moderate duplication. Same bullet list appears in 3 places.

**Recommendation:** 
- Create canonical source in docs/reference/FRAMEWORKS.md
- README/INSTALLATION → Keep minimal inline
- Link to reference for "why these versions"

---

### 3. MCP Configuration Examples

**Content:** `.mcp.json` structure with command/args

| File | Examples Count |
|------|----------------|
| README.md | 3-4 examples |
| INSTALLATION.md | 10+ examples (per client) |
| AGENTS.md | 2 examples |
| copilot-instructions.md | 1 reference |

**Assessment:** Acceptable duplication — each serves different purpose.

**Recommendation:** 
- README.md → 1 quick start example only
- INSTALLATION.md → Keep all (self-contained setup guide)
- AGENTS.md → Keep minimal for context
- copilot-instructions.md → Link only

---

### 4. Tool Descriptions

**Content:** The 24 tools with descriptions

| File | Format | Detail |
|------|--------|--------|
| README.md | Markdown table | One-line descriptions |
| AGENTS.md | Architecture table | Component responsibilities (includes non-tools) |

**Assessment:** Low overlap — different perspectives (user-facing vs technical).

**Recommendation:** 
- README.md → Keep summary table OR move to docs/reference/TOOLS.md
- AGENTS.md → Keep architecture table (includes infrastructure components)
- Consider creating comprehensive tool reference with examples

---

### 5. Workspace Modes

**Content:** MSBuildWorkspace vs AdhocWorkspace explanation

| File | Section |
|------|---------|
| README.md | "Workspace modes" section (~40 lines) |
| INSTALLATION.md | "Multi-project workspaces" section (~15 lines) |
| AGENTS.md | Architecture section (~10 lines) |

**Assessment:** High duplication, varying detail levels.

**Recommendation:** 
- Create docs/guides/WORKSPACE_MODES.md (canonical deep-dive)
- README.md → Summary + link
- INSTALLATION.md → User-facing summary (when matters for setup)
- AGENTS.md → Technical reference only

---

### 6. Troubleshooting

**Content:** Common issues and solutions

| File | Issues Covered |
|------|----------------|
| README.md | "Troubleshooting" section (previously had multi-framework error) |
| INSTALLATION.md | "Troubleshooting" section (larger, client-specific) |

**Assessment:** Moderate overlap with client-specific divergence.

**Recommendation:** 
- Create docs/guides/TROUBLESHOOTING.md (comprehensive)
- README.md → Remove section entirely, link to guide
- INSTALLATION.md → Keep inline client-specific issues, link to guide for general issues

---

### 7. Architecture Components

**Content:** WorkspaceManager, WorkspaceResolver, tool infrastructure

| File | Format |
|------|--------|
| README.md | "Design" section (brief) |
| AGENTS.md | "Architecture" section (comprehensive table) |

**Assessment:** Low duplication — README is high-level, AGENTS is detailed.

**Recommendation:** 
- README.md → Keep high-level only OR remove entirely, link to AGENTS.md
- AGENTS.md → Canonical technical reference

---

## Recommended Actions

### Phase 1: Quick Wins (Minimal Changes)

**Priority: High | Effort: Low**

1. **README.md § Building**
   ```diff
   - [Full 10-line explanation of frameworks]
   + See [INSTALLATION.md](INSTALLATION.md) for complete build instructions.
   + 
   + Quick start:
   + ```bash
   + dotnet publish src/RoslynMcp/RoslynMcp.csproj -c Release -f net10.0 -o ./publish/net10.0
   + ```
   ```

2. **README.md § Troubleshooting**
   ```diff
   - [Full troubleshooting section]
   + **Troubleshooting:** See [INSTALLATION.md § Troubleshooting](INSTALLATION.md#troubleshooting) 
   + for common setup issues.
   ```

3. **CONTRIBUTING.md § Code Style**
   ```diff
   - [Duplicate style guidelines]
   + **See [AGENTS.md § Code Style](AGENTS.md#code-style) for complete guidelines.**
   + 
   + Quick checklist:
   + - Modern C# (pattern matching, target-typed new)
   + - Braces on same line for control flow
   + - Explain *why*, not *what*
   ```

**Impact:** Reduces duplication by ~20%, improves maintainability.

---

### Phase 2: Modular Reference Docs

**Priority: Medium | Effort: Medium**

4. **Create docs/reference/TOOLS.md**
   - Move full 24-tool table from README.md
   - Add expanded descriptions, examples, use cases
   - README.md gets summary table with "See TOOLS.md for details"

5. **Create docs/guides/TROUBLESHOOTING.md**
   - Consolidate common issues from README + INSTALLATION
   - Add solutions with code examples
   - Both README/INSTALLATION link here for general issues
   - INSTALLATION keeps client-specific issues inline

6. **Create docs/guides/WORKSPACE_MODES.md**
   - Deep-dive: MSBuildWorkspace vs AdhocWorkspace
   - When to use each, tradeoffs, performance characteristics
   - README/INSTALLATION/AGENTS all link here

**Impact:** Reduces duplication by ~40%, creates single sources of truth.

---

### Phase 3: Tiered Documentation Structure

**Priority: Low | Effort: High**

7. **README.md** → **Trim to ~200 lines**
   - Project description + key features
   - Minimal quick start
   - Links to all other docs
   - Remove: detailed tool table, architecture, troubleshooting, workspace modes

8. **INSTALLATION.md** → **Keep self-contained**
   - Accept duplication for better UX
   - Users shouldn't jump between docs during setup

9. **AGENTS.md** → **Consolidate technical content**
   - Canonical reference for AI agents + contributors
   - Absorb architecture details from README
   - Expand tool implementation patterns

10. **CONTRIBUTING.md** → **Focus on workflow**
    - Git branching, commit messages, PR process
    - Testing procedures
    - Link to AGENTS.md for code style

**Impact:** Clear separation of concerns, ~60% reduction in maintenance burden.

---

## File Purpose Matrix

| File | Audience | Purpose | Detail Level | Accept Duplication? |
|------|----------|---------|--------------|---------------------|
| **README.md** | GitHub visitors, new users | Quick overview, selling points | Minimal | No — link aggressively |
| **INSTALLATION.md** | Users setting up | Step-by-step setup for all clients | Complete | Yes — UX priority |
| **AGENTS.md** | AI agents, contributors | Technical reference, working rules | Comprehensive | No — single source |
| **CONTRIBUTING.md** | New contributors | Workflow, testing, PR guidelines | Workflow-focused | No — link to AGENTS.md |
| **copilot-instructions.md** | GitHub Copilot | Copilot-specific overrides | Minimal | No — links only |
| **docs/reference/** | Deep-divers | Canonical technical references | Exhaustive | N/A — sources of truth |
| **docs/guides/** | Problem-solvers | Task-oriented how-tos | Practical | Low — link internally |

---

## Implementation Plan

### Option A: Minimal (Phase 1 Only)
**Effort:** 1-2 hours  
**Impact:** 20% duplication reduction  
**Risk:** Low  

**Steps:**
1. Trim README.md (building, troubleshooting → links)
2. Update CONTRIBUTING.md (code style → link to AGENTS.md)
3. Verify all links work

---

### Option B: Moderate (Phases 1-2)
**Effort:** 3-4 hours  
**Impact:** 40% duplication reduction  
**Risk:** Medium (need to update multiple cross-references)  

**Steps:**
1. Execute Phase 1
2. Create docs/reference/TOOLS.md (move from README)
3. Create docs/guides/TROUBLESHOOTING.md (consolidate)
4. Create docs/guides/WORKSPACE_MODES.md (consolidate)
5. Update all references
6. Verify navigation flow

---

### Option C: Comprehensive (All Phases)
**Effort:** 6-8 hours  
**Impact:** 60% duplication reduction  
**Risk:** High (major restructure, many link updates)  

**Steps:**
1. Execute Phases 1-2
2. Restructure README.md (trim to ~200 lines)
3. Strengthen AGENTS.md (absorb README architecture)
4. Refocus CONTRIBUTING.md (workflow only)
5. Create docs/reference/ structure
6. Create docs/guides/ structure
7. Update all cross-references
8. Audit navigation experience
9. Update DOC_REVIEW_CHECKLIST.md

---

## Decision Needed

**Question for maintainer:** Which option should we pursue?

- [ ] **Option A** — Quick wins only (minimal changes, low risk)
- [ ] **Option B** — Moderate refactor (create reference docs)
- [ ] **Option C** — Comprehensive restructure (full DRY approach)
- [ ] **Defer** — Ship v0.3.0 as-is, address post-release

**Considerations:**
- **Pre-v1.0:** More flexibility for breaking changes
- **Post-public:** User-facing doc URLs matter (avoid breaking links)
- **Maintenance burden:** Current duplication manageable but will grow

---

## Notes

- Session files (`docs/sessions/HANDOFF.md`) intentionally duplicate content — they're point-in-time snapshots
- Plan files (`docs/plans/*.md`) also duplicate — acceptable for context preservation
- This analysis focuses on **user-facing** and **reference** documentation only

---

## Related

- [DOC_REVIEW_CHECKLIST.md](DOC_REVIEW_CHECKLIST.md) — validation commands
- [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) — pre-release verification
- [v0.3.0 documentation review](../../.github/copilot-instructions.md) — recent updates

---

**Last Updated:** 2025-01-XX (v0.3.0 documentation review)
