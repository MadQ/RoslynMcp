using System.ComponentModel;
using System.Diagnostics;

namespace RoslynMcp.DevHost;

/// <summary>
///     One server process and the two pipes to it. Stderr is not redirected: the server writes to the
///     host's own, so the client sees it as it would without a host.
/// </summary>
sealed class ServerProcess
{
	static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);
	
	readonly Process process;
	long             forwards;
	
	/// <summary>
	///     Changes with every line of the server's that is passed on, and is odd while one is being
	///     written: what tells a forwarder that is still at work from one that waits for a pipe nobody
	///     will write to again.
	/// </summary>
	public long Forwards => Volatile.Read(ref forwards);
	
	public void MarkForward() => Interlocked.Increment(ref forwards)
	;
	
	public string     Build  { get; }
	public Stream     Input  { get; }
	public LineReader Output { get; }
	public int        Id     => process.Id;
	
	ServerProcess(Process process, string build)
	{
		this.process = process;
		Build        = build;
		Input        = process.StandardInput.BaseStream;
		Output       = new LineReader(process.StandardOutput.BaseStream);
	}
	
	/// <summary>
	///     Starts the server found in <paramref name="directory"/> with the host's own arguments and
	///     working directory. Null, with the reason, when there is no server there or it cannot be started.
	/// </summary>
	public static ServerProcess? Start(string directory, string build, IReadOnlyList<string> arguments, out string failure)
	{
		var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "RoslynMcp.exe" : "RoslynMcp");
		var assembly   = Path.Combine(directory, "RoslynMcp.dll");
		
		ProcessStartInfo start;
		
		if(File.Exists(executable))
			start = new(executable);
		else if(File.Exists(assembly)) {
			
			start = new("dotnet");
			start.ArgumentList.Add(assembly);
		}
		else {
			
			failure = $"no RoslynMcp executable or RoslynMcp.dll in {directory}";
			
			return null;
		}
		
		foreach(var argument in arguments)
			start.ArgumentList.Add(argument);
		
		start.UseShellExecute        = false;
		start.RedirectStandardInput  = true;
		start.RedirectStandardOutput = true;
		
		try {
			
			failure = "";
			
			return new(Process.Start(start)!, build);
		}
		catch(Exception thrown) when(thrown is Win32Exception or InvalidOperationException or IOException) {
			
			failure = $"{Path.GetFileName(start.FileName)} did not start: {thrown.Message}";
			
			return null;
		}
	}
	
	public async Task<int> WaitForExitAsync()
	{
		await process.WaitForExitAsync();
		
		return process.ExitCode;
	}
	
	/// <summary>
	///     Ends the server the way a client does: its stdin closes, and it gets a few seconds to shut
	///     down before it is killed.
	/// </summary>
	public async Task StopAsync()
	{
		try {
			Input.Close();
		}
		catch(IOException) { }
		
		using var grace = new CancellationTokenSource(ExitGrace);
		
		try {
			await process.WaitForExitAsync(grace.Token);
		}
		catch(OperationCanceledException) {
			Kill();
		}
	}
	
	public void Kill()
	{
		try {
			process.Kill(entireProcessTree: true);
		}
		catch(Exception failure) when(failure is InvalidOperationException or Win32Exception) { }
	}
}
