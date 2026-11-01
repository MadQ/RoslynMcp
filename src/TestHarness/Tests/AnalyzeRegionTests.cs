using System.Text.Json.Nodes;

/// <summary>
///     Exercises <c>roslyn_analyze_region</c> against one fixture whose line numbers the tests name
///     directly. Each case pins a behavior of Roslyn's flow analyses that the tool has to shape or
///     work around, so the expected values come from what the compiler can prove about the fixture,
///     not from what the tool happened to return.
/// </summary>
static class AnalyzeRegionTests
{
	const string TopLevelSource = "var x = 1;\nvar y = x + 1;\nSystem.Console.WriteLine(y);\n";
	
	// The line numbers in the tests below index this array, so a line added here moves them.
	static readonly string[] Flow = [
		/*  1 */ "using System;",
		/*  2 */ "",
		/*  3 */ "class Flow",
		/*  4 */ "{",
		/*  5 */ "\tint field = 2;",
		/*  6 */ "\t",
		/*  7 */ "\tint Data(int p)",
		/*  8 */ "\t{",
		/*  9 */ "\t\tvar total = p + field;",
		/* 10 */ "\t\tFunc<int> f = () => total + 1;",
		/* 11 */ "\t\tvar later = total * 2;",
		/* 12 */ "\t\treturn later + f();",
		/* 13 */ "\t}",
		/* 14 */ "\t",
		/* 15 */ "\tint EveryPathLeaves(int p)",
		/* 16 */ "\t{",
		/* 17 */ "\t\tif(p > 0) {",
		/* 18 */ "\t\t\treturn 1;",
		/* 19 */ "\t\t}",
		/* 20 */ "\t\telse {",
		/* 21 */ "\t\t\tthrow new InvalidOperationException();",
		/* 22 */ "\t\t}",
		/* 23 */ "\t}",
		/* 24 */ "\t",
		/* 25 */ "\tvoid FallsThrough(int p)",
		/* 26 */ "\t{",
		/* 27 */ "\t\tif(p > 0) {",
		/* 28 */ "\t\t\tp++;",
		/* 29 */ "\t\t}",
		/* 30 */ "\t\tp--;",
		/* 31 */ "\t}",
		/* 32 */ "\t",
		/* 33 */ "\tint Loop(int p)",
		/* 34 */ "\t{",
		/* 35 */ "\t\tforeach(var i in new[] { 1, 2 }) {",
		/* 36 */ "\t\t\tif(i == p)",
		/* 37 */ "\t\t\t\tbreak;",
		/* 38 */ "\t\t}",
		/* 39 */ "\t\treturn p;",
		/* 40 */ "\t}",
		/* 41 */ "\t",
		/* 42 */ "\tint Arrow(int p) => p + 1;",
		/* 43 */ "\t",
		/* 44 */ "\tvoid LambdaThrow()",
		/* 45 */ "\t{",
		/* 46 */ "\t\tAction a = () => throw new InvalidOperationException();",
		/* 47 */ "\t\ta();",
		/* 48 */ "\t}",
		/* 49 */ "\t",
		/* 50 */ "\t// A comment with no code.",
		/* 51 */ "#if NEVER",
		/* 52 */ "\tvoid Disabled() { }",
		/* 53 */ "#endif",
		/* 54 */ "\t",
		/* 55 */ "\tint Unreachable()",
		/* 56 */ "\t{",
		/* 57 */ "\t\treturn 1;",
		/* 58 */ "\t\tConsole.WriteLine();",
		/* 59 */ "\t}",
		/* 60 */ "\t",
		/* 61 */ "\tint Empty()",
		/* 62 */ "\t{",
		/* 63 */ "\t}",
		/* 64 */ "\t",
		/* 65 */ "\tint Outer()",
		/* 66 */ "\t{",
		/* 67 */ "\t\tint Local(int x)",
		/* 68 */ "\t\t{",
		/* 69 */ "\t\t\tif(x > 0)",
		/* 70 */ "\t\t\t\treturn 1;",
		/* 71 */ "\t\t\tthrow new InvalidOperationException();",
		/* 72 */ "\t\t}",
		/* 73 */ "\t\treturn Local(1);",
		/* 74 */ "\t}",
		/* 75 */ "\t",
		/* 76 */ "\t[Obsolete]",
		/* 77 */ "\tvoid Marked()",
		/* 78 */ "\t{",
		/* 79 */ "\t\tConsole.WriteLine();",
		/* 80 */ "\t}",
		/* 81 */ "\t",
		/* 82 */ "\tint Prop",
		/* 83 */ "\t{",
		/* 84 */ "\t\tget { return 1; }",
		/* 85 */ "\t}",
		/* 86 */ "}",
	];
	
