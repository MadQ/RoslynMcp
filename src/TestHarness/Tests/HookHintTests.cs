using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
///     Covers the pre-tool-use hook's hint (#310). The hook used to answer every intercepted
///     built-in tool with one fixed list that was mostly read tools, recommended
///     <c>roslyn_build_project</c>, and reached Copilot only — Claude Code got nothing. It now
///     names the roslyn_* tools for the kind of operation being intercepted, and sends the hint
///     to Claude Code as well.
///     <para>
///         Each test runs the real <c>hook</c> subcommand as a child process, writes one hook
///         event to its stdin, and reads the JSON it prints — the same contract the agent
///         clients use. Nothing is mocked. The hook stays silent unless a server heartbeat is
///         fresh; the harness's own server, already running, provides one.
///     </para>
///     <para>
///         One tool per category is enough to pin the mapping: <c>Read</c>, <c>Grep</c>,
///         <c>Glob</c> and <c>Edit</c> in Claude Code's format, plus <c>create</c> in Copilot's
///         format — Copilot's name for writing a new file, which the hook did not recognise
///         before. Every hint is checked for tools that belong to it and for tools from another
///         category that must be absent; a hint that listed everything would pass the first
///         check and fail the second. The two pass-through cases guard the other direction: a
///         file tool on a non-C# file, and a tool the hook does not redirect even though its
///         argument ends in <c>.cs</c>.
///     </para>
///     <para>
///         Copilot's <c>permissionDecision</c> is deliberately not asserted: whether the hook
///         should send it at all is still open under #310.
///     </para>
/// </summary>
static class HookHintTests
{
	// Never to be suggested by any hint: a build is not how compilation is checked (#306), and
	// the last two exist only in DEBUG builds.
	static readonly string[] neverSuggested = ["roslyn_build_project", "roslyn_respawn", "roslyn_debug_attach"];
	
	internal static TestGroup Build(TestContext ctx)
	{
		
		var tests = new List<TestCase> {
			
			Hint(ctx, "Claude Code Read suggests the read tools",
				new { hook_event_name = "PreToolUse", tool_name = "Read", tool_input = new { file_path = "src/Foo.cs" } },
				claudeFormat: true,
				expected: ["roslyn_read_file", "roslyn_get_file_outline", "roslyn_get_member_body"],
				absent:   ["roslyn_replace_in_code", "roslyn_search_files", "roslyn_list_files"]),
			
			Hint(ctx, "Claude Code Grep suggests the search tools",
				new { hook_event_name = "PreToolUse", tool_name = "Grep", tool_input = new { pattern = "TODO", glob = "*.cs" } },
				claudeFormat: true,
				expected: ["roslyn_search_files", "roslyn_semantic_search", "roslyn_find_references"],
				absent:   ["roslyn_read_file", "roslyn_replace_in_code", "roslyn_list_files"]),
			
			Hint(ctx, "Claude Code Glob suggests only the listing tool",
				new { hook_event_name = "PreToolUse", tool_name = "Glob", tool_input = new { pattern = "**/*.cs" } },
				claudeFormat: true,
				expected: ["roslyn_list_files"],
				absent:   ["roslyn_read_file", "roslyn_search_files", "roslyn_replace_in_code"]),
			
			Hint(ctx, "Claude Code Edit suggests the editing tools and the diagnostics check",
				new { hook_event_name = "PreToolUse", tool_name = "Edit", tool_input = new { file_path = "src/Foo.cs", old_string = "a", new_string = "b" } },
				claudeFormat: true,
				expected: ["roslyn_replace_in_code", "roslyn_replace_in_file", "roslyn_insert_lines", "roslyn_write_file", "roslyn_get_diagnostics"],
				absent:   ["roslyn_read_file", "roslyn_search_files", "roslyn_list_files"]),
			
			Hint(ctx, "Copilot create is recognised as an edit",
				new { toolName = "create", toolArgs = new { path = "src/New.cs", file_text = "class New { }" } },
				claudeFormat: false,
				expected: ["roslyn_write_file", "roslyn_replace_in_code"],
				absent:   ["roslyn_read_file", "roslyn_search_files"]),
			
			PassThrough(ctx, "a file tool on a non-C# file passes through",
				new { hook_event_name = "PreToolUse", tool_name = "Read", tool_input = new { file_path = "README.md" } }),
			
			PassThrough(ctx, "a tool the hook does not redirect passes through",
				new { hook_event_name = "PreToolUse", tool_name = "Bash", tool_input = new { command = "cat src/Foo.cs" } }),
		};
		
		return new TestGroup($"Hook Hint ({tests.Count} tests)", tests);
	}
	
