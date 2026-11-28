using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class ProjectInfoTool : RoslynMcpTool
{
	public ProjectInfoTool(WorkspaceResolver workspace, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache) { }
	
	// Matches a TFM segment in a path like ...\net8.0\... or .../net11.0/...
	private static readonly Regex TfmPattern = new(@"[/\\](net\d+\.\d+(?:-\w+)?)[/\\]", RegexOptions.Compiled)
	;
	
	// Matches NuGet package paths: ...\.nuget\packages\<name>\<version>\...
	private static readonly Regex NuGetPattern = new(@"[/\\]packages[/\\]([^/\\]+)[/\\]([^/\\]+)[/\\]", RegexOptions.Compiled | RegexOptions.IgnoreCase)
	;
	
	[
		McpServerTool(Name = "roslyn_get_project_info", ReadOnly = true, Title = "Get Project Info", OpenWorld = false, Idempotent = true)
	,	Description(
			"Use this to inspect project metadata — target framework, language version, nullable context, " +
			"output kind, NuGet package references, and additional files — and to ask what an MSBuild property evaluates to. " +
			"Prefer this over reading the .csproj directly: a project file does not say what Directory.Build.props, a condition or the SDK make of it. " +
			"When an MSBuildWorkspace is active, target_frameworks (every framework the project is loaded for), root_namespace, " +
			"allow_unsafe_blocks and warnings_as_errors are what the loaded project has. " +
			"For one project (not a solution), version is the evaluated Version, and properties: ['RuntimeIdentifiers', 'OutputPath', ...] " +
			"returns the evaluated value of each property named, as a build would see it: MSBuild evaluates the project (dotnet msbuild, no target runs, about a second). " +
			"configuration and targetFramework say what to evaluate for, since a value can depend on both; without them the project's defaults apply. " +
			"A property that is not set has an empty value. A value that is an environment variable of the server process and that no project file sets is not given: the property says withheld. Each property comes with set_in: the elements that set it in the project file, in the " +
			"Directory.Build.props / Directory.Build.targets / Directory.Packages.props above it and in the files those import, with file, line, " +
			"condition and text, so the place to read or edit is known; is_value marks the element whose text is the evaluated value, and is absent " +
			"where the text needs evaluating. The SDK's own files are not searched. When the project cannot be evaluated (no project file, a legacy " +
			"project dotnet cannot evaluate, a failure), evaluation_unavailable says why and version is read from the .csproj XML as written. " +
			"By default (directOnly=true) package references are read directly from the .csproj XML, returning only " +
			"packages your project explicitly declares. Set directOnly=false to infer packages from resolved metadata " +
			"references — useful when no .csproj is available, but may include transitive dependencies. " +
			"Note: the target framework is inferred from the output path or metadata reference paths and may be null " +
			"if the project has not been built. " +
			"load_warnings (null when healthy) reports load-health issues: MSBuild workspace load failures and " +
			"projects whose metadata references were silently dropped by the design-time build — symbol results " +
			"over those projects may be incomplete; a server restart (fresh load) usually clears it. " +
			"For a solution (projectPath omitted with a solution as default workspace, or a .sln/.slnx given) the answer is " +
			"{ solution, projects: [...] } with every project in that same shape and version as the .csproj XML has it; " +
			"properties, configuration and targetFramework need one project, named by projectPath. " +
			"For build errors or NuGet restore issues, use roslyn_build_project instead.")
	]
	public async Task<object> GetProjectInfo(
		CancellationToken cancellationToken,
		[Description("When true (default), reads package references directly from the .csproj — fast and accurate for explicit references only. When false, infers packages from resolved metadata reference paths, which may include transitive dependencies.")] bool directOnly = true,
		[Description(OptionalProjectPathDescription)] string? projectPath = null,
		[Description("MSBuild property names to evaluate, e.g. ['Version', 'RuntimeIdentifiers', 'OutputPath']. Needs one project: name it with projectPath when the default workspace is a solution. Default: none.")] string[]? properties = null,
		[Description("The configuration to evaluate properties for, e.g. 'Release'. Default: the project's own default.")] string? configuration = null,
		[Description("The target framework to evaluate properties for, e.g. 'net10.0': a multi-targeted project sets some properties per framework. Default: none, the evaluation every framework shares.")] string? targetFramework = null)
	{
		using var scope = BeginTool("roslyn_get_project_info", null, new { directOnly, projectPath, properties, configuration, targetFramework });
		
		if(!TryResolveProjectArg(projectPath, out projectPath, out var projectError))
			
			return scope.Error(projectError);
		
		if(!TryResolveWorkspaceInfo(projectPath, out var rootPath, out var isMSBuild, out _, out var wsError))
			
			return scope.Error(wsError);
		
		string[] asked = [..(properties ?? []).Select(name => name.Trim()).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
		
		configuration   = configuration?.Trim() is { Length: > 0 } givenConfiguration ? givenConfiguration : null;
		targetFramework = targetFramework?.Trim() is { Length: > 0 } givenFramework ? givenFramework : null;
		
		if(ProjectEvaluation.Invalid(asked, configuration, targetFramework) is { } invalid)
			
			return scope.Error(new ErrorResult(invalid));
		
		var evaluating   = asked.Length > 0 || configuration is not null || targetFramework is not null;
		var loadWarnings = workspace.GetLoadWarnings(projectPath) is { Length: > 0 } warnings ? warnings : null;
		
		// A solution, given or the default one, is every project of it (#296): a multi-targeted
		// project once, since its frameworks share the project file the facts are read from.
		if(isMSBuild && ResolvedSolutionPath(projectPath) is { } solutionPath) {
			
			if(!TryResolveSolution(projectPath, out var solution, out var solutionError))
				
				return scope.Error(solutionError);
			
			var projectFiles = solution.Projects
				.Where(candidate => candidate.FilePath is not null)
				.DistinctBy(candidate => candidate.FilePath, SecurityBoundary.PathComparer)
				.ToArray()
			;
			
			// An evaluation is one MSBuild run per project, and what is asked about is one
			// project's property: which one has to be said.
			if(evaluating)
				
				return scope.Error(new ErrorResult(
					"properties, configuration and targetFramework are evaluated for one project, and the path given stands for a solution.",
					$"Pass projectPath with one of: {string.Join(", ", projectFiles.Take(20).Select(candidate => Path.GetRelativePath(rootPath, candidate.FilePath!)))}"));
			
			ProjectInfoResult[] projects = [..projectFiles.Select(candidate => Describe(candidate, rootPath, isMSBuild, directOnly, null, null))];
			
			// Said once, and for a project without a version too: that is the very case of a
			// version set in Directory.Build.props, which reads as "none" without a word.
			return scope.Outcome($"{projects.Length} project(s)", new SolutionInfoResult(Path.GetRelativePath(rootPath, solutionPath), projects, loadWarnings) {
				Caution = "Each project's version is what its project file writes, and absent where the file writes none: Directory.Build.props, conditions and the SDK are not taken into account for a solution. Name one project with projectPath for its evaluated version."
			});
		}
		
		if(!TryGetProject(projectPath, out var project, out var error))
			
			return scope.Error(error);
		
		var described = Describe(project, rootPath, isMSBuild, directOnly, AdhocCaution(projectPath), loadWarnings);
		
		if(!isMSBuild || project.FilePath is not { } projectFile)
			
			return scope.Outcome(project.Name, evaluating
				? described with { EvaluationUnavailable = "There is no project file to evaluate: the workspace was built from the source files of a directory (AdhocWorkspace)." }
				: described);
		
		if(!TryResolveFileContext(projectPath, out _, out var boundary, out var contextError))
			
			return scope.Error(contextError);
		
		// What the evaluated version stands on: kept while the workspace has not changed, so that
		// a call that asks for nothing else does not start MSBuild, and wait for its turn behind
		// a build, each time. Read before the evaluation: a change during it must not be missed.
		WorkspaceChangeCounter? changes = null;
		var                     at      = 0L;
		
		try {
			changes = workspace.GetChangeCounter(projectPath, reloadFirst: true);
			at      = changes.Value;
		}
		catch(Exception ex) when(ex is not OperationCanceledException) {
			scope.Record($"no change counter, version not kept: {ex.GetType().Name}");
		}
		
		if(!evaluating && changes is not null && ProjectEvaluation.KeptVersion(projectFile, changes) is { } kept)
			
			return scope.Outcome($"{project.Name}; version as last evaluated", described with { Version = kept.Length > 0 ? kept : null });
		
		ProjectEvaluation.Evaluated evaluated;
		
		try {
			// Version with whatever was asked for: the one fixed field the loaded project does not have.
			evaluated = await ProjectEvaluation.EvaluateAsync(projectFile, [..asked.Append("Version").Distinct(StringComparer.OrdinalIgnoreCase)], configuration, targetFramework, scope.Record, cancellationToken);
		}
		catch(OperationCanceledException) {
			// Logged as a cancellation rather than an unhandled exception; the SDK answers the client.
			scope.Failed("cancelled");
			throw;
		}
		
		if(evaluated.Values is not { } values)
			
			return scope.Outcome($"{project.Name}; not evaluated", described with {
				EvaluationUnavailable = evaluated.Failure,
				Caution               = described.Version is not null
					? "version is read from the .csproj XML as written: conditions, Directory.Build.props and the SDK are not taken into account."
					: "version is absent because the project could not be evaluated and its project file writes none; it may be set in Directory.Build.props."
			});
		
		var evaluatedVersion = values.GetValueOrDefault("Version", "");
		
		// The files are read for the properties asked for, and for a version that looks like the
		// server's own environment: whether a file sets it decides whether it is given.
		var files = asked.Length > 0 || ProjectEvaluation.IsEnvironmentValue("Version", evaluatedVersion) ? ProjectEvaluation.AuthoredFiles(projectFile, boundary) : [];
		
		// What is withheld from a property asked for (below) is withheld from the version too.
		var versionWithheld = Withheld("Version", evaluatedVersion, ProjectEvaluation.Sources(files, "Version", evaluatedVersion, rootPath));
		
		if(versionWithheld)
			evaluatedVersion = "";
		
		// Only the evaluation every later call without arguments would make.
		else if(changes is not null && configuration is null && targetFramework is null)
			ProjectEvaluation.KeepVersion(projectFile, changes, at, evaluatedVersion);
		
		// MSBuild hands out every environment variable of the process as a property, and the
		// server's environment is not the project's to read: a token, a key. A value that is the
		// variable's, with no element in the files that could have set it, is not given. Not the
		// configuration or the target framework this call itself passed: that value is the caller's.
		bool Withheld(string name, string value, PropertySourceInfo[] sources) =>
			ProjectEvaluation.IsEnvironmentValue(name, value)
			&& !sources.Any(source => source.IsValue != false)
			&& !(configuration is not null && name.Equals("Configuration", StringComparison.OrdinalIgnoreCase))
			&& !(targetFramework is not null && name.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase));
		
		const string WithheldReason = "The value is an environment variable of the server process, which no project file sets: it is not given.";
		
		return scope.Outcome($"{project.Name}; {asked.Length} propert{(asked.Length == 1 ? "y" : "ies")} evaluated", described with {
			Version      = evaluatedVersion.Length > 0 ? evaluatedVersion : null,
			Properties   = asked.Length > 0
				? [..asked.Select(name => {
					
					var value   = values.GetValueOrDefault(name, "");
					var sources = ProjectEvaluation.Sources(files, name, value, rootPath);
					
					return Withheld(name, value, sources)
						? new EvaluatedPropertyInfo(name, "", sources.Length > 0 ? sources : null) { Withheld = WithheldReason }
						: new EvaluatedPropertyInfo(name, value, sources.Length > 0 ? sources : null);
				})]
				: null,
			EvaluatedFor = configuration is not null || targetFramework is not null ? new(configuration, targetFramework) : null,
			Caution      = versionWithheld ? $"version is absent: {WithheldReason}" : described.Caution
		});
	}
	
	/// <summary>
	///     The facts of one project. <paramref name="adhocCaution"/> and <paramref name="loadWarnings"/>
	///     are the single-project call's; for a solution they are said once, on the whole answer.
	/// </summary>
	static ProjectInfoResult Describe(Project project, string rootPath, bool isMSBuild, bool directOnly, string? adhocCaution, string[]? loadWarnings)
	{
		var compOpts  = project.CompilationOptions  as CSharpCompilationOptions;
		var parseOpts = project.ParseOptions         as CSharpParseOptions;
		
		var tfm          = InferTfm(project);
		var outputKind   = compOpts?.OutputKind.ToString() ?? "Unknown";
		var nullable     = compOpts?.NullableContextOptions.ToString() ?? "Unknown";
		var langVersion  = parseOpts?.LanguageVersion.ToDisplayString() ?? "Unknown";
		var packages     = directOnly && project.FilePath is not null
			? ExtractDirectPackages(project.FilePath)
			: ExtractPackages(project.MetadataReferences)
		;
		string[] extraFiles = [..
			project.AdditionalDocuments
				.Select(d => Path.GetRelativePath(rootPath, d.FilePath ?? d.Name))
				.Order()
		];
		
		var fromProject = isMSBuild && project.FilePath is not null;
		
		// The version as the project file writes it: the one fact here the loaded project does
		// not have. A call for one project replaces it with the evaluated one (#244).
		var version = fromProject ? ReadVersion(project.FilePath!) : null;
		
		// What the design-time build made of the project, not what its XML says (#244): the XML's
		// first TargetFrameworks element was taken with its condition ignored, and a value set in
		// Directory.Build.props was not seen at all. A multi-targeted project is one project per
		// framework, named Foo(net8.0).
		string[]? frameworks = fromProject
			? [..project.Solution.Projects
				.Where(sibling => SecurityBoundary.PathComparer.Equals(sibling.FilePath, project.FilePath))
				.Select(sibling => BuildTool.FrameworkOf(sibling) ?? InferTfm(sibling))
				.OfType<string>()
				.Distinct(StringComparer.OrdinalIgnoreCase)]
			: null
		;
		
		
		return new ProjectInfoResult(
			project.Name,
			project.AssemblyName,
			project.FilePath is not null
				? Path.GetRelativePath(rootPath, project.FilePath)
				: null,
			tfm,
			langVersion,
			outputKind,
			nullable,
			isMSBuild,
			packages,
			extraFiles,
			version,
			fromProject ? project.DefaultNamespace : null,
			frameworks is { Length: > 0 } ? frameworks : null,
			fromProject ? compOpts?.AllowUnsafe : null,
			fromProject && compOpts is not null ? compOpts.GeneralDiagnosticOption == ReportDiagnostic.Error : null,
			loadWarnings)
		{
			Caution = adhocCaution
		};
	}
	
	private static string? InferTfm(Project project)
	{
		// Try output path first — most reliable, e.g. bin\Debug\net11.0\RoslynMcp.exe
		var candidate = project.OutputFilePath ?? project.FilePath;
		
		if(candidate is not null) {
			
			var m = TfmPattern.Match(candidate);
			
			if(m.Success)
				
				return m.Groups[1].Value;
		}
		
		// Fall back: scan metadata reference paths for a consistent TFM segment.
		foreach(var r in project.MetadataReferences) {
			
			if(r.Display is null)
				continue;
			
			var m = TfmPattern.Match(r.Display);
			
			if(m.Success)
				
				return m.Groups[1].Value;
		}
		
		return null;
	}
	
	private static PackageRef[] ExtractPackages(IReadOnlyList<MetadataReference> references)
	{
		// NuGet packages end up in the global packages cache — parse name+version from the path.
		// Framework assemblies (from dotnet/shared/...) are not NuGet packages and are skipped.
		
		return [..
			references
				.Select(r => r.Display)
				.Where(p => p is not null)
				.Select(p => {
					
					var m = NuGetPattern.Match(p!);
					
					return m.Success
						? new PackageRef(m.Groups[1].Value, m.Groups[2].Value)
						: null;
				})
				.Where(p => p is not null)
				.Select(p => p!)
				.DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
				.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
		]
		;
	}
	
	// The version as the project file's XML has it, in MSBuild's order: Version, then
	// VersionPrefix[-VersionSuffix], then AssemblyVersion. First element of each, conditions not
	// evaluated: the answer for a solution, and for a project that could not be evaluated.
	private static string? ReadVersion(string csprojPath)
	{
		try {
			
			var doc = FileReader.LoadXml(csprojPath);
			
			string? Prop(string name) =>
				doc.Descendants(name).FirstOrDefault()?.Value?.Trim() is { Length: > 0 } v ? v : null
			;
			
			var prefix = Prop("VersionPrefix");
			var suffix = Prop("VersionSuffix");
			
			return Prop("Version")
				?? (prefix is not null ? suffix is not null ? $"{prefix}-{suffix}" : prefix : null)
				?? Prop("AssemblyVersion");
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) {
			
			return null;
		}
	}
	
	
	private static PackageRef[] ExtractDirectPackages(string csprojPath)
	{
		try {
			
			var doc = FileReader.LoadXml(csprojPath);
			
			return [..
				doc.Descendants("PackageReference")
					.Select(e => new PackageRef(
						e.Attribute("Include")?.Value ?? "?",
						e.Attribute("Version")?.Value ?? "*"
					))
					.DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
					.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
			]
			;
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) {
			return [];
		}
	}
	
	private sealed record PackageRef(string Name, string Version);
}