	internal static TestGroup Build(TestContext ctx)
	{
		
		var fx = TestFixtures.NewMsBuildProject("AnalyzeRegion", targetFrameworks: "net10.0");
		
		fx.Write("Flow.cs", string.Join("\n", Flow) + "\n");
		fx.Write("Events.cs", "using System;\n\nclass Events\n{\n\tevent Action Changed = () => { };\n\tstatic Func<int, int> Pick(Func<int, int> f) => f;\n\tFunc<int, int> picked = Pick((int x =\n\t\t1\n\t) => x);\n}\n");
		fx.Write("Brace.cs", "class Brace\n{\n\tvoid Run() {\n\t\tSystem.Console.WriteLine();\n\t\tSystem.Console.WriteLine();\n\t}\n}\n");
		fx.Write("Header.cs", "using System;\n\nclass Header\n{\n\t[Obsolete]\n\tint marked = 1;\n\t\n\t[Obsolete]\n\tint Doubled => 2;\n}\n");
		fx.Write("Shape.cs", "abstract class Shape\n{\n\tpublic abstract int Area();\n}\n");
		fx.Write("Broken.cs", "class Broken\n{\n\tvoid Run()\n\t{\n\t\tvar broken = Missing();\n\t\tSystem.Console.WriteLine(broken);\n\t}\n}\n");
		
		var exe   = TestFixtures.NewMsBuildProject("AnalyzeRegionExe", targetFrameworks: "net10.0", extraProjectXml: "<PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>");
		var adhoc = TestFixtures.NewAdhocDir("AnalyzeRegionAdhoc");
		
		exe.Write("Top.cs", TopLevelSource);
		adhoc.Write("Plain.cs", "class Plain\n{\n\tint Twice(int p)\n\t{\n\t\treturn p * 2;\n\t}\n}\n");
		
		Task<(bool pass, string message)> Region(int startLine, int endLine, Func<JsonNode?, bool> validate)
			=> ctx.RunTestAsync("roslyn_analyze_region", new { filePath = "Flow.cs", startLine, endLine, projectPath = fx.Csproj }, validate)
		;
		
		var tests = new List<TestCase> {
			
			// The flagship case. 'p' and 'this' flow in, 'total' and 'f' are read after the region so
			// they flow out, and the lambda captures 'total'.
			new("roslyn_analyze_region: data flow names parameters, outputs and captures",
				() => Region(9, 10, data => {
					
					var flow = data?["data_flow"];
					
					return Lines(data, 9, 10)
						&& Has(flow?["variables_declared"], "int total")
						&& Has(flow?["variables_declared"], "Func<int> f")
						&& Has(flow?["data_flows_in"], "int p")
						&& Has(flow?["data_flows_out"], "int total")
						&& Has(flow?["data_flows_out"], "Func<int> f")
						&& Has(flow?["captured"], "int total");
				})),
			
			// Roslyn's Captured covers the whole method, so line 9 would list 'total' although the
			// lambda capturing it is on line 10. A region answers for what it contains.
			new("roslyn_analyze_region: a capture outside the region is not reported",
				() => Region(9, 9, data => Has(data?["data_flow"]?["variables_declared"], "int total")
					&& data?["data_flow"]?["captured"] is null)),
			
			new("roslyn_analyze_region: a capture inside the region is reported",
				() => Region(10, 10, data => Has(data?["data_flow"]?["captured"], "int total"))),
			

			// Roslyn tracks 'this', not the field read through it. The tool description promises
			// this, and an agent relies on it: the field must not appear, and 'this' must.
			new("roslyn_analyze_region: a field read reports 'this' and not the field",
				() => Region(9, 9, data => Has(data?["data_flow"]?["data_flows_in"], "Flow this")
					&& data?["data_flow"]?.ToJsonString().Contains("field") == false)),
			
			// Roslyn's ExitPoints omits throw, so the reported throw comes from the tool's own walk.
			// Line numbers prove each site is the right one.
			new("roslyn_analyze_region: every path leaves, with the return and the throw reported apart",
				() => Region(17, 22, data => {
					
					var flow  = data?["control_flow"];
					var exits = flow?["exit_points"]?.AsArray();
					
					return flow?["end_reachable"]?.GetValue<bool>() == false
						&& exits is [{ } exit]
						&& exit["kind"]?.GetValue<string>() == "return"
						&& exit["line"]?.GetValue<int>() == 18
						&& flow?["throws"]?.AsArray() is [{ } thrown]
						&& thrown.GetValue<int>() == 21;
				})),
			
			// The counterpart: no jump anywhere, so the end is reachable and the lists are omitted.
			new("roslyn_analyze_region: a region that falls through has a reachable end and no exits",
				() => Region(27, 30, data => data?["control_flow"]?["end_reachable"]?.GetValue<bool>() == true
					&& data?["control_flow"]?["exit_points"] is null
					&& data?["control_flow"]?["throws"] is null)),
			
			// Line 28 is in the nested block and line 30 is not. Handing those two statements to
			// Roslyn throws, so the range must widen to the whole 'if' and say so.
			new("roslyn_analyze_region: a range across statement lists widens and reports the widened lines",
				() => Region(28, 30, data => data?["error"] is null && Lines(data, 27, 30))),
			
			// Such a range lands on the declaration, not on a statement, and an agent copying a
			// method's line range from an outline will send exactly this.
			new("roslyn_analyze_region: a whole method analyzes its body",
				() => Region(7, 13, data => Lines(data, 9, 12)
					&& data?["control_flow"]?["end_reachable"]?.GetValue<bool>() == false)),
			
			// The one way to ask whether an empty non-void method is missing a return, as it is while
			// being written.
			new("roslyn_analyze_region: an empty body is analyzed, and its end is reachable",
				() => Region(61, 63, data => Lines(data, 62, 63)
					&& data?["control_flow"]?["end_reachable"]?.GetValue<bool>() == true)),
			
			new("roslyn_analyze_region: an abstract method has no body to analyze",
				() => ctx.RunTestAsync(
					"roslyn_analyze_region",
					new { filePath = "Shape.cs", startLine = 3, endLine = 3, projectPath = fx.Csproj },
					data => data?["error"]?.GetValue<string>().Contains("No analyzable code") == true)),
			

			// A break leaves the loop body, but not the loop.
			new("roslyn_analyze_region: a break is an exit of the loop body",
				() => Region(36, 37, data => data?["control_flow"]?["exit_points"]?.AsArray() is [{ } exit]
					&& exit["kind"]?.GetValue<string>() == "break"
					&& exit["line"]?.GetValue<int>() == 37)),
			
			new("roslyn_analyze_region: a break inside the region's own loop is not an exit",
				() => Region(35, 38, data => Lines(data, 35, 38) && data?["control_flow"]?["exit_points"] is null)),
			
			// The lambda throws when called, which is not on any path through this region.
			new("roslyn_analyze_region: a throw inside a lambda is not reported",
				() => Region(46, 47, data => data?["control_flow"] is not null
					&& data?["control_flow"]?["throws"] is null)),
			
			// Roslyn has no control-flow overload for an expression, so the section must be absent
			// rather than empty, which would read as 'no exits'.
			new("roslyn_analyze_region: an expression body has data flow and no control flow",
				() => Region(42, 42, data => Lines(data, 42, 42)
					&& Has(data?["data_flow"]?["data_flows_in"], "int p")
					&& data?["control_flow"] is null)),
			
			new("roslyn_analyze_region: a field initializer is analyzed as an expression",
				() => Region(5, 5, data => data?["error"] is null && data?["control_flow"] is null)),
			
			// An event field is a different syntax node from an ordinary field.
			new("roslyn_analyze_region: an event initializer is analyzed as an expression",
				() => ctx.RunTestAsync(
					"roslyn_analyze_region",
					new { filePath = "Events.cs", startLine = 5, endLine = 5, projectPath = fx.Csproj },
					data => Lines(data, 5, 5) && data?["control_flow"] is null)),
			

			// Line 8 sits in the lambda's default value, itself inside the field's initializer. The
			// field's initializer is the region, not the default value on the way to it.
			new("roslyn_analyze_region: the outermost initializer wins over a nested default value",
				() => ctx.RunTestAsync(
					"roslyn_analyze_region",
					new { filePath = "Events.cs", startLine = 8, endLine = 8, projectPath = fx.Csproj },
					data => Lines(data, 7, 9))),
			
			// The declaration of a local function has no flow of its own. Analyzing it reported a
			// reachable end and no exits for a body where every path leaves.
			new("roslyn_analyze_region: a whole local function analyzes its body",
				() => Region(67, 72, data => {
					
					var flow = data?["control_flow"];
					
					return Lines(data, 69, 71)
						&& flow?["end_reachable"]?.GetValue<bool>() == false
						&& flow?["exit_points"]?.AsArray() is [{ } exit]
						&& exit["line"]?.GetValue<int>() == 70
						&& flow?["throws"]?.AsArray() is [{ } thrown]
						&& thrown.GetValue<int>() == 71;
				})),
			
			// 'total' is read in line 11 and written nowhere in it, so it does not flow out. Only
			// read_outside can say that it is used elsewhere, which is the question agents ask.
			new("roslyn_analyze_region: a local read but not written in the region is reported as used outside",
				() => Region(11, 11, data => {
					
					var flow = data?["data_flow"];
					
					return Has(flow?["read_outside"], "int total")
						&& Has(flow?["written_outside"], "int total")
						&& !Has(flow?["data_flows_out"], "int total")
						&& data?["file"]?.GetValue<string>() == "Flow.cs";
				})),
			
			// Both lines lie outside the body, and an answer about the body would be about other code.
			new("roslyn_analyze_region: an attribute line is not the method's body",
				() => Region(76, 76, data => data?["error"]?.GetValue<string>().Contains("No analyzable code") == true)),
			
			new("roslyn_analyze_region: a signature line is not the method's body",
				() => Region(77, 77, data => data?["error"]?.GetValue<string>().Contains("No analyzable code") == true)),
			
			// The opening brace is on the signature line here, so it touches the block without touching
			// a single statement.
			new("roslyn_analyze_region: a signature line ending in the opening brace is not the body",
				() => ctx.RunTestAsync(
					"roslyn_analyze_region",
					new { filePath = "Brace.cs", startLine = 3, endLine = 3, projectPath = fx.Csproj },
					data => data?["error"]?.GetValue<string>().Contains("No analyzable code") == true)),
			
			// A range holding the signature and one statement answers for that statement alone.
			new("roslyn_analyze_region: a range over a signature and one statement stops at that statement",
				() => ctx.RunTestAsync(
					"roslyn_analyze_region",
					new { filePath = "Brace.cs", startLine = 3, endLine = 4, projectPath = fx.Csproj },
					data => Lines(data, 4, 4))),
			

			// An attribute line walks up to the member, whose initializer or expression body lies on
			// the next line. The same member's own line must keep working.
			new("roslyn_analyze_region: an attribute line is not a field's initializer",
				() => ctx.RunTestAsync(
					"roslyn_analyze_region",
					new { filePath = "Header.cs", startLine = 5, endLine = 5, projectPath = fx.Csproj },
					data => data?["error"]?.GetValue<string>().Contains("No analyzable code") == true)),
			
			new("roslyn_analyze_region: a field's own line is its initializer",
				() => ctx.RunTestAsync(
					"roslyn_analyze_region",
					new { filePath = "Header.cs", startLine = 6, endLine = 6, projectPath = fx.Csproj },
					data => Lines(data, 6, 6))),
			
			new("roslyn_analyze_region: an attribute line is not a property's expression body",
				() => ctx.RunTestAsync(
					"roslyn_analyze_region",
					new { filePath = "Header.cs", startLine = 8, endLine = 8, projectPath = fx.Csproj },
					data => data?["error"]?.GetValue<string>().Contains("No analyzable code") == true)),
			
			new("roslyn_analyze_region: a property's own line is its expression body",
				() => ctx.RunTestAsync(
					"roslyn_analyze_region",
					new { filePath = "Header.cs", startLine = 9, endLine = 9, projectPath = fx.Csproj },
					data => Lines(data, 9, 9))),
			

			// Its accessors are separate bodies, so the hint has to say to narrow the range rather
			// than claim there is nothing there.
			new("roslyn_analyze_region: a whole property asks for a narrower range",
				() => Region(82, 85, data => data?["hint"]?.GetValue<string>().Contains("narrowed to one body") == true)),
			
			new("roslyn_analyze_region: one accessor of a property is analyzed",
				() => Region(84, 84, data => data?["error"] is null
					&& data?["control_flow"]?["end_reachable"]?.GetValue<bool>() == false)),
			

			new("roslyn_analyze_region: code after a return has an unreachable start",
				() => Region(58, 58, data => data?["control_flow"]?["start_reachable"]?.GetValue<bool>() == false)),
			
			// Without a trivia check each of these would widen to the enclosing declaration or
			// statement and answer a question nobody asked.
			new("roslyn_analyze_region: a comment is not code",
				() => Region(50, 50, data => data?["error"]?.GetValue<string>().Contains("No analyzable code") == true)),
			
			new("roslyn_analyze_region: disabled #if text is not code",
				() => Region(52, 52, data => data?["error"]?.GetValue<string>().Contains("No analyzable code") == true)),
			
			new("roslyn_analyze_region: blank lines are not code",
				() => Region(6, 6, data => data?["error"]?.GetValue<string>().Contains("No analyzable code") == true)),
			
			new("roslyn_analyze_region: a declaration with no body is not code",
				() => Region(3, 3, data => data?["error"]?.GetValue<string>().Contains("No analyzable code") == true)),
			
			new("roslyn_analyze_region: a range past the end of the file is refused",
				() => Region(1, 1000, data => data?["error"]?.GetValue<string>().Contains("line(s)") == true)),
			
			new("roslyn_analyze_region: a reversed range is refused",
				() => Region(10, 9, data => data?["error"]?.GetValue<string>().Contains("not a valid range") == true)),
			
			// Top-level statements sit in no block, so the resolver has to collect them itself.
			new("roslyn_analyze_region: top-level statements",
				() => ctx.RunTestAsync(
					"roslyn_analyze_region",
					new { filePath = "Top.cs", startLine = 1, endLine = 2, projectPath = exe.Csproj },
					data => Lines(data, 1, 2) && Has(data?["data_flow"]?["data_flows_out"], "int y"))),
			
			new("roslyn_analyze_region: an AdhocWorkspace result carries its caution",
				() => ctx.RunTestAsync(
					"roslyn_analyze_region",
					new { filePath = "Plain.cs", startLine = 5, endLine = 5, projectPath = adhoc.ProjectPath },
					data => Has(data?["data_flow"]?["data_flows_in"], "int p") && data?["_caution"] is not null)),
			
			// Code under edit often fails to compile, and the region must still read cleanly: a local
			// of an unresolved type once came back as ' broken', with a blank where the type goes.
			new("roslyn_analyze_region: a local of an unresolved type is reported by name alone",
				() => ctx.RunTestAsync(
					"roslyn_analyze_region",
					new { filePath = "Broken.cs", startLine = 5, endLine = 5, projectPath = fx.Csproj },
					data => Has(data?["data_flow"]?["variables_declared"], "broken"))),
		};
		
		return new TestGroup($"Analyze Region ({tests.Count} tests)", tests, Teardown: () => {
			
			fx.Dispose();
			exe.Dispose();
			adhoc.Dispose();
			
			return Task.CompletedTask;
		});
	}
	
	static bool Lines(JsonNode? data, int start, int end)
		=> data?["region"]?["start"]?.GetValue<int>() == start
		&& data?["region"]?["end"]?.GetValue<int>() == end
	;
	
	static bool Has(JsonNode? list, string value)
		=> list?.AsArray().Any(item => item?.GetValue<string>() == value) == true
	;
}
