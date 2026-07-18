# MadQ.RoslynMcp

**A [Model Context Protocol](https://modelcontextprotocol.io/) server that gives AI coding agents real Roslyn-powered code intelligence for C#.**

Instead of grepping text, your agent gets resolved type information, live compiler diagnostics, cross-file references, symbol resolution, and safe semantic edits — all from an in-process Roslyn workspace, with no build spawned and no leaving the process.

---

## Install

`MadQ.RoslynMcp` ships as a .NET global tool. While the project is in **beta**, pass `--prerelease`:

```bash
dotnet tool install -g MadQ.RoslynMcp --prerelease
```

This installs the `madq-roslynmcp` command on your PATH. Requires a **.NET 10** (or newer) runtime.

> Naming note: the plain `RoslynMcp` / `roslynmcp` names belong to an unrelated package, so this tool is namespaced under **`MadQ`** — package **`MadQ.RoslynMcp`**, command **`madq-roslynmcp`**.

## Configure your MCP client

Add a stdio server entry (the outer key varies by client — `"mcpServers"` for Claude, `"servers"` for Copilot):

```json
{
  "MadQ.RoslynMcp": {
    "type": "stdio",
    "command": "madq-roslynmcp"
  }
}
```

Restart your client. Full per-client setup (Claude Desktop, Claude Code, GitHub Copilot, and more) is in the [Installation Guide](https://github.com/MadQ/RoslynMcp/blob/v0.8.1-beta/INSTALLATION.md).

## What you get

Every `roslyn_*` tool takes a `projectPath`, so one running server can serve multiple projects without a restart. Highlights from the 40+ tools:

- **Semantic analysis** — diagnostics, symbol info, type members, hierarchies, dependencies, references, callers, and call graphs.
- **Precise search** — regex file search, syntax-tree-aware semantic search, and string-literal search with glob and decoded-value matching.
- **Safe edits** — semantic C# node replacement that validates syntax and preserves formatting, plus a two-phase **preview → apply** flow for renames and signature changes.
- **Crash-safe writes** — automatic pre/post-write backups with token-based local-history undo.
- **Build & project** — Roslyn-first diagnostics, `dotnet` build/clean/restore, and project metadata.

Auto-detects a `.csproj`/`.sln` for a full **MSBuildWorkspace** (NuGet resolution, multi-project) and falls back to a fast source-only **AdhocWorkspace** when none is found.

## Links

- **Repository:** https://github.com/MadQ/RoslynMcp
- **Installation guide:** https://github.com/MadQ/RoslynMcp/blob/v0.8.1-beta/INSTALLATION.md
- **Changelog:** https://github.com/MadQ/RoslynMcp/blob/v0.8.1-beta/CHANGELOG.md
- **Issues:** https://github.com/MadQ/RoslynMcp/issues

## Security

RoslynMcp runs with your user permissions and currently has broad filesystem access. Use it only with trusted agents and on projects you control. See [issue #9](https://github.com/MadQ/RoslynMcp/issues/9).

## License

MIT © Quinten Martens
