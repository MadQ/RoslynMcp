using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

/// <summary>
///     Covers what <c>setup-project</c> does to the Copilot CLI hook file,
///     <c>.github/hooks/roslynmcp.json</c> (#505). That file is committed, so a run must not
///     leave it changed when it had nothing to change: a rerun used to write the file again as
///     the JSON serializer formats it, without the line break at its end, and every checkout
///     the command ran in showed the file as modified.
///     <para>
///         The tests call the real method, <c>SetupProjectCommand.InstallHook</c>, by reflection
///         on the server assembly the harness built, against files in a temp directory. Nothing
///         is mocked: each test writes a file, runs the method, and reads the file and its
///         backup back. The method returns a tuple of what it did and the backup's path, read
///         here through <see cref="ITuple"/> because the outcome's enum is private to the server.
///     </para>
///     <para>
///         The inputs are the states a repository can be in: no hook file; the file a previous
///         run wrote and somebody committed, with and without a final line break and in another
///         indentation than the serializer's; a file from before #310 with the entry under the
///         old event, beside somebody else's hook; and a file that is not JSON.
///     </para>
///     <para>
///         The last two tests run the whole command as a process, because the server config,
///         <c>.madq_roslynmcp.json</c>, is under the same rule and its logic reads the console:
///         a repository whose two files already say what a rerun comes to must come out of it
///         without a changed byte, and a config that does need a change must still get it.
///     </para>
/// </summary>
static class ProjectHookSetupTests
{
	const string HookCommand = "madq-roslynmcp hook";
	
	// The hook file as it is committed in a repository: two-space indentation, line feeds, a
	// final line break. Not the serializer's output on Windows, which has CRLF.
	const string Committed = "{\n  \"version\": 1,\n  \"hooks\": {\n    \"postToolUse\": [\n      {\n        \"type\": \"command\",\n        \"powershell\": \"madq-roslynmcp hook\",\n        \"bash\": \"madq-roslynmcp hook\",\n        \"timeoutSec\": 5\n      }\n    ]\n  }\n}\n";
	
