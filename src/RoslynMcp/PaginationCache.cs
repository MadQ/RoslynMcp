using System.Diagnostics.CodeAnalysis;
using RoslynMcp.Tools;

namespace RoslynMcp;

/// <summary>
///     Caches paginated query results keyed by id. A page token (<see cref="PageCursor"/>) names
///     an entry and a position in it, so an agent that passes the token back gets the next page
///     without the query being re-executed. Sliding-window TTL resets on each access so active
///     paging sessions stay alive.
///     See docs/plans/pagination-cache.md for design.
/// </summary>
internal sealed class PaginationCache
{
	// Shape is the tool's own page-to-result function, stored with the items so every later page
	// is built by the same code as the first — a Func<PaginatedResult<T>, object> for the T[] in Items.
	record CachedResult(Array Items, Delegate Shape, DateTime LastAccess);
	
	private readonly Dictionary<string, CachedResult> cache = new();
	private readonly object syncRoot = new();
	
	private const int              MaxEntries = 50;
	private static readonly TimeSpan Ttl      = TimeSpan.FromSeconds(60);
	
	/// <summary>Stores a full result set with its shape function and returns the entry's id.</summary>
	public string Store<T>(T[] items, Func<PaginatedResult<T>, object> shape)
	{
		var id = Guid.NewGuid().ToString("N")[..12];
		
		lock(syncRoot) {
			
			EvictExpired();
			
			while(cache.Count >= MaxEntries) {
				// O(n) scan is fine — MaxEntries is 50, so this is at most 50 comparisons.
				var oldest = cache.OrderBy(kvp => kvp.Value.LastAccess).First()
				;
				
				cache.Remove(oldest.Key);
			}
			
			cache[id] = new CachedResult(items, shape, DateTime.UtcNow);
		}
		
		return id;
	}
	
	/// <summary>
	///     Looks up an entry by id. False when it is unknown, expired, or holds a different element type;
	///     a mismatch leaves the entry it belongs to untouched. The element type is the only check: an
	///     id presented to another tool with the same element type still hits, and is served with the
	///     shape of the tool that stored it (#316).
	/// </summary>
	public bool TryGet<T>(
		string id,
		out ReadOnlyMemory<T> items,
		[NotNullWhen(true)] out Func<PaginatedResult<T>, object>? shape)
	{
		lock(syncRoot) {
			
			if(cache.TryGetValue(id, out var entry)
				&& DateTime.UtcNow - entry.LastAccess < Ttl
				&& entry.Items is T[] typed
				&& entry.Shape is Func<PaginatedResult<T>, object> typedShape) {
				
				// Sliding window: reset TTL on each access.
				cache[id] = entry with { LastAccess = DateTime.UtcNow }
				;
				
				items = typed.AsMemory();
				shape = typedShape;
				
				return true;
			}
			
			items = ReadOnlyMemory<T>.Empty;
			shape = null;
			
			return false;
		}
	}
	
	public void InvalidateAll()
	{
		lock(syncRoot)
			cache.Clear();
	}
	
	void EvictExpired()
	{
		var now     = DateTime.UtcNow;
		var expired = cache
			.Where(kvp => now - kvp.Value.LastAccess >= Ttl)
			.Select(kvp => kvp.Key)
			.ToArray()
		;
		
		foreach(var key in expired)
			cache.Remove(key);
	}
}

/// <summary>
///     A page token: which cached result set (<paramref name="Id"/>), where the next page starts
///     (<paramref name="Skip"/>), and how large it is (<paramref name="Take"/>). Serialized as
///     <c>{id}.{skip}.{take}</c>. Carrying the position is what lets an agent pass the token alone and
///     receive the next page (#305) — an id-only token named a result set, so a token-only request
///     restarted at page 1. The position also survives a cache miss: the query is re-run and served from
///     <paramref name="Skip"/> rather than from the top.
/// </summary>
internal readonly record struct PageCursor(string Id, int Skip, int Take)
{
	public override string ToString() => $"{Id}.{Skip}.{Take}";
	
	/// <summary>Parses a token; false for null, empty, or anything not in the three-part form.</summary>
	public static bool TryParse(string? token, out PageCursor cursor)
	{
		cursor = default;
		
		if(string.IsNullOrEmpty(token))
			
			return false;
		
		var parts = token.Split('.');
		
		if(parts.Length != 3
			|| parts[0].Length == 0
			|| !int.TryParse(parts[1], out var skip) || skip < 0
			|| !int.TryParse(parts[2], out var take) || take < 1)
			
			return false;
		
		cursor = new PageCursor(parts[0], skip, take);
		
		return true;
	}
}
