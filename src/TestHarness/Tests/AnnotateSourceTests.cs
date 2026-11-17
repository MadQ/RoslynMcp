using System.Text.Json.Nodes;

/// <summary>
///     Exercises <c>roslyn_annotate_source</c> (#355) against one fixture whose line numbers the
///     tests name directly. The expected annotations are what the compiler knows about the fixture
///     and the text does not say; the lines expected to have none are the ones where the text says
///     it all. An answer is three blocks after its header: the source, a one-line JSON count, and
///     the annotations as <c>line: fact</c>.
/// </summary>
static class AnnotateSourceTests
{
	// The line numbers in the tests below index this array, so a line added here moves them.
	static readonly string[] Shop = [
		/*  1 */ "#nullable enable",
		/*  2 */ "using System;",
		/*  3 */ "using System.Collections.Generic;",
		/*  4 */ "using System.Linq;",
		/*  5 */ "",
		/*  6 */ "class Order { public int Id; }",
		/*  7 */ "",
		/*  8 */ "class Shop",
		/*  9 */ "{",
		/* 10 */ "\tList<Order?> GetItems() => new();",
		/* 11 */ "\tvoid Process(Order order) { }",
		/* 12 */ "\tvoid Process(string text) { }",
		/* 13 */ "\tstatic T First<T>(IEnumerable<T> items) => items.First();",
		/* 14 */ "\t",
		/* 15 */ "\tint Run(string? name)",
		/* 16 */ "\t{",
		/* 17 */ "\t\tvar items = GetItems();",
		/* 18 */ "\t\tvar count = 5;",
		/* 19 */ "\t\tint total = 0;",
		/* 20 */ "\t\tforeach(var o in items) {",
		/* 21 */ "\t\t\tProcess(o!);",
		/* 22 */ "\t\t}",
		/* 23 */ "\t\t",
		/* 24 */ "\t\tvar first = First(items);",
		/* 25 */ "\t\tvar ids = items.Select(i => i == null ? 0 : i.Id);",
		/* 26 */ "\t\tif(int.TryParse(name, out var parsed))",
		/* 27 */ "\t\t\ttotal += parsed;",
		/* 28 */ "\t\treturn total + count + name.Length + (first == null ? 0 : 1) + ids.Sum();",
		/* 29 */ "\t}",
		/* 30 */ "\t",
		/* 31 */ "\tvoid Broken()",
		/* 32 */ "\t{",
		/* 33 */ "\t\tvar missing = Nowhere();",
		/* 34 */ "\t}",
		/* 35 */ "\t",
		/* 36 */ "\tstatic void Par(params int[] values) { }",
		/* 37 */ "\tstatic void Par(string text, params object[] values) { }",
		/* 38 */ "\t",
		/* 39 */ "\tint Edge(Dictionary<string, List<int>> map, List<string>? list, int? number)",
		/* 40 */ "\t{",
		/* 41 */ "\t\tmap.TryGetValue(\"k\", out var found);",
		/* 42 */ "\t\tPar(1, 2, 3, 4);",
		/* 43 */ "\t\tif(number.HasValue)",
		/* 44 */ "\t\t\treturn number.Value;",
		/* 45 */ "\t\treturn list.Count();",
		/* 46 */ "\t}",
		/* 47 */ "}",
	];
	
