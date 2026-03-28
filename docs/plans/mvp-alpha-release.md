# MVP Alpha Release Plan

**Goal:** Ship a binary release that makes people say "I can't go back to grep."

**Status:** Pre-release. Engine is solid, product packaging needs work.

---

## Phase 1: Response Shape Cleanup (next up)

The anonymous-type inconsistency is a correctness issue — agents can't reliably parse responses.

- [ ] Define shared `record` types: `ErrorResult(string Error, string Message, string? Hint)`, `PagedResult<T>`
- [ ] Audit every tool's return shape — document current vs desired
- [ ] Migrate error responses first (highest duplication, most agent-facing)
- [ ] Migrate paged responses to `PagedResult<T>`
- [ ] Per-tool result records where the shape is unique
- [ ] Update test harness assertions to match new shapes

Ref: ScratchPad "Replace anonymous types with records", issue #57

## Phase 2: Token Usage Estimation

Agents burn tokens on every tool response. Users need visibility into the cost.

- [ ] Calculate or estimate token count for each tool response (chars / 4 as rough estimate, or use a tokenizer)
- [ ] Add `estimated_tokens` field to `LogTool` output in FileLogger
- [ ] Log per-tool and cumulative token usage per session
- [ ] Surface in LogViewer (running total, per-tool breakdown)

## Phase 3: README Rewrite

First impression matters. The README should sell RoslynMcp in the first screenful.

- [ ] **Hero section:** What it is, why it matters, one compelling example (e.g. "rename a symbol across 50 files in one tool call")
- [ ] **Quick start:** Install → configure MCP client → first tool call. Under 60 seconds.
- [ ] **Agent instructions:** How to configure your agent to prefer roslyn_* tools (CLAUDE.md example, AGENTS.md example, etc.)
- [ ] **Tool catalog:** Grouped by category with one-line descriptions
- [ ] **"Help wanted" section:** Honest about current state:
  - First-call latency (~10s for workspace load) — looking for ideas to improve
  - Platform testing — tested on Windows, looking for Linux/macOS testers
  - Large solution testing — looking for volunteers to test against real-world codebases
  - Tone: confident but honest. "This is alpha. It works. We want to make it great. Here's how you can help."
- [ ] **Contributing guide:** Link to CONTRIBUTING.md, mention that even small PRs (tool description tweaks) are welcome

## Phase 4: Battle-Test Against Real Repos

Ship confidence, not hope. Test against repos that stress different axes.

- [ ] **Small, well-known:** Something like `Humanizer` or `Bogus` — single project, clean structure
- [ ] **Medium, multi-project:** Something like `Spectre.Console` or `MediatR` — multiple projects, some complexity
- [ ] **Large:** Something like `Roslyn` itself or `Orleans` — stress test workspace load time, memory, pagination
- [ ] Document what works, what breaks, what's slow
- [ ] Fix showstoppers, file issues for the rest
- [ ] Add observed load times to README ("Loads a 5-project solution in ~10s, a 50-project solution in ~45s" — real numbers)

## Phase 5: Binary Release

Only after Phases 1-4. This is the front door.

- [ ] Self-contained single-file publish for win-x64, linux-x64, osx-arm64
- [ ] GitHub Release with binaries + changelog
- [ ] `dotnet tool install` (NuGet package) — stretch goal, nice to have
- [ ] MCP client configuration examples (Claude Code `.mcp.json`, VS Code, etc.)
- [ ] Tag as `v0.8.0-alpha` or `v1.0.0-alpha` depending on scope completed
- [ ] Announce somewhere (Reddit r/dotnet? Twitter/X? Hacker News?)

---

## Parking Lot (post-MVP, don't block on these)

- Call graph tools (#32) and find_unused (#33) — v0.8.0 milestone, huge differentiators but not MVP-blocking
- Style-aware editing (#34, #35) — v0.9.0
- Security boundaries (#9) — important for multi-user, not for alpha
- `ExpandEnvironmentVariables` sweep — nice to have
- `roslyn_insert_lines` anchor improvements — ship, iterate
- FrozenDictionary for SyntaxKind — perf optimization, not user-facing
- LogViewer as embedded resource — DX improvement, not user-facing

---

## Sequencing

```
Phase 1 (response shapes)  ──→  Phase 2 (token estimation)  ──→  Phase 3 (README)
                                                                       │
                                                                       ▼
                                                              Phase 4 (battle-test)
                                                                       │
                                                                       ▼
                                                              Phase 5 (binary release)
```

Phases 1-2 are code work. Phase 3 is writing. Phase 4 is testing. Phase 5 is packaging.
Realistic timeline: this is a solo project with a day job. No dates. Ship when it's ready.
