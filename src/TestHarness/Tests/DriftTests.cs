/// <summary>
///     Verifies source drift measures stale workspace text, not the age of an RM-owned write (#299).
///     The temporary MSBuild project explicitly compiles files under obj, and its .madq_roslynmcp.json
///     ignores obj — so no watcher covers that directory, and no debounce flush can heal a missed
///     edit or hide a broken probe. obj alone would not do: it is watched, and a tracked document
///     under it is flushed like any other (review of #382).
///     Each case loads its own fixture before writing and ages the sync clock past its two-second
///     tolerance. Tool edits must remain clean, whereas a direct disk edit must remain drifted even
///     after another file is edited through RM. A timestamp-only touch is not stale source, and a
///     missing file is. Fixtures are disposed per case so filtered runs have no ordering dependency.
/// </summary>
static class DriftTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		async Task<(bool, string)> CheckAsync(string? tool, object? edit, bool external = false, bool touch = false, bool delete = false, int? codePage = null, bool undecodable = false)
		{
			var codePageXml = codePage is null ? string.Empty : $"<PropertyGroup><CodePage>{codePage}</CodePage></PropertyGroup>";
			using var fx = TestFixtures.NewMsBuildProject("Drift", extraProjectXml:
				codePageXml + "<ItemGroup><Compile Include=\"obj/Edited.cs\" /><Compile Include=\"obj/Outside.cs\" /></ItemGroup>");
			
			// Before the first load, which reads it: no watcher may cover obj (see the class remarks).
			fx.Write(".madq_roslynmcp.json", "{ \"ignore\": [\"obj\"] }\n");
			var editedContent = codePage is null ? "class Edited\n{\n\tint value = 1;\n}\n" : "class Edited { const string value = \"café\"; }\n";
			var edited  = fx.Write("obj/Edited.cs", editedContent);

			if(codePage is not null)
				File.WriteAllBytes(edited, System.Text.Encoding.Latin1.GetBytes(editedContent));

			var outside = fx.Write("obj/Outside.cs", "class Outside { }\n");
			
			var (loaded, loadMessage) = await ctx.RunTestAsync("roslyn_get_diagnostics",
				new { projectPath = fx.Csproj, severity = "errors", take = 0 },
				data => data?["error_count"]?.GetValue<int>() == 0);
			
			if(!loaded)
				
				return (false, $"FAIL  (fixture did not load) {loadMessage}");
			
			// Let startup reconciliation finish before aging the global clock.
			await Task.Delay(3500);
			
			if(external)
				File.AppendAllText(outside, "// Outside edit missed by the watcher.\n");
			
			if(tool is not null) {
				
				var arguments = System.Text.Json.JsonSerializer.SerializeToNode(edit)!.AsObject();
				arguments["projectPath"] = fx.Csproj;
				
				var (changed, changeMessage) = await ctx.RunTestAsync(tool, arguments,
					data => data is not null && data["error"] is null);
				
				if(!changed)
					
					return (false, $"FAIL  (tool edit failed) {changeMessage}");
				
				if(!File.ReadAllText(edited).Contains("299"))
					
					return (false, "FAIL  (tool did not persist its edit)");
			}
			
			if(touch)
				File.SetLastWriteTimeUtc(edited, DateTime.UtcNow);
			
			if(delete)
				File.Delete(edited);
			
			// Bytes that are no UTF-8 at all: a strict decoder throws on them, and the probe must
			// report the file as drifted rather than fail as a whole (review of #382).
			if(undecodable)
				File.WriteAllBytes(edited, [..File.ReadAllBytes(edited), 0xC3, 0x28, (byte) '\n']);
			
			var expectDrift = external || delete || undecodable;
			
			return await ctx.RunTestAsync("roslyn_check_drift", new { projectPath = fx.Csproj },
				data => data?["drifted"]?.GetValue<bool>() == expectDrift
					&& data?["drifted_count"]?.GetValue<int>() == (expectDrift ? 1 : 0)
					&& (!expectDrift || data?["drifted_files"]?.AsArray().Any(file =>
						file?.GetValue<string>()?.EndsWith(external ? "Outside.cs" : "Edited.cs") == true) == true));
		}
		
		return new TestGroup("Source Drift", [
			new("drift: replace_in_file leaves tracked source in sync", () => CheckAsync("roslyn_replace_in_file",
				new { filePath = "obj/Edited.cs", pattern = "value = 1", replacement = "value = 299" })),
			new("drift: replace_in_code leaves tracked source in sync", () => CheckAsync("roslyn_replace_in_code",
				new { filePath = "obj/Edited.cs", nodeKind = "FieldDeclaration", textPattern = "value",
					replacement = "int value = 299;" })),
			new("drift: insert_lines leaves tracked source in sync", () => CheckAsync("roslyn_insert_lines",
				new { filePath = "obj/Edited.cs", atLine = 1, text = "// Issue 299" })),
			new("drift: direct disk edit remains visible", () => CheckAsync(null, null, external: true)),
			new("drift: own edit does not hide unrelated external drift", () => CheckAsync("roslyn_insert_lines",
				new { filePath = "obj/Edited.cs", atLine = 1, text = "// Issue 299" }, external: true)),
			new("drift: timestamp-only touch is not stale source", () => CheckAsync(null, null, touch: true)),
			new("drift: BOM-less source uses its project code page", () => CheckAsync(null, null, touch: true, codePage: 1252)),
			new("drift: deleted source remains drifted", () => CheckAsync(null, null, delete: true)),
			new("drift: source that no longer decodes is drifted, not an error", () => CheckAsync(null, null, undecodable: true)),
		]);
	}
}
