# Development host

Development only (#426). Nothing here is in the `MadQ.RoslynMcp` tool package: `dotnet tool install` gives the plain server. The host runs on a developer's machine once it is installed there (#464).

## Why use it

Working on RoslynMcp means dogfooding it: the agent helping with a change uses the server to read, search and edit the repository. The server it uses is a dotnet tool, and on Windows a dotnet tool cannot be replaced while a session runs it. Without the host, trying a change in the live server means ending every session, reinstalling the tool and starting the sessions again, and the agent loses its context each time.

With the host, a change is live in the running sessions a few seconds after one command:

- Server builds are published side by side, each into a directory of its own. Nothing running is overwritten.
- The MCP client launches the host (`src/RoslynMcp.DevHost`) instead of the server. The host starts the build a pointer file names and forwards stdio.
- When the pointer is written, every running host puts the new build in place of its server, without the client noticing more than a `tools/list_changed` notification.

## Setting it up

Once per machine, and again only when the host itself changes:

```powershell
.\scripts\Publish-DevServer.ps1 -InstallHost
```

This installs the host into `%LOCALAPPDATA%\RoslynMcp\dev\host`, publishes a first server build and prints the host's path. The host is installed outside the repository, so a solution build never finds its own output in use. Installing it again needs the sessions that run it to end, since its files are then in use.

### Clients that read the repository's config

The committed `.github/mcp.json`, which Copilot CLI reads in every checkout and worktree, does not name the host: it runs the launcher `scripts/Start-RoslynMcp.ps1`, which picks the server.

| Installed | What the launcher starts |
|---|---|
| The host, with a published build | The host |
| Not the host, or no build published yet | The installed tool, `madq-roslynmcp` |
| Neither | Nothing: an error on stderr says what to install |

So a contributor without the host gets the plain server as before, and installing the host is all it takes to switch. Set `ROSLYNMCP_USE_DEVHOST=0` in the environment the client starts servers with to run the installed tool even when the host is there, e.g. to check whether a problem also happens without the host.

The launcher passes every argument on and writes nothing to stdout. The server it starts inherits the standard handles, so the protocol never passes through PowerShell. It needs PowerShell 7 (`pwsh`) on `PATH`, as the repository's other scripts do, and costs a few hundred milliseconds of PowerShell startup per session.

### Clients configured elsewhere

A client that is configured outside the repository (Claude Code's `~/.claude.json`, VS Code's user `mcp.json`, Copilot CLI outside this repository) is pointed at the host by hand. Set `command` to the host's path and keep the server's arguments:

```json
{
  "MadQ.RoslynMcp": {
    "type": "stdio",
    "command": "C:\\Users\\<you>\\AppData\\Local\\RoslynMcp\\dev\\host\\RoslynMcp.DevHost.exe",
    "args": ["--root", "."]
  }
}
```

There is no fallback there: until the host is installed and a build published, such a client has no server. Neither script edits a client configuration, but `madq-roslynmcp setup` and `madq-roslynmcp update` do: they point a configured client back at the tool (asking first when the entry is not one they recognize — decline it for the host's). Their startup warning that a config "points to" another path is expected under the host, and is not a reason to run `update`.

## Day to day

| Command | What it does |
|---|---|
| `.\scripts\Publish-DevServer.ps1` | Publishes the working tree as a new build and writes the pointer. Every running host swaps to it. |
| `.\scripts\Publish-DevServer.ps1 -Restart` | Writes the pointer again as it is. Every running host starts the same build afresh: `roslyn_respawn` for all sessions at once. |

`roslyn_info` shows the pid and build hash of the server a session has now.

## What a swap does and does not do

- **It waits for an idle moment.** A host swaps only while no request is waiting for an answer, from the client or from the server (an elicitation). It does not hold new requests back to get there, and it never cuts a call off. A session whose calls overlap without a gap keeps its server until it pauses.
- **A build that does not run costs nothing.** The new server is started and has answered the client's own `initialize`, replayed, before the old one is stopped. A build that cannot be started, exits, does not answer within 30 s, or answers with another protocol version is refused: the session keeps its server, and the host does not try that pointer again until it is written again. The reason is in the host's log and on stderr.
- **The server's state is gone.** Preview tokens and session approvals do not survive (an apply with an old token is answered as an unknown token), and the workspace loads again at the next call. Page tokens survive, since a token whose entry is gone re-runs the query.
- **The server's own exit is the host's.** After `roslyn_respawn` or a crash the host exits with the server's exit code, and the client restarts it as it would the server. One exception: a server that exits before answering the first `initialize` is replaced once by the last build that did answer (`last-good`), so a broken build does not fail every new session.

## Files

Under `%LOCALAPPDATA%\RoslynMcp\dev`, or the directory `ROSLYNMCP_DEV_HOME` names:

| Path | What |
|---|---|
| `builds\<time>-<sha>[-dirty]\` | One published server build each. The script keeps the newest five, the ones `current` and `last-good` name, and any a server still runs from. |
| `current` | The pointer: a directory name under `builds`, or a full path. Its content and its write time both count, so writing the same value is a restart request. Replace it by renaming a file over it, never by writing in place. |
| `last-good` | The build that last answered a handshake. Written by the hosts. |
| `host\` | The installed host. |
| `logs\devhost.<pid>.log` | One per host process: the server it started, every swap, every refusal. Removed after 30 days. |

## How the host forwards

Messages are forwarded as the bytes they are, one line at a time. The host reads only the top-level `id` and `method` of each, to know who is waiting for an answer, and it keeps the client's `initialize` request and `notifications/initialized` to replay them to a new server. A `notifications/cancelled` takes its request off the waiting list, since a cancelled request may never be answered. The only message the host writes of its own is `notifications/tools/list_changed` after a swap.

When something looks like a server bug, check whether it also happens without the host (`ROSLYNMCP_USE_DEVHOST=0` for a client that runs the launcher); the host's log says when it swapped.

## Tests

Harness group *Dev Host* runs a real host over the server the harness built. Every "build" there is that one build under another pointer value.
