# Multi-Instance Architecture: Shared Workspace Service

**Status:** Design
**Issue:** #85
**Priority:** Post-MVP (v0.9.0 or v1.0.0)

---

## Problem

Each MCP client (agent, subagent) spawns its own RoslynMcp process. Each process loads its own MSBuild workspace independently:

- ~100MB+ RAM per Roslyn workspace
- ~10s cold start per process
- Concurrent MSBuild loads contend on disk/lock files
- Three parallel subagents = three redundant copies of the same compilation

This doesn't scale. A developer with Claude Code spawning background agents on a large solution will hit resource exhaustion quickly.

---

## Proposed Architecture

```
Agent A (stdio) ──┐
                  │
Agent B (stdio) ──┼── Named Pipe ── Workspace Service (one per solution)
                  │                  ├── MSBuild workspace (loaded once)
Agent C (stdio) ──┘                  ├── Compilation cache
                                     ├── Pagination cache
LogViewer ────────────────────────── ├── Log event stream
                                     └── FileSystemWatcher
```

### Components

**MCP Adapter (thin):** Each agent still spawns a process via stdio — that's what MCP requires. But the process is a lightweight adapter that:
1. Receives MCP JSON-RPC on stdin
2. Forwards tool calls to the Workspace Service via named pipe
3. Returns responses on stdout
4. Minimal memory footprint (~10MB)

**Workspace Service (heavy):** A single long-lived process per solution that:
1. Owns the MSBuild workspace, compilation cache, pagination cache
2. Handles all Roslyn operations (find references, get member body, etc.)
3. Manages the FileSystemWatcher
4. Serializes write operations, allows concurrent reads
5. Streams log events to connected listeners (LogViewer, MCP adapters)

### Named Pipe Design

**Pipe name:** `roslynmcp-{hash}` where `{hash}` is the first 8 chars of SHA256 of the canonical solution/project path. This naturally groups instances by solution — two agents working on the same `.slnx` share the same service.

**Why named pipes over HTTP:**
- No port management, no firewall prompts, no "is port taken?" conflicts
- No TCP overhead — named pipes are kernel-level IPC, faster for local communication
- Cross-platform via `System.IO.Pipes` on .NET
- Pipe name encodes the solution identity — no discovery protocol needed
- Simpler security model — filesystem ACLs control access

**Why not shared memory / memory-mapped files:**
- Roslyn objects aren't designed for shared-memory access
- Serialization boundary is unavoidable — named pipes make it explicit
- Shared memory would require custom allocators and synchronization primitives

### Process Lifecycle

**Startup:**
1. MCP adapter starts, computes pipe name from project path
2. Tries to connect to existing pipe
3. If no service running: acquires a mutex (`Global\roslynmcp-{hash}`), spawns itself with `--service` flag
4. Service loads workspace, opens named pipe server, releases mutex
5. Adapter connects, forwards first tool call

**Steady state:**
- Adapter forwards tool calls over pipe, receives responses
- Service handles calls with `ReaderWriterLockSlim` (concurrent reads, serialized writes)
- Multiple adapters share the same pipe (NamedPipeServerStream supports multiple connections)

**Shutdown:**
- When the last adapter disconnects, service starts a grace period (30s)
- If no new connections during grace period, service exits cleanly
- If service crashes, next adapter tool call detects dead pipe and respawns
- Agent sees one slow call (service restart + workspace reload), then fast calls resume

**Group death:** When the user closes their IDE or kills all agents, all adapters disconnect, grace period expires, service exits. No orphan processes.

---

## Concurrency Model

Roslyn's `MSBuildWorkspace` is not thread-safe for mutations. Current `WorkspaceInstance` already uses `ReaderWriterLockSlim`:

| Operation | Lock | Concurrent? |
|-----------|------|-------------|
| GetSolution, GetCompilation, GetProject | Read | Yes |
| find_references, get_member_body, etc. | Read | Yes |
| InvalidateFile, ReloadIfNeeded | Write | Serialized |
| preview_rename, change_signature (read) | Read | Yes |
| apply_rename, apply_signature_change (write) | Write | Serialized |
| replace_in_code, replace_in_file (write) | Write | Serialized |

This model translates directly to the service. Read-heavy agent workloads (navigation, discovery) scale well. Write-heavy workloads (rapid edits) serialize — but that's already the case today.

