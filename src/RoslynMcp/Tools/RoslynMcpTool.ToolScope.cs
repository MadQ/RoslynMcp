using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
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
		long    deferredElapsed;   // Elapsed time at Dispose, for an entry the call filter writes later (#371).
		
		internal string? PaginationKey { get; private set; }
		
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
		
		WorkspaceChangeCounter? usedWorkspace;
		long                    usedWorkspaceAt;
		bool                    usedWorkspaceForCode;
		
		/// <summary>
		///     Records the workspace this call computes its result from, and the count of its
		///     changes from before the call took its view of it (see <c>ViewOf</c>). Called by
		///     every resolution guard, beside <see cref="SetWorkspaceMode"/>. The first guard that
		///     reads code counts, since that is where the call first looked at the code; a guard
		///     that only resolved a root before it has read none, and its count is replaced.
		/// </summary>
		internal void NoteWorkspace(WorkspaceChangeCounter changes, long at, bool readsCode)
		{
			if(usedWorkspace is not null && (usedWorkspaceForCode || !readsCode))
				
				return;
			
			usedWorkspace        = changes;
			usedWorkspaceAt      = at;
			usedWorkspaceForCode = readsCode;
		}
		
		/// <summary>
		///     Whether the workspace this call used is still as it was when the call took its view
		///     of it, for a result that is kept beyond the call (#435). Null for a call that used
		///     no workspace: its result is not tied to one. A cached result carries this, and a
		///     later page is cut from it only while it says yes: a change made through the server
		///     clears the cache, but a save in an editor reaches the workspace through the watcher
		///     and cleared nothing, so the next page came from before the change.
		/// </summary>
		internal Func<bool>? WorkspaceUnchanged
		{
			get
			{
				if(usedWorkspace is not { } changes)
					
					return null;
				
				var at = usedWorkspaceAt;
				
				return () => changes.Value == at;
			}
		}
		
		/// <summary>
		///     The <c>filePath</c> of this call when it names no document of any project in the solution
		///     the call resolved to (#376). The call keeps the solution — a tool that only reads or writes
		///     the disk needs nothing more — and a compilation asked of it then fails as "file not
		///     found", which is the actual problem, rather than as a solution given where one project
		///     was needed.
		/// </summary>
		internal string? FileInNoProject { get; set; }
		
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
			PaginationKey = CreatePaginationKey();
			
			var hasCursor = PageCursor.TryParse(pageToken, out var cursor);
			
			if(hasCursor) {
				
				if(skip == 0)
					skip = cursor.Skip;
				
				if(take == defaultTake)
					take = cursor.Take;
			}
			
			take = Math.Clamp(take, 1, maxTake);
			
			if(!hasCursor || !paginationCache.TryGet<T>(cursor.Id, PaginationKey, out var cached, out var shape)) {
				
				if(hasCursor)
					Record("page token not cached for this query — query re-run");
				
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
		///     Paginated tools must include every query argument, untruncated: together with the tool
		///     name and subject these identify the cached query. Paging controls do not identify it.
		/// </summary>
		public void SetArgs<T>(T argsObj)
		{
			try {
				args = JsonSerializer.Serialize(argsObj, RoslynMcpJson.Compact);
			}
			catch {
				// Diagnostic serialization must not crash a tool call. Without a complete identity,
				// cache hits are disabled rather than risking a page from a different query.
				args = null;
				PaginationKey = null;
				Record("arguments unavailable — pagination cache hits disabled");
			}
		}
		
		string? CreatePaginationKey()
		{
			if(args is null)
				
				return null;
			
			using var document = JsonDocument.Parse(args);
			using var buffer = new MemoryStream();
			
			using(var writer = new Utf8JsonWriter(buffer)) {
				
				writer.WriteStartArray();
				writer.WriteStringValue(name);
				writer.WriteStringValue(subject);
				writer.WriteStartObject();
				
				foreach(var property in document.RootElement.EnumerateObject())
					if(property.Name is not ("skip" or "take" or "page_token"))
						property.WriteTo(writer);
				
				writer.WriteEndObject();
				writer.WriteEndArray();
			}
			
			return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int) buffer.Length)));
		}
		
		/// <summary>Records a success detail appended to the log line on dispose.</summary>
		public void Outcome(string detail) { this.detail = detail; completed = true; }
		
		/// <summary>Records a success detail and returns <paramref name="returnValue"/> for fluent use in return statements.</summary>
		public T Outcome<T>(string detail, T returnValue)
		{
			returnValue = Complete(detail, returnValue);
			
			(estimatedTokens, responsePeek) = SerializeResponse(returnValue);
			
			return returnValue!;
		}
		
		/// <summary>
		///     Marks the call successful and merges the session note and any pending hint or caution into
		///     <paramref name="returnValue"/>. Shared by the success terminals, which differ only in how
		///     they measure the response for the log.
		/// </summary>
		T Complete<T>(string detail, T returnValue)
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
			
			return returnValue!;
		}
		
		/// <summary>
		///     Success terminal for tools that return source text: one JSON header block followed by the
		///     raw source. Inside a JSON string the same code costs about 12% more characters and reaches
		///     the model with its tabs, quotes and backslashes escaped, which an exact-match edit built
		///     from the response then has to undo (#328).
		/// </summary>
		public ContentBlock[] Outcome(string detail, ToolResult header, string source)
			=> Outcome(detail, header, [(null, source)]);
		
		/// <summary>
		///     Multi-part form of the source terminal: each part is a one-line JSON header followed by its
		///     source. A client may join text blocks with nothing between them (Claude Code does), so
		///     every block ends with a newline and each header states the line range that follows. The
		///     boundaries stay readable without the block structure, and no file content can forge one.
		/// </summary>
		public ContentBlock[] Outcome(string detail, ToolResult header, IReadOnlyList<(object? Header, string Source)> parts)
		{
			// Not the generic terminal: that one would serialize the header a second time for a peek and a
			// token estimate that are replaced below.
			header = Complete(detail, header);
			
			var texts = new List<string>(1 + parts.Count * 2) { ToJson(header) + "\n" };
			
			foreach(var (partHeader, source) in parts) {
				
				if(partHeader is not null)
					texts.Add(ToJson(partHeader) + "\n");
				
				// Appended unconditionally: source whose last line is empty already ends in a newline,
				// and treating that as the terminator would drop the line.
				texts.Add(source + "\n");
			}
			
			var length = 0;
			var peek   = new StringBuilder(PeekLength);
			
			foreach(var text in texts) {
				
				length += text.Length;
				
				if(peek.Length < PeekLength)
					peek.Append(text, 0, Math.Min(text.Length, PeekLength - peek.Length));
			}
			
			estimatedTokens = length / 4;
			responsePeek    = length <= PeekLength ? peek.ToString() : peek + "…";
			
			return [..texts.Select(text => new TextContentBlock { Text = text })];
		}
		
		// The runtime type, not the declared one: a header typed as ToolResult would otherwise
		// serialize only the base record's properties.
		static string ToJson(object value) => JsonSerializer.Serialize(value, value.GetType(), RoslynMcpJson.Compact);
		
		/// <summary>Characters of the response kept for the log viewer — enough to show meaningful content.</summary>
		const int PeekLength = 600;
		

		const string SessionNote =
			"[RoslynMcp hint — shown once per session]\n" +
			"For .cs file operations in this session, roslyn_* tools provide semantic accuracy " +
			"via the Roslyn compiler. Prefer them over built-in file tools:\n" +
			"  • Reading files  → roslyn_read_file, roslyn_get_member_body\n" +
			"  • Searching code → roslyn_search_files, roslyn_semantic_search\n" +
			"  • Listing files  → roslyn_list_files\n" +
			"  • Editing C#     → roslyn_replace_in_code, roslyn_replace_in_file\n" +
			"  • Refactoring    → roslyn_list_refactorings, roslyn_preview_refactoring, roslyn_apply_refactoring (move type to file, extract method, …) instead of retyping code\n" +
			"  • Fixing a diagnostic → roslyn_preview_code_fix, roslyn_apply_code_fix (Roslyn's own fixers)\n" +
			"  • Diagnostics    → roslyn_get_diagnostics (includeAnalyzers + severity hidden for IDE0xxx)\n" +
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
			var elapsed = sw.ElapsedMilliseconds;
			
			// An unhandled exception bypasses Outcome/Failed — detect it here so the log
			// entry correctly shows success:false instead of silently logging success:true.
			if(!completed) {
				
				failed = true;
				detail ??= "unhandled exception";
				
				// The exception is unwinding through this using, but a scope cannot see it. Under the
				// call filter the entry waits for the filter's catch, which can (#371). One deferred
				// scope per call: a second one, should a call ever hold two, writes as before.
				if(callFrame.Value is { Unfinished: null } frame) {
					
					deferredElapsed  = elapsed;
					frame.Unfinished = this;
					onDispose();
					
					return;
				}
			}
			
			Write(elapsed);
			onDispose();
		}
		
		/// <summary>
		///     Writes the entry <see cref="Dispose"/> deferred to the call filter: with the exception that
		///     ended the call, or without one when the tool returned without a terminal call.
		/// </summary>
		internal void WriteDeferred(Exception? exception)
		{
			if(exception is not null)
				detail = $"unhandled {DescribeException(exception)}";
			
			Write(deferredElapsed);
		}
		
		void Write(long elapsed)
		{
			// Outcome first — it is what the log viewer's detail column should lead with.
			var logged = notes is null ? detail
				: detail is null ? notes
				: $"{detail}; {notes}"
			;
			
			log.LogTool(name, elapsed, !failed, isMSBuild, estimatedTokens, subject, logged, cacheTag, responsePeek, args);
			
			// Tells the call filter this call's failure is already in the log.
			if(failed && callFrame.Value is { } frame)
				frame.FailureLogged = true;
		}
		
		
		/// <summary>
		///     Serializes the response to JSON, returns the token estimate and a truncated peek string.
		///     The peek is capped at 600 chars — enough for the log viewer to show meaningful content.
		/// </summary>
		static (int tokens, string? peek) SerializeResponse<T>(T value)
		{
			try {
				
				// By the runtime type, as the SDK serializes the response: a helper declared to
				// return a base result (RoslynMcpTool.ReadFailure) would otherwise log a peek
				// without the derived type's fields.
				var json   = value is null
					? "null"
					: JsonSerializer.Serialize(value, value.GetType(), RoslynMcpJson.Compact)
				;
				var tokens = json.Length / 4;
				var peek   = json.Length <= PeekLength ? json : json[..PeekLength] + "…";
				
				return (tokens, peek);
			}
			catch {
				return (0, null);
			}
		}
	}
}
