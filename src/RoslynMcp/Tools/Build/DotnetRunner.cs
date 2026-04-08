using System.ComponentModel;
using System.Diagnostics;

namespace RoslynMcp.Tools;

// Shared helper for running dotnet CLI commands from tools.
// All tools that shell out to dotnet use this — prevents duplicated process-management code
// and ensures consistent behaviour: concurrent stdout/stderr drain, ArgumentList population,
// and deterministic process disposal.
internal static class DotnetRunner
{
	// Runs `dotnet <args>` in workingDirectory and returns the combined stdout+stderr output,
	// elapsed time, and exit code. Throws InvalidOperationException if the process fails to start
	// or if output cannot be read — callers map these to their own error result types.
	// record is an optional sink for diagnostic annotations (e.g. scope.Record in BuildTool).
	internal static async Task<(string combined, TimeSpan elapsed, int exitCode)> RunAsync(
		IEnumerable<string> args,
		string workingDirectory,
		Action<string>? record = null,
		CancellationToken ct = default)
	{
		var psi = new ProcessStartInfo("dotnet") {
			
			RedirectStandardOutput = true,
			RedirectStandardError  = true,
			UseShellExecute        = false,
			CreateNoWindow         = true,
			WorkingDirectory       = workingDirectory
		};
		
		// ArgumentList avoids shell quoting/injection issues with paths containing spaces or
		// special characters — do not use the Arguments string property instead.
		foreach(var arg in args)
			psi.ArgumentList.Add(arg);
		
		var argsDisplay = string.Join(" ", args);
		record?.Invoke($"dotnet {argsDisplay}");
		record?.Invoke($"cwd={workingDirectory}");
		
		Process? process = null;
		
		try {
			process = new Process { StartInfo = psi };
			
			if(!process.Start())
				throw new InvalidOperationException("Process.Start() returned false — process did not start.");
			
			record?.Invoke($"pid={process.Id}");
		}
		catch(Exception ex) when(ex is Win32Exception or InvalidOperationException) {
			process?.Dispose();
			throw new InvalidOperationException("Failed to start dotnet process. Is dotnet installed and in PATH?", ex);
		}
		
		var sw = Stopwatch.StartNew();
		
		try {
			
			// Start both reads before WaitForExitAsync — prevents deadlock when the process
			// writes enough to fill the pipe buffer before we start draining.
			// Use CancellationToken.None for the drains: ct controls process lifetime (WaitForExitAsync
			// + Kill below), but once the process has exited the pipes will close naturally. Passing
			// ct here would abandon already-produced output if ct fires after WaitForExitAsync returns.
			var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
			var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
			
			await process.WaitForExitAsync(ct);
			sw.Stop();
			
			// Capture exit code before disposing.
			var exitCode = process.ExitCode;
			record?.Invoke($"exit={exitCode} elapsed={sw.Elapsed.TotalSeconds:F1}s");
			
			string stdout, stderr;
			
			try {
				stdout = await stdoutTask;
				stderr = await stderrTask;
				record?.Invoke($"stdout={stdout.Length} stderr={stderr.Length} chars");
			}
			catch(IOException ex) {
				throw new InvalidOperationException("Failed to read process output.", ex);
			}
			
			// MSBuild writes diagnostics to stdout; stderr is typically empty or SDK noise.
			// Ensure a line break between streams when both are non-empty — stdout may not
			// end with a newline, which would merge the last stdout line with the first stderr line.
			var combined = string.IsNullOrWhiteSpace(stderr)
				? stdout
				: stdout + (stdout.EndsWith('\n') ? "" : Environment.NewLine) + stderr
			;
			
			return (combined, sw.Elapsed, exitCode);
		}
		catch(OperationCanceledException) {
			
			try {
				// Kill the entire process tree so the child dotnet process doesn't keep running
				// and holding file locks after the caller's token is cancelled.
				if(!process.HasExited)
					process.Kill(entireProcessTree: true);
			}
			catch(Exception) {
				// Best-effort kill — process may have exited between HasExited check and Kill.
				// Swallow so the original OperationCanceledException propagates cleanly.
			}
			
			// Wait for the OS to confirm exit before returning — otherwise the caller may see
			// stale file locks even though we returned.
			await process.WaitForExitAsync(CancellationToken.None);
			throw;
		}
		finally {
			process.Dispose();
		}
	}
}
