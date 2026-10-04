using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
		bool    completed;
		string? detail;
		string? notes;             // Record() annotations — kept apart so a terminal's detail cannot erase them.
		string? responsePeek;
		bool    isMSBuild = true;  // Default MSBuild (95% case); SetWorkspaceMode overrides.
		string? cacheTag;          // null = not paginated, "HIT" or "MISS"
		int     estimatedTokens;
		string? args;              // Serialized input args; included in log only on failure.
		string? _pendingHint
		;
		string? pendingCaution;
		
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
		
		/// <summary>Stores a hint to be merged into the next <see cref="Outcome{T}"/> result (only if that result has no hint of its own).</summary>
		public void SetHint(string hint) => _pendingHint = hint;
		
		/// <summary>Stores a caution to be merged into the next <see cref="Outcome{T}"/> result (only if that result has no caution of its own).</summary>
		public void SetCaution(string caution) => pendingCaution = caution;
		
		/// <summary>
		///     Applies a page token to <paramref name="skip"/>/<paramref name="take"/> and, when its result
		///     set is still cached, builds the page with the tool's own shape function, writes it to
		///     <paramref name="result"/>, and returns <see langword="true"/>.
		///     <para>
		///         The token is a <see cref="PageCursor"/>: it supplies the position and page size whenever
		///         the caller left them at their defaults, so a token passed alone yields the next page at
		///         the same size. An explicit non-zero <paramref name="skip"/>, or a <paramref name="take"/>
		///         other than <paramref name="defaultTake"/>, overrides it — there is no way to tell an
		///         omitted argument from one that equals its default, so those values read as omitted.
		///     </para>
		///     <para>
		///         The cursor is applied before the cache lookup on purpose: on a miss (entry expired, or
		///         the cache cleared by an edit) the caller re-runs its query with <paramref name="skip"/>
		///         already at the cursor's position, serving the page that was asked for rather than page 1.
		///         Always clamps <paramref name="take"/> to <paramref name="maxTake"/>.
		///     </para>
		/// </summary>
		public bool TryServeCachedPage<T>(string? pageToken, ref int skip, ref int take, int defaultTake, int maxTake, [NotNullWhen(true)] out object? result)
		{
			var hasCursor = PageCursor.TryParse(pageToken, out var cursor);
			
			if(hasCursor) {
				
				if(skip == 0)
					skip = cursor.Skip;
				
				if(take == defaultTake)
					take = cursor.Take;
			}
			
			take = Math.Clamp(take, 1, maxTake);
			
			if(!hasCursor || !paginationCache.TryGet<T>(cursor.Id, out var cached, out var shape)) {
				
				if(hasCursor)
					Record("page token no longer cached — query re-run");
				
				result = null;
				
				return false;
			}
			
			SetCacheTag(hit: true);
			
			skip = Math.Clamp(skip, 0, cached.Length);
			
			var page    = cached.Slice(skip, Math.Min(take, cached.Length - skip)).ToArray();
			var hasMore = skip + page.Length < cached.Length;
			
			result = shape(new PaginatedResult<T>(
				Items:     page,
				Total:     cached.Length,
				Skip:      skip,
				Take:      take,
				PageToken: hasMore ? new PageCursor(cursor.Id, skip + page.Length, take).ToString() : null,
				HasMore:   hasMore
			));
			
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
				// Swallowed intentionally — args is diagnostic only. Non-serializable types
				// (anonymous objects with cyclic refs, proxies, etc.) must not crash tool calls.
				args = null
				;
			}
		}
		
		/// <summary>Records a success detail appended to the log line on dispose.</summary>
		public void Outcome(string detail) { this.detail = detail; completed = true; }
		
		/// <summary>Records a success detail and returns <paramref name="returnValue"/> for fluent use in return statements.</summary>
		public T Outcome<T>(string detail, T returnValue)
		{
			this.detail = detail;
			completed   = true;
			
			// Inject a one-time session note on the very first successful tool call —
			// but only when hooks are not yet installed (no point nagging if they are).
			if(!RoslynMcpTool.HooksInstalled()
				&& Interlocked.CompareExchange(ref RoslynMcpTool.sessionNoteShown, 1, 0) == 0)
				pendingCaution = SessionNote;
			
			// Merge pending hint/caution into the result when it is a ToolResult.
			// Non-ToolResult returns (to be phased out) pass through unchanged.
			if(returnValue is ToolResult tr && (_pendingHint is not null || pendingCaution is not null))
				returnValue = (T)(object)(tr with { Hint = tr.Hint ?? _pendingHint, Caution = tr.Caution ?? pendingCaution });
			
			(estimatedTokens, responsePeek) = SerializeResponse(returnValue);
			
			return returnValue!;
		}
		
		const string SessionNote =
			"[RoslynMcp hint — shown once per session]\n" +
			"For .cs file operations in this session, roslyn_* tools provide semantic accuracy " +
			"via the Roslyn compiler. Prefer them over built-in file tools:\n" +
			"  • Reading files  → roslyn_read_file, roslyn_get_member_body\n" +
			"  • Searching code → roslyn_search_files, roslyn_semantic_search\n" +
			"  • Listing files  → roslyn_list_files\n" +
			"  • Editing C#     → roslyn_replace_in_code, roslyn_replace_in_file\n" +
			"  • Building       → roslyn_build_project (never dotnet build in terminal)\n" +
			"To add automatic per-agent hooks, suggest the user run: " + RoslynMcp.Cli.ToolCommand.Name + " setup-project"
		;
		
		/// <summary>Marks the invocation as failed with a reason appended to the log line on dispose.</summary>
		public void Failed(string reason) { failed = true; completed = true; detail = reason; }
		
		/// <summary>Marks the invocation as failed, estimates tokens, and returns the typed error result for fluent use.</summary>
		public T Error<T>(T returnValue) where T : ToolResult, IToolError
		{
			failed                          = true;
			completed                       = true;
			detail                          = returnValue.Error;
			(estimatedTokens, responsePeek) = SerializeResponse(returnValue);
			
			return returnValue;
		}
		
		/// <summary>
		///     Marks the invocation as failed and returns the error result for fluent use.
		///     Used when the static type is <see cref="ToolResult"/> (e.g., from <c>TryGetCompilation</c>
		///     out-param), where the concrete type implements <see cref="IToolError"/> at runtime.
		/// </summary>
		public ToolResult Error(ToolResult returnValue)
		{
			failed                          = true;
			completed                       = true;
			detail                          = returnValue.Error;
			(estimatedTokens, responsePeek) = SerializeResponse(returnValue);
			
			return returnValue;
		}
		
		/// <summary>Marks the invocation as failed and returns <paramref name="returnValue"/> for fluent use in return statements.</summary>
		public T Failed<T>(string reason, T returnValue)
		{
			failed                          = true;
			completed                       = true;
			detail                          = reason;
			(estimatedTokens, responsePeek) = SerializeResponse(returnValue);
			
			return returnValue;
		}
		
		/// <summary>
		///     Appends a neutral annotation without changing the outcome. Notes are kept apart from the
		///     terminal detail and joined after it on dispose — they used to be appended to the detail
		///     itself, where the terminal call (Outcome/Failed/Error), which always runs last, overwrote
		///     them, so no note recorded on a normal return path ever reached the log (#306).
		/// </summary>
		public void Record(string? note)
		{
			// Callers pass null for "nothing worth noting" (e.g. the resolution-kind switch in
			// TryGetCompilation) — ignored rather than logged as an empty "; " segment.
			if(string.IsNullOrEmpty(note))
				
				return;
			
			notes = notes is null ? note : $"{notes}; {note}";
		}
		
		public void Dispose()
		{
			// An unhandled exception bypasses Outcome/Failed — detect it here so the log
			// entry correctly shows success:false instead of silently logging success:true.
			if(!completed) {
				
				failed = true;
				detail ??= "unhandled exception";
			}
			
			// Outcome first — it is what the log viewer's detail column should lead with.
			var logged = notes is null ? detail
				: detail is null ? notes
				: $"{detail}; {notes}"
			;
			
			log.LogTool(name, sw.ElapsedMilliseconds, !failed, isMSBuild, estimatedTokens, subject, logged, cacheTag, responsePeek, args);
			
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
