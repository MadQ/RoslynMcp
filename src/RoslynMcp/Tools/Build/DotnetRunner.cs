using System.ComponentModel;
using System.Diagnostics;

namespace RoslynMcp.Tools;

// Shared helper for running dotnet CLI commands from tools.
// All tools that shell out to dotnet use this — prevents duplicated process-management code
// and ensures consistent behaviour: concurrent stdout/stderr drain, ArgumentList population,
// and deterministic process disposal.
internal static class DotnetRunner
{
	// One dotnet CLI run at a time, process-wide (#306). Builds, cleans, and restores of projects in
	// one solution write the same obj\ output — a project referenced by two targets is built by both
	// — so concurrent runs race on its state files ("Could not write state file …", "… being used by
	// another process"), the contention that corrupted a workspace reload in #300. Agents do issue
	// these in parallel, so the server queues them rather than trusting the tool descriptions alone.
	// Global rather than per-solution: concurrent runs over different solutions are rare, and for a
	// disk-mutating operation correctness beats throughput.
	static readonly SemaphoreSlim gate = new(1, 1);
	
	// Runs `dotnet <args>` in workingDirectory and returns the combined stdout+stderr output,
	// elapsed time, and exit code. Throws InvalidOperationException if the process fails to start
	// or if output cannot be read — callers map these to their own error result types.
	// record is an optional sink for diagnostic annotations (e.g. scope.Record in BuildTool).
	// Waits for any other dotnet run to finish first; the wait is recorded, and is not part of the
	// returned elapsed time, which stays the process's own runtime. Cancellation while queued throws
	// OperationCanceledException without ever starting the process.
	internal static async Task<(string combined, TimeSpan elapsed, int exitCode)> RunAsync(
		IEnumerable<string> args,
		string workingDirectory,
		Action<string>? record = null,
		CancellationToken ct = default)
	{
		// Recorded before any wait, so a run cancelled while queued still says what it was.
		record?.Invoke($"dotnet {string.Join(" ", args)}");
		record?.Invoke($"cwd={workingDirectory}");
		
		// A request cancelled before it got here must not start a process — with the gate free, it
		// would otherwise launch dotnet only for RunCoreAsync to kill it straight away.
		if(ct.IsCancellationRequested) {
			
			record?.Invoke("cancelled before start — dotnet never started");
			ct.ThrowIfCancellationRequested();
		}
		
		if(!gate.Wait(0)) {
			
			var queued = Stopwatch.StartNew();
			
			try {
				await gate.WaitAsync(ct);
			}
			catch(OperationCanceledException) {
				
				record?.Invoke($"cancelled after {queued.ElapsedMilliseconds} ms queued — dotnet never started");
				throw;
			}
			
			record?.Invoke($"queued {queued.ElapsedMilliseconds} ms behind another dotnet command");
		}
		
		try {
			
			// WaitAsync can hand over the gate in the same instant the token is cancelled — the release
			// and the cancellation race — so re-check before starting anything. The finally still
			// releases the gate just acquired.
			if(ct.IsCancellationRequested) {
				
				record?.Invoke("cancelled on acquiring the gate — dotnet never started");
				ct.ThrowIfCancellationRequested();
			}
			
			return await RunCoreAsync(args, workingDirectory, record, ct);
		}
		finally {
			gate.Release();
		}
	}
	
	static async Task<(string combined, TimeSpan elapsed, int exitCode)> RunCoreAsync(
		IEnumerable<string> args,
		string workingDirectory,
		Action<string>? record,
		CancellationToken ct)
	{
		var psi = new ProcessStartInfo("dotnet") {
			
			RedirectStandardOutput = true,
			RedirectStandardError  = true,
			UseShellExecute        = false,
			CreateNoWindow         = true,
			WorkingDirectory       = workingDirectory
		};
		
		// MSBuildLocator.RegisterDefaults() (called by MSBuildBootstrap) sets MSBUILD_EXE_PATH
		// as a process-wide env var so Roslyn can load the right MSBuild assemblies. When a
		// child `dotnet build` inherits it, the child skips its own SDK discovery and uses the
		// parent's MSBuild DLL path — which causes a pre-compilation MSBuild failure with
		// "Build FAILED. 0 Warning(s) 0 Error(s)". Unset it so the child does clean discovery.
		psi.Environment.Remove("MSBUILD_EXE_PATH")
		;
		psi.Environment.Remove("MSBuildExtensionsPath");
		psi.Environment.Remove("MSBuildSDKsPath");
		
		// Disable the MSBuild build server — when the server runs `dotnet build` as a child,
		// a shared build server node may have stale state from the parent's dotnet run context.
		psi.Environment["MSBUILDUSESERVER"] = "0"
		;
		
		// ArgumentList avoids shell quoting/injection issues with paths containing spaces or
		// special characters — do not use the Arguments string property instead.
		foreach(var arg in args)
			psi.ArgumentList.Add(arg);
		
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
			var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None)
			;
			var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
			
			await process.WaitForExitAsync(ct);
			sw.Stop();
			
			// Capture exit code before disposing.
			var exitCode = process.ExitCode
			;
			record?.Invoke($"exit={exitCode} elapsed={sw.Elapsed.TotalSeconds:F1}s");
			
			string stdout, stderr;
			
			try {
				
				stdout = await stdoutTask;
				stderr = await stderrTask;
				record?.Invoke($"stdout={stdout.Length} stderr={stderr.Length} chars");
				
				// Preview first 300 chars so log entries reveal suppressed diagnostics.
				var preview = (stdout + stderr).Replace('\r', ' ').Replace('\n', '↵')
				;
				if(preview.Length > 0)
					record?.Invoke($"output_preview={preview[..Math.Min(300, preview.Length)]}");
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
			await process.WaitForExitAsync(CancellationToken.None)
			;
			throw;
		}
		finally {
			process.Dispose();
		}
	}
}
