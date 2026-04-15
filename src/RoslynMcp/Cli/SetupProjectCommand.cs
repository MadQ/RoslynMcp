using System.Text.Json;

namespace RoslynMcp.Cli;

/// <summary>
///     Implements the <c>dotnet roslynmcp setup-project</c> subcommand. Writes a per-project
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
			Console.WriteLine("    Run 'dotnet roslynmcp setup-project' from inside a git repository.")
			;
			Console.WriteLine()
			;
			
			return 1;
		}
		
		var githubDir = Path.Combine(repoRoot, ".github");
		
		Directory.CreateDirectory(githubDir);
		
		var hookFile = Path.Combine(githubDir, "roslynmcp.json");
		
		// dotnet roslynmcp hook reads tool event JSON from stdin and writes allow/additionalContext.
		// Using the dotnet tool invocation (not a raw binary path) keeps it cross-platform and
		// stable across tool reinstalls — no path updates needed after dotnet tool update.
		const string hookCommand = "dotnet roslynmcp hook"
		;
		
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
		
		var json = JsonSerializer.Serialize(hookConfig, new JsonSerializerOptions {
			
			WriteIndented        = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		});
		
		File.WriteAllText(hookFile, json);
		
		var relativePath = Path.GetRelativePath(Environment.CurrentDirectory, hookFile);
		
		Console.WriteLine();
		Console.WriteLine($"  ✓ Wrote project hook: {relativePath}");
		Console.WriteLine();
		Console.WriteLine("  Commit .github/roslynmcp.json so all project contributors benefit.");
		Console.WriteLine("  The hook guides agents to prefer roslyn_* tools for .cs files —");
		Console.WriteLine("  no file operations are blocked.");
		
		if(ExecutablePath is null) {
			
			Console.WriteLine();
			Console.WriteLine("  ⚠ 'dotnet roslynmcp hook' must be available in PATH for the hook to work.");
			Console.WriteLine("    Install globally: dotnet tool install -g RoslynMcp");
		}
		
		Console.WriteLine();
		Console.WriteLine($"  RoslynMcp v{CurrentVersion} — feedback & issues: https://github.com/MadQ/RoslynMcp");
		Console.WriteLine();
		
		return 0;
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
