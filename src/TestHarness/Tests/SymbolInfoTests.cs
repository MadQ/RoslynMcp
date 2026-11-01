using System.Text.Json.Nodes;

/// <summary>
///     Exercises what <c>roslyn_get_symbol_info</c> reports beyond the symbol itself: the facts about
///     an expression that an agent cannot read off the text. The fixture project leaves nullable
///     off, so the first section's <c>#nullable enable</c> also proves that nullability is decided
///     per position, not by the project setting. Columns count a tab as one character.
/// </summary>
static class SymbolInfoTests
{
	// The positions in the tests below index this array, so a line added here moves them.
	static readonly string[] Facts = [
		/*  1 */ "#nullable enable",
		/*  2 */ "using System;",
		/*  3 */ "",
		/*  4 */ "class Facts",
		/*  5 */ "{",
		/*  6 */ "\tconst int K = 2 + 3;",
		/*  7 */ "\t",
		/*  8 */ "\tvoid Over(int a) { }",
		/*  9 */ "\tvoid Over(long a) { }",
		/* 10 */ "\tvoid Amb(int a, long b) { }",
		/* 11 */ "\tvoid Amb(long a, int b) { }",
		/* 12 */ "\tstring? Maybe() => null;",
		/* 13 */ "\t",
		/* 14 */ "\tvoid Run(string? s, bool b)",
		/* 15 */ "\t{",
		/* 16 */ "\t\tvar before = s;",
		/* 17 */ "\t\tConsole.WriteLine(before);",
		/* 18 */ "\t\tif(s is null)",
		/* 19 */ "\t\t\treturn;",
		/* 20 */ "\t\tConsole.WriteLine(s);",
		/* 21 */ "\t\tlong widened = 1;",
		/* 22 */ "\t\tobject boxed = 2;",
		/* 23 */ "\t\tOver(1);",
		/* 24 */ "\t\tAmb(1, 1);",
		/* 25 */ "\t\tConsole.WriteLine(K);",
		/* 26 */ "\t\tforeach(var item in new[] { 1 })",
		/* 27 */ "\t\t\tConsole.WriteLine(item);",
		/* 28 */ "\t}",
		/* 29 */ "}",
		/* 30 */ "",
		/* 31 */ "#nullable disable",
		/* 32 */ "class Off",
		/* 33 */ "{",
		/* 34 */ "\tvoid Run(string s) { Console.WriteLine(s); }",
		/* 35 */ "}",
	];
	