	internal static TestGroup Build(TestContext ctx)
	{
		var fx = TestFixtures.NewMsBuildProject("AnnotateSource", targetFrameworks: "net10.0");
		
		fx.Write("Shop.cs", string.Join("\n", Shop) + "\n");
		
		// A second project that calls into the first: a method of the solution that is not of
		// the project asked about (#502).
		var lib = TestFixtures.NewMsBuildProject("AnnotateSourceLib", targetFrameworks: "net10.0");
		var app = TestFixtures.NewMsBuildProject("AnnotateSourceApp", "net10.0", $"<ItemGroup><ProjectReference Include=\"{lib.Csproj}\" /></ItemGroup>");
		
		lib.Write("Stock.cs", "public static class Stock\n{\n\tpublic static void Put(int count) { }\n\tpublic static void Put(string name) { }\n}\n");
		app.Write("Clerk.cs", string.Join("\n", [
			/* 1 */ "using System.Linq;",
			/* 2 */ "",
			/* 3 */ "class Clerk",
			/* 4 */ "{",
			/* 5 */ "\tint Work(int[] counts)",
			/* 6 */ "\t{",
			/* 7 */ "\t\tStock.Put(counts.Length);",
			/* 8 */ "\t\treturn counts.Select(c => c + 1).First();",
			/* 9 */ "\t}",
			/* 10 */ "}",
		]) + "\n");
		
		static string SourceOf(int startLine, int endLine) => string.Join("\n", Shop[(startLine - 1)..endLine]) + "\n";
		
		// The annotations of an answer, one per line; empty when the answer has none.
		static string[] Annotations(string[] later) => later is [_, _, var annotations] ? annotations.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
		
		var tests = new List<TestCase> {
			
			// The flagship case: one member, all three kinds. Each expected line is a fact the text
			// of Run does not hold: what `var` stands for, which Process was meant, what First's T
			// became, and that `name` was never checked before line 28. `items` is not nullable
			// although the declared type of a `var` always is: GetItems() cannot return null. Lines
			// 18 and 19 say their type themselves (a literal, a written type) and must have nothing.
			// The source block is the file's text, so it can be quoted in an edit.
			new("roslyn_annotate_source: a member's implicit types, null states and resolved calls",
				() => ctx.RunTestAsync(
					"roslyn_annotate_source",
					new { symbolName = "Run", containingType = "Shop", projectPath = fx.Csproj },
					(data, later) => {
						
						var lines = Annotations(later);
						
						return data?["symbol_name"]?.GetValue<string>().Contains("Run") == true
							&& data["start_line"]?.GetValue<int>() == 15
							&& data["end_line"]?.GetValue<int>() == 29
							&& later is [var source, var count, _]
							&& source == SourceOf(15, 29)
							&& JsonNode.Parse(count)?["annotations"]?.GetValue<int>() == lines.Length
							&& lines.Contains("17: items: List<Order?>")
							&& lines.Contains("20: o: Order?")
							&& lines.Any(line => line.StartsWith("21: Process(Order)", StringComparison.Ordinal) && line.EndsWith("1 of 2 overloads", StringComparison.Ordinal))
							&& lines.Contains("24: first: Order?")
							&& lines.Any(line => line.StartsWith("24: First<Order?>(", StringComparison.Ordinal) && !line.Contains("overloads", StringComparison.Ordinal))
							&& lines.Contains("25: i: Order?")
							&& lines.Contains("26: parsed: int")
							&& lines.Contains("28: name may be null here")
							&& !lines.Any(line => line.StartsWith("18:", StringComparison.Ordinal) || line.StartsWith("19:", StringComparison.Ordinal))
							// The overloads of Sum() differ only in the receiver's type, which the
							// source shows: no annotation.
							&& !lines.Any(line => line.Contains("Sum(", StringComparison.Ordinal))
							// Select on line 25 is overloaded and infers its type arguments, and is the
							// framework's: no annotation (#502). What it inferred is said by `i`.
							&& !lines.Any(line => line.Contains("Select<", StringComparison.Ordinal));
					},
					expectJson: true)),
			
			// Four cases where the plain rule says the wrong thing. `found` may be null although
			// the parameter's type is not nullable: TryGetValue says so with [MaybeNullWhen(false)].
			// Four arguments fit both Par overloads through their params, so the call is overloaded.
			// `number.HasValue` asks about the null and is no dereference, and `number.Value` behind
			// it cannot be null. `list.Count()` is an extension call, but Count's receiver parameter
			// is not nullable, so a null list fails there.
			new("roslyn_annotate_source: an out var that may be null, params overloads, HasValue, and a null receiver of an extension call",
				() => ctx.RunTestAsync(
					"roslyn_annotate_source",
					new { symbolName = "Edge", containingType = "Shop", projectPath = fx.Csproj },
					(data, later) => {
						
						var lines = Annotations(later);
						
						return lines.Contains("41: found: List<int>?")
							&& lines.Any(line => line.StartsWith("42: Par(params int[])", StringComparison.Ordinal) && line.EndsWith("1 of 2 overloads", StringComparison.Ordinal))
							&& !lines.Any(line => line.Contains("number may be null", StringComparison.Ordinal))
							&& lines.Contains("45: list may be null here")
							// Count<string> on that line is the framework's call: said by nothing (#502).
							&& !lines.Any(line => line.Contains("Count<", StringComparison.Ordinal));
					},
					expectJson: true)),
			
			// Whose method a call is decides whether it is annotated (#502), and "the solution's own"
			// has to include a project the asked-about one references: Put is declared in the Lib
			// fixture and overloaded, so the call on line 7 names the overload. Select and First on
			// line 8 are overloaded too and infer their type arguments, and are the framework's: in
			// the tool's first real use, calls like them were twelve of fourteen annotations. Only
			// calls are narrowed: the lambda parameter on that line still gets its type.
			new("roslyn_annotate_source: a call is annotated when its method is the solution's own, also from another project",
				() => ctx.RunTestAsync(
					"roslyn_annotate_source",
					new { symbolName = "Work", containingType = "Clerk", projectPath = app.Csproj },
					(data, later) => {
						
						var lines = Annotations(later);
						
						return lines.Any(line => line.StartsWith("7: Put(int)", StringComparison.Ordinal) && line.EndsWith("1 of 2 overloads", StringComparison.Ordinal))
							&& lines.Contains("8: c: int")
							&& !lines.Any(line => line.Contains("Select<", StringComparison.Ordinal) || line.Contains("First<", StringComparison.Ordinal));
					},
					expectJson: true)),
			
			// A line range and one kind: line 17 has an implicit type, and the call on it has one
			// overload and no type arguments, so `types` alone yields exactly one annotation.
			new("roslyn_annotate_source: a line range, with only the kind asked for",
				() => ctx.RunTestAsync(
					"roslyn_annotate_source",
					new { filePath = "Shop.cs", startLine = 17, endLine = 18, annotate = "types", projectPath = fx.Csproj },
					(data, later) => data?["symbol_name"] is null
						&& later is [var source, _, _]
						&& source == SourceOf(17, 18)
						&& Annotations(later) is ["17: items: List<Order?>"],
					expectJson: true)),
			
			// Lines 18 and 19 hold nothing the text does not say: the answer is the header with a
			// note and the source, and no annotation block at all.
			new("roslyn_annotate_source: lines whose text says it all get a note and no annotations",
				() => ctx.RunTestAsync(
					"roslyn_annotate_source",
					new { filePath = "Shop.cs", startLine = 18, endLine = 19, projectPath = fx.Csproj },
					(data, later) => data?["note"]?.GetValue<string>().StartsWith("No annotations", StringComparison.Ordinal) == true
						&& later is [var source] && source == SourceOf(18, 19),
					expectJson: true)),
			
			// `Nowhere()` does not bind. An error type is not a fact, so `missing` gets no type.
			new("roslyn_annotate_source: code that does not bind is not annotated",
				() => ctx.RunTestAsync(
					"roslyn_annotate_source",
					new { symbolName = "Broken", containingType = "Shop", projectPath = fx.Csproj },
					(data, later) => data?["error"] is null && Annotations(later).Length == 0,
					expectJson: true)),
			
			// `Process` has two overloads, on lines 11 and 12. Answering for the first alone would
			// hide the second, so the answer is both with their lines; and a member named beside a
			// range is refused, because one of the two would be dropped without a word.
			new("roslyn_annotate_source: an overloaded name lists its overloads, and a member beside a range is refused",
				async () => {
					
					var overloaded = await ctx.CallToolJsonAsync("roslyn_annotate_source", new { symbolName = "Process", containingType = "Shop", projectPath = fx.Csproj });
					var both       = await ctx.CallToolJsonAsync("roslyn_annotate_source", new { symbolName = "Run", containingType = "Shop", filePath = "Shop.cs", startLine = 17, endLine = 18, projectPath = fx.Csproj });
					var error      = overloaded?["error"]?.GetValue<string>() ?? "";
					var pass       = error.Contains("Process(Order) (Shop.cs, lines 11-11)", StringComparison.Ordinal)
						&& error.Contains("Process(string) (Shop.cs, lines 12-12)", StringComparison.Ordinal)
						&& both?["error"]?.GetValue<string>().Contains("both", StringComparison.Ordinal) == true;
					
					return (pass, pass ? "PASS" : $"FAIL  (overloaded: {overloaded} | both: {both})");
				}),
			
			new("roslyn_annotate_source: an unknown kind and a call that names nothing are structured errors",
				async () => {
					
					var unknown = await ctx.CallToolJsonAsync("roslyn_annotate_source", new { symbolName = "Run", containingType = "Shop", annotate = "types,everything", projectPath = fx.Csproj });
					var nothing = await ctx.CallToolJsonAsync("roslyn_annotate_source", new { filePath = "Shop.cs", projectPath = fx.Csproj });
					var pass    = unknown?["error"]?.GetValue<string>().Contains("annotate") == true
						&& nothing?["error"]?.GetValue<string>() is { Length: > 0 }
						&& nothing["hint"]?.GetValue<string>().Contains("startLine") == true;
					
					return (pass, pass ? "PASS" : $"FAIL  (unknown kind: {unknown} | nothing named: {nothing})");
				}),
		};
		
		return new TestGroup($"Annotate Source ({tests.Count} tests)", tests, Teardown: () => {
			
			fx.Dispose();
			app.Dispose();
			lib.Dispose();
			
			return Task.CompletedTask;
		});
	}
}
