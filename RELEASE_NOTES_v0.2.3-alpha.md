# Release v0.2.3-alpha

## Improvements

### JsonSerializerOptions Enhancement

**Change:** Improved JSON serialization configuration for better compatibility and explicit configuration.

**What Changed:**
- Switched to `JsonSerializerDefaults.Web` as the base configuration for web-appropriate defaults
- Explicitly set `TypeInfoResolver` to ensure proper serialization
- Explicitly disabled `WriteIndented` for compact responses
- Maintains `UnsafeRelaxedJsonEscaping` from v0.2.2-alpha (printable ASCII as-is instead of `\uXXXX` sequences)

**Benefits:**
- More robust JSON serialization with industry-standard web defaults
- Explicit configuration prevents future serialization issues
- Improved compatibility with various MCP clients

**Code:**
```csharp
.WithToolsFromAssembly(serializerOptions: new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    Encoder          = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
  , TypeInfoResolver = JsonSerializerOptions.Default.TypeInfoResolver
  , WriteIndented    = false
})
```

## Testing

Added `test_mcp_manual.ps1` - manual MCP server testing script for quick verification outside of the test harness.

## What Changed

- `src/RoslynMcp/Program.cs` — improved JsonSerializerOptions configuration
- `test_mcp_manual.ps1` — new manual testing script (added)
- `docs/sessions/HANDOFF.md` — reconstructed session state documentation (added)
- `docs/ScratchPad.md` — development notes and planning (added)
- `docs/github-issues/*.md` — planning documents for future features (added)

## Upgrade from v0.2.2-alpha

Drop-in replacement — same configuration, same API, same 24 tools. All tools remain backward compatible.

## Files

- `RoslynMcp-v0.2.3-alpha-net10.0-win-x64.zip` — .NET 10 build (recommended)
- `RoslynMcp-v0.2.3-alpha-net8.0-win-x64.zip` — .NET 8 LTS build

## Installation

See [INSTALLATION.md](INSTALLATION.md) for setup instructions.

## Version History

- **v0.2.3-alpha** — JsonSerializerOptions improvements
- **v0.2.2-alpha** — Unicode escaping fix + pagination (issue #3)
- **v0.2.1-alpha** — BuildHost DLL exclusion fix (issue #4)
- **v0.2.0-alpha** — Initial public alpha release (24 tools)
