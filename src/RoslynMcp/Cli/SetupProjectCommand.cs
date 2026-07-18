using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynMcp.Cli;

/// <summary>
///     Implements the <c>madq-roslynmcp setup-project</c> subcommand. Writes a per-project
///     hook file to the nearest git repository root, enabling per-agent tool guidance for
///     .cs file operations in VS Code Copilot and Copilot CLI.
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
		
		Console.WriteLine();
		Console.WriteLine("  Commit .github/roslynmcp.json so all project contributors benefit.");
		Console.WriteLine("  The hook guides agents to prefer roslyn_* tools for .cs files —");
		Console.WriteLine("  no file operations are blocked.");
		
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
	
	// Walks up from startDir to find the nearest directory containing a .git folder.
	static string? FindRepoRoot(string startDir)
	{
		var dir = startDir;
		
		while(true) {
			
			if(Directory.Exists(Path.Combine(dir, ".git")))
				
				return dir;
			
			var parent = Directory.GetParent(dir);
			
			if(parent is null || parent.FullName == dir)
				
				return null;
			
			dir = parent.FullName;
		}
	}
}
