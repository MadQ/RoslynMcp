# Token-Based Pagination Cache

**Issues:** [#40](https://github.com/MadQ/RoslynMcp/issues/40) (cache), [#305](https://github.com/MadQ/RoslynMcp/issues/305) (token as cursor), [#316](https://github.com/MadQ/RoslynMcp/issues/316) (query binding)
**Status:** Implemented (PR #55; cursor semantics in #305)

## Problem

Every paginated tool re-executes its full query on each call. An agent paging through 200 references makes 4 identical `FindReferencesAsync` calls, discarding progressively more results each time.

## Design: the token is a cursor

A response that has more pages returns a `page_token`. The token names the cached result set **and** the next position and page size, so passing it back alone returns the next page.

**First call** (no `page_token`):
```
Agent:  roslyn_find_references(symbolName: "Foo", projectPath: "...", take: 50)
Server: { references: [...50], total_references: 120, skip: 0, take: 50, page_token: "3f9a1c0b7d2e.50.50", has_more: true }
```

**Next page** (token only — no `skip`, no `take`):
```
Agent:  roslyn_find_references(symbolName: "Foo", projectPath: "...", page_token: "3f9a1c0b7d2e.50.50")
Server: { references: [...50], total_references: 120, skip: 50, take: 50, page_token: "3f9a1c0b7d2e.100.50", has_more: true }
```

**Last page:**
```
Agent:  roslyn_find_references(symbolName: "Foo", projectPath: "...", page_token: "3f9a1c0b7d2e.100.50")
Server: { references: [...20], total_references: 120, skip: 100, take: 50, page_token: null, has_more: false }
```

Rules:

- **Every page has the tool's own shape.** The tool supplies a shape function with the result set; the cache keeps it and builds later pages with it, so `references`, `caution`, `hint`, and the rest are present on every page.
- **Each response carries the token for the page after it**, and none when `has_more` is false. A token is never reused for a different page, so following tokens cannot loop.
- **A result set that fits one page is not cached** and returns no token.
- **An explicit `skip` or `take` wins.** A non-zero `skip`, or a `take` other than the tool's default, overrides the token's value. A value equal to the default cannot be told apart from an omitted argument, so it reads as omitted.
- **A token outlives its cache entry.** When the entry has expired or an edit cleared the cache, the tool re-runs the query from the token's position — the caller gets the page it asked for, not page 1 and not an error. The original arguments must therefore be passed along with the token.
- **Cached results belong to the issuing tool and query.** The entry stores a SHA-256 identity of the tool name, subject and complete query arguments, excluding `skip`, `take` and `page_token`. Changing the tool, project, primary query or any filter treats the token as a miss: the current query runs from the cursor position and page size, just as after expiry. A mismatch neither removes nor refreshes the original entry. Identity uses bound argument values, not semantic equivalence: alternate path spellings or explicitly supplied defaults that differ from their bound omitted values can cause a harmless re-query.
- **The default-value ambiguity remains intentional.** Explicit `take` equal to the tool's default cannot override the cursor size (and `skip: 0` cannot reset the cursor). Use no token to restart with those values. Distinguishing omission would require nullable paging parameters across the public tool signatures; #316 does not change those signatures.
- **An unreadable token is treated as no token.**

### History (#305)

The first design made the token only a cache key: the position still came from `skip`, and the same token was returned on every page. An agent that followed the documented "pass `page_token` to get the next page" received page 1 again, indefinitely. Cached pages also came back in a generic `{ items, total }` shape that dropped the tool's own fields. Both are fixed by the rules above.

### Components

**`PaginationCache`** — DI singleton:
- `Store<T>(T[] items, Func<PaginatedResult<T>, object> shape, string? queryKey)` → returns the entry id (the cache owns the array from this point)
- `TryGet<T>(string id, string? queryKey, out ReadOnlyMemory<T> items, out shape)` → matching query's cached results as `ReadOnlyMemory<T>`, plus the shape function; a missing identity never hits
- `InvalidateAll()` → clears on workspace changes
- Sliding-window TTL (60s, reset on each access so active paging stays alive)
- Capped at 50 entries; the least recently used is evicted

**`PageCursor`** — the token: `{id}.{skip}.{take}`.

**`RoslynMcpTool` base class helpers:**
- `scope.TryServeCachedPage<T>(pageToken, ref skip, ref take, defaultTake, maxTake, out result)` → applies the cursor to `skip`/`take`, then serves the page from the cache when the entry is still there
- `PaginateAndStore<T>(allResults, ref skip, take, shape)` → cache-miss path; stores the results when more pages remain and returns a `PaginatedResult<T>` with the next token
- `PageTokenDescription` → the one description every `page_token` parameter uses

**The shape function must not capture Roslyn objects.** It lives in the cache for up to the TTL; a captured `Compilation`, `Solution`, or symbol would stay alive with it. Each tool builds it with a `static` factory that takes plain values (names, the caution string).

**Paginated tools must record complete, untruncated query arguments in `BeginTool`/`SetArgs`.** The scope combines these with the tool name and subject to identify the query. If argument serialization fails, cache hits are disabled and the log records why.

**Invalidation:**
- `WorkspaceResolver.InvalidateFile` and the workspace write paths call `PaginationCache.InvalidateAll()`
- Sliding TTL provides natural expiry for FSW-triggered changes

### Memory Safety

Cached arrays are returned as `ReadOnlyMemory<T>` — callers can slice but not mutate. The array is owned by the cache from the moment `Store` is called. Each page is a fresh `T[]` copy, so the cached data is never exposed directly.

### Wired Tools

`roslyn_find_references`, `roslyn_find_callers`, `roslyn_find_implementations`, `roslyn_find_unused`, `roslyn_get_call_graph`, `roslyn_get_type_hierarchy`, `roslyn_get_type_members`, `roslyn_get_type_dependencies`, `roslyn_get_file_outline`, `roslyn_list_types`, `roslyn_get_trivia`, `roslyn_search_files`, `roslyn_semantic_search`, `roslyn_find_string_literal`, `roslyn_list_files`.

`roslyn_get_diagnostics` does not use this cache; it has its own stateless token.

### Test Coverage

`src/TestHarness/Tests/PaginationTests.cs` covers the cursor contract on temp fixtures: token-only next page at the same size, a new token per page, no token on the last page or for a single-page result, explicit `skip`/`take` overrides, the explicit-default `take` limitation, a token used after an edit cleared the cache, and an unreadable token. Changed patterns, filters and projects are compared with fresh queries at the cursor position both with and without the original cache entry; a cross-tool token sharing the same element type must retain the current tool's shape. Mismatches leave the original token usable. `NavigationTests` and `FindStringLiteralTests` each page a second tool by token alone and check the tool's own shape.
