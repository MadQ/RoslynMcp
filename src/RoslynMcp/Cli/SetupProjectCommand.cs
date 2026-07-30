using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynMcp.Cli;

/// <summary>
///     Implements the <c>madq-roslynmcp setup-project</c> subcommand. Writes committable
///     per-project files to the nearest git repository root: a hook file under <c>.github/</c>
///     enabling per-agent tool guidance for .cs file operations, and (opt-in) a
///     <c>.madq_roslynmcp.json</c> server config holding commit-worthy server settings
///     (<c>elicit</c>, <c>workspace</c>) that the server folds in where no CLI arg or env var
///     was given — see <see cref="ProjectConfig"/>.
/// </summary>
internal class SetupProjectCommand : CliCommand
{
	public static int Run()
	{
		var repoRoot = FindRepoRoot(Environment.CurrentDirectory);
		
		if(repoRoot is null) {
			
			Console.WriteLine();
			Console.WriteLine("  ✗ Could not find a git repository root from the current directory.");
			Console.WriteLine("    Run '" + ToolCommand.Name + " setup-project' from inside a git repository.")
			;
			Console.WriteLine()
			;
			
			return 1;
		}
		
		var githubDir = Path.Combine(repoRoot, ".github");
		
		Directory.CreateDirectory(githubDir);
		
		var hookFile = Path.Combine(githubDir, "roslynmcp.json");
		
		// The hook reads tool event JSON from stdin and writes allow/additionalContext.
		// See ToolCommand.HookCommand for why this is the bare command name (not `dotnet …`
		// and not an absolute path — this file is committed and shared across contributors).
		const string hookCommand = ToolCommand.HookCommand;
		
		string? backupPath = null;
		bool    isNew      = !File.Exists(hookFile);
		string  json;
		
		if(!isNew) {
			
			// Backup first, then merge our entry into the existing file.
			backupPath = hookFile + ".roslynmcp.bak";
			File.Copy(hookFile, backupPath, overwrite: true);
			json = MergeHookEntry(hookFile, hookCommand) ?? BuildFreshJson(hookCommand);
		}
		else {
			
			json = BuildFreshJson(hookCommand);
		}
		
		File.WriteAllText(hookFile, json);
		
		var relativePath = Path.GetRelativePath(Environment.CurrentDirectory, hookFile);
		
		Console.WriteLine();
		Console.WriteLine(isNew
			? $"  ✓ Wrote project hook: {relativePath}"
			: $"  ✓ Updated project hook: {relativePath}");
		
		if(backupPath is not null)
			Console.WriteLine($"      Backup: {Path.GetRelativePath(Environment.CurrentDirectory, backupPath)}");
		
		var wroteConfig = ConfigureServerConfig(repoRoot);
		
		Console.WriteLine();
		Console.WriteLine(wroteConfig
			? "  Commit .github/roslynmcp.json and " + ProjectConfig.FileName + " so all project contributors benefit."
			: "  Commit .github/roslynmcp.json so all project contributors benefit.");
		Console.WriteLine("  The hook guides agents to prefer roslyn_* tools for .cs files —");
		Console.WriteLine("  no file operations are blocked.");
		
		if(wroteConfig) {
			
			Console.WriteLine();
			Console.WriteLine("  Note: --elicit/--workspace args in an agent's global MCP config (written by");
			Console.WriteLine("  '" + ToolCommand.Name + " setup') override the project file. Precedence:");
			Console.WriteLine("  CLI arg > env var > project file > default. Running servers pick up changes");
			Console.WriteLine("  on restart.");
		}
		
		if(ExecutablePath is null) {
			
			Console.WriteLine();
			Console.WriteLine("  ⚠ '" + ToolCommand.HookCommand + "' must be available in PATH for the hook to work.");
			Console.WriteLine("    Install globally: dotnet tool install -g " + ToolCommand.PackageId);
		}
		
		Console.WriteLine();
		Console.WriteLine($"  RoslynMcp v{CurrentVersion} — feedback & issues: https://github.com/MadQ/RoslynMcp");
		Console.WriteLine();
		
		return 0;
	}
	