	internal static TestGroup Build(TestContext ctx)
	{
		
		var fx = TestFixtures.NewMsBuildProject("SymbolInfoFacts", targetFrameworks: "net10.0");
		
		fx.Write("Facts.cs", string.Join("\n", Facts) + "\n");
		
		Task<(bool pass, string message)> At(int line, int column, Func<JsonNode?, bool> validate)
			=> ctx.RunTestAsync("roslyn_get_symbol_info", new { filePath = "Facts.cs", line, column, projectPath = fx.Csproj }, validate)
		;
		
		Task<(bool pass, string message)> WithOverloads(int line, int column, Func<JsonNode?, bool> validate)
			=> ctx.RunTestAsync("roslyn_get_symbol_info", new { filePath = "Facts.cs", line, column, includeOverloads = true, projectPath = fx.Csproj }, validate)
		;
		
		var tests = new List<TestCase> {
			
			// The same local on both sides of a guard: only flow analysis tells them apart.
			new("roslyn_get_symbol_info: a nullable local is MaybeNull before a guard",
				() => At(17, 21, data => Text(data, "nullable_flow") == "MaybeNull" && Text(data, "type") == "string?")),
			
			new("roslyn_get_symbol_info: a guarded parameter is NotNull, and its type is flow-adjusted",
				() => At(20, 21, data => Text(data, "nullable_flow") == "NotNull"
					&& Text(data, "type") == "string"
					&& Text(data, "nullable_context") is null)),
			
			// Nullability follows the position: the project has it off and the first section turns
			// it on, so these two 'string' parameters differ only in which section they sit in.
			new("roslyn_get_symbol_info: nullable_context reports a disabled section, and no flow is claimed",
				() => At(34, 41, data => Text(data, "nullable_context") == "disabled"
					&& Text(data, "nullable_flow") is null
					&& Text(data, "type") == "string")),
			
			new("roslyn_get_symbol_info: a numeric widening reports the conversion and the converted type",
				() => At(21, 18, data => Text(data, "type") == "int"
					&& Text(data, "converted_type") == "long"
					&& Text(data, "conversion") == "ImplicitNumeric"
					&& Text(data, "constant_value") == "1")),
			
			new("roslyn_get_symbol_info: a boxing conversion is reported",
				() => At(22, 18, data => Text(data, "converted_type") == "object" && Text(data, "conversion") == "Boxing")),
			
			// An int has no null state to report, and saying NotNull would only cost tokens.
			new("roslyn_get_symbol_info: a value type has no flow state",
				() => At(21, 18, data => Text(data, "nullable_flow") is null)),
			
			new("roslyn_get_symbol_info: a constant is folded",
				() => At(25, 21, data => Text(data, "constant_value") == "5" && Text(data, "name")?.Contains('K') == true)),
			
			// Without identical fields omitted, every plain expression would pay for two more.
			new("roslyn_get_symbol_info: no conversion means no conversion fields",
				() => At(20, 21, data => Text(data, "converted_type") is null && Text(data, "conversion") is null)),
			
			new("roslyn_get_symbol_info: a failed overload resolution lists its candidates",
				() => At(24, 3, data => data?["candidates"]?.AsArray().Count == 2
					&& Text(data, "candidate_reason") == "OverloadResolutionFailure")),
			
			// CandidateSymbols is empty on a resolved call, so the overload set must come from the
			// method group, and only when it was asked for.
			new("roslyn_get_symbol_info: a resolved call lists no candidates and no overloads by default",
				() => At(23, 3, data => Text(data, "kind") == "method"
					&& data?["candidates"] is null
					&& data?["overloads"] is null)),
			
			new("roslyn_get_symbol_info: includeOverloads lists the method group",
				() => WithOverloads(23, 3, data => data?["overloads"]?.AsArray().Count == 2)),
			
			// A method name on a member access is the case a plain name lookup would miss: the group
			// is only available from the member access, and the type from the whole call.
			new("roslyn_get_symbol_info: a member-access call gives the group and the call's type",
				() => WithOverloads(20, 11, data => data?["overloads"]?.AsArray().Count > 2 && Text(data, "type") == "void")),
			
			new("roslyn_get_symbol_info: includeOverloads adds nothing away from a method name",
				() => WithOverloads(20, 21, data => data?["overloads"] is null)),
			
			// Each of these failed with 'No symbol resolved': a declaration is not a reference, so
			// GetSymbolInfo has nothing to say about it.
			new("roslyn_get_symbol_info: a class declaration resolves",
				() => At(4, 7, data => Text(data, "kind") == "namedtype" && Text(data, "name") == "Facts")),
			
			new("roslyn_get_symbol_info: a method declaration resolves",
				() => At(12, 10, data => Text(data, "kind") == "method")),
			
			new("roslyn_get_symbol_info: a local declaration resolves, with its declared type",
				() => At(16, 7, data => Text(data, "kind") == "local"
					&& Text(data, "type") == "string?"
					&& Text(data, "nullable_flow") is null)),
			
			new("roslyn_get_symbol_info: a parameter declaration resolves, with its declared type",
				() => At(14, 19, data => Text(data, "kind") == "parameter" && Text(data, "type") == "string?")),
			
			new("roslyn_get_symbol_info: a foreach variable resolves",
				() => At(26, 15, data => Text(data, "kind") == "local")),
			
			// The declared-name limit: a brace belongs to the declaration node, and resolving it to
			// the class would answer a question about punctuation.
			new("roslyn_get_symbol_info: a brace of a declaration is not the declared symbol",
				() => At(5, 1, data => data?["error"] is not null)),
			
			new("roslyn_get_symbol_info: a position past the file is refused",
				() => At(500, 1, data => data?["error"]?.GetValue<string>().Contains("out of range") == true)),
		};
		
		return new TestGroup($"Symbol Info ({tests.Count} tests)", tests, Teardown: () => {
			
			fx.Dispose();
			
			return Task.CompletedTask;
		});
	}
	
	static string? Text(JsonNode? data, string field) => data?[field]?.GetValue<string>();
}
