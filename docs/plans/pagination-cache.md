# Token-Based Pagination Cache

**Issue:** [#40](https://github.com/MadQ/RoslynMcp/issues/40)
**Status:** Implemented (PR #55)

## Problem

Every paginated tool re-executes its full query on each call. An agent paging through 200 references makes 4 identical `FindReferencesAsync` calls, discarding progressively more results each time.

## Design: Token-Based Caching

Instead of parameter hashing, use explicit tokens — the same pattern agents already understand from `roslyn_preview_rename` → `roslyn_apply_rename`.

### How It Works

**First call** (no `page_token`):
```
Agent: roslyn_find_references(symbolName: "Foo", projectPath: "...", take: 50)
Server: { references: [...50], total: 200, page_token: "abc123", has_more: true }
```

**Subsequent call** (with `page_token`):
```
Agent: roslyn_find_references(page_token: "abc123", skip: 50, take: 50)
Server: { items: [...50], total: 200, page_token: "abc123", has_more: true }
```

Note: subsequent-page responses use a standardized shape (`items`/`total`/`page_token`/`has_more`). Tool-specific metadata (symbol_type, _caution, etc.) is only in the first-page response — the agent already has that context.

### Why Tokens Over Parameter Hashing

- **No ambiguity** about what constitutes "the same query" — the token IS the identity
- **No hash collisions** — each query gets a unique token
- **Agents already understand tokens** from the rename workflow
- **Simpler for agents** — pass token back instead of calculating skip values
- **Zero breaking changes** — `skip`/`take` still work without token; token is purely additive

### Components

**`PaginationCache`** — DI singleton, similar to `ApprovalStore`:
- `Store<T>(T[] items)` → returns token string (cache owns the array from this point)
- `TryGet<T>(string token, out ReadOnlyMemory<T> items)` → returns cached results as `ReadOnlyMemory<T>` to prevent mutation of cached data
- `InvalidateAll()` → clears on workspace changes
- Sliding-window TTL (60s, reset on each access so active paging stays alive)
- Capped at 50 entries, LRU eviction

**`RoslynMcpTool` base class helpers:**
- `TryServeCachedPage<T>(scope, pageToken, ref skip, take)` → one-line cache-hit path; returns standardized response or null on miss
- `PaginateAndStore<T>(allResults, ref skip, take)` → cache-miss path; stores results and returns `PaginatedResult<T>` with token
- `Paginate<T>(ReadOnlyMemory<T>, ref skip, take)` → zero-copy slice overload for cache hits

**Invalidation:**
- `WorkspaceResolver.InvalidateFile` calls `PaginationCache.InvalidateAll()`
- Sliding TTL provides natural expiry for FSW-triggered changes
- Editing tools trigger explicit invalidation via `InvalidateFile`

### Memory Safety

Cached arrays are returned as `ReadOnlyMemory<T>` — callers can slice but not mutate. The array is owned by the cache from the moment `Store` is called. `Paginate` creates a new `T[]` for the page slice via `.ToArray()`, so the cached data is never exposed directly.

### Wired Tools

| Tool | Status |
|------|--------|
| `find_references` | ✅ `page_token` parameter + cache |
| `find_implementations` | ✅ `page_token` parameter + cache |
| `type_hierarchy` | ✅ `page_token` parameter + cache |
| `type_members` | ✅ `page_token` parameter + cache |
| `file_outline` | ✅ `page_token` parameter + cache |
| `search_files` | ✅ `page_token` parameter + cache |
| `semantic_search` | ✅ `page_token` parameter + cache |
| `list_files` | Needs `skip` parameter first |
| `get_trivia` | Needs `skip` parameter first |
| `list_types` | Needs `skip`/`take` parameters first |

### Token Lifecycle

1. Generated on first query (12-char hex, same as rename tokens)
2. Valid for 60 seconds from last access (sliding window)
3. Can be reused for multiple page requests within the TTL
4. Expired tokens → cache miss → tool re-executes the query (no error)
5. Max 50 cached result sets; oldest evicted when full
6. All tokens invalidated when any file is edited via `InvalidateFile`

### Test Coverage

TestHarness includes 2 dedicated pagination tests (29/29 total):
- Page 1: small `take`, verify `page_token` + `has_more: true`
- Page 2: pass `page_token`, verify items served from cache with same token
