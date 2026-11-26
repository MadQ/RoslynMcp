using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
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
		"Returns succeeded, error_count, warning_count and the errors and warnings grouped as roslyn_get_diagnostics groups its findings: " +
		"files[] by file, in[] by the declaration each is in ('method Enqueue', with its lines to pass to roslyn_read_file and where its node starts " +
		"for roslyn_replace_in_code), and findings[] once per code and message with places[] of line, column and the line's text, " +
		"so an error can often be fixed without reading the file. " +
		"NuGet/MSBuild errors (NU*, MSB*) without a source location are under general, each with the project file MSBuild named as project. " +
		"exit_code is null when build_skipped is true (no dotnet build ran). " +
		"When succeeded is false but error_count is 0, check the error_details field — it contains the raw build " +
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
		
		// Never restored: the build below runs with --no-restore and cannot succeed — it would hold the
		// MSBuild gate for its whole length to fail with NETSDK1004 (#399 review). The advice is the
		// answer. forceBuild still builds, as asked. Not for a target outside the loaded workspace: its
		// health was not narrowed to it, so the workspace's other projects would decide for it.
		if(!forceBuild && !outsideWorkspace && health is { OnlyNeedsRestore: true }) {
			
			var reason = $"The workspace {health.Problem}. Builds run with --no-restore, so this one could only fail: call "
				+ "roslyn_restore_packages first, then roslyn_build_project again.";
			
			return scope.Failed("not restored — build skipped", Result(
				succeeded:    false,
				findings:     [],
				source:       "roslyn",
				buildSkipped: true,
				skipReason:   reason,
				durationMs:   0,
				exitCode:     null,
				errorDetails: null
			));
		}
		
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
			
			var aggregated = new List<DiagnosticPlace>();
			
			foreach(var (compilation, _) in targets)
				aggregated.AddRange(GetRoslynDiagnostics(compilation, rootPath, cancellationToken));
			
			// A file linked into several projects, and every file of a multi-targeted project, is compiled —
			// and diagnosed — once per target, with identical places where the targets agree (the fast path
			// carries no per-project or TFM context to tell them apart).
			// Record equality is exact here: a place holds plain values, and no frameworks on this path.
			var roslynDiagnostics = DiagnosticPlace.Sorted(aggregated.Distinct());
			var roslynHasErrors   = roslynDiagnostics.Any(d => d.Severity == "error");
			
			// Health again, of the compilations just checked: getting them can wait for a reload, and a
			// reload may promote an unhealthy workspace — past the discard cap, or one that breaks nothing
			// the live one had working. The health read above describes the workspace before it, and its
			// errors would then stop a build they have no say in (#300 review).
			if(roslynHasErrors
			   && TryGetHealth(projectPath) is { } now
			   && targets.FirstOrDefault(target => target.Project is not null).Project is { } first
			   && now.Covering(first.Solution, targets.Select(target => target.Project?.FilePath).OfType<string>()) is { IsHealthy: false } narrowed) {
				
				health             = narrowed;
				unhealthyWorkspace = true;
				
				scope.Record("workspace turned unhealthy while the Roslyn check ran — building instead");
			}
			else if(roslynHasErrors) {
				
				// How the findings grouped, and what they took as flat items (#487).
				scope.Record(DiagnosticPlace.GroupingNote(roslynDiagnostics));
				
				return scope.Failed("Roslyn reported errors — fix these first, then call roslyn_build_project again.", Result(
					succeeded:    false,
					findings:     roslynDiagnostics,
					source:       "roslyn",
					buildSkipped: true,
					skipReason:   "Roslyn reported errors — fix these first, then call roslyn_build_project again.",
					durationMs:   0,
					exitCode:     null,
					errorDetails: null
				));
			}
		}
		
		// Slow path: run actual dotnet build. A solution built with -f fails for every project that
		// does not target that framework (NETSDK1005), so with both given the projects that do are
		// built one after the other; what they reference is built with them (#298). With none found
		// the solution is handed to dotnet as it is, which then says what is wrong.
		string[] buildTargets = solutionPath is not null && targetFramework is not null && !outsideWorkspace
			&& ProjectsTargeting(workspace.PeekSolution(projectPath), targetFramework) is { Length: > 0 } targeting
				? targeting
				: [buildTarget]
		;
		
		if(buildTargets.Length > 1 || buildTargets[0] != buildTarget)
			scope.Record($"{buildTargets.Length} project(s) target {targetFramework}");
		
		var output   = "";
		var elapsed  = TimeSpan.Zero;
		var exitCode = 0;
		
		// The output of the first build that failed, for the tail shown when no error line parses.
		string? failedOutput = null;
		
		try {
			
			foreach(var target in buildTargets) {
				
				var (text, took, code) = await RunDotnetAsync(BuildArgs(target, targetFramework), rootPath, scope, cancellationToken);
				
				output  += text;
				elapsed += took;
				
				// Each build is judged on its own output: read from the joined text, one project's
				// "Build succeeded." would pass for another's failure. The first failure is the one
				// reported; the projects after it are still built, as a solution build goes on past
				// a project that fails.
				if(failedOutput is null && !BuildSucceeded(text, code, rootPath)) {
					
					failedOutput = text;
					exitCode     = code;
				}
				else if(failedOutput is null && exitCode == 0) {
					
					// The code dotnet gave, also for a build the output shows to have succeeded.
					exitCode = code;
				}
			}
		}
		catch(OperationCanceledException) {
			
			// Logged as a cancellation rather than an unhandled exception; the SDK answers the client.
			scope.Failed("cancelled");
			throw;
		}
		catch(InvalidOperationException ex) {
			
			return scope.Failed(ex.Message, Result(
				succeeded:    false,
				findings:     [],
				source:       "msbuild",
				buildSkipped: true,
				skipReason:   ex.Message,
				durationMs:   0,
				exitCode:     null,
				errorDetails: ex.InnerException?.Message
			));
		}
		
		// What the build said, each place with the text of its line and the declaration it is
		// in (#487). Without a boundary to ask, no file is read and the places stay bare.
		SecurityBoundary? boundary = null;
		Solution?         loaded   = null;
		
		if(TryResolveFileContext(projectPath, out _, out var allowed, out _))
			boundary = allowed;
		else
			scope.Record("the workspace's boundary could not be resolved: the build's places are listed without their lines' text");
		
		// The solution only says how a file is parsed (the symbols of its project's #if); a
		// target outside the workspace has none, and its files are parsed without. The
		// snapshot the workspace holds, not a current one: asking for that waits for a reload
		// the build itself may have caused, and no reload changes how a file is parsed here.
		if(boundary is not null && !outsideWorkspace)
			loaded = workspace.PeekSolution(projectPath);
		
		var diagnostics = await LocateAsync(ParseMSBuildDiagnostics(output, rootPath), boundary, loaded, rootPath, cancellationToken);
		
		scope.Record(DiagnosticPlace.GroupingNote(diagnostics));
		
		var succeeded = failedOutput is null;
		
		// When genuinely failed with no structured errors (locked file, linker, restore),
		// surface the raw output tail so agents don't need to run dotnet build themselves. Asked
		// of the failed build's own output: another project's errors do not explain this one.
		var errorDetails = failedOutput is not null && !ParseMSBuildDiagnostics(failedOutput, rootPath).Any(d => d.Severity == "error")
			? TailLines(failedOutput, 30)
			: null
		;
		
		return scope.Outcome("msbuild", Result(
			succeeded,
			diagnostics,
			source:       "msbuild",
			buildSkipped: false,
			skipReason:   null,
			durationMs:   (int) elapsed.TotalMilliseconds,
			exitCode:     exitCode,
			errorDetails: errorDetails
		) with {
			// Say so explicitly: a build that succeeds while roslyn_get_diagnostics reports
			// hundreds of errors is otherwise baffling.
			Hint = unhealthyWorkspace && health!.OnlyNeedsRestore
				? $"The Roslyn fast-path was skipped: the workspace {health.Problem}. This result comes from a real "
					+ "dotnet build (forceBuild), which runs with --no-restore."
				: unhealthyWorkspace
				? $"The Roslyn fast-path was skipped: the workspace {health!.Problem}, so its diagnostics may be phantom. "
					+ "This result comes from a real dotnet build. If it fails the same way, the project file itself "
					+ "may be broken (see load_warnings from roslyn_get_project_info); if it succeeds, call "
					+ "roslyn_respawn to reload the workspace."
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
	
	/// <summary>
	///     Whether one dotnet build succeeded. dotnet build sometimes exits with a non-zero code
	///     despite a clean compilation: MSBuild analyzer diagnostics (MSBL*, NU*) can set the exit
	///     code without emitting a parseable CS error line. The output text is trusted over the exit
	///     code: "Build succeeded." with no structured error is a build that succeeded.
	/// </summary>
	static bool BuildSucceeded(string output, int exitCode, string rootPath)
		=> exitCode == 0
			|| (output.Contains("Build succeeded.", StringComparison.OrdinalIgnoreCase)
				&& !ParseMSBuildDiagnostics(output, rootPath).Any(d => d.Severity == "error"))
	;
	
	/// <summary>
	///     The project files of a solution to build for a framework: those that target it, each
	///     once, without the ones another of them references, since building a project builds what
	///     it references. A multi-targeted project says what it targets in its name
	///     (<see cref="FrameworkOf"/>). A project with one framework does not, and is told by the
	///     framework directory of its output path; one whose output path shows none is kept, and
	///     dotnet decides, as it did for every project before.
	/// </summary>
	static string[] ProjectsTargeting(Solution solution, string targetFramework)
	{
		var targeting = new List<Project>();
		
		foreach(var project in solution.Projects) {
			
			if(project.FilePath is not { } file || targeting.Any(taken => SecurityBoundary.PathComparer.Equals(taken.FilePath, file)))
				continue;
			
			var targets = FrameworkOf(project) is { } framework
				? string.Equals(framework, targetFramework, StringComparison.OrdinalIgnoreCase)
				: FrameworkDirectories(project) is not { Length: > 0 } directories
					|| directories.Contains(targetFramework, StringComparer.OrdinalIgnoreCase)
			;
			
			if(targets)
				targeting.Add(project);
		}
		
		var graph = solution.GetProjectDependencyGraph();
		
		// By id, which is one framework of a project: a reference to another framework of a
		// multi-targeted project does not build the one asked for.
		var referenced = targeting
			.SelectMany(project => graph.GetProjectsThatThisProjectTransitivelyDependsOn(project.Id))
			.ToHashSet()
		;
		
		return [..targeting.Where(project => !referenced.Contains(project.Id)).Select(project => project.FilePath!)];
	}
	
	/// <summary>
	///     The directories of a project's output path that are named like a target framework
	///     (<c>bin/Debug/net10.0/</c>), looked for below the project's own directory only: a
	///     checkout under <c>C:\net48-legacy\</c> says nothing about the project. Empty when the
	///     output lies elsewhere or shows no framework.
	/// </summary>
	static string[] FrameworkDirectories(Project project)
	{
		if(project.OutputFilePath is not { } outputPath || Path.GetDirectoryName(project.FilePath) is not { } projectDirectory)
			
			return [];
		
		var below = Path.GetRelativePath(projectDirectory, Path.GetDirectoryName(outputPath) ?? outputPath);
		
		if(Path.IsPathRooted(below) || below.StartsWith("..", StringComparison.Ordinal))
			
			return [];
		
		return [..below.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Where(IsFrameworkName)];
	}
	
	/// <summary>
	///     Whether a directory name reads as a target framework: "net" and a digit, or
	///     "netstandard"/"netcoreapp", and not a name that merely starts with net.
	/// </summary>
	static bool IsFrameworkName(string part)
		=> part.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase)
			|| part.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase)
			|| (part.Length > 3 && part.StartsWith("net", StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(part[3]))
	;
	
	async Task<(string output, TimeSpan elapsed, int exitCode)> RunDotnetAsync(string[] args, string workingDirectory, ToolScope scope, CancellationToken cancellationToken)
		=> await DotnetRunner.RunAsync(args, workingDirectory, scope.Record, cancellationToken);
	
	// The errors and warnings of one compilation as places, each with the text of its line and
	// the declaration it is in; the caller sorts them, once for all targets.
	private static IEnumerable<DiagnosticPlace> GetRoslynDiagnostics(Compilation compilation, string rootPath, CancellationToken cancellationToken)
		=> compilation.GetDiagnostics(cancellationToken)
			.Where(d => IsUnderRoot(d, rootPath))
			.Where(d => d.Severity >= DiagnosticSeverity.Warning)
			.Where(d => !IgnoredDiagnostics.Contains(d.Id))
			.Select(d => DiagnosticPlace.Of(d, null, TryMakeRelative(d.Location.GetLineSpan().Path, rootPath), cancellationToken))
	;
	
	/// <summary>
	///     The answer for a build, or for the check that stood in for one, with its findings counted
	///     and listed as <c>roslyn_get_diagnostics</c> lists them (#487).
	/// </summary>
	static BuildResult Result(bool succeeded, DiagnosticPlace[] findings, string source, bool buildSkipped, string? skipReason, long durationMs, int? exitCode, string? errorDetails)
		=> new(
			succeeded,
			findings.Count(finding => finding.Severity == "error"),
			findings.Count(finding => finding.Severity == "warning"),
			DiagnosticPlace.Group(findings),
			DiagnosticPlace.General(findings),
			source,
			buildSkipped,
			skipReason,
			durationMs,
			exitCode,
			errorDetails)
	;
	
	/// <summary>
	///     The diagnostics a build printed, as places. MSBuild names a file, a line and a column;
	///     for a C# file the place gets the text of the line and the declaration it is in. They
	///     are read from the file on disk and not from the workspace: the disk is what the build
	///     compiled, a generated file under obj is no stranger to it, and a file written a moment
	///     before the build is not a document yet. Only a file the workspace's boundary allows is
	///     read; any other keeps its line and column alone. A diagnostic with no line is about
	///     the project and not about a place in a file: it has no file, and the file MSBuild
	///     named, the project's, is its <c>project</c>.
	/// </summary>
	static async Task<DiagnosticPlace[]> LocateAsync(DiagnosticItem[] items, SecurityBoundary? boundary, Solution? solution, string rootPath, CancellationToken cancellationToken)
	{
		var places = new List<DiagnosticPlace>(items.Length);
		
		// Each file once, however many diagnostics name it; null for one that is not read. By
		// the name as the build wrote it, exactly: two names that differ in case are two files
		// where the directory says so, and one file read twice is all a stricter key costs.
		var sources = new Dictionary<string, (SourceText Text, SyntaxNode Root)?>(StringComparer.Ordinal);
		
		foreach(var item in items) {
			
			var rank       = item.Severity == "error" ? (int) DiagnosticSeverity.Error : (int) DiagnosticSeverity.Warning;
			var frameworks = item.TargetFrameworks is { Length: > 0 } named ? string.Join(',', named) : null;
			
			if(item.Line <= 0 || item.File is null) {
				
				places.Add(new(item.Code, item.Severity, rank, item.Message, null, item.File, 0, 0, null, null) { Frameworks = frameworks });
				continue;
			}
			
			string?               text        = null;
			EnclosingDeclaration? declaration = null;
			
			if(!sources.TryGetValue(item.File, out var known))
				sources[item.File] = known = await ReadSourceAsync(item.File, rootPath, boundary, solution, cancellationToken);
			
			// A line past the end: the file changed since the build read it.
			if(known is var (source, root) && item.Line <= source.Lines.Count) {
				
				var line   = source.Lines[item.Line - 1];
				var column = Math.Clamp(item.Column - 1, 0, line.Span.Length);
				
				text        = SearchFilesTool.Excerpt(source.ToString(line.Span), column);
				declaration = EnclosingDeclaration.At(root, source, line.Start + column).Declaration;
			}
			
			places.Add(new(item.Code, item.Severity, rank, item.Message, item.File, null, item.Line, item.Column, text, declaration) { Frameworks = frameworks });
		}
		
		return DiagnosticPlace.Sorted(places);
	}
	
	// A C# file a build diagnostic names, parsed for its declarations; null when it is not
	// C#, lies where the workspace may not read, or cannot be read. The path comes from the
	// build's output, so the boundary is asked as for a path a caller gave, and a path that is
	// none is a file not read, never a build's answer lost.
	// Parsed as its project parses it, where the workspace has the file as a document: with
	// no symbols defined, a member inside '#if DEBUG' or '#if NET10_0' is text the parser skips,
	// and an error in it was listed under the type. A file of several target frameworks is
	// parsed as the first of them does.
	static async Task<(SourceText Text, SyntaxNode Root)?> ReadSourceAsync(string file, string rootPath, SecurityBoundary? boundary, Solution? solution, CancellationToken cancellationToken)
	{
		if(boundary is null || !IsCSharpSourcePath(file))
			
			return null;
		
		try {
			
			var fullPath = Path.GetFullPath(Path.Combine(rootPath, file));
			
			if(!boundary.IsPathAllowed(fullPath))
				
				return null;
			
			var options = solution?.GetDocumentIdsWithFilePath(fullPath) is [var id, ..]
				? solution.GetDocument(id)?.Project.ParseOptions as CSharpParseOptions
				: null;
			
			var text = SourceText.From(await FileReader.ReadAllTextAsync(fullPath, FileReader.DocumentLimit));
			
			return (text, await CSharpSyntaxTree.ParseText(text, options, cancellationToken: cancellationToken).GetRootAsync(cancellationToken));
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
			
			return null;
		}
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
