using System.Diagnostics;

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
		bool    isMSBuild = true;  // Default MSBuild (95% case); SetWorkspaceMode overrides.
		string? cacheTag;          // null = not paginated, "HIT" or "MISS"

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

		/// <summary>Records a success detail appended to the log line on dispose.</summary>
		public void Outcome(string detail) => this.detail = detail;

		/// <summary>Records a success detail and returns <paramref name="returnValue"/> for fluent use in return statements.</summary>
		public T Outcome<T>(string detail, T returnValue) { this.detail = detail; return returnValue; }

		/// <summary>Marks the invocation as failed with a reason appended to the log line on dispose.</summary>
		public void Failed(string reason) { failed = true; detail = reason; }

		/// <summary>Marks the invocation as failed and returns <paramref name="returnValue"/> for fluent use in return statements.</summary>
		public T Failed<T>(string reason, T returnValue) { failed = true; detail = reason; return returnValue; }

		/// <summary>Appends a neutral annotation without changing the outcome.</summary>
		public void Record(string note) => detail = detail is null ? note : $"{detail}; {note}";

		public void Dispose()
		{
			log.LogTool(name, sw.ElapsedMilliseconds, !failed, subject, detail, isMSBuild, cacheTag);
			onDispose();
		}
	}
}
