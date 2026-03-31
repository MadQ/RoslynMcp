# RoslynMcp Battle-Test Results

**First battle-test run — Sonnet 4.6, medium effort, March 2026**

We tested RoslynMcp against three repos with the same prompts, with and without roslyn_* tools. Here's what we found — the good, the bad, and the honest.

**Test repos:**
- [Spectre.Console](https://github.com/spectreconsole/spectre.console) (26 projects) — beautiful console UI library
- [Orleans](https://github.com/dotnet/orleans) (63 projects) — distributed actor framework by Microsoft
- [RoslynMcp](https://github.com/MadQ/RoslynMcp) itself (5 projects) — dogfooding

---

## The Numbers

### Spectre.Console (26 projects) — 7 tests

| Test | roslyn (tokens) | built-in (tokens) | Savings | Winner |
|------|------------|---------------|---------|--------|
| Find implementations | +7k | +4k | -43% | built-in tools |
| Understand method | +6k | +8k | +25% | roslyn tools |
| Type hierarchy | +4k | +6k | +33% | roslyn (but built-in was richer) |
| Find references | +5k | +7k | +29% | roslyn (but built-in found 14+ vs 5 refs) |
| File overview | +5k | +9k | +44% | roslyn tools |
| Diagnostics (build errors) | +3k | +3k | tie | roslyn (18× faster) |
| Rename (full) | ~10k | ~25k | +60% | roslyn tools |

### RoslynMcp Self-Refactoring — scope.Error migration

| | Tokens | Time | Tool calls |
|---|--------|------|------------|
| **roslyn tools** | +10k | 1m 0s | 11 |
| **built-in tools** | +32k | 1m 23s | ~31 |
| **Original subagent** | unknown | 8+ min | 93 |

**69% fewer tokens with RoslynMcp.** The regex replacement approach (one search + 9 parallel replace_in_file) was fundamentally smarter than 9 file reads + 22 individual edits.

### Orleans (63 projects) — Full 13-prompt workflow

| Metric | roslyn tools | built-in tools |
|--------|---------------|---------|
| Total context delta | **+37k** | +60k |
| Cold start | 7m 6s (MSBuild, 235 projects) | 0 |
| Actual work time | ~5m | ~8m |
| Build verification | 3× instant, all confirmed | 1× attempted, timed out |
| Bug found | null-deref (34s) | ToString() drops stack trace (1m 22s) |
| Strategy pivots | 0 | 1 |

**38% fewer tokens for the full workflow.**

---

## Where RoslynMcp Wins Clearly

### 1. Rename — 60% token savings, zero risk
One call to preview, one call to apply. Atomic — either succeeds completely or fails cleanly. The built-in tools run needed 11 Edit calls with an error/retry. Haiku + RoslynMcp produced the identical rename as Sonnet — the model doesn't matter when the tool does the work.

### 2. Build verification — 18× faster, actually verifies
`roslyn_get_diagnostics` returns in milliseconds. `dotnet build` on Orleans took 1m+ and timed out on one project. The built-in tools run said "yes, it's fine" without actually building once. The roslyn tools run proved it three times.

### 3. Large-scale refactoring — 69% fewer tokens
The self-refactoring test is the clearest win. `roslyn_search_files` found all 22 matches in one call. `roslyn_replace_in_file` with regex applied the fix to 9 files in parallel. No file reading, no manual edit construction.

### 4. Cheap model parity
Haiku + RoslynMcp = same correctness as Sonnet + RoslynMcp for semantic operations. The tool does the heavy lifting, not the model.

### 5. Zero strategy pivots
Across the full Orleans workflow (13 prompts), the roslyn tools run had zero strategy pivots. Every approach was direct. The built-in tools run had to retry (MSBuild multi-project error) and sometimes abandoned approaches.

---

## Where Built-In Tools Won or Tied

### 1. Simple grep-friendly tasks
Test 1 (find implementations of `IAnsiConsole`) — grep won because the interface name is unique. No false positives, fewer tokens. For unique names in small-to-medium repos, grep is fine.

### 2. Answer richness on exploration
The built-in tools runs consistently produced more detailed, contextualized answers for exploration prompts. Reading full files gives the agent ambient context it can reference later. RoslynMcp's tools return precise but minimal data.

### 3. Cached context advantage
After reading a file once, the built-in tools agent could answer follow-up questions about that file for free (already in context). The roslyn tools agent made fresh tool calls for each question. This advantage fades on cross-project work.

### 4. The ToString() bug discovery
The built-in tools agent found a more impactful bug (ToString() dropping stack traces) because it read the full 180-line file and noticed the override while scanning for constructors. The roslyn tools agent found a different bug (null-deref in constructor) by tracing the call chain precisely — but didn't see ToString() because `get_member_body` only returned what was asked for.

---

## Honest Assessment

**RoslynMcp's value is clearest for:** rename, refactoring, build verification, large-scale search-and-replace, and cross-project analysis. These are operations where precision and atomicity matter more than breadth.

**RoslynMcp's tools need improvement for:** exploration (richer responses needed), type hierarchy (missing per-type details), find_references (needs context snippets), and "awareness" of related members the agent didn't ask about.

**The cold start problem is real.** 7 minutes for Orleans (235 compiled projects) is painful. The adhoc fallback works (~20s) but loses MSBuild semantics. We need the `workspaceMode` parameter (#101) and the large-solution heuristic.

**Token savings are real but not universal.** 38-69% savings on refactoring/editing workflows. Modest savings on exploration (25-44%). Grep wins on simple unique-name searches. The headline is NOT "X% fewer tokens on everything" — it's "dramatically fewer tokens where it matters most, and comparable or slightly more where it doesn't."

---

## Bugs Found During Testing

### In RoslynMcp
1. `.slnx` parser missed `<Folder>`-nested projects (`.Elements` → `.Descendants`)
2. `ResolveProjectPath` doesn't handle `.slnx` input
3. Path resolution requires absolute paths — agents always try relative first
4. 7-minute cold start on large solutions — needs `workspaceMode` option
5. `scope.Outcome` missing on several success paths
6. `roslyn_insert_lines` never chosen by agents — description needs improvement
7. Multi-line `roslyn_replace_in_file` patterns still broken (CRLF in MCP JSON)

### In Orleans (real bugs found by both agents)
1. `InconsistentStateException` constructor null-deref on `storageException.Message` (found by roslyn tools)
2. `InconsistentStateException.ToString()` drops stack trace (found by built-in tools)
3. Typo: "help" → "held" in XML doc (found by both, in two files)

---

## Improvement Roadmap (from battle-test findings)

1. **Enrich `find_references`** — add context snippets per reference
2. **Enrich `get_type_hierarchy`** — per-type interfaces, intermediate base classes
3. **"Awareness hints"** — note related overrides (ToString, Dispose, etc.) when returning member bodies
4. **`workspaceMode` parameter** — opt-in adhoc for large solutions (#101)
5. **Smarter `projectPath` resolution** — handle .slnx, try CWD-relative, search by filename
6. **`roslyn_replace_body`** — replace method body only, keep signature intact
7. **Diagnostics grouping** — `groupBy: "code"` for summarized error/warning view
8. **Fix CRLF in multi-line patterns** — investigate MCP JSON string escaping

---

## Glossary

**Strategy pivot** — when the agent abandons its current approach and tries something different. Examples: "wait, let me try a different file", "actually, let me search for that instead", "that didn't work, let me approach this differently." Each pivot wastes the tokens already spent on the abandoned approach. Fewer pivots = more efficient, more focused work.

**Cold start** — the one-time cost of loading the Roslyn workspace on the first tool call. Subsequent calls reuse the cached workspace and are near-instant.

**Narrow focus vs greedy file reading** — a fundamental trade-off we observed. Roslyn tools return precisely what was asked for (a single method body, a list of references) — efficient but the agent only sees what it requests. Built-in tools read entire files — expensive but the agent gains ambient context that can surface unexpected findings. In our testing, roslyn tools produced faster, more focused answers with fewer wasted tokens. Built-in tools occasionally discovered issues the roslyn tools agent missed because it never looked at the surrounding code. Neither approach is universally better; the ideal is roslyn tools with optional "awareness hints" that flag related code worth investigating.

---

## Test Methodology Notes

- Same model (Sonnet 4.6), same thinking level (medium), same prompts
- `/context` before and after each prompt for token tracking
- Fresh Claude Code terminal for each roslyn/built-in run
- Orleans run included deliberately planted bugs (missing semicolon, unused variable)
- The `/context` commands may have influenced agent behavior (agents may optimize for token savings when they see the user checking context repeatedly)
- Retry/pivot tracking was observational, not automated — future runs should use hooks
