using System.Text.Json.Nodes;

/// <summary>
///     Covers #276: <c>WorkspaceInstance.InvalidateFile</c> applies edits to an already-tracked
///     <c>AdditionalFiles</c> item incrementally via <c>Solution.WithAdditionalDocumentText</c>
///     instead of flagging a full MSBuild reload. Also guards the crash bug this change exposed and
///     fixed as a side effect: before #276, editing an EXISTING tracked additional or analyzer-config
///     document called <c>Solution.WithDocumentText</c> on a non-source <c>DocumentId</c>, which
///     throws <c>InvalidOperationException</c> — uncaught by the existing
///     <c>IOException</c>/<c>UnauthorizedAccessException</c> filter. <c>MSBuildWorkspace</c> does not
///     support <c>TryApplyChanges</c> for analyzer-config documents (verified against Roslyn 5.3.0), so
///     an <c>.editorconfig</c> edit must still fall back to a full reload — this group asserts that
///     fallback happens cleanly (no protocol error) rather than propagating the exception. Fixtures
///     live in a throwaway MSBuild project under %TEMP% per #274.
/// </summary>
static class IncrementalAdditionalDocTests
{
	internal static async Task<TestGroup> BuildAsync(TestContext ctx)
	{
		
		var fx = TestFixtures.NewMsBuildProject("IncrementalAdditionalDoc",
			extraProjectXml: """
				<ItemGroup>
				  <AdditionalFiles Include="TrackedInputs/**/*.txt" />
				</ItemGroup>
				<ItemGroup>
				  <ProjectReference Include="AdditionalFileProbeGenerator/AdditionalFileProbeGenerator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
				</ItemGroup>
				""");
		
		var csproj = fx.Csproj!;
		
		fx.Write("AdditionalFileProbeGenerator/AdditionalFileProbeGenerator.csproj", """
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup>
			    <TargetFramework>netstandard2.0</TargetFramework>
			    <LangVersion>preview</LangVersion>
			    <Nullable>enable</Nullable>
			    <ImplicitUsings>enable</ImplicitUsings>
			    <IsRoslynComponent>true</IsRoslynComponent>
			  </PropertyGroup>
			  <ItemGroup>
			    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.11.0" PrivateAssets="all" />
			    <PackageReference Include="Microsoft.CodeAnalysis.Analyzers" Version="3.11.0" PrivateAssets="all" />
			  </ItemGroup>
			</Project>
			""");
		fx.Write("AdditionalFileProbeGenerator/NotesAdditionalFileGenerator.cs", """
			using System.Linq;
			using Microsoft.CodeAnalysis;
			using Microsoft.CodeAnalysis.Text;
			using System.Text;
			
			[Generator]
			public sealed class NotesAdditionalFileGenerator : ISourceGenerator
			{
				public void Initialize(GeneratorInitializationContext context) { }
				
				public void Execute(GeneratorExecutionContext context)
				{
					var noteFile = context.AdditionalFiles.FirstOrDefault(file =>
						string.Equals(System.IO.Path.GetFileName(file.Path), "Notes.txt", System.StringComparison.OrdinalIgnoreCase));
					var noteText = noteFile?.GetText(context.CancellationToken)?.ToString() ?? "";
					var suffix = noteText.Contains("2", System.StringComparison.Ordinal) ? "2" : "1";
					var source = new StringBuilder()
						.AppendLine("public sealed class GeneratedNote_" + suffix)
						.AppendLine("{")
						.AppendLine("}")
						.ToString()
					;
					
					context.AddSource("GeneratedNote.g.cs", SourceText.From(source, Encoding.UTF8));
				}
			}
			""");
		
		// Written before the first tool call so the initial load tracks TrackedInputs/Notes.txt as an
		// additional document and .editorconfig as an analyzer-config document — no FSW timing
		// involved.
		fx.Write("Probe.cs",       "class Probe { GeneratedNote_1? Value; }\n");
		fx.Write("TrackedInputs/Notes.txt", "note = 1\n");
		fx.Write(".editorconfig", "root = true\nprobe = 1\n");
		
		async Task<(bool ok, JsonNode? data)> Call(string tool, object args)
		{
			
			await ctx.SendAsync(new { jsonrpc = "2.0", id = ctx.NextId(), method = "tools/call",
				@params = new { name = tool, arguments = args } });
			
			var resp = await ctx.ReceiveAsync();
			var text = resp?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
			
			JsonNode? data = null;
			
			try { data = JsonNode.Parse(text); }
			catch { }
			
			return (resp?["error"] is null && text.Length > 0, data);
		}
		
		async Task<bool?> ReloadPendingAsync()
		{
			
			var (ok, data) = await Call("roslyn_check_drift", new { projectPath = csproj });
			
			if(!ok || data?["drifted"] is null)
				
				return null;
			
			return data["reload_pending"]?.GetValue<bool>() ?? false;
		}
		
		async Task<bool> SettleAsync()
		{
			
			var deadline = DateTime.UtcNow.AddSeconds(15);
			
			while(DateTime.UtcNow < deadline) {
				
				await Call("roslyn_get_diagnostics", new { projectPath = csproj, take = 0, severity = "errors" });
				
				if(await ReloadPendingAsync() == false)
					
					return true;
				
				await Task.Delay(250);
			}
			
			return false;
		}
		
		async Task<string?> NoteErrorAsync()
		{
			var (ok, data) = await Call("roslyn_get_diagnostics", new {
				projectPath       = csproj,
				filePath          = "Probe.cs",
				severity          = "errors",
				take              = 20
			});
			
			if(!ok)
				return null;
			
			if(data?["items"] is not JsonArray items)
				return "";
			
			foreach(var item in items) {
				
				if(item?["code"]?.GetValue<string>() == "CS0246")
					return item["message"]?.GetValue<string>() ?? "";
			}
			
			return "";
		}
		
		async Task<(bool pass, string message)> AssertFlag(
			string tool,
			object args,
			bool expectFlagged,
			string what,
			string? expectedErrorSubstring = null)
		{
			
			if(!await SettleAsync())
				
				return (false, "FAIL  (precondition: reload_pending never cleared before the write)");
			
			var (ok, data) = await Call(tool, args);
			
			if(!ok || data?["error"] is not null)
				
				return (false, $"FAIL  ({tool} failed: {data?["error"]?.GetValue<string>() ?? "protocol error"})");
			
			var pending = await ReloadPendingAsync();
			
			if(pending is null)
				
				return (false, "FAIL  (roslyn_check_drift returned no reload_pending)");
			
			if(pending != expectFlagged)
				
				return (false, $"FAIL  ({what}: expected reload_pending={expectFlagged}, got {pending})");
			
			if(expectedErrorSubstring is not null) {
				
				var actualDiagnostic = await NoteErrorAsync();
				
				if(actualDiagnostic is null)
					return (false, "FAIL  (roslyn_get_diagnostics failed)");
				
				if(!actualDiagnostic.Contains(expectedErrorSubstring, StringComparison.Ordinal))
					return (false, $"FAIL  ({what}: expected error containing '{expectedErrorSubstring}', got '{actualDiagnostic}')");
				
				return (true, $"PASS  ({what}: reload_pending={pending}; diagnostic='{actualDiagnostic}')");
			}
			
			return (true, $"PASS  ({what}: reload_pending={pending})");
		}
		
		// Pay the MSBuild load now so the first test measures the edit, not the load.
		await fx.WarmAsync(ctx);
		
		var tests = new List<TestCase> {
			
			// The headline case. Pre-fix, an existing tracked AdditionalFiles item took the same
			// full-reload path as a brand-new input; post-fix it applies via
			// WithAdditionalDocumentText and never flags a reload. The generator-backed compiler error
			// proves the in-memory AdditionalDocuments text changed too, not only the reload flag.
			new("incremental additional doc: editing a tracked AdditionalFiles item does not flag a reload",
				() => AssertFlag("roslyn_replace_in_file",
					new { filePath = "TrackedInputs/Notes.txt", pattern = "note = 1", replacement = "note = 2", projectPath = csproj },
					expectFlagged: false, what: "AdditionalFiles edit",
					expectedErrorSubstring: "GeneratedNote_1")),
			
			// A new file that only enters the project through a wildcard AdditionalFiles include has no
			// tracked DocumentId in the watcher path yet, so the shared reload classifier must still
			// recognize it as a real workspace input and schedule a full reload.
			new("incremental additional doc: creating a declared AdditionalFiles item flags a reload",
				async () => {
					
					if(!await SettleAsync())
						return (false, "FAIL  (precondition: reload_pending never cleared before the write)");
					
					await File.WriteAllTextAsync(fx.PathOf("TrackedInputs/AddedLater.txt"), "later = 1\n");
					
					var deadline = DateTime.UtcNow.AddSeconds(5);
					
					while(DateTime.UtcNow < deadline) {
						
						var pending = await ReloadPendingAsync();
						
						if(pending is null)
							return (false, "FAIL  (roslyn_check_drift returned no reload_pending)");
						
						if(pending == true)
							return (true, "PASS  (new AdditionalFiles item: reload_pending=True)");
						
						await Task.Delay(100);
					}
					
					return (false, "FAIL  (new AdditionalFiles item: expected reload_pending=True, got False)");
				}),
			
			// MSBuildWorkspace.CanApplyChange(ChangeAnalyzerConfigDocument) is false, so this must
			// still take the full-reload path — but cleanly. Pre-fix this called WithDocumentText on
			// an analyzer-config DocumentId, throwing InvalidOperationException uncaught by the
			// existing IOException/UnauthorizedAccessException filter; that regression is what this
			// assertion (ok == true, no error) guards against.
			new("incremental additional doc: editing a tracked .editorconfig still flags a reload cleanly",
				() => AssertFlag("roslyn_replace_in_file",
					new { filePath = ".editorconfig", pattern = "probe = 1", replacement = "probe = 2", projectPath = csproj },
					expectFlagged: true, what: ".editorconfig edit")),
		};
		
		return new TestGroup($"Incremental Additional/AnalyzerConfig Doc ({tests.Count} tests)", tests, Teardown: async () =>
		{
			
			// Leave the server settled for the next group, then drop the temp project.
			await SettleAsync();
			
			fx.Dispose();
		});
	}
}
