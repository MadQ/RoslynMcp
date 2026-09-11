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
		var tempVerbatimFile = Path.Combine(ctx.TargetPath, ".test_verbatim_temp.cs");
		
		File.WriteAllText(tempTextFile,   "// Test line 1\nvar handle = IntPtr.Zero;\n// Test line 3\n");
		File.WriteAllText(tempCodeFile,   "class TestClass { private int oldField = 42; }");
		File.WriteAllText(tempInsertFile, "line one\nline two\nline three\n");
		
		// Constructor-replacement fixture (#267): a type whose constructor shares its name, so the
		// "no return type" misparse has something to trip on.
		var tempCtorFile = Path.Combine(ctx.TargetPath, ".test_ctor_temp.cs");
		
		File.WriteAllText(tempCtorFile, "class Widget { int size; Widget(int size) { this.size = size; } }");
		
		var tempMultiCtorFile = Path.Combine(ctx.TargetPath, ".test_multi_ctor_temp.cs");

		File.WriteAllText(tempMultiCtorFile, "class Widget { int size; Widget(int size) { this.size = size; } } class Gadget { int size; Gadget(int size) { this.size = size; } }");

		// Line-ending fixtures (#268): one CRLF file and one LF file. A multi-line replacement
		// arrives from MCP clients with LF; the write must adopt the file's style either way.
		var tempCrlfFile = Path.Combine(ctx.TargetPath, ".test_crlf_temp.cs");
		var tempLfFile   = Path.Combine(ctx.TargetPath, ".test_lf_temp.cs");
		
		File.WriteAllText(tempCrlfFile, "class Crlf\r\n{\r\n\tvoid M()\r\n\t{\r\n\t}\r\n}\r\n");
		File.WriteAllText(tempLfFile,   "class Lf\n{\n\tvoid M()\n\t{\n\t}\n}\n");

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
			
			// ── Regression: unified match-mode enum (#253) ──
			// mode:"regex" is the new preferred form of the old useRegex=true. '\w+' is a regex
			// metacharacter sequence: in the DEFAULT literal mode it would be sought verbatim and match
			// nothing; under mode:"regex" it matches word runs, so match_count>0 proves the mode was
			// honored (not silently treated as literal). Dry run — does not mutate the shared temp file.
			new("roslyn_replace_in_file: mode=regex is honored (dry run)",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_file",
					new { filePath = ".test_replace_temp.cs", pattern = @"\w+", replacement = "X", mode = "regex", dryRun = true, projectPath = ctx.TargetPath },
					data => data?["error"] is null && data?["match_count"]?.GetValue<int>() > 0 && data?["applied"]?.GetValue<bool>() == false)),
			
			// The deprecated useRegex alias must still work AND surface a _caution steering callers to
			// mode. Asserts TryResolveMatchMode wired the deprecation notice onto the result. Dry run.
			new("roslyn_replace_in_file: deprecated useRegex emits _caution (dry run)",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_file",
					new { filePath = ".test_replace_temp.cs", pattern = @"\w+", replacement = "X", useRegex = true, dryRun = true, projectPath = ctx.TargetPath },
					data => data?["_caution"]?.GetValue<string>()?.Contains("deprecated") == true)),
			
			// ── Regression: literal/glob replacement inserted VERBATIM (#254 review) ──
			// Regex.Replace interprets '$' substitution tokens in the replacement ('$&' = whole match,
			// '$$' = literal '$', '${name}', etc.) — correct for regex mode, but WRONG for literal/glob
			// where the replacement must be inserted as-is (and where '$'-tokens over a capture-less
			// pattern can even throw). The fix routes literal/glob through a MatchEvaluator. This applies
			// a LITERAL replacement whose text contains '$&' and reads the file back: '$&' must survive
			// verbatim, NOT expand to the matched text ("AAA"). Fails against the pre-fix Regex.Replace.
			new("roslyn_replace_in_file: literal mode inserts replacement verbatim ($ not substituted)",
				async () => {
					
					File.WriteAllText(tempVerbatimFile, "// AAA marker\n");
					
					var (pass, msg) = await ctx.RunTestAsync(
						"roslyn_replace_in_file",
						new { filePath = ".test_verbatim_temp.cs", pattern = "AAA", replacement = "$&X", mode = "literal", projectPath = ctx.TargetPath },
						data => data?["applied"]?.GetValue<bool>() == true && data?["match_count"]?.GetValue<int>() == 1);
					
					if(!pass)
						return (false, msg);
					
					var content = File.ReadAllText(tempVerbatimFile);
					
					return content.Contains("$&X") && !content.Contains("AAAX")
						? (true,  "PASS  (verbatim)")
						: (false, $"FAIL  (replacement not verbatim: '{content.Trim()}')");
				}),
			
			// Companion: regex mode MUST still perform '$' substitution — the same '$&X' replacement
			// expands to the matched text plus 'X'. Guards against the fix over-reaching and disabling
			// backreferences for the mode that is supposed to keep them.
			new("roslyn_replace_in_file: regex mode still substitutes $& in replacement",
				async () => {
					
					File.WriteAllText(tempVerbatimFile, "// AAA marker\n");
					
					var (pass, msg) = await ctx.RunTestAsync(
						"roslyn_replace_in_file",
						new { filePath = ".test_verbatim_temp.cs", pattern = "AAA", replacement = "$&X", mode = "regex", projectPath = ctx.TargetPath },
						data => data?["applied"]?.GetValue<bool>() == true && data?["match_count"]?.GetValue<int>() == 1);
					
					if(!pass)
						return (false, msg);
					
					var content = File.ReadAllText(tempVerbatimFile);
					
					return content.Contains("AAAX") && !content.Contains("$&X")
						? (true,  "PASS  (substituted)")
						: (false, $"FAIL  (regex substitution not applied: '{content.Trim()}')");
				}),
			
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
			
			// ── Regression: constructor replacement (#267) ──
			// The validator used to route ConstructorDeclaration through ParseExpression, so a
			// byte-for-byte valid constructor was rejected with "Invalid expression term 'int'".
			// A constructor is now parsed inside a dummy type of the same name. This asserts the
			// write succeeds AND lands: the file must contain the new body afterwards.
			new("roslyn_replace_in_code: constructor replacement is accepted and applied",
				async () => {
					
					var (pass, msg) = await ctx.RunTestAsync(
						"roslyn_replace_in_code",
						new { filePath = ".test_ctor_temp.cs", nodeKind = "ConstructorDeclaration", textPattern = "Widget", replacement = "Widget(int size) { this.size = size * 2; }", projectPath = ctx.TargetPath },
						data => data?["error"] is null && data?["applied"]?.GetValue<bool>() == true && data?["change_count"]?.GetValue<int>() == 1);
					
					if(!pass)
						return (false, msg);
					
					var content = File.ReadAllText(tempCtorFile);
					
					return content.Contains("size * 2")
						? (true,  "PASS  (constructor body replaced)")
						: (false, $"FAIL  (constructor not written: '{content.Trim()}')");
				}),
			
			// The 'ctor' alias must resolve to ConstructorDeclaration (dry run — no write).
			new("roslyn_replace_in_code: 'ctor' alias resolves to ConstructorDeclaration",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = ".test_ctor_temp.cs", nodeKind = "ctor", textPattern = "Widget", replacement = "Widget() { }", dryRun = true, projectPath = ctx.TargetPath },
					data => data?["error"] is null && data?["change_count"]?.GetValue<int>() == 1)),
			
			// dryRun used to return before any parsing, so it reported a would-be-fine replacement
			// for text the real write then rejected. It must now fail on the same invalid input.
			new("roslyn_replace_in_code: dry run rejects invalid replacement text",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = ".test_ctor_temp.cs", nodeKind = "ConstructorDeclaration", textPattern = "Widget", replacement = "Widget(int { ", dryRun = true, projectPath = ctx.TargetPath },
					data => data?["error"]?.GetValue<string>()?.Contains("syntax errors") == true)),
			
			// A forced batch that spans constructors from differently named enclosing types must parse
			// the replacement in each match's own wrapper type. Reusing the first parsed constructor
			// would let this dry run pass, then write an invalid constructor into Gadget.
			new("roslyn_replace_in_code: dry run rejects forced constructor batch across different types",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = ".test_multi_ctor_temp.cs", nodeKind = "ConstructorDeclaration", textPattern = "g", replacement = "Widget(int size) { this.size = size * 2; }", dryRun = true, force = true, projectPath = ctx.TargetPath },
					data => data?["error"]?.GetValue<string>()?.Contains("syntax errors") == true)),

			// A replacement that smuggles in a second member (or closes the type early) must be
			// refused — the tool replaces exactly one node.
			new("roslyn_replace_in_code: replacement must be exactly one member",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = ".test_ctor_temp.cs", nodeKind = "ConstructorDeclaration", textPattern = "Widget", replacement = "Widget() { } void Extra() { }", dryRun = true, projectPath = ctx.TargetPath },
					data => data?["error"]?.GetValue<string>() is { Length: > 0 } && data?["details"]?.GetValue<string>()?.Contains("exactly one") == true)),
			
			// ── Regression: line endings (#268) ──
			// The replacement node used to be spliced in with the replacement text's own newlines
			// (LF from MCP clients), leaving a CRLF file mixed. Both directions are checked: the
			// file's style must win over the replacement's, whichever way they disagree.
			new("roslyn_replace_in_code: LF replacement adopts CRLF file's line endings",
				async () => {
					
					var (pass, msg) = await ctx.RunTestAsync(
						"roslyn_replace_in_code",
						new { filePath = ".test_crlf_temp.cs", nodeKind = "MethodDeclaration", textPattern = "M", replacement = "void M()\n{\n\tvar x = 1;\n\tvar y = x;\n}", projectPath = ctx.TargetPath },
						data => data?["error"] is null && data?["applied"]?.GetValue<bool>() == true);
					
					if(!pass)
						return (false, msg);
					
					var content = File.ReadAllText(tempCrlfFile);
					var lfCount = content.Count(c => c == '\n');
					var crCount = content.Count(c => c == '\r');
					
					return crCount == lfCount && content.Contains("var y = x;")
						? (true,  $"PASS  (all {lfCount} line endings CRLF)")
						: (false, $"FAIL  (mixed endings: {crCount} CR vs {lfCount} LF)");
				}),
			
			new("roslyn_replace_in_code: CRLF replacement adopts LF file's line endings",
				async () => {
					
					var (pass, msg) = await ctx.RunTestAsync(
						"roslyn_replace_in_code",
						new { filePath = ".test_lf_temp.cs", nodeKind = "MethodDeclaration", textPattern = "M", replacement = "void M()\r\n{\r\n\tvar x = 1;\r\n\tvar y = x;\r\n}", projectPath = ctx.TargetPath },
						data => data?["error"] is null && data?["applied"]?.GetValue<bool>() == true);
					
					if(!pass)
						return (false, msg);
					
					var content = File.ReadAllText(tempLfFile);
					
					return !content.Contains('\r') && content.Contains("var y = x;")
						? (true,  "PASS  (no carriage returns introduced)")
						: (false, $"FAIL  ({content.Count(c => c == '\r')} stray CR in LF file)");
				}),
			
			// The opt-out must be honored: with normalizeLineEndings=false the replacement's own
			// endings are written verbatim, mixed or not — that is the caller's explicit choice.
			new("roslyn_replace_in_code: normalizeLineEndings=false writes replacement verbatim",
				async () => {
					
					var (pass, msg) = await ctx.RunTestAsync(
						"roslyn_replace_in_code",
						new { filePath = ".test_lf_temp.cs", nodeKind = "MethodDeclaration", textPattern = "M", replacement = "void M()\r\n{\r\n}", normalizeLineEndings = false, projectPath = ctx.TargetPath },
						data => data?["error"] is null && data?["applied"]?.GetValue<bool>() == true);
					
					if(!pass)
						return (false, msg);
					
					var content = File.ReadAllText(tempLfFile);
					
					return content.Contains("M()\r\n{\r\n}")
						? (true,  "PASS  (CRLF kept as supplied)")
						: (false, "FAIL  (opt-out ignored — replacement was normalized)");
				}),
			
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
			
			// ── Regression: guarded workspace resolution (transient mid-reload hardening) ──
			// Before the guard, editing tools called workspace.GetRootPath / GetSecurityBoundary /
			// GetSolution / ApplyChanges directly. When the workspace threw during resolution — e.g. a
			// project reloading after a prior edit — the exception propagated unhandled and the MCP
			// transport reported an opaque "An error occurred invoking '<tool>'": no JSON, no detail.
			// An empty projectPath deterministically forces a resolution failure, standing in for the
			// transient case that cannot be provoked on demand. The fix must now return a STRUCTURED
			// JSON error (parseable, carrying an "error" field) instead of an unhandled throw.
			// RunTestAsync fails on non-JSON content, so each of these would fail against the pre-fix
			// server — they are not tautological: they assert the exception was caught and shaped.
			
			new("roslyn_replace_in_code: structured error (not crash) on resolution failure",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = ".test_code_temp.cs", nodeKind = "IdentifierName", textPattern = "x", replacement = "y", projectPath = "" },
					data => data?["error"]?.GetValue<string>() is { Length: > 0 })),
			
			new("roslyn_replace_in_file: structured error (not crash) on resolution failure",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_file",
					new { filePath = ".test_replace_temp.cs", pattern = "x", replacement = "y", dryRun = true, projectPath = "" },
					data => data?["error"]?.GetValue<string>() is { Length: > 0 })),
			
			new("roslyn_insert_lines: structured error (not crash) on resolution failure",
				() => ctx.RunTestAsync(
					"roslyn_insert_lines",
					new { filePath = ".test_insert_temp.txt", text = "x", atLine = 1, projectPath = "" },
					data => data?["error"]?.GetValue<string>() is { Length: > 0 })),
			
			new("roslyn_write_file: structured error (not crash) on resolution failure",
				() => ctx.RunTestAsync(
					"roslyn_write_file",
					new { filePath = ".test_write_temp.cs", content = "x", createNew = true, dryRun = true, projectPath = "" },
					data => data?["error"]?.GetValue<string>() is { Length: > 0 })),
			
			new("roslyn_local_history: structured error (not crash) on resolution failure",
				() => ctx.RunTestAsync(
					"roslyn_local_history",
					new { action = "list", filePath = ".test_code_temp.cs", projectPath = "" },
					data => data?["error"]?.GetValue<string>() is { Length: > 0 })),
		};
		
		return new TestGroup($"File Editing Tools ({tests.Count} tests)", tests, Teardown: () =>
		{
			
			try { File.Delete(tempTextFile);   } catch { }
			try { File.Delete(tempCodeFile);   } catch { }
			try { File.Delete(tempInsertFile); } catch { }
			try { File.Delete(tempVerbatimFile); } catch { }
			try { File.Delete(tempCtorFile);     } catch { }
			try { File.Delete(tempMultiCtorFile); } catch { }
			try { File.Delete(tempCrlfFile);     } catch { }
			try { File.Delete(tempLfFile);       } catch { }
			try { File.Delete(Path.Combine(ctx.TargetPath, ".test_code_debug.cs")); } catch { }
			
			return Task.CompletedTask;
		});
	}
}
