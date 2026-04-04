namespace RoslynMcp;

/// <summary>
///     Caches paginated query results keyed by token. Agents pass the token back
///     to get subsequent pages without re-executing the query. Sliding-window TTL
///     resets on each access so active paging sessions stay alive.
///     See docs/plans/pagination-cache.md for design.
/// </summary>
internal sealed class PaginationCache
{
	record CachedResult(Array Items, DateTime LastAccess);
	
	private readonly Dictionary<string, CachedResult> cache = new();
	private readonly object syncRoot = new();
	
	private const int              MaxEntries = 50;
	private static readonly TimeSpan Ttl      = TimeSpan.FromSeconds(60);
	
	public string Store<T>(T[] items)
	{
		var token = Guid.NewGuid().ToString("N")[..12];
		
		lock(syncRoot) {
			
			EvictExpired();
			
			while(cache.Count >= MaxEntries) {
				var oldest = cache.OrderBy(kvp => kvp.Value.LastAccess).First();
				
				cache.Remove(oldest.Key);
			}
			
			cache[token] = new CachedResult(items, DateTime.UtcNow);
		}
		
		return token;
	}
	
	public bool TryGet<T>(string token, out ReadOnlyMemory<T> items)
	{
		lock(syncRoot) {
			
			if(cache.TryGetValue(token, out var entry)
				&& DateTime.UtcNow - entry.LastAccess < Ttl
				&& entry.Items is T[] typed) {
				
				// Sliding window: reset TTL on each access.
				cache[token] = entry with { LastAccess = DateTime.UtcNow };
				
				items = typed.AsMemory();
				
				return true;
			}
			
			items = ReadOnlyMemory<T>.Empty;
			
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
