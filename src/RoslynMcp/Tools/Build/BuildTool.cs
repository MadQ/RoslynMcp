using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class BuildTool : RoslynMcpTool
{
	public BuildTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	// Diagnostic codes to ignore (non-actionable SDK/tooling warnings).
	private static readonly HashSet<string> IgnoredDiagnostics = new(StringComparer.OrdinalIgnoreCase) {
		"NETSDK1209", // "The current Visual Studio version does not support targeting .NET X"
	};
	
	// Matches MSBuild diagnostic lines:
	//   path(line,col): error CS0103: message [proj::TargetFramework=net10.0]
	//   path(line,col): warning CS8600: message [proj]
	// The path, location, and project suffix are all optional (some messages omit them).
	private static readonly Regex DiagnosticLine = new(
		@"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s+(?<severity>error|warning)\s+(?<code>\w+):\s+(?<message>.+?)(?:\s+\[.+\])?$",
		RegexOptions.Compiled | RegexOptions.IgnoreCase
	);
	
	[McpServerTool(Name = "roslyn_build_project", ReadOnly = true)]
	[Description(
		"Validates the project in two tiers. " +
		"Tier 1 (always): Roslyn in-process — instant C# type/symbol errors, zero process spawn. " +
		"Tier 2 (only when Roslyn is clean): 'dotnet build' — validates what Roslyn cannot see: " +
		"NuGet restore, MSBuild targets/props, SDK version, and source generators. " +
		"Prefer roslyn_get_diagnostics for fast C#-only checks during editing. " +
		"Use this tool when you need confidence the project fully builds (e.g. before committing). " +
		"Requires a .csproj to be present.")]
	public async Task<object> BuildProject(
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Target framework to build, e.g. 'net10.0'. Omit to build the default (first) target framework.")] string? targetFramework = null,
		[Description(
			"Default false: Roslyn errors short-circuit — dotnet build only runs when C# is clean, " +
			"validating NuGet restore, MSBuild targets, SDK props, and source generators. " +
			"Set true only when you suspect an MSBuild-specific failure Roslyn cannot see " +
			"(broken .targets file, generator crash, restore failure) — skips the Roslyn fast-path entirely."
		)] bool forceBuild = false)
	{
		using var scope = BeginTool("roslyn_build_project");
		var (rootPath, _, csprojPath) = workspace.GetWorkspaceInfo(projectPath);
		
		if(csprojPath is null)
			return scope.Error(new ErrorResult("No .csproj found — build is only available in MSBuildWorkspace mode."));
		
		// Fast path: check Roslyn diagnostics first (unless forceBuild=true).
		if(!forceBuild) {
			
			if(!TryGetCompilation(projectPath, out var compilation, out var error))
				return error;
			
			var roslynDiagnostics = GetRoslynDiagnostics(compilation, rootPath);
			var roslynErrors      = roslynDiagnostics.Where(d => d.Severity == "error").ToArray();
			
			if(roslynErrors.Length > 0) {
				
				BuildDiagnostic[] roslynWarnings = [.. roslynDiagnostics.Where(d => d.Severity == "warning")];
				
				return scope.Error(new BuildResult(
					Succeeded:     false,
					Errors:        roslynErrors,
					Warnings:      roslynWarnings,
					Source:        "roslyn",
					Build_skipped: true,
					Skip_reason:   "Roslyn reported errors — fix these first, then build will run automatically.",
					Duration_ms:   0,
					Exit_code:     null
				));
			}
		}
		
		// Slow path: run actual dotnet build.
		var args = BuildArgs(csprojPath, targetFramework);
		
		string output;
		TimeSpan elapsed;
		int exitCode;
		
		try {
			(output, elapsed, exitCode) = await RunDotnetAsync(args, rootPath, scope);
		}
		catch(InvalidOperationException ex) {

			return scope.Error(new BuildResult(
				Succeeded:     false,
				Errors:        (BuildDiagnostic[]) [],
				Warnings:      (BuildDiagnostic[]) [],
				Source:        "msbuild",
				Build_skipped: true,
				Skip_reason:   ex.Message,
				Duration_ms:   0,
				Exit_code:     null,
				Error_details: ex.InnerException?.Message
			));
		}
		
		var diagnostics = ParseMSBuildDiagnostics(output, rootPath);
		var succeeded   = exitCode == 0;
		BuildDiagnostic[] errors   = [.. diagnostics.Where(d => d.Severity == "error")  ];
		BuildDiagnostic[] warnings = [.. diagnostics.Where(d => d.Severity == "warning")];

		return scope.Outcome("msbuild", new BuildResult(
			succeeded,
			errors,
			warnings,
			Source:        "msbuild",
			Build_skipped: false,
			Skip_reason:   null,
			Duration_ms:   (int) elapsed.TotalMilliseconds,
			Exit_code:     exitCode
		));
	}
	
	private static string BuildArgs(string csprojPath, string? tfm)
	{
		// --no-restore: restore is separate; /v:quiet: only errors/warnings + summary line.
		var tfmArg = tfm is not null ? $" -f {tfm}" : string.Empty;
		
		return $"build \"{csprojPath}\"{tfmArg} --no-restore /nologo /v:quiet";
	}
	
	private async Task<(string output, TimeSpan elapsed, int exitCode)> RunDotnetAsync(string args, string workingDirectory, ToolScope scope)
	{
		var psi = new ProcessStartInfo("dotnet", args) {
			
			RedirectStandardOutput = true,
			RedirectStandardError  = true,
			UseShellExecute        = false,
			CreateNoWindow         = true,
			WorkingDirectory       = workingDirectory
		};
		
		Process? process = null;
		int exitCode = -1;
		
		try {
			
			scope.Record($"dotnet {args}");
			scope.Record($"cwd={workingDirectory}");
			
			process = new Process { StartInfo = psi };
			
			if(!process.Start()) {
				
				throw new InvalidOperationException("Process.Start() returned false — process did not start.");
			}
			
			scope.Record($"pid={process.Id}");
		}
		catch(Exception ex) when(ex is Win32Exception or InvalidOperationException) {
			
			scope.Record($"start failed: {ex.GetType().Name}");
			throw new InvalidOperationException("Failed to start dotnet process. Is dotnet installed and in PATH?", ex);
		}
		
		var sw = Stopwatch.StartNew();
		
		try {
			
			// Read both streams concurrently to avoid deadlocks on large output.
			var stdoutTask = process.StandardOutput.ReadToEndAsync();
			var stderrTask = process.StandardError.ReadToEndAsync();
			
			await process.WaitForExitAsync();
			sw.Stop();
			
			// Capture exit code BEFORE disposing.
			exitCode = process.ExitCode;
			scope.Record($"exit={exitCode} elapsed={sw.Elapsed.TotalSeconds:F1}s");
			
			string stdout, stderr;
			
			try {
				
				stdout = await stdoutTask;
				stderr = await stderrTask;
				scope.Record($"stdout={stdout.Length} stderr={stderr.Length} chars");
			}
			catch(IOException ex) {
				
				scope.Record($"read failed: {ex.Message}");
				throw new InvalidOperationException("Failed to read build output.", ex);
			}
			
			// MSBuild writes diagnostics to stdout; stderr is typically empty or SDK noise.
			var combined = string.IsNullOrWhiteSpace(stderr)
				? stdout
				: stdout + stderr
			;
			
			return (combined, sw.Elapsed, exitCode);
		}
		finally {
			process?.Dispose();
		}
	}
	
	private static BuildDiagnostic[] GetRoslynDiagnostics(Compilation compilation, string rootPath)
	{
		return [..
			compilation.GetDiagnostics()
				.Where(d => d.Severity >= DiagnosticSeverity.Warning)
				.Where(d => !IgnoredDiagnostics.Contains(d.Id))
				.Select(d => ConvertRoslynDiagnostic(d, rootPath))
		];
	}
	
	private static BuildDiagnostic ConvertRoslynDiagnostic(Diagnostic diagnostic, string rootPath)
	{
		var span     = diagnostic.Location.GetLineSpan();
		var filePath = span.Path;
		var relative = string.IsNullOrEmpty(filePath) ? "?" : TryMakeRelative(filePath, rootPath);
		var severity = diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning";
		
		return new BuildDiagnostic(
			Severity: severity,
			Code:     diagnostic.Id,
			Message:  diagnostic.GetMessage(),
			File:     relative,
			Line:     span.StartLinePosition.Line + 1,
			Column:   span.StartLinePosition.Character + 1
		);
	}
	
	private static BuildDiagnostic[] ParseMSBuildDiagnostics(string output, string rootPath)
	{
		var results = new List<BuildDiagnostic>();
		
		foreach(var raw in output.Split('\n')) {
			
			var line = raw.Trim();
			
			if(line.Length == 0)
				continue;
			
			var m = DiagnosticLine.Match(line);
			
			if(!m.Success)
				continue;
			
			var code     = m.Groups["code"].Value;
			var filePath = m.Groups["file"].Value.Trim();
			var relative = TryMakeRelative(filePath, rootPath);
			
			// Skip non-actionable SDK/tooling diagnostics.
			if(IgnoredDiagnostics.Contains(code))
				continue;
			
			results.Add(new BuildDiagnostic(
				Severity: m.Groups["severity"].Value.ToLowerInvariant(),
				Code:     code,
				Message:  m.Groups["message"].Value.Trim(),
				File:     relative,
				Line:     int.Parse(m.Groups["line"].Value),
				Column:   int.Parse(m.Groups["col"].Value)
			));
		}
		
		return [.. results];
	}
	
	private static string TryMakeRelative(string path, string rootPath)
	{
		try {
			return Path.GetRelativePath(rootPath, path);
		}
		catch(ArgumentException) {
			
			// Paths on different roots (e.g., different drives on Windows) — return absolute path.
			return path;
		}
	}
	
	private sealed record BuildDiagnostic(
		string Severity,
		string Code,
		string Message,
		string File,
		int    Line,
		int    Column
	);
}