	// Prompts for the commit-worthy server settings and writes them to .madq_roslynmcp.json at
	// the repo root — the file ProjectConfig discovers by walking up from each request's project
	// path. Reruns preserve earlier choices (prompt defaults reflect the existing file) and any
	// unknown keys in it. Returns whether the config file was written.
	static bool ConfigureServerConfig(string repoRoot)
	{
		var configPath = Path.Combine(repoRoot, ProjectConfig.FileName);
		var exists     = File.Exists(configPath);
		
		var root = new JsonObject();
		
		if(exists) {
			
			try {
				
				var parsed = JsonNode.Parse(File.ReadAllText(configPath), documentOptions: new JsonDocumentOptions {
					
					AllowTrailingCommas = true,
					CommentHandling     = JsonCommentHandling.Skip,
				});
				
				if(parsed is JsonObject obj)
					root = obj;
				else
					Console.WriteLine($"  ⚠ Existing {ProjectConfig.FileName} is not a JSON object — starting fresh (backup kept).");
			}
			catch(Exception ex) {
				
				Console.WriteLine($"  ⚠ Could not parse existing {ProjectConfig.FileName} ({ex.Message}) — starting fresh (backup kept).");
			}
		}
		
		// Current values drive the prompt defaults so a rerun preserves earlier choices.
		var currentElicit = root["elicit"] is JsonValue elicitNode
			&& elicitNode.TryGetValue<bool>(out var existingElicit)
			&& existingElicit;
		
		var currentWorkspace = root["workspace"] is JsonValue workspaceNode
			&& workspaceNode.TryGetValue<string>(out var existingWorkspace)
			? existingWorkspace.ToLowerInvariant()
			: "auto";
		
		if(currentWorkspace is not ("sdk" or "vs" or "adhoc"))
			currentWorkspace = "auto";
		
		// Same explanation as the global setup wizard's --elicit prompt, scoped per-project.
		Console.WriteLine();
		Console.WriteLine("  On an ambiguous symbol match, roslyn_preview_rename / roslyn_change_signature");
		Console.WriteLine("  return a structured candidate list the agent resolves on its own (default).");
		Console.WriteLine("  With elicitation they instead ask you to pick — but only in MCP clients that");
		Console.WriteLine("  support elicitation (Claude Code/Desktop do; many others don't and silently");
		Console.WriteLine("  fall back to the candidate list).");
		Console.Write($"  Enable interactive elicitation for this project? [{(currentElicit ? "Y/n" : "y/N")}]: ");
		
		var elicitAnswer = Console.ReadLine()?.Trim() ?? "";
		
		bool elicit;
		
		if(elicitAnswer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
		   elicitAnswer.Equals("yes", StringComparison.OrdinalIgnoreCase))
			
			elicit = true;
		
		else if(elicitAnswer.Equals("n", StringComparison.OrdinalIgnoreCase) ||
		        elicitAnswer.Equals("no", StringComparison.OrdinalIgnoreCase))
			
			elicit = false;
		
		else
			// Enter (or anything unrecognized) keeps the current value.
			elicit = currentElicit;
		
		Console.WriteLine();
		Console.WriteLine("  Pin the MSBuild workspace mode for this repo: sdk (modern SDK-style projects),");
		Console.WriteLine("  vs (.NET Framework via Visual Studio MSBuild), adhoc (no MSBuild, reduced");
		Console.WriteLine("  semantics); auto detects per project.");
		Console.Write($"  Workspace mode for this project (auto/sdk/vs/adhoc) [{currentWorkspace}]: ");
		
		var workspaceAnswer = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
		
		string workspace;
		
		switch(workspaceAnswer) {
			
			case "auto":
			case "sdk":
			case "vs":
			case "adhoc":
				workspace = workspaceAnswer;
				break;
			
			case "":
				workspace = currentWorkspace;
				break;
			
			default:
				Console.WriteLine($"    Unrecognized mode '{workspaceAnswer}' — keeping '{currentWorkspace}'.");
				workspace = currentWorkspace;
				break;
		}
		
		// Nothing meaningful chosen and nothing to preserve — don't create the file at all.
		if(!exists && !elicit && workspace == "auto") {
			
			Console.WriteLine();
			Console.WriteLine($"  No project server settings selected — {ProjectConfig.FileName} not written.");
			
			return false;
		}
		
		// Compose: unknown keys in the existing file are preserved via the JsonObject round-trip.
		// elicit is always written explicitly — a committed team decision is self-documenting.
		root["version"] = 1;
		root["elicit"]  = elicit;
		
		if(workspace == "auto")
			root.Remove("workspace");
		else
			root["workspace"] = workspace;
		
		string? backupPath = null;
		
		if(exists) {
			
			backupPath = configPath + ".roslynmcp.bak";
			File.Copy(configPath, backupPath, overwrite: true);
		}
		
		// Atomic write: temp → final so a crash mid-write can't corrupt the config.
		var tempPath = configPath + ".roslynmcp.tmp";
		
		try {
			
			File.WriteAllText(tempPath, root.ToJsonString(new JsonSerializerOptions {
				
				WriteIndented = true,
				Encoder       = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
			}));
			
			File.Move(tempPath, configPath, overwrite: true);
		}
		finally {
			
			// Clean up temp file if the move didn't happen (exception path).
			if(File.Exists(tempPath)) {
				
				try { File.Delete(tempPath); } catch { }
			}
		}
		
		var relativeConfig = Path.GetRelativePath(Environment.CurrentDirectory, configPath);
		
		Console.WriteLine();
		Console.WriteLine(exists
			? $"  ✓ Updated project server config: {relativeConfig}"
			: $"  ✓ Wrote project server config: {relativeConfig}");
		
		if(backupPath is not null)
			Console.WriteLine($"      Backup: {Path.GetRelativePath(Environment.CurrentDirectory, backupPath)}");
		
		return true;
	}
	
