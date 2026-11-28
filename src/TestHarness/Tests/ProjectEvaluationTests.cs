using System.Text.Json.Nodes;

/// <summary>
///     Covers the evaluated view of a project (#244). <c>roslyn_get_project_info</c> read the project
///     file's XML alone: a version set in <c>Directory.Build.props</c> was null, a conditional element
///     was taken or left by its place in the file, and no property could be asked for by name. The
///     tool now has MSBuild evaluate the project (<c>dotnet msbuild -getProperty</c>) and looks for
///     the elements that set a property in the files a person edits.
///     <para>
///         The fixture has a value in each place the answer must reach: a version prefix in
///         <c>Directory.Build.props</c>, a property the project file sets twice (once under a
///         condition on the configuration), and one set in an imported file from another property,
///         whose text therefore says nothing about its value.
///     </para>
/// </summary>
static class ProjectEvaluationTests
{
	internal static async Task<TestGroup> BuildAsync(TestContext ctx, string logDir, string logGlob)
	{
		var fx = TestFixtures.NewMsBuildProject("Evaluated", extraProjectXml: """
			<PropertyGroup><Flavor>plain</Flavor></PropertyGroup>
			<PropertyGroup Condition="'$(Configuration)' == 'Release'"><Flavor>fast</Flavor></PropertyGroup>
			<Import Project="extra.props" />
			<Choose><When Condition="'$(Flavor)' != ''"><PropertyGroup><Chosen>yes</Chosen></PropertyGroup></When></Choose>
			<Target Name="NeverRun"><PropertyGroup><Chosen>in-target</Chosen></PropertyGroup></Target>
			""");
		
		fx.Write("Probe.cs", "class Probe { }\n");
		fx.Write("Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <VersionPrefix>3.2.1</VersionPrefix>\n  </PropertyGroup>\n</Project>\n");
		fx.Write("extra.props", "<Project>\n  <PropertyGroup>\n    <FromImport>$(Flavor)-x</FromImport>\n  </PropertyGroup>\n</Project>\n");
		
		// A project MSBuild cannot evaluate: its import is not there. The workspace still loads it.
		var broken = TestFixtures.NewMsBuildProject("EvaluatedBroken", extraProjectXml: """<Import Project="not-there.props" />""");
		
		broken.Write("Probe.cs", "class Probe { }\n");
		
		var adhoc = TestFixtures.NewAdhocDir("EvaluatedAdhoc");
		
		File.WriteAllText(Path.Combine(adhoc.Dir, "Loose.cs"), "class Loose { }\n");
		
		await fx.WarmAsync(ctx);
		
		Task<JsonNode?> Info(object args) => ctx.CallToolJsonAsync("roslyn_get_project_info", args);
		
		static JsonNode? Property(JsonNode? answer, string name) =>
			(answer?["properties"] as JsonArray)?.FirstOrDefault(property => property?["name"]?.GetValue<string>() == name);
		
		static (bool, string) Verdict(bool pass, JsonNode? answer) => pass ? (true, "PASS") : (false, $"FAIL  ({answer?.ToJsonString() ?? "null"})");
		
		var tests = new List<TestCase> {
			
			// The version is nowhere in the project file: only an evaluation finds the prefix that
			// Directory.Build.props sets. The fixed fields beside it are the loaded project's.
			new("project evaluation: version is the evaluated one, also when Directory.Build.props sets it", async () =>
			{
				var info       = await Info(new { projectPath = fx.Csproj });
				var frameworks = info?["target_frameworks"] is JsonArray array ? array.Select(item => item?.GetValue<string>()).ToArray() : [];
				
				return Verdict(info?["version"]?.GetValue<string>() == "3.2.1"
					&& info["_caution"] is null
					&& info["evaluation_unavailable"] is null
					&& info["properties"] is null
					&& frameworks.Length == 1 && frameworks[0]?.StartsWith("net", StringComparison.Ordinal) == true, info);
			}),
			
			// Values as a build has them, and where they are set. Flavor has two elements: the one
			// without a condition is the value, the one for Release is not. FromImport is set from
			// another property, so its text does not say whether it is the value. A property that
			// is not set has an empty value and no place.
			new("project evaluation: properties come with their value and the elements that set them", async () =>
			{
				var info     = await Info(new { projectPath = fx.Csproj, properties = new[] { "Flavor", "FromImport", "VersionPrefix", "NoSuchProperty_244", "Chosen" } });
				
				// A group inside a Choose is evaluated and is a place; one inside a target is neither.
				var chosen   = Property(info, "Chosen");
				var flavor   = Property(info, "Flavor");
				var imported = Property(info, "FromImport");
				var prefix   = Property(info, "VersionPrefix");
				var none     = Property(info, "NoSuchProperty_244");
				
				var flavorSet = flavor?["set_in"] as JsonArray ?? [];
				var plain     = flavorSet.FirstOrDefault(source => source?["text"]?.GetValue<string>() == "plain");
				var fast      = flavorSet.FirstOrDefault(source => source?["text"]?.GetValue<string>() == "fast");
				
				return Verdict(flavor?["value"]?.GetValue<string>() == "plain"
					&& flavorSet.Count == 2
					&& plain?["is_value"]?.GetValue<bool>() == true && plain["condition"] is null && plain["line"]?.GetValue<int>() > 0
					&& fast?["is_value"]?.GetValue<bool>() == false && fast["condition"]?.GetValue<string>().Contains("Release", StringComparison.Ordinal) == true
					&& plain["file"]?.GetValue<string>() == Path.GetFileName(fx.Csproj)
					&& imported?["value"]?.GetValue<string>() == "plain-x"
					&& imported["set_in"]?[0]?["file"]?.GetValue<string>() == "extra.props"
					&& imported["set_in"]?[0]?["line"]?.GetValue<int>() == 3
					&& imported["set_in"]?[0]?["is_value"] is null
					&& prefix?["set_in"]?[0]?["file"]?.GetValue<string>() == "Directory.Build.props"
					&& prefix["set_in"]?[0]?["line"]?.GetValue<int>() == 3
					&& prefix["set_in"]?[0]?["is_value"]?.GetValue<bool>() == true
					&& none?["value"]?.GetValue<string>() == "" && none["set_in"] is null
					&& chosen?["value"]?.GetValue<string>() == "yes"
					&& chosen["set_in"] is JsonArray { Count: 1 } chosenIn && chosenIn[0]?["text"]?.GetValue<string>() == "yes"
					&& info?["evaluated_for"] is null, info);
			}),
			
			// A value can depend on the configuration: asked for Release, the conditional element wins,
			// and the answer says what it was evaluated for.
			new("project evaluation: a configuration is evaluated for when given", async () =>
			{
				var info   = await Info(new { projectPath = fx.Csproj, properties = new[] { "Flavor" }, configuration = "Release" });
				var flavor = Property(info, "Flavor");
				var fast   = (flavor?["set_in"] as JsonArray)?.FirstOrDefault(source => source?["text"]?.GetValue<string>() == "fast");
				
				return Verdict(flavor?["value"]?.GetValue<string>() == "fast"
					&& fast?["is_value"]?.GetValue<bool>() == true
					&& info?["evaluated_for"]?["configuration"]?.GetValue<string>() == "Release", info);
			}),
			
			// What reaches MSBuild's command line is a name or a plain value: a ';' would set a second
			// property, and is refused before anything runs.
			new("project evaluation: a property name or configuration that is none is refused", async () =>
			{
				var name   = await Info(new { projectPath = fx.Csproj, properties = new[] { "Flavor;OutDir" } });
				var config = await Info(new { projectPath = fx.Csproj, properties = new[] { "Flavor" }, configuration = "Debug;OutDir=x" });
				var inside = await Info(new { projectPath = fx.Csproj, properties = new[] { "Flavor" }, configuration = "Rel\nease" });
				
				// White space around a value is not part of it: 'Release' with a line break behind it is
				// Release, where it used to pass the check and reach MSBuild as another configuration.
				var padded = await Info(new { projectPath = fx.Csproj, properties = new[] { "Flavor" }, configuration = "Release\n" });
				
				return Verdict(name?["error"]?.GetValue<string>().Contains("not an MSBuild property name", StringComparison.Ordinal) == true
					&& config?["error"]?.GetValue<string>().Contains("not a configuration name", StringComparison.Ordinal) == true
					&& inside?["error"]?.GetValue<string>().Contains("not a configuration name", StringComparison.Ordinal) == true
					&& Property(padded, "Flavor")?["value"]?.GetValue<string>() == "fast"
					&& padded?["evaluated_for"]?["configuration"]?.GetValue<string>() == "Release", new JsonArray(name?.DeepClone(), config?.DeepClone(), inside?.DeepClone(), padded?.DeepClone()));
			}),
			
			// MSBuild hands out every environment variable as a property, and the server's environment
			// is not the project's: a variable no project file sets is named as withheld, its value not
			// given. Asked for in lower case: MSBuild does not tell the cases of a property name apart,
			// and where the environment does (Linux, macOS) a lookup by the name as asked missed it.
			// A target framework is evaluated for like a configuration.
			new("project evaluation: an environment variable is withheld, and a target framework is evaluated for", async () =>
			{
				var info      = await Info(new { projectPath = fx.Csproj, properties = new[] { "path", "TargetFramework" }, targetFramework = "net10.0" });
				var path      = Property(info, "path");
				var framework = Property(info, "TargetFramework");
				
				return Verdict(path?["value"]?.GetValue<string>() == ""
					&& path["withheld"]?.GetValue<string>().Contains("environment variable", StringComparison.Ordinal) == true
					&& info?.ToJsonString().Contains(Environment.GetEnvironmentVariable("PATH") is { Length: > 20 } value ? System.Text.Json.JsonSerializer.Serialize(value)[1..^1] : "\u0000never", StringComparison.Ordinal) == false
					&& framework?["value"]?.GetValue<string>() == "net10.0"
					&& info?["evaluated_for"]?["target_framework"]?.GetValue<string>() == "net10.0", info);
			}),
			
			// A second call that asks for nothing is answered from the version kept, with the same
			// answer; and a project MSBuild cannot evaluate says why, with what the file writes.
			new("project evaluation: the version is kept between calls, and a failed evaluation says why", async () =>
			{
				var first  = await Info(new { projectPath = fx.Csproj });
				var second = await Info(new { projectPath = fx.Csproj });
				var failed = await Info(new { projectPath = broken.Csproj });
				
				if(failed?["error"] is not null)
					
					return Skip.SetupUnavailable($"the workspace does not load a project whose import is missing: {failed.ToJsonString()}");
				
				// That the second call did not evaluate is in the log alone: its answer is the same.
				if(!ServerLog.TryRead(logDir, logGlob, 1, out var lines, out var failure))
					
					return (false, $"FAIL  ({failure})");
				
				if(!lines.Any(line => line.Contains("EvaluatedFixture; version as last evaluated", StringComparison.Ordinal)))
					
					return (false, "FAIL  (no call was answered from the version kept: the log has no 'version as last evaluated' entry)");
				
				return Verdict(first?["version"]?.GetValue<string>() == "3.2.1"
					&& second?["version"]?.GetValue<string>() == "3.2.1"
					&& second["evaluation_unavailable"] is null
					&& failed?["evaluation_unavailable"]?.GetValue<string>().Contains("not-there.props", StringComparison.Ordinal) == true
					&& failed["version"] is null
					&& failed["_caution"]?.GetValue<string>().Contains("could not be evaluated", StringComparison.Ordinal) == true, new JsonArray(first?.DeepClone(), second?.DeepClone(), failed?.DeepClone()));
			}),
			
			// What cannot be evaluated is said, not left out: a directory of source files has no project
			// file, and a solution is several projects, of which one has to be named.
			new("project evaluation: a workspace without a project file, and a solution, say why nothing is evaluated", async () =>
			{
				var loose    = await Info(new { projectPath = adhoc.Dir, properties = new[] { "Version" } });
				var solution = await Info(new { properties = new[] { "Version" } });
				
				// The harness server's default workspace is this repository's solution; where it is
				// not, the call names one project and there is nothing to check.
				var solutionSaid = solution?["error"]?.GetValue<string>().Contains("one project", StringComparison.Ordinal) == true
					&& solution["hint"]?.GetValue<string>().Contains(".csproj", StringComparison.Ordinal) == true
					|| solution?["properties"] is not null;
				
				return Verdict(loose?["evaluation_unavailable"]?.GetValue<string>().Contains("no project file", StringComparison.Ordinal) == true
					&& loose["properties"] is null
					&& solutionSaid, new JsonArray(loose?.DeepClone(), solution?.DeepClone()));
			}),
		};
		
		return new TestGroup($"Project Evaluation ({tests.Count} tests)", tests, Teardown: () => {
			
			fx.Dispose();
			broken.Dispose();
			adhoc.Dispose();
			
			return Task.CompletedTask;
		});
	}
}
