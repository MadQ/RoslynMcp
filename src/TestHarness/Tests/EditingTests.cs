using System.Text.Json.Nodes;

static class EditingTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		
		// NOTE: The empty-file-after-write detection added to all four editing tools
		// (roslyn_write_file, roslyn_replace_in_file, roslyn_replace_in_code, roslyn_insert_lines)
		// cannot be exercised from here. The condition it guards against — the OS or antivirus
		// silently truncating a file after an apparently successful write — requires environmental
		// interference that cannot be provoked via the MCP API. The guard condition itself
		// (writeBytes.Length > 4 && new FileInfo(fullPath).Length <= 4) is covered implicitly:
		// every successful write test below confirms the check does not false-positive on real files.
		
		var tempTextFile   = Path.Combine(ctx.TargetPath, ".test_replace_temp.cs");
		var tempCodeFile   = Path.Combine(ctx.TargetPath, ".test_code_temp.cs");
		var tempInsertFile = Path.Combine(ctx.TargetPath, ".test_insert_temp.txt");
		
		File.WriteAllText(tempTextFile,   "// Test line 1\nvar handle = IntPtr.Zero;\n// Test line 3\n");
		File.WriteAllText(tempCodeFile,   "class TestClass { private int oldField = 42; }");
		File.WriteAllText(tempInsertFile, "line one\nline two\nline three\n");
		
		var tests = new List<TestCase> {
			
			new("roslyn_replace_in_file: dry run literal replacement",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_file",
					new { filePath = ".test_replace_temp.cs", pattern = "IntPtr", replacement = "nint", dryRun = true, projectPath = ctx.TargetPath },
					data => data?["match_count"]?.GetValue<int>() == 1 && data?["applied"]?.GetValue<bool>() == false)),
			
			new("roslyn_replace_in_file: apply literal replacement",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_file",
					new { filePath = ".test_replace_temp.cs", pattern = "IntPtr", replacement = "nint", dryRun = false, projectPath = ctx.TargetPath },
					data => data?["match_count"]?.GetValue<int>() == 1 && data?["applied"]?.GetValue<bool>() == true)),
			
			new("roslyn_replace_in_file: regex replacement with capture groups",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_file",
					new { filePath = ".test_replace_temp.cs", pattern = @"var (\w+) = nint\.Zero", replacement = "nint $1 = 0", useRegex = true, projectPath = ctx.TargetPath },
					data => data?["match_count"]?.GetValue<int>() == 1 && data?["changed_lines"]?.AsArray()[0]?.GetValue<int>() == 2)),
			
			new("roslyn_replace_in_code: dry run identifier replacement",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = ".test_code_temp.cs", nodeKind = "IdentifierName", textPattern = "oldField", replacement = "newField", dryRun = true, projectPath = ctx.TargetPath },
					data => data?["error"] is null && data?["change_count"] is not null)),
			
			new("roslyn_replace_in_code: apply identifier replacement",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = ".test_code_temp.cs", nodeKind = "IdentifierName", textPattern = "newField", replacement = "finalField", dryRun = false, projectPath = ctx.TargetPath },
					data => data?["error"] is null && data?["applied"] is not null)),
			
			new("roslyn_insert_lines: dry run insertAfter",
				() => ctx.RunTestAsync(
					"roslyn_insert_lines",
					new { filePath = ".test_insert_temp.txt", text = "inserted line", insertAfter = "line one", dryRun = true, projectPath = ctx.TargetPath },
					data => data?["applied"]?.GetValue<bool>() == false && data?["inserted_at"]?.GetValue<int>() == 2 && data?["line_count"]?.GetValue<int>() == 1)),
			
			new("roslyn_insert_lines: apply insertAfter",
				() => ctx.RunTestAsync(
					"roslyn_insert_lines",
					new { filePath = ".test_insert_temp.txt", text = "after one", insertAfter = "line one", projectPath = ctx.TargetPath },
					data => data?["applied"]?.GetValue<bool>() == true && data?["inserted_at"]?.GetValue<int>() == 2)),
			
			new("roslyn_insert_lines: apply insertBefore",
				() => ctx.RunTestAsync(
					"roslyn_insert_lines",
					new { filePath = ".test_insert_temp.txt", text = "before three", insertBefore = "line three", projectPath = ctx.TargetPath },
					data => data?["applied"]?.GetValue<bool>() == true && data?["inserted_at"]?.GetValue<int>() == 4)),
			
			new("roslyn_insert_lines: apply atLine",
				() => ctx.RunTestAsync(
					"roslyn_insert_lines",
					new { filePath = ".test_insert_temp.txt", text = "at line 1", atLine = 1, projectPath = ctx.TargetPath },
					data => data?["applied"]?.GetValue<bool>() == true && data?["inserted_at"]?.GetValue<int>() == 1)),
			
			new("roslyn_insert_lines: error when no location specified",
				() => ctx.RunTestAsync(
					"roslyn_insert_lines",
					new { filePath = ".test_insert_temp.txt", text = "oops", projectPath = ctx.TargetPath },
					data => data?["error"]?.GetValue<string>().Contains("exactly one") == true)),
		};
		
		return new TestGroup($"File Editing Tools ({tests.Count} tests)", tests, Teardown: () =>
		{
			
			try { File.Delete(tempTextFile);   } catch { }
			try { File.Delete(tempCodeFile);   } catch { }
			try { File.Delete(tempInsertFile); } catch { }
			try { File.Delete(Path.Combine(ctx.TargetPath, ".test_code_debug.cs")); } catch { }
			
			return Task.CompletedTask;
		});
	}
}
