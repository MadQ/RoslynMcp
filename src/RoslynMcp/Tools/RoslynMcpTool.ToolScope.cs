using System.Diagnostics;
using System.Text.Json;
using RoslynMcp;

namespace RoslynMcp.Tools;

internal abstract partial class RoslynMcpTool
{
	/// <summary>
	///     Disposable scope that logs a tool invocation with elapsed time on dispose.
	///     Obtained via <see cref="BeginTool"/>.
	/// </summary>
	protected sealed class ToolScope : IDisposable
	{
		readonly string          name;
		readonly string?         subject;
		readonly FileLogger      log;
		readonly Stopwatch       sw              = Stopwatch.StartNew();
		readonly Action          onDispose;
		readonly PaginationCache paginationCache;
		
		bool    failed;
		string? detail;
		string? responsePeek;
		bool    isMSBuild = true;  // Default MSBuild (95% case); SetWorkspaceMode overrides.
		string? cacheTag;          // null = not paginated, "HIT" or "MISS"
		int     estimatedTokens;
		string? args;              // Serialized input args; included in log only on failure.
		
		internal ToolScope(string name, string? subject, FileLogger log, Action onDispose, PaginationCache paginationCache)
		{
			this.name            = name;
			this.subject         = subject;
			this.log             = log;
			this.onDispose       = onDispose;
			this.paginationCache = paginationCache;
		}
		
		/// <summary>Records the workspace mode so the log line can show MSB/ADH.</summary>
		internal void SetWorkspaceMode(bool isMSBuild) => this.isMSBuild = isMSBuild;
		
		/// <summary>Records whether a pagination cache hit or miss occurred.</summary>
		internal void SetCacheTag(bool hit) => cacheTag = hit ? "HIT" : "MISS";
		
		/// <summary>
		///     Checks the pagination cache for <paramref name="pageToken"/> and, on a hit, writes
		///     the page slice to <paramref name="result"/> and returns <see langword="true"/>.
		///     Always clamps <paramref name="take"/> to <paramref name="maxTake"/>.
		/// </summary>
		public bool TryServeCachedPage<T>(string? pageToken, ref int skip, ref int take, int maxTake, out object? result)
		{
			take = Math.Clamp(take, 1, maxTake);
			
			if(pageToken is null || !paginationCache.TryGet<T>(pageToken, out var cached)) {
				result = null;
				return false;
			}
			
			SetCacheTag(hit: true);
			
			skip = Math.Clamp(skip, 0, cached.Length);
			
			var page = cached.Slice(skip, Math.Min(take, cached.Length - skip)).ToArray();
			
			result = new CachedPageResult<T>(
				Items:     page,
				Total:     cached.Length,
				Skip:      skip,
				Take:      take,
				PageToken: pageToken!,
				HasMore:   skip + page.Length < cached.Length
			);
			
			return true;
		}
		
		/// <summary>
		///     Records key input arguments for failure diagnosis.
		///     Serialized to compact JSON; included in the log entry only when the tool fails.
		///     Truncate large string values before passing to avoid bloating the log.
		/// </summary>
		public void SetArgs<T>(T argsObj)
		{
			try {
				args = JsonSerializer.Serialize(argsObj, RoslynMcpJson.Compact);
			}
			catch {
				args = null;
			}
		}
		
		/// <summary>Records a success detail appended to the log line on dispose.</summary>
		public void Outcome(string detail) => this.detail = detail;
		
		/// <summary>Records a success detail and returns <paramref name="returnValue"/> for fluent use in return statements.</summary>
		public T Outcome<T>(string detail, T returnValue)
		{
			this.detail     = detail;
			(estimatedTokens, responsePeek) = SerializeResponse(returnValue);
			
			return returnValue;
		}
		
		/// <summary>Marks the invocation as failed with a reason appended to the log line on dispose.</summary>
		public void Failed(string reason) { failed = true; detail = reason; }
		
		/// <summary>Marks the invocation as failed, estimates tokens, and returns the error result for fluent use.</summary>
		public T Error<T>(T returnValue) where T : ToolErrorResult
		{
			failed                          = true;
			detail                          = returnValue.Error;
			(estimatedTokens, responsePeek) = SerializeResponse(returnValue);
			
			return returnValue;
		}
		
		/// <summary>Marks the invocation as failed and returns <paramref name="returnValue"/> for fluent use in return statements.</summary>
		public T Failed<T>(string reason, T returnValue)
		{
			failed                          = true;
			detail                          = reason;
			(estimatedTokens, responsePeek) = SerializeResponse(returnValue);
			
			return returnValue;
		}
		
		/// <summary>Appends a neutral annotation without changing the outcome.</summary>
		public void Record(string note) => detail = detail is null ? note : $"{detail}; {note}";
		
		public void Dispose()
		{
			log.LogTool(name, sw.ElapsedMilliseconds, !failed, subject, detail, isMSBuild, cacheTag, estimatedTokens, responsePeek, args);
			
			onDispose();
		}
		
		
		/// <summary>
		///     Serializes the response to JSON, returns the token estimate and a truncated peek string.
		///     The peek is capped at 600 chars — enough for the log viewer to show meaningful content.
		/// </summary>
		static (int tokens, string? peek) SerializeResponse<T>(T value)
		{
			try {
				var json   = JsonSerializer.Serialize(value, RoslynMcpJson.Compact);
				var tokens = json.Length / 4;
				var peek   = json.Length <= 600 ? json : json[..600] + "…";
				
				return (tokens, peek);
			}
			catch {
				return (0, null);
			}
		}
	}
}
