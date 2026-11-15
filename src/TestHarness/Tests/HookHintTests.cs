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
///         Copilot CLI takes the hint from a <c>postToolUse</c> hook, whose event carries the
///         tool's result: its <c>preToolUse</c> output is a permission decision, and the hint
///         must not be one. So a Copilot event with a result gets the hint and nothing else,
///         and one without, which a hook file from before #310 still sends, gets <c>{}</c>.
///     </para>
///     <para>
///         The last four tests are about which server counts (#310). A server alive anywhere
///         on the machine used to make the hook advise in every session, also in one without
///         RoslynMcp. Each of them gives the hook a heartbeat directory of its own with one
///         made-up heartbeat in it, and a session directory in the event.
///     </para>
/// </summary>
static class HookHintTests
{
	// Never to be suggested by any hint: a build is not how compilation is checked (#306), a respawn
	// is a remedy for a broken workspace and never a way to edit a file (it ships in every build since
	// #406), and the debugger attach exists only in DEBUG builds.
	static readonly string[] neverSuggested = ["roslyn_build_project", "roslyn_respawn", "roslyn_debug_attach"];
	
	/// <summary>
	///     The name of a heartbeat the harness puts into its heartbeat directory before its first
	///     server starts, dated two days back: what a server that was killed leaves behind.
	/// </summary>
	internal const string DeadHeartbeat = "heartbeat.4194305";
	
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
			
			// An edit is also the moment to offer Roslyn's refactorings and fixers (#407): the hint
			// names the three-step refactoring workflow and the code-fix preview beside the editing tools.
			Hint(ctx, "Claude Code Edit suggests the editing tools, the diagnostics check, the refactorings and the code fixes",
				new { hook_event_name = "PreToolUse", tool_name = "Edit", tool_input = new { file_path = "src/Foo.cs", old_string = "a", new_string = "b" } },
				claudeFormat: true,
				expected: ["roslyn_replace_in_code", "roslyn_replace_in_file", "roslyn_insert_lines", "roslyn_write_file", "roslyn_multi_edit", "roslyn_get_diagnostics", "roslyn_list_refactorings", "roslyn_preview_refactoring", "roslyn_apply_refactoring", "roslyn_preview_code_fix"],
				absent:   ["roslyn_read_file", "roslyn_search_files", "roslyn_list_files"]),
			
			Hint(ctx, "Copilot create is recognised as an edit, after the tool ran",
				new { toolName = "create", toolArgs = new { path = "src/New.cs", file_text = "class New { }" }, toolResult = new { resultType = "success", textResultForLlm = "Created." } },
				claudeFormat: false,
				expected: ["roslyn_write_file", "roslyn_replace_in_code", "roslyn_list_refactorings"],
				absent:   ["roslyn_read_file", "roslyn_search_files"]),
			
			// The same event without a result is Copilot's preToolUse, where the only thing a hook
			// can say is a permission decision. Answered with the hint and "allow", as it was, the
			// hook granted a permission nobody asked it for, in a field the client does not document.
			PassThrough(ctx, "a Copilot event before the tool ran passes through",
				new { toolName = "create", toolArgs = new { path = "src/New.cs", file_text = "class New { }" } }),
			
			PassThrough(ctx, "a file tool on a non-C# file passes through",
				new { hook_event_name = "PreToolUse", tool_name = "Read", tool_input = new { file_path = "README.md" } }),
			
			PassThrough(ctx, "a tool the hook does not redirect passes through",
				new { hook_event_name = "PreToolUse", tool_name = "Bash", tool_input = new { command = "cat src/Foo.cs" } }),
			
			// A killed server never removes its heartbeat, and they were kept for ninety days: forty
			// dead ones stood beside one live one. A server that starts now removes those older
			// than an hour. The harness's server has started by the time any test runs; its own
			// heartbeat must be there, and the dead one gone.
			new("hook: a heartbeat left by a dead server is removed when a server starts", () => {
				
				var directory = Environment.GetEnvironmentVariable("ROSLYNMCP_HEARTBEAT_PATH") ?? "";
				var left      = Directory.Exists(directory) ? Directory.GetFiles(directory, "heartbeat.*").Select(Path.GetFileName).ToArray() : [];
				
				return Task.FromResult(left.Length > 0 && !left.Contains(DeadHeartbeat)
					? (true, "PASS")
					: (false, $"FAIL  (heartbeats in {directory}: [{string.Join(", ", left)}])"));
			}),
			