	internal static TestGroup Build(TestContext ctx)
	{
		var dir = Path.Combine(TestFixtures.TempRoot, $"ProjectHookSetup.{Guid.NewGuid():N}");
		
		Directory.CreateDirectory(dir);
		
		var tests = new List<TestCase> {
			
			// No hook file: the run creates one entry under postToolUse, makes no backup (there
			// is nothing to back up), and the file ends with a line break, which is what keeps a
			// formatter or an editor from changing it later.
			Case("a new hook file has one entry and ends with a line break", () => {
				
				var path = Path.Combine(dir, "fresh.json");
				
				var (outcome, backup) = Install(ctx, path);
				var text              = File.ReadAllText(path);
				
				return outcome == "Created"
					&& backup is null
					&& !File.Exists(path + ".roslynmcp.bak")
					&& text.EndsWith('\n')
					&& Entries(path, "postToolUse") is [JsonObject entry]
					&& entry["powershell"]?.GetValue<string>() == HookCommand
					&& entry["bash"]?.GetValue<string>() == HookCommand
					? null
					: $"outcome {outcome}, backup {backup ?? "none"}, content: {text}";
			}),
			
			// The case of #505: the committed file already says everything the run would write.
			// It must come back byte for byte (the bytes are compared, since a rewrite with CRLF
			// or without the final line break parses the same), with no backup beside it.
			Case("a rerun leaves a file that already holds the hook byte for byte, without a backup", () => {
				
				var path = Path.Combine(dir, "rerun.json");
				
				File.WriteAllText(path, Committed);
				
				var before            = File.ReadAllBytes(path);
				var (outcome, backup) = Install(ctx, path);
				
				return outcome == "Unchanged"
					&& backup is null
					&& !File.Exists(path + ".roslynmcp.bak")
					&& File.ReadAllBytes(path).AsSpan().SequenceEqual(before)
					? null
					: $"outcome {outcome}, backup {backup ?? "none"}, content: {File.ReadAllText(path)}";
			}),
			
			// "Unchanged" is about what the file says, not how it is written: one line, no final
			// line break, keys in another order. A run that normalised such a file would be the
			// same unasked-for diff in a repository that formats its JSON differently.
			Case("a file that says the same in another form is left as it is", () => {
				
				const string compact = "{\"hooks\":{\"postToolUse\":[{\"timeoutSec\":5,\"bash\":\"madq-roslynmcp hook\",\"powershell\":\"madq-roslynmcp hook\",\"type\":\"command\"}]},\"version\":1}";
				
				var path = Path.Combine(dir, "compact.json");
				
				File.WriteAllText(path, compact);
				
				var (outcome, _) = Install(ctx, path);
				
				return outcome == "Unchanged" && File.ReadAllText(path) == compact
					? null
					: $"outcome {outcome}, content: {File.ReadAllText(path)}";
			}),
			
			// A file that does need a change is still changed: the entry a version before #310
			// left under preToolUse moves to postToolUse, somebody else's hook beside it stays,
			// the backup holds the file as it was, and the new file ends with a line break. This
			// guards the other side of the fix, a comparison that calls everything unchanged.
			Case("a file with the entry under the old event is rewritten, with a backup", () => {
				
				const string legacy = "{ \"version\": 1, \"hooks\": { \"preToolUse\": [ { \"type\": \"command\", \"bash\": \"lint.sh\" }, { \"type\": \"command\", \"powershell\": \"madq-roslynmcp hook\", \"bash\": \"madq-roslynmcp hook\" } ] } }";
				
				var path = Path.Combine(dir, "legacy.json");
				
				File.WriteAllText(path, legacy);
				
				var (outcome, backup) = Install(ctx, path);
				var text              = File.ReadAllText(path);
				
				return outcome == "Updated"
					&& backup == path + ".roslynmcp.bak"
					&& File.ReadAllText(backup) == legacy
					&& text.EndsWith('\n')
					&& Entries(path, "preToolUse") is [JsonObject other]
					&& other["bash"]?.GetValue<string>() == "lint.sh"
					&& Entries(path, "postToolUse") is [JsonObject ours]
					&& ours["bash"]?.GetValue<string>() == HookCommand
					? null
					: $"outcome {outcome}, backup {backup ?? "none"}, content: {text}";
			}),
			
			// RoslynMcp's entry stays where it is. The merge used to take it out and append it, so
			// a file with somebody else's hook after it was reordered by the first rerun: a change
			// nobody asked for, in a committed file. With the current command in first place the
			// file is unchanged; with a command form of an earlier version there ("dotnet
			// roslynmcp hook") it is rewritten, and the new entry is still first.
			Case("the entry keeps its place before somebody else's hook", () => {
				
				const string current = "{ \"version\": 1, \"hooks\": { \"postToolUse\": [ { \"type\": \"command\", \"powershell\": \"madq-roslynmcp hook\", \"bash\": \"madq-roslynmcp hook\", \"timeoutSec\": 5 }, { \"type\": \"command\", \"bash\": \"lint.sh\" } ] } }";
				
				var path = Path.Combine(dir, "first.json");
				
				File.WriteAllText(path, current);
				
				var (outcome, _) = Install(ctx, path);
				
				if(outcome != "Unchanged" || File.ReadAllText(path) != current)
					return $"current command first: outcome {outcome}, content: {File.ReadAllText(path)}";
				
				File.WriteAllText(path, current.Replace("madq-roslynmcp hook", "dotnet roslynmcp hook"));
				
				(outcome, _) = Install(ctx, path);
				
				return outcome == "Updated"
					&& Entries(path, "postToolUse") is [JsonObject ours, JsonObject other]
					&& ours["bash"]?.GetValue<string>() == HookCommand
					&& other["bash"]?.GetValue<string>() == "lint.sh"
					? null
					: $"earlier command first: outcome {outcome}, content: {File.ReadAllText(path)}";
			}),
			
			// A file that is not JSON cannot be merged into, and it cannot be "unchanged" either:
			// it is replaced by a fresh hook file, and the backup keeps what was there.
			Case("a file that is not JSON is replaced, and kept in the backup", () => {
				
				const string broken = "{ \"hooks\": ";
				
				var path = Path.Combine(dir, "broken.json");
				
				File.WriteAllText(path, broken);
				
				var (outcome, backup) = Install(ctx, path);
				
				return outcome == "Updated"
					&& backup is not null
					&& File.ReadAllText(backup) == broken
					&& Entries(path, "postToolUse") is [JsonObject]
					? null
					: $"outcome {outcome}, backup {backup ?? "none"}, content: {File.ReadAllText(path)}";
			}),
		};
		
		// The whole command, as a user reruns it, on a repository that has both of its files
		// committed: the hook file, and a server config written by hand, with a comment and without
		// a final line break. Every prompt is answered with Enter. Neither file may change by a
		// byte and no backup may appear, since that is exactly the state `git status` reports as
		// modified. The config file is the reason this runs the process: its prompts read the
		// console, so its logic cannot be called by reflection as InstallHook is.
		tests.Add(Case("a rerun of setup-project with every answer left as it is changes no file", () => {
			
			const string config = "{\n  // team settings\n  \"version\": 1,\n  \"elicit\": false,\n  \"workspace\": \"sdk\"\n}";
			
			var repo       = Path.Combine(dir, "rerun-repo");
			var hookPath   = Path.Combine(repo, ".github", "hooks", "roslynmcp.json");
			var configPath = Path.Combine(repo, ".madq_roslynmcp.json");
			
			Directory.CreateDirectory(Path.GetDirectoryName(hookPath)!);
			File.WriteAllText(hookPath, Committed);
			File.WriteAllText(configPath, config);
			
			var exitCode = RunSetupProject(ctx, repo);
			
			return exitCode == 0
				&& File.ReadAllText(hookPath) == Committed
				&& File.ReadAllText(configPath) == config
				&& Directory.GetFiles(repo, "*.bak", SearchOption.AllDirectories).Length == 0
				? null
				: $"exit code {exitCode}; hook: {File.ReadAllText(hookPath)}; config: {File.ReadAllText(configPath)}; backups: {string.Join(", ", Directory.GetFiles(repo, "*.bak", SearchOption.AllDirectories))}";
		}));
		
		// The other side for the config file: one that lacks a setting the command always writes
		// (elicit) is not what the answers come to, so it is written, with what it held kept
		// (workspace), a final line break, and the old text in the backup. Guards against a
		// comparison that calls every config unchanged.
		tests.Add(Case("a config file that lacks a setting is written, with a backup and a final line break", () => {
			
			const string config = "{ \"version\": 1, \"workspace\": \"sdk\" }";
			
			var repo       = Path.Combine(dir, "change-repo");
			var configPath = Path.Combine(repo, ".madq_roslynmcp.json");
			
			Directory.CreateDirectory(repo);
			File.WriteAllText(configPath, config);
			
			var exitCode = RunSetupProject(ctx, repo);
			var text     = File.ReadAllText(configPath);
			var root     = JsonNode.Parse(text);
			var backup   = configPath + ".roslynmcp.bak";
			
			return exitCode == 0
				&& text.EndsWith('\n')
				&& root?["elicit"]?.GetValue<bool>() == false
				&& root["workspace"]?.GetValue<string>() == "sdk"
				&& File.Exists(backup)
				&& File.ReadAllText(backup) == config
				? null
				: $"exit code {exitCode}; config: {text}";
		}));
		
		return new TestGroup($"Project Hook Setup ({tests.Count} tests)", tests, Teardown: () => {
			
			// Best effort: a file still held open, or one made read-only, must not abort the run.
			try { Directory.Delete(dir, recursive: true); }
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			
			return Task.CompletedTask;
		});
	}
	
