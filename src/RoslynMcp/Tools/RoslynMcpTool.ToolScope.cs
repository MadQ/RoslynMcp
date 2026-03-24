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

        bool    failed;
        string? detail;

        internal ToolScope(string name, string? subject, FileLogger log)
        {
            this.name    = name;
            this.subject = subject;
            this.log     = log;
        }

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
            var label = subject is null ? name : $"{name}({subject})";
            log.LogTool(label, sw.ElapsedMilliseconds, !failed, detail);
        }
    }
}
