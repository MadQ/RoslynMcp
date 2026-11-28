using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace RoslynMcp.Tools;

/// <summary>
///     The evaluated value of a project's MSBuild properties, and where in the authored files a
///     property is set (#244).
///     <para>
///         <c>roslyn_get_project_info</c> read the project file's XML and nothing else: a version set
///         in <c>Directory.Build.props</c> was null, a conditional element was taken or left by its
///         place in the file, and a property the tool did not know by name could not be asked for.
///         The value a build would use is only had by evaluating, and the evaluation is MSBuild's:
///         <c>dotnet msbuild -getProperty</c> evaluates the project with the SDK its directory
///         resolves, as a build does, and runs no target. Not MSBuild's API in this process: that is
///         the server's own MSBuild, which is not the project's wherever a <c>global.json</c> says
///         otherwise, and cannot be loaded at all for a Visual Studio (.NET Framework) instance.
///     </para>
///     <para>
///         That gives values and no origins. Where a property is set is looked for in the files a
///         person would edit: the project file, the <c>Directory.Build.props</c>,
///         <c>Directory.Build.targets</c> and <c>Directory.Packages.props</c> above it, and what
///         those import by a path that can be read off the text, all inside the workspace's
///         boundary. Each element that sets the property is listed with its file, line and
///         condition; which of several won is MSBuild's to say, so an element is only marked when
///         its text is the evaluated value. The SDK's own files are not searched: nobody edits them.
///     </para>
/// </summary>
internal static partial class ProjectEvaluation
{
	const int MaxProperties   = 50;
	const int MaxFiles        = 30;
	const int MaxSources      = 20;
	const int MaxSourceLength = 200;
	
	// An MSBuild property name, and nothing that could end the switch it is passed in.
	// \z, not $: that also matches before a final line break, which would then reach MSBuild.
	[GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_.\-]*\z")]
	private static partial Regex PropertyName();
	
	// A configuration or a target framework: no ';' or '=', which would set a second property, and
	// no quote or space.
	[GeneratedRegex(@"\A[A-Za-z0-9_.+\-]+\z")]
	private static partial Regex GlobalValue();
	
	// Asked for with every evaluation, so that there are always two properties: MSBuild prints one
	// property bare and several as JSON. A name no project sets and no caller asks for, since it
	// is left out of the answer.
	const string Sentinel = "_RoslynMcpEvaluationSentinel";
	
	/// <summary>Why the arguments cannot be passed to MSBuild, or null when they can.</summary>
	public static string? Invalid(IReadOnlyCollection<string> properties, string? configuration, string? targetFramework)
	{
		if(properties.Count > MaxProperties)
			
			return $"At most {MaxProperties} properties can be asked for in one call.";
		
		if(properties.FirstOrDefault(name => !PropertyName().IsMatch(name)) is { } bad)
			
			return $"'{bad}' is not an MSBuild property name: letters, digits, '_', '.' and '-', starting with a letter or '_'.";
		
		if(configuration is not null && !GlobalValue().IsMatch(configuration))
			
			return $"configuration '{configuration}' is not a configuration name: letters, digits, '_', '.', '+' and '-'.";
		
		if(targetFramework is not null && !GlobalValue().IsMatch(targetFramework))
			
			return $"targetFramework '{targetFramework}' is not a target framework: letters, digits, '_', '.', '+' and '-'.";
		
		return null;
	}
	
	/// <summary>The values MSBuild evaluated, by property name as asked for, or why there are none.</summary>
	public sealed record Evaluated(Dictionary<string, string>? Values, string? Failure);
	
