using System.Text.Json.Nodes;

// What the server's log records about a call beyond its outcome (#449). The log is the only
// record of how the tools are used, and a usage analysis could not answer four questions from it
// because the fact was not written down. Each test makes a call and reads the entry it left.
static class LogFieldTests
{
	internal static TestGroup Build(TestContext ctx, string serverLogDir, string serverLogGlob)
	{
		var fx = TestFixtures.NewMsBuildProject("LogFields");
		
		// Line 1, column 14 is the class name: where a refactoring that works on a type is offered.
		fx.Write("LogFieldProbe.cs", "public class LogFieldProbe\n{\n\tpublic int Value() => 1;\n\n\tpublic string Name() => \"probe\";\n}\n");
		
		Task<JsonNode?> Call(string tool, object args) => ctx.CallToolJsonAsync(tool, args);
		
		static (bool, string) Verdict(bool pass, string failure)
			=> pass ? (true, "PASS") : (false, $"FAIL  ({failure})");
		
		// The details of the log entries one tool wrote about one subject, oldest first.
		(string[] Details, string? Failure) Details(string tool, string subjectPart)
		{
			if(!ServerLog.TryRead(serverLogDir, serverLogGlob, 1, out var lines, out var failure))
				
				return ([], failure);
			
			var details = new List<string>();
			
			foreach(var line in lines) {
				
				JsonNode? entry;
				
				try {
					entry = JsonNode.Parse(line);
				}
				catch(System.Text.Json.JsonException) {
					continue;
				}
				
				if(entry?["level"]?.GetValue<string>() == "TOOL"
					&& entry["tool_name"]?.GetValue<string>() == tool
					&& (entry["subject"]?.GetValue<string>() ?? "").Contains(subjectPart, StringComparison.Ordinal))
					details.Add(entry["detail"]?.GetValue<string>() ?? "");
			}
			
			return ([..details], null);
		}
		
		var tests = new List<TestCase> {
			
			// roslyn_replace_in_file is the most-called tool, and most of its calls edit C#. Whether
			// those are one-line literals or patterns across lines decides which other tool could
			// have made the edit, and the entry held the file and the match count only. Two dry
			// runs, one of each mode, must each say its mode in the entry. The mode was among the
			// arguments, but arguments are logged for a failed call alone.
			new("log fields: roslyn_replace_in_file records whether the pattern was a literal or a regex",
				async () => {
					
					await Call("roslyn_replace_in_file", new { filePath = "LogFieldProbe.cs", pattern = "probe", replacement = "probed", dryRun = true, projectPath = fx.Csproj });
					await Call("roslyn_replace_in_file", new { filePath = "LogFieldProbe.cs", pattern = "pro[b]e", replacement = "probed", mode = "regex", dryRun = true, projectPath = fx.Csproj });
					
					var (details, failure) = Details("replace_in_file", "LogFieldProbe.cs");
					
					if(failure is not null)
						
						return (false, failure);
					
					return Verdict(details.Length == 2 && details[0].Contains("literal") && details[1].Contains("regex"),
						$"expected a literal entry and a regex entry; got [{string.Join(" | ", details)}]");
				}),
			
			// A failed call must be traceable to the project it was resolved against. Seven "type not
			// found" answers in a real log could be tied to their project only through a
			// neighbouring line, and since #296 the project is often one the server picked. A type
			// that does not exist is asked for; the entry must name the fixture's project file.
			new("log fields: a failed call records the project it was resolved against",
				async () => {
					
					await Call("roslyn_get_type_members", new { typeName = "NoSuchTypeForLogFields", projectPath = fx.Csproj });
					
					var (details, failure) = Details("get_type_members", "NoSuchTypeForLogFields");
					
					if(failure is not null)
						
						return (false, failure);
					
					return Verdict(details.Length == 1 && details[0].Contains("type not found") && details[0].Contains($"project {fx.Csproj}"),
						$"expected 'type not found' with the project path; got [{string.Join(" | ", details)}]");
				}),
			
			// The refactorings that need a dialog are on the server's deny-list and removed before
			// the list is built, so they could never be counted as unavailable, and nothing showed
			// whether agents would have been offered them. At a class name with public members
			// Extract interface applies. The entry must name it; the answer must not, since the
			// agent cannot run it and is shown nothing it cannot run from that list.
			new("log fields: roslyn_list_refactorings records the deny-listed refactorings that would have applied",
				async () => {
					
					var answer = await Call("roslyn_list_refactorings", new { filePath = "LogFieldProbe.cs", line = 1, column = 14, projectPath = fx.Csproj });
					
					var (details, failure) = Details("list_refactorings", "LogFieldProbe.cs");
					
					if(failure is not null)
						
						return (false, failure);
					
					return Verdict(details.Length == 1
						&& details[0].Contains("denied with actions:") && details[0].Contains("ExtractInterface")
						&& !(answer?.ToJsonString() ?? "").Contains("ExtractInterface"),
						$"expected ExtractInterface in the log entry and not in the answer; got [{string.Join(" | ", details)}] and {answer?.ToJsonString() ?? "null"}");
				}),
		};
		
		return new TestGroup($"Log Fields ({tests.Count} tests)", tests, Teardown: () => { fx.Dispose(); return Task.CompletedTask; });
	}
}
