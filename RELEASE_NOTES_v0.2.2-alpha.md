# Release v0.2.2-alpha

## Bug Fixes

Fixes #3 — two critical issues causing excessive token usage and response size limits:

### 1. Unicode Escaping Fixed

**Problem:** JSON responses were escaping printable ASCII characters like `'`, `>`, `<`, and newlines as `\uXXXX` sequences (e.g., `\u0027`, `\u003e`, `\u000a`). This inflated response size by up to 5x for symbol signatures and doc comments.

**Fix:** Added `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` to the MCP SDK serializer options. Printable ASCII now emits as-is, drastically reducing response size.

**Example:**
- **Before:** `"signature":"public void .ctor(string rootPath)\u000a        Never returns null after construction."`
- **After:** `"signature":"public void .ctor(string rootPath)\n        Never returns null after construction."`

### 2. Pagination Added to 5 Unbounded Tools

**Problem:** Several tools returned all results in a single response with no way to page through large result sets, causing responses to exceed the model's context window.

**Fix:** Added `skip` and `take` parameters to the following tools:

| Tool | Default | Max | What's Paged |
|------|---------|-----|--------------|
| `get_file_outline` | 20 | 100 | Types per file |
| `find_references` | 50 | 200 | Reference locations |
| `find_implementations` | 50 | 200 | Implementations/overrides |
| `get_type_members` | 50 | 200 | Members per type |
| `get_type_hierarchy` | 50 | 200 | Interfaces + derived types |

All responses now include `total_*`, `skip`, and `take` fields for navigation.

**Example:**
```json
{
  "total_members": 18,
  "skip": 0,
  "take": 50,
  "members": [...]
}
```

## What Changed

- `src/RoslynMcp/Program.cs` — added `UnsafeRelaxedJsonEscaping` to `WithToolsFromAssembly()`
- 5 tool files — added `skip`/`take` parameters with defaults and validation

## Upgrade from v0.2.1-alpha

Drop-in replacement — same configuration, same API, same 23 tools. All tools remain backward compatible (skip/take default to sensible values).

## Files

- `RoslynMcp-v0.2.2-alpha-net10.0-win-x64.zip` — .NET 10 build (recommended)
- `RoslynMcp-v0.2.2-alpha-net8.0-win-x64.zip` — .NET 8 LTS build