	// Builds a fresh hook config JSON string.
	static string BuildFreshJson(string hookCommand)
	{
		var hookConfig = new {
			
			version = 1,
			hooks = new {
				
				preToolUse = new[] {
					
					new {
						
						type       = "command",
						powershell = hookCommand,
						bash       = hookCommand,
						timeoutSec = 5
					}
				}
			}
		};
		
		return JsonSerializer.Serialize(hookConfig, new JsonSerializerOptions {
			
			WriteIndented        = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		});
	}
	
	// Reads the existing hook file, upserts our hook entry in preToolUse, and returns the
	// merged JSON. Returns null on parse failure — caller falls back to BuildFreshJson.
	static string? MergeHookEntry(string hookFile, string hookCommand)
	{
		try {
			
			var root = JsonNode.Parse(File.ReadAllText(hookFile)) as JsonObject;
			
			if(root is null)
				return null;
			
			if(root["hooks"] is not JsonObject hooksObj) {
				
				hooksObj      = new JsonObject();
				root["hooks"] = hooksObj;
			}
			
			if(hooksObj["preToolUse"] is not JsonArray preToolUse) {
				
				preToolUse             = new JsonArray();
				hooksObj["preToolUse"] = preToolUse;
			}
			
			var ourEntry = new JsonObject {
				
				["type"]       = "command",
				["powershell"] = hookCommand,
				["bash"]       = hookCommand,
				["timeoutSec"] = 5
			};
			
			// Remove any existing RoslynMcp entries (current or legacy command forms), then add
			// one fresh. Matching by the whole invocation — not exact string equality — so a
			// renamed command doesn't leave a stale duplicate behind.
			for(var i = preToolUse.Count - 1; i >= 0; i--) {
				
				if(preToolUse[i] is not JsonObject entry)
					continue;
				
				if(ToolCommand.IsOurCommandInvocation(entry["powershell"]?.GetValue<string>()) ||
				   ToolCommand.IsOurCommandInvocation(entry["bash"]?.GetValue<string>()))
					
					preToolUse.RemoveAt(i);
			}
			
			preToolUse.Add(ourEntry);
			
			return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
		}
		catch {
			
			return null;
		}
	}
	
	// Walks up from startDir to find the nearest directory containing a .git entry.
	// .git is a directory in a normal checkout but a file in git worktrees and submodules —
	// accept both so setup-project works from a worktree.
	static string? FindRepoRoot(string startDir)
	{
		var dir = startDir;
		
		while(true) {
			
			var gitPath = Path.Combine(dir, ".git");
			
			if(Directory.Exists(gitPath) || File.Exists(gitPath))
				
				return dir;
			
			var parent = Directory.GetParent(dir);
			
			if(parent is null || parent.FullName == dir)
				
				return null;
			
			dir = parent.FullName;
		}
	}
}
