using System.Diagnostics;

namespace RoslynMcp;

/// <summary>
///     Measures how long a work item waits for a thread-pool thread, and logs when it is long (#449).
///     <para>
///         A tool call runs on pool threads, and so does everything the SDK does between reading a
///         request and invoking the tool. When the pool is starved — its threads blocked, new ones
///         injected one at a time — a request can wait for minutes while the log shows nothing: an
///         entry is written when a tool finishes, and a tool that has not started has no entry
///         (#445: a request ran seventy seconds after it was sent, in 267 ms). This asks the pool
///         directly, once a second, how long a trivial item waits.
///     </para>
///     <para>
///         The clock is a thread of its own, not a timer: a timer's callback is itself a pool work
///         item, and would be late by the very delay it is meant to see.
///     </para>
/// </summary>
internal static class ThreadPoolProbe
{
	const int IntervalMs = 1_000;
	
	// A wait this long is logged. Well above what a busy but healthy pool shows.
	const int SlowMs = 1_000;
	
	// How often a wait that has not ended yet is logged.
	const int StillWaitingMs = 5_000;
	
	static int started;
	
	/// <summary>Starts the probe, once per process. The thread is a background one and ends with the process.</summary>
	public static void Start(FileLogger logger)
	{
		if(Interlocked.Exchange(ref started, 1) != 0)
			
			return;
		
		new Thread(() => Run(logger)) { IsBackground = true, Name = "RoslynMcp thread-pool probe" }.Start();
	}
	
	/// <summary>The pool's state in a few words, for a log line: its threads and the items waiting for one.</summary>
	public static string Describe()
		=> $"pool: {ThreadPool.ThreadCount} thread(s), {ThreadPool.PendingWorkItemCount} item(s) waiting"
	;
	
	static void Run(FileLogger logger)
	{
		// One item at a time: while one is waiting, queueing more would only add to what it waits behind.
		var waiting  = 0;
		var queued   = 0L;
		var reported = 0L;
		
		while(true) {
			
			Thread.Sleep(IntervalMs);
			
			if(Interlocked.Exchange(ref waiting, 1) != 0) {
				
				// The item's own line is written when it runs. A pool that never gives it a thread, or a
				// server killed before it does, would leave nothing, so this thread says it meanwhile.
				var soFar = (long) Stopwatch.GetElapsedTime(queued).TotalMilliseconds;
				
				if(soFar - reported >= StillWaitingMs) {
					
					reported = soFar;
					logger.LogInfo("ThreadPool", $"a queued work item has been waiting {soFar} ms for a thread ({Describe()}): tool calls sent since then have not started");
				}
				
				continue;
			}
			
			var item = queued = Stopwatch.GetTimestamp();
			
			reported = 0;
			
			ThreadPool.UnsafeQueueUserWorkItem(_ => {
				
				var waited = (long) Stopwatch.GetElapsedTime(item).TotalMilliseconds;
				
				Volatile.Write(ref waiting, 0);
				
				if(waited >= SlowMs)
					logger.LogInfo("ThreadPool", $"a queued work item waited {waited} ms for a thread ({Describe()}): tool calls sent in that time started late");
			}, null);
		}
	}
}
