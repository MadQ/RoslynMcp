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
	[GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.\-]*$")]
	private static partial Regex PropertyName();
	
	// A configuration or a target framework: no ';' or '=', which would set a second property, and
	// no quote or space.
	[GeneratedRegex(@"^[A-Za-z0-9_.+\-]+$")]
	private static partial Regex GlobalValue();
	
	// Asked for with every evaluation, so that there are always two properties: MSBuild prints one
	// property bare and several as JSON.
	const string Sentinel = "MSBuildProjectFullPath";
	
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
		string workingDirectory, Action<string> record, CancellationToken cancellationToken)
	{
		List<string> args = ["msbuild", projectFile, "-nologo", "-tl:off", $"-getProperty:{Sentinel}"];
		
		args.AddRange(names.Select(name => $"-getProperty:{name}"));
		
		if(configuration is not null)
			args.Add($"-p:Configuration={configuration}");
		
		if(targetFramework is not null)
			args.Add($"-p:TargetFramework={targetFramework}");
		
		string output;
		int    exitCode;
		
		try {
			(output, _, exitCode) = await DotnetRunner.RunAsync([..args], workingDirectory, record, cancellationToken);
		}
		catch(InvalidOperationException ex) {
			
			return new(null, ex.Message);
		}
		
		if(exitCode != 0)
			
			return new(null, $"MSBuild could not evaluate the project (exit code {exitCode}): {Brief(ErrorLines(output))}");
		
		// The JSON object, behind whatever MSBuild printed before it: each of its braces has a
		// line of its own.
		var lines = output.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
		var open  = Array.IndexOf(lines, "{");
		var close = Array.LastIndexOf(lines, "}");
		
		if(open < 0 || close < open)
			
			return new(null, $"MSBuild's answer was not understood: {Brief(output)}");
		
		try {
			
			using var document = JsonDocument.Parse(string.Join('\n', lines[open..(close + 1)]));
			
			if(!document.RootElement.TryGetProperty("Properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
				
				return new(null, $"MSBuild's answer was not understood: {Brief(output)}");
			
			var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			
			foreach(var property in properties.EnumerateObject())
				if(!property.NameEquals(Sentinel))
					values[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.GetRawText();
			
			return new(values, null);
		}
		catch(JsonException) {
			
			return new(null, $"MSBuild's answer was not understood: {Brief(output)}");
		}
	}
	
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
				
				using var stream = FileReader.OpenRead(path, FileReader.DocumentLimit);
				
				document = XDocument.Load(stream, LoadOptions.SetLineInfo);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or XmlException) {
				continue;
			}
			
			files.Add((path, document));
			
			var directory = Path.GetDirectoryName(path)!;
			
			foreach(var import in document.Descendants().Where(element => element.Name.LocalName == "Import")) {
				
				if(((string?) import.Attribute("Project"))?.Trim() is not { Length: > 0 } project)
					continue;
				
				project = project.Replace("$(MSBuildThisFileDirectory)", directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
				
				// A path that needs evaluating, or a wildcard: not read off the text.
				if(project.Contains("$(", StringComparison.Ordinal) || project.Contains('*') || project.Contains('?'))
					continue;
				
				try {
					
					var target = Path.GetFullPath(project.Replace('\\', Path.DirectorySeparatorChar), directory);
					
					if(File.Exists(target))
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
				element.Name.LocalName.Equals(property, StringComparison.OrdinalIgnoreCase) && element.Parent?.Name.LocalName == "PropertyGroup")) {
				
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