	// A test body returns null on success or a description of what was wrong.
	static TestCase Case(string name, Func<string?> body)
		=> new($"project hook setup: {name}", () => {
			
			string? failure;
			
			try { failure = body(); }
			catch(Exception ex) { failure = $"{ex.GetType().Name}: {ex.Message}"; }
			
			return Task.FromResult(failure is null ? (true, "PASS") : (false, $"FAIL  ({failure})"));
		})
	;
	
	// Runs SetupProjectCommand.InstallHook on the file. Its result is (HookFileOutcome, string?
	// backup path); the outcome comes back as the enum member's name.
	static (string Outcome, string? Backup) Install(TestContext ctx, string path)
	{
		var method = ServerAssembly.Load(ctx)
			.GetType("RoslynMcp.Cli.SetupProjectCommand", throwOnError: true)!
			.GetMethod("InstallHook", BindingFlags.Static | BindingFlags.NonPublic)!;
		
		var result = (ITuple) method.Invoke(null, [path, HookCommand])!;
		
		return (result[0]!.ToString()!, (string?) result[1]);
	}
	
	// The entries of one hook event in the file, or null when the event is not there.
	static JsonArray? Entries(string path, string eventName) => JsonNode.Parse(File.ReadAllText(path))?["hooks"]?[eventName] as JsonArray;
	
	/// <summary>
	///     Runs <c>setup-project</c> on the already-built server in <paramref name="repo"/>, a
	///     directory made to look like a repository root by an empty <c>.git</c> directory in it
	///     (the command only looks for the name). Standard input is closed at once, so every prompt
	///     reads end of input, which the command takes as Enter: each setting keeps its current
	///     value. That is the rerun of #505, where nobody changes anything. Returns the exit code,
	///     or -1 when the process did not exit within a minute.
	/// </summary>
	static int RunSetupProject(TestContext ctx, string repo)
	{
		Directory.CreateDirectory(Path.Combine(repo, ".git"));
		
		var psi = new ProcessStartInfo("dotnet") {
			
			Arguments              = $"run --no-build --no-launch-profile --project \"{ctx.ServerProj}\" -f net10.0 -- setup-project",
			WorkingDirectory       = repo,
			RedirectStandardInput  = true,
			RedirectStandardOutput = true,
			RedirectStandardError  = true,
			UseShellExecute        = false,
		};
		
		using var process = Process.Start(psi)!;
		
		// Both drained, so that the prompts the command prints can never fill a pipe and stall it.
		process.OutputDataReceived += (_, _) => { };
		process.ErrorDataReceived  += (_, _) => { };
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		process.StandardInput.Close();
		
		if(process.WaitForExit(TimeSpan.FromSeconds(60)))
			return process.ExitCode;
		
		process.Kill(entireProcessTree: true);
		
		return -1;
	}
}
