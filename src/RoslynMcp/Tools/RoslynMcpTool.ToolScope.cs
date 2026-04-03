using System.Diagnostics;
using System.Text.Json;

namespace RoslynMcp.Tools;

internal abstract partial class RoslynMcpTool
{
	/// <summary>
	///     Disposable scope that logs a tool invocation with elapsed time on dispose.
	///     Obtained via <see cref="BeginTool"/>.
	/// </summary>
	protected sealed class ToolScope : IDisposable
	{
		readonly string     name;
		readonly string?    subject;
		readonly FileLogger log;
		readonly Stopwatch  sw = Stopwatch.StartNew();
		readonly Action     onDispose;

		bool    failed;
		string? detail;
		string? responsePeek;
		bool    isMSBuild = true;  // Default MSBuild (95% case); SetWorkspaceMode overrides.
		string? cacheTag;          // null = not paginated, "HIT" or "MISS"
		int     estimatedTokens;
		string? args;              // Serialized input args; included in log only on failure.

		internal ToolScope(string name, string? subject, FileLogger log, Action onDispose)
		{
			this.name      = name;
			this.subject   = subject;
			this.log       = log;
			this.onDispose = onDispose;
		}

		/// <summary>Records the workspace mode so the log line can show MSB/ADH.</summary>
		internal void SetWorkspaceMode(bool isMSBuild) => this.isMSBuild = isMSBuild;

		/// <summary>Records whether a pagination cache hit or miss occurred.</summary>
		internal void SetCacheTag(bool hit) => cacheTag = hit ? "HIT" : "MISS";

		/// <summary>
		///     Records key input arguments for failure diagnosis.
		///     Serialized to compact JSON; included in the log entry only when the tool fails.
		///     Truncate large string values before passing to avoid bloating the log.
		/// </summary>
		public void SetArgs<T>(T argsObj)
		{
			try {
				args = JsonSerializer.Serialize(argsObj, new JsonSerializerOptions { WriteIndented = false });
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
		public T Error<T>(T returnValue)
		{
			failed                          = true;
			detail                          = returnValue is ErrorResult err ? err.Error : "error";
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
				var json   = JsonSerializer.Serialize(value);
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
