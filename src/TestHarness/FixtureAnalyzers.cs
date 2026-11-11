using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
///     Small analyzer assemblies compiled in the harness, in as many builds as a test needs (#418,
///     #419). The tests of the analyzer loader need what no shipped assembly offers: two assemblies
///     of one name that behave differently, and a way to tell from outside which one the server
///     ran. A build is two files of fixed names:
///     <list type="bullet">
///         <item>
///             <c>Fixture.Analyzers.dll</c>: an analyzer that reports <see cref="DiagnosticId"/> on
///             every type, with a message that names its own build and the build of the helper it
///             found (<see cref="Message"/>), and as many fix providers as its build number, so the
///             provider count of a source says which build it was read from.
///         </item>
///         <item>
///             <c>Fixture.Helper.dll</c>: the assembly the analyzer depends on, which is how a test
///             sees which of several helpers of that name a dependency resolved to.
///         </item>
///     </list>
///     Both carry their build number as assembly version. They are compiled against the harness's
///     own runtime and Roslyn, which are the server's, so they load there as they would from a package.
/// </summary>
internal static class FixtureAnalyzers
{
	public const string AnalyzerFile = "Fixture.Analyzers.dll";
	public const string HelperFile   = "Fixture.Helper.dll";
	public const string DiagnosticId = "FXB001";
	
	/// <summary>The message the analyzer of one build reports when it found the helper of another.</summary>
	public static string Message(int analyzerBuild, int helperBuild)
		=> $"analyzer {analyzerBuild} with helper {helperBuild}";
	
	/// <summary>
	///     Writes build <paramref name="build"/> of both assemblies into <paramref name="directory"/>,
	///     over what is there, and returns their paths, the analyzer first. A file written over gets
	///     a modification time two seconds after the one it replaced, whatever the clock says: the
	///     server tells a rebuilt assembly by that time, and a file system with coarse timestamps
	///     could give two writes in one test the same.
	/// </summary>
	public static string[] Write(string directory, int build)
	{
		var helper   = Path.Combine(directory, HelperFile);
		var analyzer = Path.Combine(directory, AnalyzerFile);
		
		var helperImage = Compile("Fixture.Helper", build, HelperSource(build), []);
		
		Replace(helper, helperImage);
		Replace(analyzer, Compile("Fixture.Analyzers", build, AnalyzerSource(build), [MetadataReference.CreateFromImage(helperImage)]));
		
		return [analyzer, helper];
	}
	
	static void Replace(string path, byte[] image)
	{
		DateTime? before = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
		
		File.WriteAllBytes(path, image);
		
		if(before is { } earlier && File.GetLastWriteTimeUtc(path) < earlier.AddSeconds(2))
			File.SetLastWriteTimeUtc(path, earlier.AddSeconds(2));
	}
	
	static byte[] Compile(string name, int build, string source, MetadataReference[] others)
	{
		var compilation = CSharpCompilation.Create(
			name,
			[CSharpSyntaxTree.ParseText(source)],
			[..PlatformReferences.Value, ..others],
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
		
		using var image = new MemoryStream();
		
		var result = compilation.Emit(image);
		
		if(!result.Success)
			throw new InvalidOperationException($"The fixture assembly {name} (build {build}) does not compile: {string.Join("; ", result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Take(3))}");
		
		return image.ToArray();
	}
	
	// Everything the harness itself runs on: the framework, and Roslyn from the harness's output.
	static readonly Lazy<MetadataReference[]> PlatformReferences = new(() =>
		[..((string?) AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
			.Select(path => (MetadataReference) MetadataReference.CreateFromFile(path))]);
	
	static string HelperSource(int build) => $$"""
		[assembly: System.Reflection.AssemblyVersion("{{build}}.0.0.0")]
		
		namespace Fixture;
		
		public static class Helper
		{
			public static int Build => {{build}};
		}
		""";
	
	static string AnalyzerSource(int build) => $$"""
		using System.Collections.Immutable;
		using System.Threading.Tasks;
		using Microsoft.CodeAnalysis;
		using Microsoft.CodeAnalysis.CodeFixes;
		using Microsoft.CodeAnalysis.Diagnostics;
		
		[assembly: System.Reflection.AssemblyVersion("{{build}}.0.0.0")]
		
		namespace Fixture;
		
		[DiagnosticAnalyzer(LanguageNames.CSharp)]
		public sealed class BuildAnalyzer : DiagnosticAnalyzer
		{
			static readonly DiagnosticDescriptor Rule = new("{{DiagnosticId}}", "Fixture build", "analyzer {{build}} with helper {0}", "Fixture", DiagnosticSeverity.Warning, isEnabledByDefault: true);
			
			public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);
			
			public override void Initialize(AnalysisContext context)
			{
				context.EnableConcurrentExecution();
				context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
				context.RegisterSymbolAction(Report, SymbolKind.NamedType);
			}
			
			// The helper is first needed here, in a method of its own, so that the analyzer type
			// itself loads without it.
			static void Report(SymbolAnalysisContext context)
				=> context.ReportDiagnostic(Diagnostic.Create(Rule, context.Symbol.Locations[0], Helper.Build));
		}
		
		{{string.Concat(Enumerable.Range(1, build).Select(index => $$"""
		[ExportCodeFixProvider(LanguageNames.CSharp, Name = "FixtureFix{{index}}")]
		public sealed class FixtureFix{{index}} : CodeFixProvider
		{
			public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("{{DiagnosticId}}");
			
			public override Task RegisterCodeFixesAsync(CodeFixContext context) => Task.CompletedTask;
		}
		
		"""))}}
		""";
}
