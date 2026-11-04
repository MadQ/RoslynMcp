/// <summary>
///     Verifies source drift measures stale workspace text, not the age of an RM-owned write (#299).
///     The temporary MSBuild project explicitly compiles files under obj: watcher classification
///     excludes that directory, so no debounce flush can heal a missed edit or hide a broken probe.
///     Each case loads its own fixture before writing and ages the sync clock past its two-second
///     tolerance. Tool edits must remain clean, whereas a direct disk edit must remain drifted even
///     after another file is edited through RM. A timestamp-only touch is not stale source, and a
///     missing file is. Fixtures are disposed per case so filtered runs have no ordering dependency.
/// </summary>
static class DriftTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		async Task<(bool, string)> CheckAsync(string? tool, object? edit, bool external = false, bool touch = false, bool delete = false)
		{
			using var fx = TestFixtures.NewMsBuildProject("Drift", extraProjectXml:
				"<ItemGroup><Compile Include=\"obj/Edited.cs\" /><Compile Include=\"obj/Outside.cs\" /></ItemGroup>");
			var edited  = fx.Write("obj/Edited.cs", "class Edited\n{\n\tint value = 1;\n}\n");
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
			
			return await ctx.RunTestAsync("roslyn_check_drift", new { projectPath = fx.Csproj },
				data => data?["drifted"]?.GetValue<bool>() == (external || delete)
					&& data?["drifted_count"]?.GetValue<int>() == (external || delete ? 1 : 0)
					&& (!(external || delete) || data?["drifted_files"]?.AsArray().Any(file =>
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
			new("drift: deleted source remains drifted", () => CheckAsync(null, null, delete: true)),
		]);
	}
}
