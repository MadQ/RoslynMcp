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
	private readonly SortedDictionary<long, HashSet<string>> accessBuckets = new();
	private readonly object syncRoot = new();
	
	private const int              MaxEntries = 50;
	private static readonly TimeSpan Ttl      = TimeSpan.FromSeconds(60);
	
	public string Store<T>(T[] items)
	{
		var token = Guid.NewGuid().ToString("N")[..12];
		var now   = DateTime.UtcNow;
		
		lock(syncRoot) {
			EvictExpired();
			
			if(cache.TryGetValue(token, out _))
				RemoveToken(token);
			
			while(cache.Count >= MaxEntries) {
				var oldest = FindOldestToken();
				if(oldest is null)
					break;
				
				RemoveToken(oldest);
			}
			
			cache[token] = new CachedResult(items, now);
			AddAccessBucket(token, now);
		}
		
		return token;
	}
	
	public bool TryGet<T>(string token, out ReadOnlyMemory<T> items)
	{
		lock(syncRoot) {
			var now = DateTime.UtcNow;
			
			if(cache.TryGetValue(token, out var entry)
				&& now - entry.LastAccess < Ttl
				&& entry.Items is T[] typed) {
				UpdateLastAccess(token, now);
				items = typed.AsMemory();
				return true;
			}
			
			if(cache.ContainsKey(token))
				RemoveToken(token);
			
			items = ReadOnlyMemory<T>.Empty;
			return false;
		}
	}
	
	public void InvalidateAll()
	{
		lock(syncRoot) {
			cache.Clear();
			accessBuckets.Clear();
		}
	}
	
	private void UpdateLastAccess(string token, DateTime lastAccess)
	{
		if(!cache.TryGetValue(token, out var entry))
			return;
		
		RemoveBucketEntry(token, entry.LastAccess);
		cache[token] = entry with { LastAccess = lastAccess };
		AddAccessBucket(token, lastAccess);
	}
	
	private void AddAccessBucket(string token, DateTime lastAccess)
	{
		var bucketKey = lastAccess.Ticks;
		if(!accessBuckets.TryGetValue(bucketKey, out var bucket)) {
			bucket = new HashSet<string>();
			accessBuckets[bucketKey] = bucket;
		}
		
		bucket.Add(token);
	}
	
	private void RemoveBucketEntry(string token, DateTime lastAccess)
	{
		var bucketKey = lastAccess.Ticks;
		if(!accessBuckets.TryGetValue(bucketKey, out var bucket))
			return;
		
		bucket.Remove(token);
		if(bucket.Count == 0)
			accessBuckets.Remove(bucketKey);
	}
	
	private string? FindOldestToken()
	{
		foreach(var bucket in accessBuckets) {
			foreach(var token in bucket.Value) {
				if(cache.ContainsKey(token))
					return token;
			}
		}
		
		return null;
	}
	
	private void RemoveToken(string token)
	{
		if(!cache.TryGetValue(token, out var entry))
			return;
		
		RemoveBucketEntry(token, entry.LastAccess);
		cache.Remove(token);
	}
	
	private void EvictExpired()
	{
		var now = DateTime.UtcNow;
		var expired = new List<long>();
		
		foreach(var bucket in accessBuckets) {
			if(now - new DateTime(bucket.Key) >= Ttl)
				expired.Add(bucket.Key);
		}
		
		foreach(var bucketKey in expired) {
			if(!accessBuckets.TryGetValue(bucketKey, out var bucket))
				continue;
			
			foreach(var token in bucket.ToArray()) {
				if(cache.TryGetValue(token, out var entry) && now - entry.LastAccess >= Ttl)
					RemoveToken(token);
			}
			
			if(accessBuckets.TryGetValue(bucketKey, out var remaining) && remaining.Count == 0)
				accessBuckets.Remove(bucketKey);
		}
	}
}