	/// <summary>
	///     Evaluates <paramref name="projectFile"/> and returns the value of each of
	///     <paramref name="names"/>: empty for a property that is not set, which MSBuild does not
	///     tell from one set to nothing. Takes the MSBuild gate, as every <c>dotnet</c> run does.
	/// </summary>
	public static async Task<Evaluated> EvaluateAsync(
		string projectFile, IReadOnlyCollection<string> names, string? configuration, string? targetFramework,
		Action<string> record, CancellationToken cancellationToken)
	{
		// The project's own directory: dotnet picks the SDK by the global.json above where it is
		// started, and the project's is the one a build of it would use.
		var workingDirectory = Path.GetDirectoryName(projectFile) ?? Environment.CurrentDirectory;
		
		List<string> args = ["msbuild", projectFile, "-nologo", "-tl:off", $"-getProperty:{Sentinel}"];
		
		args.AddRange(names.Select(name => $"-getProperty:{name}"));
		
		if(configuration is not null)
			args.Add($"-p:Configuration={configuration}");
		
		if(targetFramework is not null)
			args.Add($"-p:TargetFramework={targetFramework}");
		
		string output;
		int    exitCode;
		
		try {
			(output, _, exitCode) = await DotnetRunner.RunAsync([..args], workingDirectory, record, cancellationToken, previewOutput: false);
		}
		catch(InvalidOperationException ex) {
			
			return new(null, ex.Message);
		}
		
		if(exitCode != 0)
			
			return new(null, $"MSBuild could not evaluate the project (exit code {exitCode}): {Brief(ErrorLines(output))}");
		
		// The output is the JSON object alone, or the object behind whatever MSBuild printed
		// before it (a warning of the evaluation) and before what the error stream added: each
		// of its braces has a line of its own, and no value does, since values are escaped.
		var json = output.Trim();
		
		if(!json.StartsWith('{') || !json.EndsWith('}')) {
			
			var lines = output.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
			var open  = Array.IndexOf(lines, "{");
			var close = Array.LastIndexOf(lines, "}");
			
			if(open < 0 || close < open)
				
				return new(null, "MSBuild's answer was not understood: it holds no JSON object.");
			
			json = string.Join('\n', lines[open..(close + 1)]);
		}
		
		// Not quoted when it is not understood: it holds the values, of which some are withheld.
		try {
			
			using var document = JsonDocument.Parse(json);
			
			if(!document.RootElement.TryGetProperty("Properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
				
				return new(null, "MSBuild's answer was not understood: it has no Properties object.");
			
			var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			
			foreach(var property in properties.EnumerateObject())
				if(!property.NameEquals(Sentinel))
					values[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.GetRawText();
			
			return new(values, null);
		}
		catch(JsonException ex) {
			
			return new(null, $"MSBuild's answer was not understood: {ex.Message}");
		}
	}
	
	// The evaluated Version of a project file, for the evaluation without a configuration or a
	// target framework, with the state of the workspace it was evaluated in. Any change of the
	// workspace ends it: an edit of a props file reloads, and a reload counts.
	static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (WorkspaceChangeCounter Changes, long At, long KeptAt, string Version)> versions = new(SecurityBoundary.PathComparer);
	
	const int MaxKeptVersions = 500;
	
	// A version can stand on what the workspace does not watch: a text file a property function
	// reads, the date. So it is kept for a short while only: long enough for the calls of one
	// stretch of work, not for the rest of the session.
	const long KeptForMs = 60_000;
	
	/// <summary>The version last evaluated for <paramref name="projectFile"/> while <paramref name="changes"/> has not moved since; empty for a project without one, null when nothing is kept.</summary>
	public static string? KeptVersion(string projectFile, WorkspaceChangeCounter changes) =>
		versions.TryGetValue(projectFile, out var kept) && ReferenceEquals(kept.Changes, changes) && !changes.Retired && changes.Value == kept.At
			&& Environment.TickCount64 - kept.KeptAt < KeptForMs ? kept.Version : null;
	
	/// <summary>Keeps <paramref name="version"/> as evaluated when <paramref name="changes"/> stood at <paramref name="at"/>.</summary>
	public static void KeepVersion(string projectFile, WorkspaceChangeCounter changes, long at, string version)
	{
		// Entries of workspaces long gone are not worth a sweep: start over.
		if(versions.Count >= MaxKeptVersions)
			versions.Clear();
		
		versions[projectFile] = (changes, at, Environment.TickCount64, version);
	}
	
	/// <summary>
	///     Whether <paramref name="value"/> is what an environment variable of this process named
	///     like the property holds. MSBuild hands out every environment variable as a property, and
	///     the server's environment is not a project's to read: a token, a key. Names are compared
	///     without regard to case, as MSBuild compares property names: on Linux and macOS the
	///     environment itself tells <c>GITHUB_TOKEN</c> from <c>github_token</c>, and a lookup by
	///     the name asked for would miss. The value is compared as it is and as MSBuild unescapes it
	///     (<c>%3B</c> for a semicolon).
	/// </summary>
	public static bool IsEnvironmentValue(string name, string value)
	{
		if(value.Length == 0)
			
			return false;
		
		foreach(System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
			if(variable.Key is string key && key.Equals(name, StringComparison.OrdinalIgnoreCase) && variable.Value is string held
				&& (held == value || Escaped().Replace(held, match => ((char) Convert.ToInt32(match.Groups[1].Value, 16)).ToString()) == value))
				
				return true;
		
		return false;
	}
	
	[GeneratedRegex("%([0-9A-Fa-f]{2})")]
	private static partial Regex Escaped();
	
	// The lines of an output that say what went wrong, or all of it when none says so.
	static string ErrorLines(string output)
	{
		var errors = output.Split('\n')
			.Select(line => line.Trim())
			.Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase))
			.Take(3)
			.ToArray()
		;
		
		return errors.Length > 0 ? string.Join(" ", errors) : output;
	}
	
	static string Brief(string text) => text.Trim() is { Length: > 600 } longer ? longer[..600] + "…" : text.Trim();
	
	/// <summary>
	///     The authored files a property of <paramref name="projectFile"/> can be set in, loaded
	///     with their line numbers: see the type's remarks. A file that cannot be read or is not
	///     XML is left out.
	/// </summary>
	public static List<(string Path, XDocument Document)> AuthoredFiles(string projectFile, SecurityBoundary boundary)
	{
		var files = new List<(string Path, XDocument Document)>();
		var seen  = new HashSet<string>(SecurityBoundary.PathComparer);
		var queue = new Queue<string>();
		
		queue.Enqueue(projectFile);
		
		foreach(var name in (string[]) ["Directory.Build.props", "Directory.Packages.props", "Directory.Build.targets"])
			for(var directory = Path.GetDirectoryName(projectFile); directory is not null; directory = Path.GetDirectoryName(directory)) {
				
				var candidate = Path.Combine(directory, name);
				
				// Nothing outside the boundary is looked at, not even for whether it is there.
				if(!boundary.IsPathAllowed(candidate))
					break;
				
				// MSBuild takes the nearest one and stops there.
				if(File.Exists(candidate)) {
					
					queue.Enqueue(candidate);
					
					break;
				}
			}
		
		while(queue.Count > 0 && files.Count < MaxFiles) {
			
			var path = queue.Dequeue();
			
			if(!seen.Add(path) || !boundary.IsPathAllowed(path))
				continue;
			
			XDocument document;
			
			try {
				document = FileReader.LoadXml(path, LoadOptions.SetLineInfo);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or XmlException) {
				continue;
			}
			
			files.Add((path, document));
			
			var directory = Path.GetDirectoryName(path)!;
			
			foreach(var import in document.Descendants().Where(element => element.Name.LocalName == "Import")) {
				
				// An import from an SDK names a file of the SDK's, not one beside this file.
				if(import.Attribute("Sdk") is not null || ((string?) import.Attribute("Project"))?.Trim() is not { Length: > 0 } project)
					continue;
				
				project = project
					.Replace("$(MSBuildThisFileDirectory)", directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
					.Replace("$(MSBuildProjectDirectory)", Path.GetDirectoryName(projectFile), StringComparison.OrdinalIgnoreCase)
				;
				
				// A path that needs evaluating, or a wildcard: not read off the text.
				if(project.Contains("$(", StringComparison.Ordinal) || project.Contains('*') || project.Contains('?'))
					continue;
				
				try {
					
					var target = Path.GetFullPath(project.Replace('\\', Path.DirectorySeparatorChar), directory);
					
					// The boundary first: a path the text names can be anywhere, a share included.
					if(boundary.IsPathAllowed(target) && File.Exists(target))
						queue.Enqueue(target);
				}
				catch(Exception ex) when(ex is ArgumentException or NotSupportedException or PathTooLongException) { }
			}
		}
		
		return files;
	}
	
	/// <summary>
	///     The elements of <paramref name="files"/> that set <paramref name="property"/>, in the
	///     order the files were found and the elements stand in them. <c>MatchesValue</c> is said
	///     only where the element's text needs no evaluating.
	/// </summary>
	public static PropertySourceInfo[] Sources(List<(string Path, XDocument Document)> files, string property, string evaluated, string rootPath)
	{
		var sources = new List<PropertySourceInfo>();
		
		foreach(var (path, document) in files)
			foreach(var element in document.Descendants().Where(element =>
				element.Name.LocalName.Equals(property, StringComparison.OrdinalIgnoreCase)
				// Not a group inside a target: that is set when the target runs, and an evaluation
				// runs none. One inside a Choose is evaluated like any other.
				&& element.Parent is { Name.LocalName: "PropertyGroup" } group && !group.Ancestors().Any(ancestor => ancestor.Name.LocalName == "Target"))) {
				
				if(sources.Count == MaxSources)
					
					return [..sources];
				
				var text = element.Value.Trim();
				
				// A condition on the group holds for the element as much as its own.
				string?[] conditions = [(string?) element.Parent!.Attribute("Condition"), (string?) element.Attribute("Condition")];
				
				sources.Add(new(
					Path.GetRelativePath(rootPath, path),
					((IXmlLineInfo) element).LineNumber,
					string.Join(" and ", conditions.Where(condition => !string.IsNullOrWhiteSpace(condition)).Select(condition => condition!.Trim())) is { Length: > 0 } condition ? condition : null,
					text.Length > MaxSourceLength ? text[..MaxSourceLength] + "…" : text,
					text.Contains("$(", StringComparison.Ordinal) || text.Contains("@(", StringComparison.Ordinal) ? null : string.Equals(text, evaluated.Trim(), StringComparison.Ordinal)));
			}
		
		return [..sources];
	}
}
