namespace RoslynMcp;

/// <summary>
///     Lightweight file logger with rotation. All writes are thread-safe via a lock.
///     Controlled by the <c>ROSLYNMCP_LOG_PATH</c> environment variable:
///     - Not set → default path (%LOCALAPPDATA%\RoslynMcp\logs\roslynmcp.log)
///     - Set to empty string → logging disabled
///     - Set to a path → logs to that file
/// </summary>
internal sealed class FileLogger : IDisposable
{
	//
	// Could be using ILogger, but thus far I have not been able to like it one bit.
	// Did we really need a whole new DSL just to log structured messages?
	//
	
	const int    MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB
	const int    MaxRotatedFiles  = 3;
	const string EnvVar           = "ROSLYNMCP_LOG_PATH";
	
	readonly string? logPath;
	readonly object  writeLock = new();
	
	public bool IsEnabled => logPath is not null;
	
	public FileLogger()
	{
		var envValue = Environment.GetEnvironmentVariable(EnvVar);
		
		// Explicitly set to empty → disabled.
		if(envValue is not null && envValue.Length == 0) {
			
			logPath = null;
			
			return;
		}
		
		logPath = envValue is not null
			? envValue
			: Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"RoslynMcp", "logs", "roslynmcp.log"
			);
		
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
		}
		catch {
			// If we can't create the log directory, silently disable logging rather than crashing the server.
			logPath = null;
		}
	}
	
	/// <summary>Logs server start with PID and working directory.</summary>
	public void LogStart()
		=> Write("START ", $"pid={Environment.ProcessId} cwd=\"{Environment.CurrentDirectory}\" log=\"{logPath}\"");
	
	/// <summary>Logs server stop.</summary>
	public void LogStop()
		=> Write("STOP  ", "Server stopping");
	
	/// <summary>Logs a tool invocation with outcome, elapsed time, and workspace mode indicator.</summary>
	/// <param name="isMSBuild">True for MSBuildWorkspace (◆), false for AdhocWorkspace (◇), null when unknown.</param>
	public void LogTool(string toolName, long elapsedMs, bool success, string? subject = null, string? detail = null, bool isMSBuild = true, string? cacheTag = null)
	{
		var outcome   = success ? "OK   " : "ERROR";
		var ws        = isMSBuild ? "MSB" : "ADH";
		var shortName = toolName.StartsWith("roslyn_", StringComparison.Ordinal)
			? toolName[7..]
			: toolName
		;
		var cache = cacheTag switch { "HIT" => " [HIT]", "MISS" => " [MISS]", _ => "" };

		var sb = new System.Text.StringBuilder();
		sb.Append($"{ws} {outcome} {elapsedMs,5}ms {shortName,-25}");

		if(subject is not null)
			sb.Append($" {subject}");

		if(detail is not null)
			sb.Append($" — {detail}");

		sb.Append(cache);

		Write("TOOL  ", sb.ToString());
	}
	
	/// <summary>Logs an error outside of a tool call (e.g. workspace load failure).</summary>
	public void LogError(string context, string message)
		=> Write("ERROR ", $"{context} — {message}");
	
	/// <summary>Logs informational diagnostic messages (verbose logging).</summary>
	public void LogInfo(string context, string message)
		=> Write("INFO  ", $"{context} — {message}");
	
	void Write(string level, string message)
	{
		if(logPath is null)
			return;
		
		var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
		
		lock(writeLock) {
			
			try {
				
				RotateIfNeeded();
				File.AppendAllText(logPath, line);
			}
			catch {
				// Never crash the server over a logging failure.
			}
		}
	}
	
	void RotateIfNeeded()
	{
		if(!File.Exists(logPath))
			return;
		
		if(new FileInfo(logPath).Length < MaxFileSizeBytes)
			return;
		
		// Shift existing rotated files: .2 → .3, .1 → .2, (current) → .1
		for(var i = MaxRotatedFiles - 1; i >= 1; i--) {
			
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