	static TestCase Hint(TestContext ctx, string name, object hookEvent, bool claudeFormat, string[] expected, string[] absent)
		=> new($"hook: {name}", async () => {
			
			var output = await RunHookAsync(ctx, hookEvent);
			
			// Claude Code reads the hint under hookSpecificOutput and must not be handed a
			// permission decision — "allow" there would skip the user's permission prompt.
			var hint = claudeFormat
				? output?["hookSpecificOutput"]?["additionalContext"]?.GetValue<string>()
				: output?["additionalContext"]?.GetValue<string>()
			;
			
			if(hint is null)
				
				return (false, $"FAIL  (no hint in: {output?.ToJsonString() ?? "unparseable output"})");
			
			if(claudeFormat && (output?["hookSpecificOutput"]?["hookEventName"]?.GetValue<string>() != "PreToolUse"
				|| output["permissionDecision"] is not null
				|| output["hookSpecificOutput"]?["permissionDecision"] is not null))
				
				return (false, $"FAIL  (wrong Claude Code envelope: {output?.ToJsonString()})");
			
			var missing  = expected.Where(tool => !hint.Contains(tool)).ToArray();
			var unwanted = absent.Concat(neverSuggested).Where(hint.Contains).ToArray();
			
			if(missing.Any() || unwanted.Any() || hint.Contains('\n'))
				
				return (false, $"FAIL  (missing: [{string.Join(", ", missing)}], unwanted: [{string.Join(", ", unwanted)}], multi-line: {hint.Contains('\n')})");
			
			return (true, "PASS");
		})
	;
	
	static TestCase PassThrough(TestContext ctx, string name, object hookEvent)
		=> new($"hook: {name}", async () => {
			
			var output = await RunHookAsync(ctx, hookEvent);
			
			return output is JsonObject { Count: 0 }
				? (true,  "PASS")
				: (false, $"FAIL  (expected {{}}, got: {output?.ToJsonString() ?? "unparseable output"})");
		})
	;
	
	/// <summary>
	///     Runs <c>hook</c> on the already-built server (<c>--no-build</c>), feeds it one event on
	///     stdin, and parses the last non-empty line of stdout — <c>dotnet run</c> may print above
	///     it. Returns null when the process prints nothing parseable or does not exit in time.
	/// </summary>
	static async Task<JsonNode?> RunHookAsync(TestContext ctx, object hookEvent)
	{
		var psi = new ProcessStartInfo("dotnet")
		{
			
			Arguments              = $"run --no-build --no-launch-profile --project \"{ctx.ServerProj}\" -f net10.0 -- hook",
			WorkingDirectory       = TestFixtures.TempRoot,
			RedirectStandardInput  = true,
			RedirectStandardOutput = true,
			RedirectStandardError  = true,
			UseShellExecute        = false,
			StandardOutputEncoding = Encoding.UTF8,
		};
		
		using var process = Process.Start(psi)!;
		
		// Drained so a chatty stderr can never fill the pipe and stall the hook.
		process.ErrorDataReceived += (_, _) => { };
		process.BeginErrorReadLine();
		
		await process.StandardInput.WriteAsync(JsonSerializer.Serialize(hookEvent));
		process.StandardInput.Close();
		
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		
		try {
			
			var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token);
			
			await process.WaitForExitAsync(timeout.Token);
			
			var lastLine = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
			
			return lastLine is null ? null : JsonNode.Parse(lastLine);
		}
		catch(Exception ex) when(ex is OperationCanceledException or JsonException) {
			
			if(!process.HasExited)
				process.Kill(entireProcessTree: true);
			
			return null;
		}
	}
}