---

## Wire Protocol

Simple request/response over the named pipe. Each message is a length-prefixed JSON blob:

```
[4 bytes: message length (int32 via BinaryWriter, default little-endian)]
[N bytes: UTF-8 JSON payload]
```

Endianness is not a concern — this is local-only IPC on the same machine, same binary. `BinaryReader`/`BinaryWriter` with default little-endian is sufficient. No need for network byte order or `BinaryPrimitives`.

Request:
```json
{
  "id": "unique-request-id",
  "tool": "roslyn_get_member_body",
  "args": { "symbolName": "GetCompilation", "projectPath": "..." }
}
```

Response:
```json
{
  "id": "unique-request-id",
  "result": { ... },
  "elapsed_ms": 42,
  "estimated_tokens": 350
}
```

No need for JSON-RPC framing — this is internal IPC, not a public protocol. Keep it minimal.

---

## What This Unlocks

1. **Parallel subagents share one workspace** — the #85 problem, solved
2. **LogViewer connects to the same service** — real-time log streaming without polling a file
3. **Future HTTP transport (#13)** — the service already speaks request/response; adding an HTTP listener is incremental
4. **Pre-warmed workspaces** — service can stay alive between sessions if configured
5. **Cross-agent coordination** — service knows which files are being edited by which agent (future: conflict detection)

---

## Migration Path

1. **Phase 0 (current):** Each MCP process owns its workspace. Ship MVP this way.
2. **Phase 1:** Extract tool logic into a `WorkspaceService` class (in-process refactor, no IPC yet). All tools call the service instead of accessing workspace directly.
3. **Phase 2:** Add named pipe server to `WorkspaceService`. MCP adapter detects service and forwards calls. Falls back to in-process if service unavailable (backwards compatible).
4. **Phase 3:** LogViewer connects to service pipe instead of tailing log file.

Each phase ships independently. Phase 1 is pure refactoring with no user-visible change — good for a PR. Phase 2 is the big win. Phase 3 is polish.

---

## Read-Only / Read-Write Mode

Optional copy-on-write semantics for workspace access:

```
Main Agent (read-write) ── owns the mutable workspace, exclusive writes
Sub-Agent A (read-only) ──┐
Sub-Agent B (read-only) ──┼── share an immutable snapshot, full parallel reads
Sub-Agent C (read-only) ──┘
```

**How it works:**
- Each adapter registers with the service as `read-only` or `read-write` (via `roslyn_configure(mode: "read-only")` tool call or `ROSLYNMCP_MODE=readonly` env var)
- At most **one** read-write connection per solution — the service enforces this
- Read-only adapters share the same immutable `Solution` snapshot — no locks needed, fully parallel
- Write attempts from read-only adapters return an actionable error: `"Workspace is read-only. Return your proposed edits to the orchestrating agent."`
- The agent knows what to do with that error — it becomes a natural producer/consumer pattern

**Connection tracking:**
- Service tracks each adapter's PID, connection time, and mode
- Stale connection detection: if a PID dies without clean disconnect (poll `Process.GetProcessById`), reclaim the slot
- When the read-write adapter disconnects: read-only adapters continue working; no automatic promotion (too risky). A new read-write connection can be established by the next agent that requests it.
- PID tracking also enables future "which agent is editing which file" conflict detection

**Resource cost:** Modest. Read-only adapters skip FSW, skip compilation cache invalidation, and could share a frozen `Compilation` reference via the service (no serialization overhead for reads).

---

## Open Questions

- Should the service support multiple solutions simultaneously, or strictly one per process?
- How do we handle `roslyn_respawn` — restart just the adapter, or the entire service?
- Should adapters cache read-only results locally to reduce pipe round-trips?
- Windows named pipes vs Unix domain sockets — use `System.IO.Pipes` abstraction or go lower?
- What's the right grace period before service shutdown? Configurable via env var?
- How does this interact with the adhoc fallback strategy (scratchpad item)?
- Should `roslyn_configure` be a tool (agent-controlled) or strictly env var (user-controlled)?
- When read-write adapter disconnects, should pending read-only requests drain or fail immediately?

---

## Non-Goals (for this design)

- Distributed/network workspace sharing (different machines)
- Authentication/authorization between adapter and service
- Hot-reloading the service without dropping connections
- Supporting non-Roslyn tools through the same pipe
