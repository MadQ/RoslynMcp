using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynMcp.Cli;

/// <summary>
///     Implements the <c>madq-roslynmcp hook</c> subcommand, used as the target for
///     pre-tool-use hooks in Copilot CLI (<c>.github/hooks/roslynmcp.json</c>) and
///     Claude Code (<c>~/.claude/settings.json</c>). Reads hook event JSON from stdin and, when a
///     built-in file tool targets a <c>.cs</c> file, writes guidance naming the roslyn_* tools
///     for that kind of operation. It never blocks: every other case answers <c>{}</c>.
/// </summary>
internal static class HookCommand
{
	// The built-in tools worth redirecting, grouped by what the agent is trying to do so the hint
	// can name the roslyn_* tools for that job only (#310). Names cover both clients — Copilot CLI
	// (view, create, edit, grep, glob) and Claude Code (Read, Grep, Glob, Edit, MultiEdit, Write);
	// matching ignores case, so one spelling serves both.
	static readonly HashSet<string> readTools   = new(StringComparer.OrdinalIgnoreCase) { "view", "read" };
	static readonly HashSet<string> searchTools = new(StringComparer.OrdinalIgnoreCase) { "grep", "rg", "findstr" };
	static readonly HashSet<string> listTools   = new(StringComparer.OrdinalIgnoreCase) { "glob" };
	static readonly HashSet<string> editTools   = new(StringComparer.OrdinalIgnoreCase) { "edit", "multiedit", "write", "create" };

	// One short list per category. A list of every tool was tried and dropped: it buried the two or
	// three names that matter for the call at hand, on every hook hit. Only public, release-build
	// tools belong here, and not roslyn_build_project — compilation is checked with
	// roslyn_get_diagnostics (#306). "projectPath optional" is stated only for the tools where it is.
	const string ReadSuggestion   = "roslyn_read_file or roslyn_get_file_outline (projectPath optional), or roslyn_get_member_body for one member";
	const string SearchSuggestion = "roslyn_search_files or roslyn_semantic_search (projectPath optional), or roslyn_find_references for a symbol's usages";
	const string ListSuggestion   = "roslyn_list_files (projectPath optional)";
	const string EditSuggestion   = "roslyn_replace_in_code, roslyn_replace_in_file, roslyn_insert_lines or roslyn_write_file (projectPath optional), then roslyn_get_diagnostics to verify";

	/// <summary>
	///     The roslyn_* tools to suggest for an intercepted built-in tool, or <see langword="null"/>
	///     when the tool is not one the hook redirects.
	/// </summary>
	static string? SuggestionFor(string toolName)
	{
		if(readTools.Contains(toolName))
			return ReadSuggestion;

		if(searchTools.Contains(toolName))
			return SearchSuggestion;

		if(listTools.Contains(toolName))
			return ListSuggestion;

		return editTools.Contains(toolName) ? EditSuggestion : null;
	}

	public static int Run(string[] args)
	{
		// Opt-in hook logging. Voluminous in normal operation (every file op in an agent
		// session fires one) so only enabled when actively debugging hook behavior.
		var shouldLog = args.Skip(1).Any(a => a.Equals("--log", StringComparison.OrdinalIgnoreCase));
		var stopwatch = Stopwatch.StartNew();

		// State captured across the hook flow so the final log entry describes what happened.
		string? toolName  = null;
		
		var  eventName = "";
		var  outcome   = "no-op"; // default — empty input, parse failure, etc.

		try {

			var input = Console.In.ReadToEnd();

			if(string.IsNullOrWhiteSpace(input)) {

				Console.WriteLine("{}");
				outcome = "empty-input";

				return 0;
			}

			JsonNode? node;

			try {
				node = JsonNode.Parse(input);
			}
			catch {

				Console.WriteLine("{}");
				outcome = "parse-error";

				return 0;
			}

			if(node is null) {

				Console.WriteLine("{}");
				outcome = "null-input";

				return 0;
			}

			// Support both Copilot CLI format (camelCase: toolName/toolArgs) and
			// Claude Code format (snake_case: tool_name/tool_input).
			toolName = node["toolName"]?.GetValue<string>()
				?? node["tool_name"]?.GetValue<string>();

			var toolArgs = node["toolArgs"] as JsonObject
				?? node["tool_input"] as JsonObject;

			var isCopilotFormat = node["toolName"] is not null;

			// Event name for the log entry. Claude Code surfaces it directly; Copilot
			// doesn't, so we label by source.
			eventName = node["hook_event_name"]?.GetValue<string>()
				?? (isCopilotFormat ? "copilot-hook" : "claude-code-hook");

			var suggestion = toolName is null ? null : SuggestionFor(toolName);

			if(suggestion is null || !TargetsCsFile(toolArgs)) {

				Console.WriteLine("{}");
				outcome = toolName is null ? "no-tool" : "pass-through";

				return 0;
			}

			// Without a live server the roslyn_* tools are not there to prefer.
			if(!ServerHeartbeat.IsAlive()) {

				Console.WriteLine("{}");
				outcome = "allow (no server)";

				return 0;
			}

			var hint = $"ℹ️ RoslynMcp: for .cs files prefer {suggestion}. These use the Roslyn compiler, so results are semantically accurate.";

			// The operation always goes ahead; the hook only adds guidance to the agent's context.
			// Claude Code takes it under hookSpecificOutput, and no permissionDecision is sent
			// there: "allow" would skip the user's permission prompt, and advice must not grant
			// permission. The Copilot CLI shape is unchanged from before #310.
			// TODO: Copilot's hook reference lists no additionalContext output for preToolUse and
			// describes permissionDecision as deciding whether the tool runs. Does Copilot show
			// this hint at all, and should "allow" be sent? Open under #310.
			object response = isCopilotFormat
				? new { permissionDecision = "allow", additionalContext = hint }
				: new { hookSpecificOutput = new { hookEventName = "PreToolUse", additionalContext = hint } }
			;

			Console.WriteLine(JsonSerializer.Serialize(response, RoslynMcpJson.Compact));
			outcome = "allow + guidance";

			return 0;
		}
		catch {
			// Hooks must never fail — always allow on error.
			Console.WriteLine("{}");
			outcome = "error";

			return 0;
		}
		finally {

			if(shouldLog)
				try {

					ServerArgs.Initialize(args);

					using var logger = new FileLogger();

					if(logger.IsEnabled)
						logger.LogHook(eventName, toolName, stopwatch.ElapsedMilliseconds, outcome);
				}
				catch {
					// Logging must never break the hook — silent.
				}
		}
	}

	static bool TargetsCsFile(JsonObject? toolArgs)
	{
		if(toolArgs is null)

			return false;

		// Check if any string argument value looks like a .cs file or *.cs glob pattern.
		foreach(var (_, value) in toolArgs) {

			if(value?.GetValueKind() != System.Text.Json.JsonValueKind.String)
				continue;

			var str = value.GetValue<string>();

			if(HasCsExtension(str))

				return true;
		}

		return false;
	}

	static bool HasCsExtension(string value) =>
		value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
		|| value.EndsWith("*.cs", StringComparison.OrdinalIgnoreCase)
		|| value.Contains("/**/*.cs", StringComparison.OrdinalIgnoreCase)
		|| value.Contains("**/*.cs", StringComparison.OrdinalIgnoreCase)
	;
}