			// The server's directory holds the session's: the usual case, a session somewhere in
			// the repository the server was started on.
			Served(ctx, "a server whose root holds the session's directory counts",
				serverRoot: Path.Combine(TestFixtures.TempRoot, "hook-repo"), sessionDirectory: Path.Combine(TestFixtures.TempRoot, "hook-repo", "src", "App"), hinted: true),
			
			// The other way round: the session is at the repository's root, the server was started
			// on one of its folders.
			Served(ctx, "a server whose root is inside the session's directory counts",
				serverRoot: Path.Combine(TestFixtures.TempRoot, "hook-repo", "src"), sessionDirectory: Path.Combine(TestFixtures.TempRoot, "hook-repo"), hinted: true),
			
			// What the issue is about: a server of another project. Its name starts like the
			// session's directory, so a comparison of the text alone would take it for a parent.
			Served(ctx, "a server of another project does not count",
				serverRoot: Path.Combine(TestFixtures.TempRoot, "hook-repo"), sessionDirectory: Path.Combine(TestFixtures.TempRoot, "hook-repo-other"), hinted: false),
			
			// A heartbeat without a directory is a server from before #310. It says nothing about
			// where it serves, so it counts, as every heartbeat did.
			Served(ctx, "a heartbeat without a directory counts for every session",
				serverRoot: null, sessionDirectory: Path.Combine(TestFixtures.TempRoot, "hook-repo-other"), hinted: true),
		};
		
		return new TestGroup($"Hook Hint ({tests.Count} tests)", tests);
	}
	
	/// <summary>
	///     One hook run against a heartbeat directory that holds a single fresh heartbeat, of a
	///     server on <paramref name="serverRoot"/> (none: the heartbeat of a server from before
	///     #310, a PID alone), for a Claude Code <c>Read</c> of a C# file in a session working in
	///     <paramref name="sessionDirectory"/>. The directories need not exist: the hook compares
	///     paths. The PID is no process's; the hook reads the file's time, not the process.
	/// </summary>
	static TestCase Served(TestContext ctx, string name, string? serverRoot, string sessionDirectory, bool hinted)
		=> new($"hook: {name}", async () => {
			
			var heartbeats = Path.Combine(TestFixtures.TempRoot, $"hook-heartbeat.{Guid.NewGuid():N}");
			
			Directory.CreateDirectory(heartbeats);
			
			try {
				
				File.WriteAllText(Path.Combine(heartbeats, "heartbeat.4194304"), serverRoot is null ? "4194304" : $"4194304\n{serverRoot}");
				
				var output = await RunHookAsync(ctx,
					new { hook_event_name = "PreToolUse", cwd = sessionDirectory, tool_name = "Read", tool_input = new { file_path = "src/Foo.cs" } },
					heartbeats);
				
				var gotHint = output?["hookSpecificOutput"]?["additionalContext"] is not null;
				
				return gotHint == hinted && (hinted || output is JsonObject { Count: 0 })
					? (true, "PASS")
					: (false, $"FAIL  (expected {(hinted ? "a hint" : "{}")}, got: {output?.ToJsonString() ?? "unparseable output"})");
			}
			finally {
				Directory.Delete(heartbeats, recursive: true);
			}
		})
	;
	
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
			
			// Copilot is handed the hint and nothing beside it: no permission decision either.
			if(!claudeFormat && output is JsonObject { Count: not 1 })
				
				return (false, $"FAIL  (more than the hint in Copilot's answer: {output.ToJsonString()})");
			
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
	static Task<JsonNode?> RunHookAsync(TestContext ctx, object hookEvent) => RunHookAsync(ctx, hookEvent, null);
	
	/// <inheritdoc cref="RunHookAsync(TestContext, object)"/>
	/// <param name="heartbeatDirectory">The heartbeat directory the hook reads, or null for the harness's own, where its server's heartbeat is.</param>
	static async Task<JsonNode?> RunHookAsync(TestContext ctx, object hookEvent, string? heartbeatDirectory)
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
		
		if(heartbeatDirectory is not null)
			psi.Environment["ROSLYNMCP_HEARTBEAT_PATH"] = heartbeatDirectory;
		
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
