using System.Text.Json.Nodes;

static class DiscoveryTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		
		var tests = new List<TestCase> {
			
			new("roslyn_search_files: find 'WorkspaceManager' in .cs files",
				() => ctx.RunTestAsync(
					"roslyn_search_files",
					new { pattern = "WorkspaceManager", filePattern = "*.cs", take = 10, projectPath = ctx.TargetPath },
					data => data?["matches"]?.AsArray().Count > 0)),
			
			// ── Regression: unified match-mode enum (#253) ──
			// Before #253 the search tools treated `pattern` as regex UNCONDITIONALLY — there was no
			// literal mode at all, so a metacharacter-laden pattern either over-matched or hard-errored.
			// "[Description(" is invalid regex (unterminated character class '['), yet it is a literal
			// substring present in every tool file. With mode:"literal" the '[' is escaped, so the search
			// SUCCEEDS with matches instead of returning an "Invalid regex pattern" error. This fails
			// against the pre-#253 server (which had no mode param and would error) — not tautological.
			new("roslyn_search_files: mode=literal matches a pattern that is invalid regex",
				() => ctx.RunTestAsync(
					"roslyn_search_files",
					new { pattern = "[Description(", mode = "literal", filePattern = "*.cs", take = 5, projectPath = ctx.TargetPath },
					data => data?["error"] is null && data?["matches"]?.AsArray().Count > 0)),
			
			// mode:"glob" is anchored to the WHOLE line (^…$), so '*WorkspaceManager*' lowers to
			// ^.*WorkspaceManager.*$ and matches any line containing the token. Confirms glob is honored
			// as a distinct, anchored dialect (not passed through as a raw regex where '*' would throw).
			new("roslyn_search_files: mode=glob is anchored and honors wildcards",
				() => ctx.RunTestAsync(
					"roslyn_search_files",
					new { pattern = "*WorkspaceManager*", mode = "glob", filePattern = "*.cs", take = 5, projectPath = ctx.TargetPath },
					data => data?["error"] is null && data?["matches"]?.AsArray().Count > 0)),
			
			// An unrecognized mode must be a STRUCTURED validation error listing the valid values,
			// never an unhandled exception. Asserts the TryParseMatchMode guard fires before any search.
			new("roslyn_search_files: unknown mode returns structured error",
				() => ctx.RunTestAsync(
					"roslyn_search_files",
					new { pattern = "x", mode = "fuzzy", filePattern = "*.cs", take = 1, projectPath = ctx.TargetPath },
					data => data?["error"]?.GetValue<string>()?.Contains("literal") == true)),
			
			new("roslyn_semantic_search: find TODO comments only",
				() => ctx.RunTestAsync(
					"roslyn_semantic_search",
					new { pattern = "TODO", context = "comments", take = 10, projectPath = ctx.TargetPath },
					data => data?["matches"]?.AsArray().Count > 0 && data?["matches"]?[0]?["context"]?.GetValue<string>() == "comment")),
			
			new("roslyn_list_types: enumerate types in RoslynMcp.Tools namespace",
				() => ctx.RunTestAsync(
					"roslyn_list_types",
					new { namespaceFilter = "RoslynMcp.Tools", projectPath = ctx.TargetPath },
					data => data?["types"]?.AsArray().Count > 10 && data?["page_token"] is not null)),
			
			new("roslyn_list_files: enumerate tool files with glob pattern",
				() => ctx.RunTestAsync(
					"roslyn_list_files",
					new { pattern = "**/*Tool.cs", take = 50, projectPath = ctx.TargetPath },
					data => data?["count"]?.GetValue<int>() > 20
						&& data?["files"]?.AsArray().Any(f => f?.GetValue<string>().Contains("Tool.cs") == true) == true)),
			
			new("roslyn_get_file_outline: WorkspaceManager structure",
				() => ctx.RunTestAsync(
					"roslyn_get_file_outline",
					new { filePath = "WorkspaceManager.cs", projectPath = ctx.TargetPath },
					data => data?["types"]?.AsArray().Count > 0)),
			
			new("roslyn_get_project_info: verify TFM and packages",
				() => ctx.RunTestAsync(
					"roslyn_get_project_info",
					new { projectPath = ctx.TargetPath },
					data => data?["target_framework"]?.GetValue<string>()?.StartsWith("net") == true)),
			
			new("roslyn_get_usings: extract using directives from Program.cs",
				() => ctx.RunTestAsync(
					"roslyn_get_usings",
					new { filePath = "Program.cs", projectPath = ctx.TargetPath },
					data => data?["usings"]?.AsArray().Count > 0)),
			
			// ── Regression: guarded workspace resolution (transient mid-reload hardening, #249 part 2) ──
			// search_files and list_files resolve the workspace ONLY through the new read-guards
			// (TryResolveSolution / TryResolveRoot) — they have no TryGetCompilation funnel ahead of the
			// call. Before the guard a mid-reload throw propagated unhandled and surfaced as an opaque
			// "An error occurred invoking '<tool>'". An empty projectPath deterministically forces a
			// resolution failure, standing in for the transient case; the tool must now return a
			// STRUCTURED JSON error. RunTestAsync fails on non-JSON, so these fail against the pre-guard
			// server — not tautological: they assert the direct resolver call is now wrapped.
			
			new("roslyn_search_files: structured error (not crash) on resolution failure",
				() => ctx.RunTestAsync(
					"roslyn_search_files",
					new { pattern = "x", filePattern = "*.cs", take = 1, projectPath = "" },
					data => data?["error"]?.GetValue<string>() is { Length: > 0 })),
			
			new("roslyn_list_files: structured error (not crash) on resolution failure",
				() => ctx.RunTestAsync(
					"roslyn_list_files",
					new { pattern = "**/*", take = 1, projectPath = "" },
					data => data?["error"]?.GetValue<string>() is { Length: > 0 })),
		};
		
		return new TestGroup($"Discovery Tools ({tests.Count} tests)", tests);
	}
}
