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
		
		// All fixtures live in a temp MSBuild project (#274) — never in src/RoslynMcp, where each
		// scratch .cs compiled into the dogfood assembly and forced a workspace reload in every
		// server watching the repo. MSBuild rather than adhoc keeps the production write path
		// (TryApplyChanges, backups) under test. Written before the first tool call.
		var fx = TestFixtures.NewMsBuildProject("Editing");
		
		var tempTextFile     = fx.Write("replace.cs",  "// Test line 1\nvar handle = IntPtr.Zero;\n// Test line 3\n");
		var tempCodeFile     = fx.Write("code.cs",     "class TestClass { private int oldField = 42; }");
		var tempInsertFile   = fx.Write("insert.txt",  "line one\nline two\nline three\n");
		var tempRangeFile    = fx.Write("ranges.txt",  "replace\nkeep\nreplace\n");
		var tempVerbatimFile = fx.PathOf("verbatim.cs");
		
		// Literal-escape fixture (#292): line 1 holds a backslash-n escape sequence as source text,
		// and lines end in CRLF so a pattern with a real newline exercises the CRLF-agnostic path.
		fx.Write("escapes.txt", "say(\"a\\nb\");\r\nnext\r\n");
		
		// Constructor-replacement fixture (#267): a type whose constructor shares its name, so the
		// "no return type" misparse has something to trip on. Note that three of these files declare
		// a type called Widget, so this fixture project deliberately does NOT compile — every test in
		// this group is syntax-level (roslyn_replace_in_code parses the file it edits). Give any test
		// that needs clean diagnostics its own fixture project rather than fixing the duplicates.
		var tempCtorFile        = fx.Write("ctor.cs",         "class Widget { int size; Widget(int size) { this.size = size; } }");
		var tempEscapedCtorFile = fx.Write("escaped_ctor.cs", "class @Widget { int size; @Widget(int size) { this.size = size; } }");
		var tempMultiCtorFile   = fx.Write("multi_ctor.cs",   "class Widget { int size; Widget(int size) { this.size = size; } } class Gadget { int size; Gadget(int size) { this.size = size; } }");
		
		// Several-members fixture (#463): a documented method between two fields, with a comment at
		// the end of its last line, and an enum. Two copies of the class, one per test that writes.
		const string severalSource = "class Several\n{\n\tint kept = 1;\n\n\t/// <summary>Old.</summary>\n\tint Twice(int value)\n\t{\n\t\treturn value * 2;\n\t} // end of Twice\n\n\tint after = 2;\n}\n";
		
		var tempSeveralFile = fx.Write("several.cs", severalSource);
		
		fx.Write("several_bad.cs", severalSource.Replace("class Several", "class SeveralBad"));
		fx.Write("several_enum.cs", "enum Tone\n{\n\tLow,\n\tHigh,\n}\n");

		// Line-ending fixtures (#268): one CRLF file and one LF file. A multi-line replacement
		// arrives from MCP clients with LF; the write must adopt the file's style either way.
		var tempCrlfFile          = fx.Write("crlf.cs",            "class Crlf\r\n{\r\n\tvoid M()\r\n\t{\r\n\t}\r\n}\r\n");
		var tempLfFile            = fx.Write("lf.cs",              "class Lf\n{\n\tvoid M()\n\t{\n\t}\n}\n");
		var tempMostlyLfMixedFile = fx.Write("mostly_lf_mixed.cs", "class Mixed\n{\n\tvoid M()\r\n\t{\n\t}\n}\n");

		// Line and column fixture (#356): five if statements a text pattern could only tell apart by
		// their bodies, two of them on line 8. Each test edits its own copy, so none passes on
		// another's write. Line 8's second statement starts at column 26.
		const string pickSource = "class Pick\n{\n\tvoid M(int a)\n\t{\n\t\tif(a == 1) { a = 10; }\n\t\tif(a == 2) { a = 20; }\n\t\tif(a == 3) { a = 30; }\n\t\tif(a == 4) { a = 40; } if(a == 5) { a = 50; }\n\t}\n}\n";
		
		var tempPickLine   = fx.Write("pick_line.cs",   pickSource);
		var tempPickColumn = fx.Write("pick_column.cs", pickSource);
		var tempPickStale  = fx.Write("pick_stale.cs",  pickSource);
		var tempPickDry    = fx.Write("pick_dry.cs",    pickSource);
		
		fx.Write("pick_all.cs", pickSource);
		
		// Both member accesses in 'a.B.C' start at line 3, column 16, so a position cannot part them.
		fx.Write("pick_nested.cs", "class Nested\n{\n\tint M(S a) => a.B.C;\n}\n");
		
		// Comment fixture (#416): a field with a comment at the end of its line, a documented method,
		// an undocumented one holding a commented statement, two regions with a heading over them (one
		// whose first member is documented, one whose is not), a method with code switched off by
		// a false #if between its doc comment and itself, and a method whose doc comment is
		// followed by a directive and a plain comment. The tool used to give a replacement the old node's trivia whole,
		// so a comment in the replacement text was dropped and the old one stayed, with a success
		// response either way. Each test edits a copy of its own and compares the whole file, so
		// that a comment that lands in the wrong place, twice, or at the wrong indentation fails too.
		// The section heading above M is in M's leading trivia as well, a blank line away from the
		// doc comment: it is not M's comment, and no edit of M may take it. That blank line is
		// indented, as this repo writes them, so it is whitespace before its line end. Two enums
		// follow: one whose member has a comment after its comma, which belongs to the comma and
		// not to the member, and one on a single line that is the last thing in the file, which
		// does not end in a line break.
		const string docSource = "class Doc\n{\n\tint count; // old count\n\n\t// ── Methods ──\n\t\n\t/// <summary>Old words.</summary>\n\tvoid M()\n\t{\n\t}\n\n\tvoid N(int a)\n\t{\n\t\t// old reason\n\t\tif(a == 1) { a = 10; }\n\t}\n\n\t// ── Region ──\n\t#region R\n\t/// <summary>In region.</summary>\n\tvoid Q()\n\t{\n\t}\n\t#endregion\n\n\t// ── Bare region ──\n\t#region B\n\tvoid S()\n\t{\n\t}\n\t#endregion\n\n\t/// <summary>Switched.</summary>\n#if NEVER\n\tint off;\n#endif\n\tvoid T()\n\t{\n\t}\n\n\t/// <summary>Guarded.</summary>\n#pragma warning disable CS0168\n\t// kept reason\n\tvoid P()\n\t{\n\t}\n#pragma warning restore CS0168\n}\n\nenum Level\n{\n\tLow, // old low\n\tHigh\n}\n\nenum Kind { A, B }";
		
		foreach(var name in new[] { "new", "keep", "add", "trailing", "trailing_keep", "guarded", "statement", "enum_comma", "enum_inline", "file_end", "below", "directive", "directive_between", "region", "region_bare", "disabled", "trailing_below" })
			fx.Write($"doc_{name}.cs", docSource);
		
		// One replacement in the copy named, then the file must be the fixture with exactly that one
		// change, and the response must carry a caution exactly when part of the text did not land.
		Func<Task<(bool pass, string msg)>> DocEdit(string name, string nodeKind, string textPattern, string replacement, string before, string after, bool cautioned)
			=> async () => {
				
				var (pass, msg) = await ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = $"doc_{name}.cs", nodeKind, textPattern, replacement, projectPath = fx.Csproj },
					data => data?["applied"]?.GetValue<bool>() == true
						&& data?["change_count"]?.GetValue<int>() == 1
						&& (data?["_caution"]?.GetValue<string>() is { } caution
							&& caution.Contains("not applied", StringComparison.Ordinal)
							&& caution.Contains("Node at line ", StringComparison.Ordinal)) == cautioned);
				
				if(!pass)
					return (false, msg);
				
				var content = File.ReadAllText(fx.PathOf($"doc_{name}.cs"));
				
				return content == docSource.Replace(before, after)
					? (true,  "PASS  (the file holds exactly the expected change)")
					: (false, $"FAIL  (unexpected content: '{content}')");
			};
		
		var tests = new List<TestCase> {
			
			new("roslyn_replace_in_file: dry run literal replacement",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_file",
					new { filePath = "replace.cs", pattern = "IntPtr", replacement = "nint", dryRun = true, projectPath = fx.Csproj },
					data => data?["match_count"]?.GetValue<int>() == 1 && data?["applied"]?.GetValue<bool>() == false)),
			
			new("roslyn_replace_in_file: apply literal replacement",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_file",
					new { filePath = "replace.cs", pattern = "IntPtr", replacement = "nint", dryRun = false, projectPath = fx.Csproj },
					data => data?["match_count"]?.GetValue<int>() == 1 && data?["applied"]?.GetValue<bool>() == true)),
			
			new("roslyn_replace_in_file: regex replacement with capture groups",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_file",
					new { filePath = "replace.cs", pattern = @"var (\w+) = nint\.Zero", replacement = "nint $1 = 0", useRegex = true, projectPath = fx.Csproj },
					data => data?["match_count"]?.GetValue<int>() == 1 && data?["changed_line_ranges"]?.AsArray() is [var range] && range?["start"]?.GetValue<int>() == 2 && range?["end"]?.GetValue<int>() == 2)),
			
			// The result compactor must preserve non-contiguous changed lines as separate, ascending
			// inclusive ranges. Two matches on lines 1 and 3 are deliberately separated by "keep".
			new("roslyn_replace_in_file: reports discontiguous changed-line ranges",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_file",
					new { filePath = "ranges.txt", pattern = "replace", replacement = "updated", dryRun = true, projectPath = fx.Csproj },
					data => data?["match_count"]?.GetValue<int>() == 2 && data?["changed_line_ranges"]?.AsArray() is [var first, var second] && first?["start"]?.GetValue<int>() == 1 && first?["end"]?.GetValue<int>() == 1 && second?["start"]?.GetValue<int>() == 3 && second?["end"]?.GetValue<int>() == 3)),

			// ── Regression: literal backslash-n in literal mode (#292) ──
			// Regex.Escape renders a real newline as \n but a literal backslash-n as \\n; the old
			// post-escape Replace(@"\n", @"\r?\n") also rewrote the tail of \\n, so a pattern containing
			// a backslash-n escape could never match its own text. Fails against the pre-fix builder.
			new("roslyn_replace_in_file: literal mode matches a backslash-n escape sequence (dry run)",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_file",
					new { filePath = "escapes.txt", pattern = @"say(""a\nb"");", replacement = "X", mode = "literal", dryRun = true, projectPath = fx.Csproj },
					data => data?["match_count"]?.GetValue<int>() == 1 && data?["changed_line_ranges"]?.AsArray() is [var range] && range?["start"]?.GetValue<int>() == 1)),
			
			// The #292 fix splits on real newlines before escaping; a pattern with a real LF newline
			// must still match the fixture's CRLF line break, preserving the CRLF-agnostic contract.
			new("roslyn_replace_in_file: literal mode real newline still matches CRLF (dry run)",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_file",
					new { filePath = "escapes.txt", pattern = "\");\nnext", replacement = "X", mode = "literal", dryRun = true, projectPath = fx.Csproj },
					data => data?["match_count"]?.GetValue<int>() == 1)),
			
			// ── Regression: unified match-mode enum (#253) ──
			// mode:"regex" is the new preferred form of the old useRegex=true. '\w+' is a regex
			// metacharacter sequence: in the DEFAULT literal mode it would be sought verbatim and match
			// nothing; under mode:"regex" it matches word runs, so match_count>0 proves the mode was
			// honored (not silently treated as literal). Dry run — does not mutate the shared temp file.
			new("roslyn_replace_in_file: mode=regex is honored (dry run)",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_file",
					new { filePath = "replace.cs", pattern = @"\w+", replacement = "X", mode = "regex", dryRun = true, projectPath = fx.Csproj },
					data => data?["error"] is null && data?["match_count"]?.GetValue<int>() > 0 && data?["applied"]?.GetValue<bool>() == false)),
			
			// The deprecated useRegex alias must still work AND surface a _caution steering callers to
			// mode. Asserts TryResolveMatchMode wired the deprecation notice onto the result. Dry run.
			new("roslyn_replace_in_file: deprecated useRegex emits _caution (dry run)",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_file",
					new { filePath = "replace.cs", pattern = @"\w+", replacement = "X", useRegex = true, dryRun = true, projectPath = fx.Csproj },
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
						new { filePath = "verbatim.cs", pattern = "AAA", replacement = "$&X", mode = "literal", projectPath = fx.Csproj },
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
						new { filePath = "verbatim.cs", pattern = "AAA", replacement = "$&X", mode = "regex", projectPath = fx.Csproj },
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
					new { filePath = "code.cs", nodeKind = "IdentifierName", textPattern = "oldField", replacement = "newField", dryRun = true, projectPath = fx.Csproj },
					data => data?["error"] is null && data?["change_count"] is not null)),
			
			new("roslyn_replace_in_code: apply identifier replacement",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = "code.cs", nodeKind = "IdentifierName", textPattern = "newField", replacement = "finalField", dryRun = false, projectPath = fx.Csproj },
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
						new { filePath = "ctor.cs", nodeKind = "ConstructorDeclaration", textPattern = "Widget", replacement = "Widget(int size) { this.size = size * 2; }", projectPath = fx.Csproj },
						data => data?["error"] is null && data?["applied"]?.GetValue<bool>() == true && data?["change_count"]?.GetValue<int>() == 1);
					
					if(!pass)
						return (false, msg);
					
					var content = File.ReadAllText(tempCtorFile);
					
					return content.Contains("size * 2")
						? (true,  "PASS  (constructor body replaced)")
						: (false, $"FAIL  (constructor not written: '{content.Trim()}')");
				}),
			
			// ── #416: a comment in the replacement text lands; one that cannot is reported ────────────
			// The replacement's doc comment takes the place of the old one, at the file's indentation.
			new("roslyn_replace_in_code: a doc comment in the replacement replaces the old one",
				DocEdit("new", "MethodDeclaration", "M",
					"/// <summary>New words.</summary>\n\tvoid M()\n\t{\n\t\tcount++;\n\t}",
					"\t/// <summary>Old words.</summary>\n\tvoid M()\n\t{\n\t}",
					"\t/// <summary>New words.</summary>\n\tvoid M()\n\t{\n\t\tcount++;\n\t}",
					cautioned: false)),
			
			// The other half of the rule, and the reason trivia is carried over at all: an edit of
			// the body alone must not cost the node its documentation.
			new("roslyn_replace_in_code: a replacement without a comment keeps the old doc comment",
				DocEdit("keep", "MethodDeclaration", "M",
					"void M()\n\t{\n\t\tcount++;\n\t}",
					"\tvoid M()\n\t{\n\t}",
					"\tvoid M()\n\t{\n\t\tcount++;\n\t}",
					cautioned: false)),
			
			// No old comment to replace: the new one is added, indented like the node.
			new("roslyn_replace_in_code: a doc comment is added to a node that had none",
				DocEdit("add", "MethodDeclaration", "N",
					"/// <summary>Added.</summary>\n\tvoid N(int a)\n\t{\n\t}",
					"\tvoid N(int a)\n\t{\n\t\t// old reason\n\t\tif(a == 1) { a = 10; }\n\t}",
					"\t/// <summary>Added.</summary>\n\tvoid N(int a)\n\t{\n\t}",
					cautioned: false)),
			
			// The comment at the end of the node's line follows the same rule in both directions.
			new("roslyn_replace_in_code: a comment at the end of the replacement's line replaces the old one",
				DocEdit("trailing", "FieldDeclaration", "count",
					"int count = 1; // new count",
					"int count; // old count",
					"int count = 1; // new count",
					cautioned: false)),
			
			new("roslyn_replace_in_code: a replacement without an end-of-line comment keeps the old one",
				DocEdit("trailing_keep", "FieldDeclaration", "count",
					"int count = 1;",
					"int count; // old count",
					"int count = 1; // old count",
					cautioned: false)),
			
			// Not only declarations, and not only doc comments: a statement's '//' comment.
			new("roslyn_replace_in_code: a statement's comment is replaced by the replacement's",
				DocEdit("statement", "IfStatement", "",
					"// new reason\n\t\tif(a == 2) { a = 20; }",
					"// old reason\n\t\tif(a == 1) { a = 10; }",
					"// new reason\n\t\tif(a == 2) { a = 20; }",
					cautioned: false)),
			
			// A directive between the old comment and the declaration would go with the comment, so
			// both stay — and the response must say that the new comment did not land, since the
			// rest of the edit did.
			new("roslyn_replace_in_code: a comment that cannot replace the old one is reported, not dropped",
				DocEdit("guarded", "MethodDeclaration", "P",
					"/// <summary>Never lands.</summary>\n\tvoid P()\n\t{\n\t\tcount++;\n\t}",
					"\tvoid P()\n\t{\n\t}",
					"\tvoid P()\n\t{\n\t\tcount++;\n\t}",
					cautioned: true)),
			
			// The comment after an enum member's comma is trivia of the comma, a token of the list. It
			// was the one end-of-line comment the rule above could not reach: dropped, the old one kept.
			new("roslyn_replace_in_code: a comment after an enum member's comma replaces the old one",
				DocEdit("enum_comma", "EnumMemberDeclaration", "Low",
					"Low = 1, // new low",
					"Low, // old low",
					"Low = 1, // new low",
					cautioned: false)),
			
			// More code follows on the member's line, and a comment to the end of the line would
			// comment it out: the member is replaced, the comment is not written, and the response says so.
			new("roslyn_replace_in_code: a comment that would swallow the rest of the line is reported, not written",
				DocEdit("enum_inline", "EnumMemberDeclaration", "A",
					"A = 1 // one",
					"enum Kind { A, B }",
					"enum Kind { A = 1, B }",
					cautioned: true)),
			
			// The last line of a file without a final line break ends too: nothing follows the node,
			// so its comment lands and nothing is cautioned.
			new("roslyn_replace_in_code: a comment after the last node of a file without a final line break lands",
				DocEdit("file_end", "EnumDeclaration", "Kind",
					"enum Kind { A, B } // done",
					"enum Kind { A, B }",
					"enum Kind { A, B } // done",
					cautioned: false)),
			
			// Only the node and its comments are written. What stands below the node in the text
			// is not part of it; the node lands, and the rest must be named rather than vanish.
			new("roslyn_replace_in_code: text below the node in the replacement is reported, not dropped unseen",
				DocEdit("below", "MethodDeclaration", "M",
					"void M()\n\t{\n\t\tcount++;\n\t}\n\t// below the node",
					"\tvoid M()\n\t{\n\t}",
					"\tvoid M()\n\t{\n\t\tcount++;\n\t}",
					cautioned: true)),
			
			// A directive before the node is half of a pair more often than not, with the other half
			// below the node. Writing the half that is in reach left the file with a #region that
			// never ends or a warning that stays disabled, so none of it is written. Nothing stands
			// below the node here, so the caution can only be the one about the directive.
			new("roslyn_replace_in_code: a directive in the replacement is reported, not written",
				DocEdit("directive", "MethodDeclaration", "M",
					"#pragma warning disable CS0168\n\tvoid M()\n\t{\n\t\tcount++;\n\t}",
					"\tvoid M()\n\t{\n\t}",
					"\tvoid M()\n\t{\n\t\tcount++;\n\t}",
					cautioned: true)),
			
			// A directive between the new comment and the node is left out with its own indentation:
			// the node must come out indented once, under the comment, not twice.
			new("roslyn_replace_in_code: a directive between the replacement's comment and its node leaves no indentation behind",
				DocEdit("directive_between", "MethodDeclaration", "N",
					"/// <summary>Added.</summary>\n\t#pragma warning disable CS0168\n\tvoid N(int a)\n\t{\n\t}",
					"\tvoid N(int a)\n\t{\n\t\t// old reason\n\t\tif(a == 1) { a = 10; }\n\t}",
					"\t/// <summary>Added.</summary>\n\tvoid N(int a)\n\t{\n\t}",
					cautioned: true)),
			
			// The heading stands directly over the #region, which stands directly over Q's doc comment.
			// It is the region's heading: Q's comment is replaced, and heading and directive stay.
			new("roslyn_replace_in_code: a comment above a #region is not the comment of the member below it",
				DocEdit("region", "MethodDeclaration", "Q",
					"/// <summary>New in region.</summary>\n\tvoid Q()\n\t{\n\t}",
					"/// <summary>In region.</summary>",
					"/// <summary>New in region.</summary>",
					cautioned: false)),
			
			// The same heading over a region whose first member has no doc comment: the heading is
			// still the region's, so there is nothing of S's to replace and the comment is added.
			new("roslyn_replace_in_code: a doc comment is added under a #region, below the region's heading",
				DocEdit("region_bare", "MethodDeclaration", "S",
					"/// <summary>Added in region.</summary>\n\tvoid S()\n\t{\n\t}",
					"\tvoid S()\n\t{\n\t}",
					"\t/// <summary>Added in region.</summary>\n\tvoid S()\n\t{\n\t}",
					cautioned: false)),
			
			// Code a false #if switches off stands between T's doc comment and T. Replacing the
			// comment would take the #if block with it, and adding the new one under the #endif
			// would leave the old one stale above: both stay as they are, and the response says so.
			new("roslyn_replace_in_code: a doc comment above switched-off code is left alone and reported",
				DocEdit("disabled", "MethodDeclaration", "T",
					"/// <summary>Never lands.</summary>\n\tvoid T()\n\t{\n\t\tcount++;\n\t}",
					"\tvoid T()\n\t{\n\t}",
					"\tvoid T()\n\t{\n\t\tcount++;\n\t}",
					cautioned: true)),
			
			// A comment below the node is not a comment at the end of its line. It is reported like
			// any text below the node, and the comment the file has on that line must survive it.
			new("roslyn_replace_in_code: a comment below the node does not cost the line its own comment",
				DocEdit("trailing_below", "FieldDeclaration", "count",
					"int count = 1;\n\t// below the node",
					"int count; // old count",
					"int count = 1; // old count",
					cautioned: true)),
			
			// ── #356: line and column pick one of several similar nodes ─────────────────────────────
			// Before these, the third if in a method needed a unique text fragment or force=true, which
			// replaces all of them. The other statements must come through byte for byte.
			new("roslyn_replace_in_code: line replaces only the node on that line",
				async () => {
					
					var (pass, msg) = await ctx.RunTestAsync(
						"roslyn_replace_in_code",
						new { filePath = "pick_line.cs", nodeKind = "IfStatement", line = 6, replacement = "if(a == 2) { a = 99; }", projectPath = fx.Csproj },
						data => data?["applied"]?.GetValue<bool>() == true && data?["change_count"]?.GetValue<int>() == 1);
					
					if(!pass)
						return (false, msg);
					
					var content = File.ReadAllText(tempPickLine);
					
					return content == pickSource.Replace("a = 20", "a = 99")
						? (true,  "PASS  (only line 6 changed)")
						: (false, $"FAIL  (unexpected content: '{content}')");
				}),
			
			new("roslyn_replace_in_code: column tells apart two nodes on one line",
				async () => {
					
					var (pass, msg) = await ctx.RunTestAsync(
						"roslyn_replace_in_code",
						new { filePath = "pick_column.cs", nodeKind = "IfStatement", line = 8, column = 26, replacement = "if(a == 5) { a = 99; }", projectPath = fx.Csproj },
						data => data?["applied"]?.GetValue<bool>() == true && data?["change_count"]?.GetValue<int>() == 1);
					
					if(!pass)
						return (false, msg);
					
					var content = File.ReadAllText(tempPickColumn);
					
					return content == pickSource.Replace("a = 50", "a = 99")
						? (true,  "PASS  (only the second statement changed)")
						: (false, $"FAIL  (unexpected content: '{content}')");
				}),
			
			// Without column, line 8 holds two matches, and the multiple-match guard must still hold.
			new("roslyn_replace_in_code: line alone on a line with two matches applies nothing",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = "pick_all.cs", nodeKind = "IfStatement", line = 8, replacement = "if(a == 4) { }", projectPath = fx.Csproj },
					data => data?["applied"]?.GetValue<bool>() == false && data?["change_count"]?.GetValue<int>() == 2)),
			
			// The danger of a position is that it goes stale. Line 9 is the closing brace of M, so the
			// edit must find nothing and write nothing, not reach for the nearest statement.
			new("roslyn_replace_in_code: a stale line matches nothing and writes nothing",
				async () => {
					
					var (pass, msg) = await ctx.RunTestAsync(
						"roslyn_replace_in_code",
						new { filePath = "pick_stale.cs", nodeKind = "IfStatement", line = 9, replacement = "if(a == 9) { }", projectPath = fx.Csproj },
						data => data?["applied"]?.GetValue<bool>() == false
							&& data?["change_count"]?.GetValue<int>() == 0
							&& data?["message"]?.GetValue<string>().Contains("line 9") == true);
					
					if(!pass)
						return (false, msg);
					
					return File.ReadAllText(tempPickStale) == pickSource
						? (true,  "PASS  (file unchanged)")
						: (false, "FAIL  (the file was written)");
				}),
			
			new("roslyn_replace_in_code: a dry run with line reports one match and writes nothing",
				async () => {
					
					var (pass, msg) = await ctx.RunTestAsync(
						"roslyn_replace_in_code",
						new { filePath = "pick_dry.cs", nodeKind = "IfStatement", line = 7, replacement = "if(a == 3) { }", dryRun = true, projectPath = fx.Csproj },
						data => data?["applied"]?.GetValue<bool>() == false
							&& data?["change_count"]?.GetValue<int>() == 1
							&& data?["changed_nodes"]?[0]?["line"]?.GetValue<int>() == 7);
					
					if(!pass)
						return (false, msg);
					
					return File.ReadAllText(tempPickDry) == pickSource
						? (true,  "PASS  (file unchanged)")
						: (false, "FAIL  (the dry run wrote the file)");
				}),
			
			// The filters narrow textPattern and do not replace it: a match on one with a miss on the
			// other is a miss, so a stale line cannot pass because the pattern still matches elsewhere.
			new("roslyn_replace_in_code: line combines with textPattern",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = "pick_all.cs", nodeKind = "IfStatement", textPattern = "a == 1", line = 6, replacement = "if(a == 1) { }", dryRun = true, projectPath = fx.Csproj },
					data => data?["change_count"]?.GetValue<int>() == 0)),
			
			// The other half of the test above: a filter that emptied the result whenever both were set
			// would pass that one.
			new("roslyn_replace_in_code: line and textPattern that agree select the node",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = "pick_all.cs", nodeKind = "IfStatement", textPattern = "a == 2", line = 6, replacement = "if(a == 2) { }", dryRun = true, projectPath = fx.Csproj },
					data => data?["change_count"]?.GetValue<int>() == 1)),
			
			// Column 4 is inside the first statement on line 8. A column must name a start exactly,
			// not the nearest one, and the message must name what was asked for.
			new("roslyn_replace_in_code: a column that holds no node start matches nothing",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = "pick_all.cs", nodeKind = "IfStatement", line = 8, column = 4, replacement = "if(a == 4) { }", dryRun = true, projectPath = fx.Csproj },
					data => data?["change_count"]?.GetValue<int>() == 0
						&& data?["message"]?.GetValue<string>().Contains("line 8, column 4") == true
						&& data?["message"]?.GetValue<string>().Contains("without line and column") == true)),
			
			// Telling an agent that already passed a position to pass one would loop forever.
			new("roslyn_replace_in_code: nodes sharing a start get advice other than a position",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = "pick_nested.cs", nodeKind = "SimpleMemberAccessExpression", line = 3, column = 16, replacement = "x", projectPath = fx.Csproj },
					data => data?["applied"]?.GetValue<bool>() == false
						&& data?["change_count"]?.GetValue<int>() == 2
						&& data?["message"]?.GetValue<string>().Contains("same position") == true
						&& data?["message"]?.GetValue<string>().Contains("roslyn_replace_in_file") == true)),
			

			new("roslyn_replace_in_code: omitting line and column behaves as before",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = "pick_all.cs", nodeKind = "IfStatement", replacement = "if(a == 1) { }", projectPath = fx.Csproj },
					data => data?["applied"]?.GetValue<bool>() == false
						&& data?["change_count"]?.GetValue<int>() == 5
						&& data?["message"]?.GetValue<string>().Contains("line and column") == true)),
			
			new("roslyn_replace_in_code: column without line is refused",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = "pick_all.cs", nodeKind = "IfStatement", column = 3, replacement = "if(a == 1) { }", dryRun = true, projectPath = fx.Csproj },
					data => data?["hint"]?.GetValue<string>().Contains("column requires line") == true)),
			
			new("roslyn_replace_in_code: a negative line is refused",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = "pick_all.cs", nodeKind = "IfStatement", line = -1, replacement = "if(a == 1) { }", dryRun = true, projectPath = fx.Csproj },
					data => data?["error"]?.GetValue<string>().Contains("not a valid position") == true)),
			

			// The 'ctor' alias must resolve to ConstructorDeclaration (dry run — no write).
			new("roslyn_replace_in_code: 'ctor' alias resolves to ConstructorDeclaration",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = "ctor.cs", nodeKind = "ctor", textPattern = "Widget", replacement = "Widget() { }", dryRun = true, projectPath = fx.Csproj },
					data => data?["error"] is null && data?["change_count"]?.GetValue<int>() == 1)),
			
			// dryRun used to return before any parsing, so it reported a would-be-fine replacement
			// for text the real write then rejected. It must now fail on the same invalid input.
			new("roslyn_replace_in_code: dry run rejects invalid replacement text",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = "ctor.cs", nodeKind = "ConstructorDeclaration", textPattern = "Widget", replacement = "Widget(int { ", dryRun = true, projectPath = fx.Csproj },
					data => data?["error"]?.GetValue<string>()?.Contains("syntax errors") == true)),
			
			// Escaped identifiers are semantically compared by ValueText, so a constructor spelled
			// without '@' still matches an enclosing type declared as '@Widget'.
			new("roslyn_replace_in_code: escaped constructor name matches enclosing escaped type",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = "escaped_ctor.cs", nodeKind = "ConstructorDeclaration", textPattern = "Widget", replacement = "Widget(int size) { this.size = size * 2; }", dryRun = true, projectPath = fx.Csproj },
					data => data?["error"] is null && data?["change_count"]?.GetValue<int>() == 1)),
			
			// A forced batch that spans constructors from differently named enclosing types must parse
			// the replacement in each match's own wrapper type. Reusing the first parsed constructor
			// would let this dry run pass, then write an invalid constructor into Gadget.
			new("roslyn_replace_in_code: dry run rejects forced constructor batch across different types",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = "multi_ctor.cs", nodeKind = "ConstructorDeclaration", textPattern = "g", replacement = "Widget(int size) { this.size = size * 2; }", dryRun = true, force = true, projectPath = fx.Csproj },
					data => data?["error"]?.GetValue<string>()?.Contains("syntax errors") == true)),

			// One member of a type may be replaced by several (#463). "Exactly one member" turned
			// away the commonest edit there is to a type, a method and a new overload beside it, and
			// the agent took it to the text tool: two of the tool's six failures in a week of logs
			// were that message, for valid C#. The old method sits between two fields, documented,
			// with a comment at the end of its last line. After the edit the whole file is compared:
			// the first member in the old one's place under the new doc comment, the overload behind
			// it exactly as written, the end-of-line comment behind the last member (it was the end
			// of what was replaced), and both fields and their blank lines untouched.
			new("roslyn_replace_in_code: one member is replaced by several, written in order",
				async () => {
					
					const string replacement = "/// <summary>New.</summary>\n\tint Twice(int value)\n\t{\n\t\treturn Twice(value, 2);\n\t}\n\n\t/// <summary>The overload.</summary>\n\tint Twice(int value, int factor)\n\t{\n\t\treturn value * factor;\n\t}";
					const string expected    = "class Several\n{\n\tint kept = 1;\n\n\t/// <summary>New.</summary>\n\tint Twice(int value)\n\t{\n\t\treturn Twice(value, 2);\n\t}\n\n\t/// <summary>The overload.</summary>\n\tint Twice(int value, int factor)\n\t{\n\t\treturn value * factor;\n\t} // end of Twice\n\n\tint after = 2;\n}\n";
					
					var (pass, msg) = await ctx.RunTestAsync(
						"roslyn_replace_in_code",
						new { filePath = "several.cs", nodeKind = "MethodDeclaration", textPattern = "Twice", replacement, projectPath = fx.Csproj },
						data => data?["error"] is null && data?["applied"]?.GetValue<bool>() == true && data["_caution"] is null);
					
					if(!pass)
						return (false, msg);
					
					var content = File.ReadAllText(tempSeveralFile);
					
					return content == expected
						? (true, "PASS  (both members written, the rest of the file untouched)")
						: (false, $"FAIL  (file is: {content.Replace("\n", "\\n").Replace("\t", "\\t")})");
				}),
			
			// Every member of the list is checked as one member is: a syntax error in the second
			// must refuse the edit, and name what is wrong, not write the first and drop the rest.
			new("roslyn_replace_in_code: a list with a broken second member is refused",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = "several_bad.cs", nodeKind = "MethodDeclaration", textPattern = "Twice", replacement = "int Twice(int value) { return value; }\n\tint Broken( { }", dryRun = true, projectPath = fx.Csproj },
					data => data?["error"]?.GetValue<string>()?.Contains("syntax errors") == true)),
			
			// Still exactly one where a list has separators of its own: the commas between an enum's
			// members belong to the enum, and a replacement cannot bring them. And a text that closes
			// the type and opens another is no list of members of this type.
			new("roslyn_replace_in_code: an enum member is still replaced by exactly one",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = "several_enum.cs", nodeKind = "EnumMemberDeclaration", textPattern = "Low", replacement = "Low, Mid", dryRun = true, projectPath = fx.Csproj },
					data => data?["error"]?.GetValue<string>() is { Length: > 0 } && data?["details"]?.GetValue<string>()?.Contains("exactly one enum member") == true)),
			
			new("roslyn_replace_in_code: a replacement that closes the type early is refused",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = "ctor.cs", nodeKind = "ConstructorDeclaration", textPattern = "Widget", replacement = "Widget() { } } class Smuggled { void Extra() { }", dryRun = true, projectPath = fx.Csproj },
					data => data?["error"]?.GetValue<string>() is { Length: > 0 })),

			// ── Regression: line endings (#268) ──
			// The replacement node used to be spliced in with the replacement text's own newlines
			// (LF from MCP clients), leaving a CRLF file mixed. Both directions are checked: the
			// file's style must win over the replacement's, whichever way they disagree.
			new("roslyn_replace_in_code: LF replacement adopts CRLF file's line endings",
				async () => {
					
					var (pass, msg) = await ctx.RunTestAsync(
						"roslyn_replace_in_code",
						new { filePath = "crlf.cs", nodeKind = "MethodDeclaration", textPattern = "M", replacement = "void M()\n{\n\tvar x = 1;\n\tvar y = x;\n}", projectPath = fx.Csproj },
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
						new { filePath = "lf.cs", nodeKind = "MethodDeclaration", textPattern = "M", replacement = "void M()\r\n{\r\n\tvar x = 1;\r\n\tvar y = x;\r\n}", projectPath = fx.Csproj },
						data => data?["error"] is null && data?["applied"]?.GetValue<bool>() == true);
					
					if(!pass)
						return (false, msg);
					
					var content = File.ReadAllText(tempLfFile);
					
					return !content.Contains('\r') && content.Contains("var y = x;")
						? (true,  "PASS  (no carriage returns introduced)")
						: (false, $"FAIL  ({content.Count(c => c == '\r')} stray CR in LF file)");
				}),
			
			// Mixed files still need a deterministic dominant-style choice: one stray CRLF must not
			// flip a mostly-LF file to CRLF when the replacement is normalized.
			new("roslyn_replace_in_code: mostly-LF mixed file keeps LF as the dominant style",
				async () => {
					
					var (pass, msg) = await ctx.RunTestAsync(
						"roslyn_replace_in_code",
						new { filePath = "mostly_lf_mixed.cs", nodeKind = "MethodDeclaration", textPattern = "M", replacement = "void M()\r\n{\r\n\tvar x = 1;\r\n\tvar y = x;\r\n}", projectPath = fx.Csproj },
						data => data?["error"] is null && data?["applied"]?.GetValue<bool>() == true);
					
					if(!pass)
						return (false, msg);
					
					var content   = File.ReadAllText(tempMostlyLfMixedFile);
					var lfCount   = content.Count(c => c == '\n');
					var crlfCount = content.Split("\r\n").Length - 1;
					
					return !content.Contains("\r\n\tvar y = x;\r\n") && crlfCount < lfCount
						? (true,  $"PASS  ({crlfCount} CRLF vs {lfCount - crlfCount} bare LF)")
						: (false, $"FAIL  (dominant LF not preserved: {crlfCount} CRLF vs {lfCount - crlfCount} bare LF)");
				}),
			
			// The opt-out must be honored: with normalizeLineEndings=false the replacement's own
			// endings are written verbatim, mixed or not — that is the caller's explicit choice.
			new("roslyn_replace_in_code: normalizeLineEndings=false writes replacement verbatim",
				async () => {
					
					var (pass, msg) = await ctx.RunTestAsync(
						"roslyn_replace_in_code",
						new { filePath = "lf.cs", nodeKind = "MethodDeclaration", textPattern = "M", replacement = "void M()\r\n{\r\n}", normalizeLineEndings = false, projectPath = fx.Csproj },
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
					new { filePath = "insert.txt", text = "inserted line one\ninserted line two\ninserted line three", insertAfter = "line one", dryRun = true, projectPath = fx.Csproj },
					data => data?["applied"]?.GetValue<bool>() == false && data?["inserted_at"]?.GetValue<int>() == 2 && data?["line_count"]?.GetValue<int>() == 3 && data?["message"]?.GetValue<string>()?.Contains("Dry run:") == true && data?["backup_token"] is null && data?["inserted_line_ranges"]?.AsArray() is [var range] && range?["start"]?.GetValue<int>() == 2 && range?["end"]?.GetValue<int>() == 4)),
			
			new("roslyn_insert_lines: apply insertAfter",
				() => ctx.RunTestAsync(
					"roslyn_insert_lines",
					new { filePath = "insert.txt", text = "after one", insertAfter = "line one", projectPath = fx.Csproj },
					data => data?["applied"]?.GetValue<bool>() == true && data?["inserted_at"]?.GetValue<int>() == 2)),
			
			new("roslyn_insert_lines: apply insertBefore",
				() => ctx.RunTestAsync(
					"roslyn_insert_lines",
					new { filePath = "insert.txt", text = "before three", insertBefore = "line three", projectPath = fx.Csproj },
					data => data?["applied"]?.GetValue<bool>() == true && data?["inserted_at"]?.GetValue<int>() == 4)),
			
			new("roslyn_insert_lines: apply atLine",
				() => ctx.RunTestAsync(
					"roslyn_insert_lines",
					new { filePath = "insert.txt", text = "at line 1", atLine = 1, projectPath = fx.Csproj },
					data => data?["applied"]?.GetValue<bool>() == true && data?["inserted_at"]?.GetValue<int>() == 1)),
			
			new("roslyn_insert_lines: error when no location specified",
				() => ctx.RunTestAsync(
					"roslyn_insert_lines",
					new { filePath = "insert.txt", text = "oops", projectPath = fx.Csproj },
					data => data?["error"]?.GetValue<string>().Contains("exactly one") == true)),
			
			// ── Regression: guarded workspace resolution (transient mid-reload hardening) ──
			// Before the guard, editing tools called workspace.GetRootPath / GetSecurityBoundary /
			// GetSolution / ApplyChanges directly. When the workspace threw during resolution — e.g. a
			// project reloading after a prior edit — the exception propagated unhandled and the MCP
			// transport reported an opaque "An error occurred invoking '<tool>'": no JSON, no detail.
			// A projectPath that never exists deterministically forces a resolution failure, standing in for the
			// transient case that cannot be provoked on demand. The fix must now return a STRUCTURED
			// JSON error (parseable, carrying an "error" field) instead of an unhandled throw.
			// RunTestAsync fails on non-JSON content, so each of these would fail against the pre-fix
			// server — they are not tautological: they assert the exception was caught and shaped.
			
			new("roslyn_replace_in_code: structured error (not crash) on resolution failure",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_code",
					new { filePath = "code.cs", nodeKind = "IdentifierName", textPattern = "x", replacement = "y", projectPath = TestFixtures.MissingProjectPath },
					data => data?["error"]?.GetValue<string>() is { Length: > 0 })),
			
			new("roslyn_replace_in_file: structured error (not crash) on resolution failure",
				() => ctx.RunTestAsync(
					"roslyn_replace_in_file",
					new { filePath = "replace.cs", pattern = "x", replacement = "y", dryRun = true, projectPath = TestFixtures.MissingProjectPath },
					data => data?["error"]?.GetValue<string>() is { Length: > 0 })),
			
			new("roslyn_insert_lines: structured error (not crash) on resolution failure",
				() => ctx.RunTestAsync(
					"roslyn_insert_lines",
					new { filePath = "insert.txt", text = "x", atLine = 1, projectPath = TestFixtures.MissingProjectPath },
					data => data?["error"]?.GetValue<string>() is { Length: > 0 })),
			
			new("roslyn_write_file: structured error (not crash) on resolution failure",
				() => ctx.RunTestAsync(
					"roslyn_write_file",
					new { filePath = "write.cs", content = "x", createNew = true, dryRun = true, projectPath = TestFixtures.MissingProjectPath },
					data => data?["error"]?.GetValue<string>() is { Length: > 0 })),
			
			new("roslyn_local_history: structured error (not crash) on resolution failure",
				() => ctx.RunTestAsync(
					"roslyn_local_history",
					new { action = "list", filePath = "code.cs", projectPath = TestFixtures.MissingProjectPath },
					data => data?["error"]?.GetValue<string>() is { Length: > 0 })),
		};
		
		return new TestGroup($"File Editing Tools ({tests.Count} tests)", tests, Teardown: () =>
		{
			
			fx.Dispose();
			
			return Task.CompletedTask;
		});
	}
}
