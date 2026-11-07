using System.ComponentModel;
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
	
	// Matches MSBuild diagnostic lines with source location:
	//   path(line,col): error CS0103: message [proj::TargetFramework=net10.0]
	//   path(line,col): warning CS8600: message [proj]
	// The optional <context> capture contains the bracket suffix content for TFM extraction.
	private static readonly Regex DiagnosticLine = new(
		@"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s+(?<severity>error|warning)\s+(?<code>\w+):\s+(?<message>.+?)(?:\s+\[(?<context>[^\]]+)\])?$",
		RegexOptions.Compiled | RegexOptions.IgnoreCase
	);
	
	// Matches project-level diagnostics without source location, in two forms:
	//   J:\...\proj.csproj : error NU1101: Unable to find package XYZ [TargetFramework=net10.0]
	//   MSBUILD : warning MSB3245: Could not resolve assembly reference.
	//   error NETSDK1045: The current .NET SDK does not support targeting .NET 10.
	// The file prefix + separator are optional to catch bare SDK/engine errors (no source path).
	// Whitespace around ':' (when present) avoids false matches on drive-letter colons in paths.
	// Code must be letters + digits (e.g. NU1101, MSB3245, NETSDK1045) to avoid false positives.
	private static readonly Regex ProjectLevelDiagnosticLine = new(
		@"^(?:(?<file>.+?)\s+:\s+)?(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+):\s+(?<message>.+?)(?<context>(?:\s+\[[^\]]+\])+)?$",
		RegexOptions.Compiled | RegexOptions.IgnoreCase
	);
	
	[McpServerTool(Name = "roslyn_build_project", Title = "Build Project", OpenWorld = false, Destructive = false)]
	[Description(
		"Fully validate the project — use this before committing or when you need confidence it completely builds. " +
		"Runs in two tiers: (1) Roslyn in-process C# type/symbol check — fast, no process spawn; " +
		"if errors are found, dotnet build is skipped and the Roslyn errors are returned immediately. " +
		"(2) 'dotnet build' when Roslyn is clean — catches what Roslyn cannot see: NuGet restore failures, " +
		"MSBuild target errors, SDK version issues, and source generator problems. " +
		"Does not modify source files. " +
		"To check whether code compiles, use roslyn_get_diagnostics instead — it answers in-process, without spawning a build. " +
		"Build once per solution, not once per project: projects in one solution share build output, so omit projectPath " +
		"to build the whole default solution in a single run. Do not issue several builds in parallel — the server runs " +
		"one dotnet build/clean/restore at a time and queues the rest, so parallel calls only wait. " +
		"Requires a .csproj to be present. " +
		"NuGet/MSBuild errors (NU*, MSB*) without a source location appear in errors[] with line: 0, column: 0. " +
		"exit_code is null when build_skipped is true (no dotnet build ran). " +
		"When succeeded is false but errors is empty, check the error_details field — it contains the raw build " +
		"output tail (last 30 lines) and explains the failure (e.g. locked output file, linker error). " +
		"Do NOT run dotnet build in a terminal to investigate — error_details already has the output you need.")]
	public async Task<object> BuildProject(
		CancellationToken cancellationToken,
		[Description(OptionalProjectPathDescription)] string? projectPath = null,
		[Description("Target framework to build, e.g. 'net10.0'. Omit to build every target framework. When given, the Roslyn check also covers only that framework.")] string? targetFramework = null,
		[Description(
			"Default false: Roslyn errors short-circuit — dotnet build only runs when C# is clean, " +
			"validating NuGet restore, MSBuild targets, SDK props, and source generators. " +
			"Set true only when you suspect an MSBuild-specific failure Roslyn cannot see " +
			"(broken .targets file, generator crash, restore failure), or when roslyn_check_drift reports " +
			"workspace_healthy: false — skips the Roslyn fast-path entirely. Never set it just to confirm code compiles."
		)] bool forceBuild = false)
	{
		using var scope = BeginTool("roslyn_build_project", null, new { targetFramework, forceBuild });
		
		if(!TryResolveProjectArg(projectPath, out projectPath, out var projectError))
			
			return scope.Error(projectError);
		
		if(!TryResolveWorkspaceInfo(projectPath, out var rootPath, out var isMSBuild, out var csprojPath, out var wsError))
			
			return scope.Error(wsError);
		
		if(!isMSBuild)
			
			return scope.Error(new ErrorResult("No .csproj found — build is only available in MSBuildWorkspace mode."));
		
		// A solution target builds — and fast-path checks — every project in it, not just the first
		// one the workspace lists.
		var solutionPath = ResolvedSolutionPath(projectPath);
		var buildTarget  = ResolvedTargetPath(projectPath, csprojPath);
		
		// A workspace that loaded without metadata references reports a flood of phantom
		// CS0246/CS0234 for code that compiles fine, and one whose load failed on a project file can
		// have default options (Nullable off: phantom CS8632, #300). Short-circuiting on those would
		// fail a healthy project — and worse, this tool is what callers reach for to check whether the
		// workspace is lying, so repeating the lie leaves them with no way out short of knowing about
		// forceBuild. Skip straight to the real build instead (issue #235).
		var health = TryGetHealth(projectPath)
		;
		
		// A project target is only as unhealthy as what it is built from (WorkspaceHealth.Covering); a
		// solution target builds everything, so all of it counts. Narrowed against the workspace as it
		// stands: asking for the project would run a pending reload, seconds of MSBuild that a build has
		// no use for.
		if(health is not null && solutionPath is null && csprojPath is not null)
			health = health.Covering(workspace.PeekSolution(projectPath), [csprojPath]);
		
		var unhealthyWorkspace = health is { IsHealthy: false };
		
		// A .csproj the loaded workspace does not hold — one its solution does not list — has no
		// compilation for Roslyn to check, and checking another project's in its place would let that
		// project's errors block this build (#384). dotnet builds it all the same.
		var outsideWorkspace = solutionPath is null && csprojPath is null;
		
		if(outsideWorkspace)
			
			scope.Record("target is not a project of the loaded workspace — Roslyn check skipped");

		// Fast path: check Roslyn diagnostics first (unless forceBuild=true, the target is outside the
		// loaded workspace, or the workspace itself is unhealthy and its diagnostics cannot be trusted).
		if(!forceBuild && !unhealthyWorkspace && !outsideWorkspace) {

			// Every target the path stands for, as roslyn_get_diagnostics checks it (#383): every project of
			// a solution, and every target framework of a multi-targeted project — one Roslyn project per
			// framework, each compiled on its own, so an error under one framework's #if is not missed.
			// dotnet build -f builds that framework only, so an error under another one must not block it;
			// those are left out before they are compiled. A project without a framework suffix targets one
			// framework, which -f applies to as well. With nothing left, dotnet decides.
			var (targets, targetError) = await GetTargetsAsync(projectPath, forFile: false,
				project => targetFramework is null || FrameworkOf(project) is not { } framework
					|| string.Equals(framework, targetFramework, StringComparison.OrdinalIgnoreCase),
				cancellationToken)
			;
			
			if(targetError is not null)
				
				return scope.Error(targetError);
			
			var aggregated = new List<DiagnosticItem>();
			
			foreach(var (compilation, _) in targets)
				aggregated.AddRange(GetRoslynDiagnostics(compilation, rootPath));
			
			// A file linked into several projects, and every file of a multi-targeted project, is compiled —
			// and diagnosed — once per target, with identical items where the targets agree (the fast path
			// carries no per-project or TFM context to tell them apart).
			// Record equality is exact here because TargetFrameworks is always null on this path.
			DiagnosticItem[] roslynDiagnostics = [..aggregated.Distinct()];
			
			var roslynErrors      = roslynDiagnostics.Where(d => d.Severity == "error").ToArray();
			
			if(roslynErrors.Length > 0) {
				
				DiagnosticItem[] roslynWarnings = [.. roslynDiagnostics.Where(d => d.Severity == "warning")];
				
				return scope.Failed("Roslyn reported errors — fix these first, then call roslyn_build_project again.", new BuildResult(
					Succeeded:    false,
					Errors:       roslynErrors,
					Warnings:     roslynWarnings,
					Source:       "roslyn",
					BuildSkipped: true,
					SkipReason:   "Roslyn reported errors — fix these first, then call roslyn_build_project again.",
					DurationMs:   0,
					ExitCode:     null
				));
			}
		}
		
		// Slow path: run actual dotnet build.
		var args = BuildArgs(buildTarget, targetFramework)
		;
		
		string		output;
		TimeSpan	elapsed;
		int			exitCode;
		
		try {
			(output, elapsed, exitCode) = await RunDotnetAsync(args, rootPath, scope, cancellationToken);
		}
		catch(OperationCanceledException) {
			
			// Logged as a cancellation rather than an unhandled exception; the SDK answers the client.
			scope.Failed("cancelled");
			throw;
		}
		catch(InvalidOperationException ex) {
			
			return scope.Failed(ex.Message, new BuildResult(
				Succeeded:    false,
				Errors:       (DiagnosticItem[]) [],
				Warnings:     (DiagnosticItem[]) [],
				Source:       "msbuild",
				BuildSkipped: true,
				SkipReason:   ex.Message,
				DurationMs:   0,
				ExitCode:     null,
				ErrorDetails: ex.InnerException?.Message
			));
		}
		
		var diagnostics = ParseMSBuildDiagnostics(output, rootPath);
		
		DiagnosticItem[] errors   = [.. diagnostics.Where(d => d.Severity == "error")  ];
		DiagnosticItem[] warnings = [.. diagnostics.Where(d => d.Severity == "warning")]
		;
		
		// dotnet build sometimes exits with a non-zero code despite a clean compilation —
		// MSBuild analyzer diagnostics (MSBL*, NU*) can set the exit code without emitting
		// a parseable CS error line. Trust the output text over the exit code: if the output
		// says "Build succeeded." and we found no structured errors, the build succeeded.
		var buildSucceededText = output.Contains("Build succeeded.", StringComparison.OrdinalIgnoreCase)
		;
		var succeeded          = exitCode == 0 || (errors.Length == 0 && buildSucceededText);
		
		// When genuinely failed with no structured errors (locked file, linker, restore),
		// surface the raw output tail so agents don't need to run dotnet build themselves.
		var errorDetails = !succeeded && errors.Length == 0
			? TailLines(output, 30)
			: null
		;
		
		return scope.Outcome("msbuild", new BuildResult(
			succeeded,
			errors,
			warnings,
			Source:        "msbuild",
			BuildSkipped: false,
			SkipReason:   null,
			DurationMs:   (int) elapsed.TotalMilliseconds,
			ExitCode:     exitCode,
			ErrorDetails: errorDetails
		) {
			// Say so explicitly: a build that succeeds while roslyn_get_diagnostics reports
			// hundreds of errors is otherwise baffling.
			Hint = unhealthyWorkspace
				? $"The Roslyn fast-path was skipped: the workspace {health!.Problem}, so its diagnostics are phantom. "
					+ "This result comes from a real dotnet build. Call roslyn_respawn to reload the workspace."
				: null
		});
	}
	
	static string[] BuildArgs(string csprojPath, string? tfm)
	{
		// --no-restore: restore is separate; /v:quiet: only errors/warnings + summary line.
		// -tl:off: disable terminal logger — it activates even with redirected output in some
		// SDK versions and produces an indented format that breaks the DiagnosticLine regex.
		var args = new List<string> { "build", csprojPath, "--no-restore", "/nologo", "/v:quiet", "-tl:off" }
		;
		
		if(tfm is not null) {
			
			args.Add("-f");
			args.Add(tfm);
		}
		
		return [.. args];
	}
	
	/// <summary>
	///     The target framework of one Roslyn project of a multi-targeted project, which MSBuildWorkspace
	///     names <c>Name(tfm)</c>; <see langword="null"/> for a project without the suffix, which targets
	///     one framework.
	/// </summary>
	static string? FrameworkOf(Project project)
	{
		var name = project.Name;
		
		if(!name.EndsWith(')'))
			
			return null;
		
		var open = name.LastIndexOf('(');
		
		// Every TFM starts with "net" (net48, netstandard2.0, net10.0-windows); a project whose own name
		// merely ends in parentheses is not one framework of several.
		return open > 0 && name.AsSpan(open + 1).StartsWith("net", StringComparison.OrdinalIgnoreCase) ? name[(open + 1)..^1] : null;
	}
	
	async Task<(string output, TimeSpan elapsed, int exitCode)> RunDotnetAsync(string[] args, string workingDirectory, ToolScope scope, CancellationToken cancellationToken)
		=> await DotnetRunner.RunAsync(args, workingDirectory, scope.Record, cancellationToken);
	
	private static DiagnosticItem[] GetRoslynDiagnostics(Compilation compilation, string rootPath)
	{
		return [..
			compilation.GetDiagnostics()
				.Where(d => IsUnderRoot(d, rootPath))
				.Where(d => d.Severity >= DiagnosticSeverity.Warning)
				.Where(d => !IgnoredDiagnostics.Contains(d.Id))
				.Select(d => ConvertRoslynDiagnostic(d, rootPath))
				.DistinctBy(d => (d.Severity, d.Code, d.File, d.Line, d.Column, d.Message))
				.OrderByDescending(d => d.Severity == "error")
				.ThenBy(d => d.File)
				.ThenBy(d => d.Line)
				.ThenBy(d => d.Column)
				.ThenBy(d => d.Code)
				.ThenBy(d => d.Message)
		];
	}
	
	private static DiagnosticItem ConvertRoslynDiagnostic(Diagnostic diagnostic, string rootPath)
	{
		var span     = diagnostic.Location.GetLineSpan();
		var severity = diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning";
		
		return new DiagnosticItem(
			Code:     diagnostic.Id,
			Severity: severity,
			File:     TryMakeRelative(span.Path, rootPath),
			Line:     span.StartLinePosition.Line + 1,
			Column:   span.StartLinePosition.Character + 1,
			Message:  diagnostic.GetMessage()
		);
	}
	
	private static DiagnosticItem[] ParseMSBuildDiagnostics(string output, string rootPath)
	{
		var results = new List<(DiagnosticItem Item, string? Context)>();
		
		foreach(var raw in output.Split('\n')) {
			
			var line = raw.Trim();
			
			if(line.Length == 0)
				continue;
			
			var m = DiagnosticLine.Match(line);
			
			if(m.Success) {
				
				var code = m.Groups["code"].Value;
				
				if(IgnoredDiagnostics.Contains(code))
					continue;
				
				var context = m.Groups["context"].Success ? m.Groups["context"].Value : null;
				
				results.Add((new DiagnosticItem(
					Code:     code,
					Severity: m.Groups["severity"].Value.ToLowerInvariant(),
					File:     TryMakeRelative(m.Groups["file"].Value.Trim(), rootPath),
					Line:     int.Parse(m.Groups["line"].Value),
					Column:   int.Parse(m.Groups["col"].Value),
					Message:  m.Groups["message"].Value.Trim()
				), context));
				
				continue;
			}
			
			// Project-level diagnostics have no source location (NU*, MSB*, etc.).
			// Try the looser pattern so these appear in structured errors[] rather than
			// being buried in error_details.
			m = ProjectLevelDiagnosticLine.Match(line)
			;
			
			if(!m.Success)
				continue;
			
			var projCode = m.Groups["code"].Value;
			
			if(IgnoredDiagnostics.Contains(projCode))
				continue;
			
			var projContext = m.Groups["context"].Success ? m.Groups["context"].Value : null;
			
			results.Add((new DiagnosticItem(
				Code:     projCode,
				Severity: m.Groups["severity"].Value.ToLowerInvariant(),
				File:     TryMakeRelative(m.Groups["file"].Value.Trim(), rootPath),
				Line:     0,
				Column:   0,
				Message:  m.Groups["message"].Value.Trim()
			), projContext));
		}
		
		// Multi-target builds emit each diagnostic once per TFM — group by identity and
		// aggregate target framework names from the MSBuild bracket suffix. Sort TFMs for
		// deterministic output across MSBuild evaluation orders.
		
		return [..
			results
				.GroupBy(r => (r.Item.Severity, r.Item.Code, r.Item.File, r.Item.Line, r.Item.Column, r.Item.Message))
				.Select(g => {
					
					var tfms = g
						.Select(r => ExtractTargetFramework(r.Context))
						.OfType<string>()
						.Distinct(StringComparer.OrdinalIgnoreCase)
						.Order(StringComparer.OrdinalIgnoreCase)
						.ToArray()
						;
					
					return g.First().Item with { TargetFrameworks = tfms.Length > 0 ? tfms : null };
				})
				.OrderByDescending(d => d.Severity == "error")
				.ThenBy(d => d.File)
				.ThenBy(d => d.Line)
				.ThenBy(d => d.Column)
				.ThenBy(d => d.Code)
				.ThenBy(d => d.Message)
		];
	}
	
	// Extracts the TargetFramework value from an MSBuild bracket suffix context string.
	// Context format: "path/to/proj.csproj::TargetFramework=net10.0"
	// Returns null when the context is absent or carries no TargetFramework entry.
	private static string? ExtractTargetFramework(string? context)
	{
		if(context is null)
			
			return null;
		
		var idx = context.IndexOf("TargetFramework=", StringComparison.OrdinalIgnoreCase);
		
		if(idx < 0)
			
			return null;
		
		var value = context[(idx + "TargetFramework=".Length)..].Trim();
		
		// Context may include the surrounding brackets (e.g. " [proj::TargetFramework=net10.0]"),
		// so strip everything from the first terminator character onwards.
		var end = value.IndexOfAny([']', ':', ';', ' '])
		;
		
		return end >= 0 ? value[..end] : value;
	}
	
	
	private static string TailLines(string output, int count)
	{
		ReadOnlySpan<char> span  = output.AsSpan().Trim();
		var                lines = new List<Range>(count + 4);
		var                start = 0;
		
		for(var i = 0; i <= span.Length; i++) {
			
			if(i == span.Length || span[i] == '\n') {
				
				lines.Add(new Range(start, i));
				start = i + 1;
			}
		}
		
		var tail = lines.Count <= count
			? span
			: span[lines[^count].Start..]
		;
		
		return tail.Trim().ToString();
	}

}
