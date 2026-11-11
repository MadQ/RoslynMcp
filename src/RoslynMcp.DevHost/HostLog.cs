using System.Globalization;

namespace RoslynMcp.DevHost;

/// <summary>
///     The host's own log, one small text file per host process: which server it started, every swap and
///     every swap it refused. Stdout is the protocol's, and stderr is the server's as much as the host's,
///     so what the host did needs a place of its own. A log that cannot be written is not a reason to stop
///     forwarding.
/// </summary>
sealed class HostLog
{
	static readonly TimeSpan KeptFor = TimeSpan.FromDays(30);
	
	readonly string path;
	readonly object sync = new();
	
	public HostLog(DevHome home)
	{
		path = Path.Combine(home.Logs, $"devhost.{Environment.ProcessId}.log");
		
		try {
			
			Directory.CreateDirectory(home.Logs);
			
			foreach(var old in Directory.EnumerateFiles(home.Logs, "devhost.*.log"))
				if(DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > KeptFor)
					File.Delete(old);
		}
		catch(Exception failure) when(failure is IOException or UnauthorizedAccessException) { }
	}
	
	public void Write(string message)
	{
		var line = $"{DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)}  {message}{Environment.NewLine}";
		
		lock(sync) {
			
			try {
				File.AppendAllText(path, line);
			}
			catch(Exception failure) when(failure is IOException or UnauthorizedAccessException) { }
		}
	}
	
	// What the user has to know goes to stderr as well, where the client shows it.
	public void Problem(string message)
	{
		Write(message);
		Console.Error.WriteLine($"[RoslynMcp.DevHost] {message}");
	}
}
