using System.Text.Json;

namespace RoslynMcp;

/// <summary>
///     Lightweight file logger with rotation. All writes are thread-safe via a lock.
///     Outputs one <see cref="LogEntry"/> as NDJSON per line.
///     Logging is configured via <see cref="ServerArgs.Current"/>:
///     - <c>null</c> → default path (%LOCALAPPDATA%\RoslynMcp\logs\roslynmcp.log)
///     - empty string → logging disabled
///     - path → logs to that file
/// </summary>
internal sealed class FileLogger : IDisposable
{
	const int maxFileSizeBytes = 10 * 1024 * 1024; // 10 MB
	const int maxRotatedFiles  = 3;
	
	static readonly JsonSerializerOptions jsonOptions = RoslynMcpJson.Log;
	
	readonly string? logPath;

#if NET9_0_OR_GREATER
		private readonly Lock             writeLock   = new();
#else
		private readonly object           writeLock   = new();
#endif
	
	readonly int     pid       = Environment.ProcessId;
	
	int  instanceCounter;
	long sessionTokens;
	
	public bool IsEnabled => logPath is not null;
	
	public FileLogger()
	{
		logPath = ServerArgs.Current.ResolvedLogPath;
		
		if(logPath is null)
			
			return;
		
		try {
			
			var logDir = Path.GetDirectoryName(logPath)!;
			
			_= Directory.CreateDirectory(logDir);
			
			// Prune old per-PID log files (including their rotation siblings) by age.
			// Pattern: "roslynmcp.*.log*" matches roslynmcp.1234.log, roslynmcp.1234.log.1, etc.
			var rawLog			 = ServerArgs.Current.LogPath
			;
			var rawLogHasContent = rawLog is { Length: > 0 };
			var rawStem			 = rawLogHasContent ? Path.GetFileNameWithoutExtension(rawLog) : "roslynmcp";
			var rawExt			 = rawLogHasContent ? Path.GetExtension(rawLog)                : ".log";
			
			FilePruner.Prune(
				  logDir
				, $"{rawStem}.*{rawExt}*"
				, TimeSpan.FromDays(ServerArgs.Current.LogMaxAgeDays)
				, ServerArgs.Current.PruneMinRuns
			);
		}
		catch {
			logPath = null;
		}
	}
	
	/// <summary>Logs server start with PID and working directory.</summary>
	public void LogStart()
		=> Write(new LogEntry {
			
			  Timestamp = Timestamp()
			, Pid       = pid
			, Level     = "START"
			, Message   = $"cwd=\"{Environment.CurrentDirectory}\" log=\"{logPath}\""
		});
	
	/// <summary>Logs server stop.</summary>
	public void LogStop()
		=> Write(new LogEntry {
			
			  Timestamp = Timestamp()
			, Pid       = pid
			, Level     = "STOP"
			, Message   = "Server stopping"
		});
	
	/// <summary>Logs a tool invocation with outcome, elapsed time, and workspace mode indicator.</summary>
	/// <param name="isMSBuild">True for MSBuildWorkspace, false for AdhocWorkspace.</param>
	public void LogTool(
		  string	toolName
		, long		elapsedMs
		, bool		success
		, bool		isMSBuild
		, int		estimatedTokens
		, string?	subject
		, string?	detail
		, string?	cacheTag
		, string?	responsePeek
		, string?	args
	)
	{
		var instance  = Interlocked.Increment(ref instanceCounter);
		var shortName = toolName.StartsWith("roslyn_", StringComparison.Ordinal)
			? toolName[7..]
			: toolName
		;
		
		long tokens = 0;
		
		if(estimatedTokens > 0)
			tokens = Interlocked.Add(ref sessionTokens, estimatedTokens);
		
		Write(new LogEntry {
			
			  Timestamp       = Timestamp()
			, Pid             = pid
			, Level           = "TOOL"
			, Instance        = instance
			, WorkspaceMode   = isMSBuild ? "MSB" : "ADH"
			, ToolName        = shortName
			, ElapsedMs       = elapsedMs
			, Success         = success
			, Subject         = subject
			, Detail          = detail
			, CacheTag        = cacheTag
			, EstimatedTokens = estimatedTokens > 0 ? estimatedTokens : null
			, SessionTokens   = estimatedTokens > 0 ? tokens : null
			, ResponsePeek    = responsePeek
			, Args            = args
		});
	}
	
	/// <summary>
	///     Logs a hook invocation (opt-in, enabled by the <c>--log</c> flag on
	///     <c>dotnet roslynmcp hook</c>). Called once per hook process from its
	///     finally block so every invocation — including pass-throughs — is captured.
	/// </summary>
	public void LogHook(string eventName, string? toolName, long elapsedMs, string outcome)
		=> Write(new LogEntry {

			  Timestamp = Timestamp()
			, Pid       = pid
			, Level     = "HOOK"
			, ToolName  = toolName
			, ElapsedMs = elapsedMs
			, Subject   = eventName
			, Detail    = outcome
		});

	/// <summary>Logs an error outside of a tool call (e.g. workspace load failure).</summary>
	public void LogError(string context, string message)
		=> Write(new LogEntry {
			
			  Timestamp = Timestamp()
			, Pid       = pid
			, Level     = "ERROR"
			, Message   = $"{context} — {message}"
		});
	
	/// <summary>Logs informational diagnostic messages (verbose logging).</summary>
	public void LogInfo(string context, string message)
		=> Write(new LogEntry {
			
			  Timestamp = Timestamp()
			, Pid       = pid
			, Level     = "INFO"
			, Message   = $"{context} — {message}"
		});
	
	/// <summary>
	///     Logs an unhandled-exception crash under the write lock.
	///     Routed from the <see cref="AppDomain.UnhandledException"/> handler after DI is ready
	///     so the write is serialised with normal log traffic instead of racing with it.
	/// </summary>
	public void LogFatal(string message)
		=> Write(new LogEntry {
			
			  Timestamp = Timestamp()
			, Pid       = pid
			, Level     = "FATAL"
			, Message   = message
		});
	
	void Write(LogEntry entry)
	{
		if(logPath is null)
			
			return;
		
		var line = JsonSerializer.Serialize(entry, jsonOptions) + Environment.NewLine;
		
		lock(writeLock)
			try {
				
				RotateIfNeeded();
				File.AppendAllText(logPath, line);
			}
			catch {
				// Never crash the server over a logging failure.
			}
	}
	
	static string Timestamp() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
	
	void RotateIfNeeded()
	{
		if(!File.Exists(logPath))
			
			return;
		
		if(new FileInfo(logPath).Length < maxFileSizeBytes)
			
			return;
		
		// Shift existing rotated files: .2 → .3, .1 → .2, (current) → .1
		for(var i = maxRotatedFiles - 1; i >= 1; i--) {
			
			var older  = $"{logPath}.{i}";
			var newer  = $"{logPath}.{i + 1}";
			
			if(File.Exists(older)) {
				
				if(File.Exists(newer))
					File.Delete(newer);
				
				File.Move(older, newer);
			}
		}
		
		var rotated = $"{logPath}.1";
		
		if(File.Exists(rotated))
			File.Delete(rotated);
		
		File.Move(logPath, rotated);
	}
	
	public void Dispose()
	{
		// Nothing to release — File.AppendAllText opens and closes on every write.
		// This exists so FileLogger can be registered as IDisposable in DI cleanly.
	}
}
