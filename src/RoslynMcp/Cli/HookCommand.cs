using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynMcp.Cli;

/// <summary>
///     Implements the <c>dotnet roslynmcp hook</c> subcommand, used as the target for
///     pre-tool-use hooks in Copilot CLI (<c>.github/hooks/roslynmcp.json</c>) and
///     Claude Code (<c>~/.claude/settings.json</c>). Reads hook event JSON from stdin
///     and writes an allow/additionalContext response to stdout.
/// </summary>
internal static class HookCommand
{
	// Tools that read or list files — worth redirecting to roslyn_* equivalents.
	static readonly HashSet<string> FileTools = new(StringComparer.OrdinalIgnoreCase) {

		"view", "read", "grep", "rg", "glob", "findstr",
		// Claude Code equivalents
		"Read", "Grep", "Glob",
	};

	// Tools that write or edit files.
	static readonly HashSet<string> EditTools = new(StringComparer.OrdinalIgnoreCase) {

		"edit", "write",
		// Claude Code equivalents
		"Edit", "MultiEdit", "Write",
	};

	public static int Run(string[] args)
	{
		// Opt-in hook logging. Voluminous in normal operation (every file op in an agent
		// session fires one) so only enabled when actively debugging hook behavior.
		var shouldLog = args.Skip(1).Any(a => a.Equals("--log", StringComparison.OrdinalIgnoreCase));
		var stopwatch = Stopwatch.StartNew();

		// State captured across the hook flow so the final log entry describes what happened.
		string? toolName  = null;
		string  eventName = "";
		string  outcome   = "no-op"; // default — empty input, parse failure, etc.

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

			bool isCopilotFormat = node["toolName"] is not null;

			// Event name for the log entry. Claude Code surfaces it directly; Copilot
			// doesn't, so we label by source.
			eventName = node["hook_event_name"]?.GetValue<string>()
				?? (isCopilotFormat ? "copilot-hook" : "claude-code-hook");

			if(toolName is null || !IsFileOperationOnCsFile(toolName, toolArgs)) {

				Console.WriteLine("{}");
				outcome = toolName is null ? "no-tool" : "pass-through";

				return 0;
			}

			// Allow the operation but inject guidance into the agent's context.
			// Using additionalContext (Copilot format) — Claude Code's deny path is separate
			// but allowed here too since their spec accepts empty output as "allow".
			if(isCopilotFormat) {

				if(!ServerHeartbeat.IsAlive()) {

					Console.WriteLine("{}");
					outcome = "allow (no server)";

					return 0;
				}

				var response = new {

					permissionDecision = "allow",
					additionalContext  = "ℹ️ RoslynMcp: for .cs files, roslyn_* tools provide semantic " +
						"accuracy via the Roslyn compiler. Prefer: roslyn_read_file, roslyn_get_member_body, " +
						"roslyn_get_file_outline, roslyn_search_files, roslyn_list_files, " +
						"roslyn_replace_in_code, roslyn_get_diagnostics, roslyn_build_project."
				};

				Console.WriteLine(JsonSerializer.Serialize(response, RoslynMcpJson.Compact));
				outcome = "allow + guidance";
			}
			else {
				// Claude Code hooks don't support additionalContext — just allow silently.
				Console.WriteLine("{}");
				outcome = "allow (silent)";
			}

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

	static bool IsFileOperationOnCsFile(string toolName, JsonObject? toolArgs)
	{
		if(!FileTools.Contains(toolName) && !EditTools.Contains(toolName))

			return false;

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
