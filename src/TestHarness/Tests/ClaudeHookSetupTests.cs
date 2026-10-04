using System.Reflection;
using System.Text.Json.Nodes;

/// <summary>
///     Covers where and how <c>setup</c> installs the Claude Code pre-tool-use hook (#319). It
///     used to write the hook into <c>~/.claude.json</c>, a file Claude Code never reads hooks
///     from, so the hook reported as installed never ran. The install now targets the settings
///     file and removes the stale entry from the old location.
///     <para>
///         Both steps edit a user's configuration files, so what they must <b>not</b> touch
///         matters as much as what they write. The tests call the two real methods —
///         <c>ClaudeCodeClient.UpsertHookIn</c> and <c>RemoveHookFrom</c> — by reflection on
///         the server assembly the harness built, against files in a temp directory. The real
///         home directory is never read or written. Nothing is mocked: each test writes a JSON
///         file, runs the method, and reads the file back.
///     </para>
///     <para>
///         The inputs are the states a real machine can be in: no settings file yet; a settings
///         file with unrelated settings and other people's hooks; an entry of ours with a
///         hand-edited absolute path and <c>--log</c> (which a rerun must keep); an entry under
///         a legacy command name that no longer exists (which a rerun must replace); and a file
///         that is not valid JSON (which must be left exactly as it is).
///     </para>
/// </summary>
static class ClaudeHookSetupTests
{
	const string HookCommand = "madq-roslynmcp hook";
	
	static Assembly? serverAssembly;
	
	internal static TestGroup Build(TestContext ctx)
	{
		
		var dir = Path.Combine(TestFixtures.TempRoot, $"ClaudeHookSetup.{Guid.NewGuid():N}");
		
		Directory.CreateDirectory(dir);
		
		var tests = new List<TestCase> {
			
			// No settings file: the install creates it, including the missing directory, with
			// one entry scoped to the file tools and a short timeout.
			Case("fresh install creates the settings file with one scoped entry", () => {
				
				var path = Path.Combine(dir, "fresh", "settings.json");
				
				if(!Upsert(ctx, path))
					return "UpsertHookIn returned false";
				
				var groups = Groups(path);
				
				if(groups is not [JsonObject group])
					return $"expected one PreToolUse group, got {groups?.Count.ToString() ?? "none"}";
				
				var hook = group["hooks"]?[0];
				
				return group["matcher"]?.GetValue<string>() == "Read|Grep|Glob|Edit|MultiEdit|Write"
					&& hook?["type"]?.GetValue<string>() == "command"
					&& hook["command"]?.GetValue<string>() == HookCommand
					&& hook["timeout"]?.GetValue<int>() == 5
					? null
					: $"unexpected entry: {group.ToJsonString()}";
			}),
			
			// Unrelated settings, another tool's PreToolUse hook, and a different hook event
			// must all survive. The comment and trailing comma are the lenient JSON that
			// hand-edited settings files often contain.
			Case("install preserves unrelated settings and other hooks", () => {
				
				var path = Path.Combine(dir, "preserve.json");
				
				File.WriteAllText(path, """
					{
					  // hand-edited
					  "model": "opus",
					  "hooks": {
					    "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "guard.sh && echo 'ok'" } ] } ],
					    "Stop":       [ { "hooks": [ { "type": "command", "command": "notify.sh" } ] } ],
					  },
					}
					""");
				
				if(!Upsert(ctx, path))
					return "UpsertHookIn returned false";
				
				var root   = JsonNode.Parse(File.ReadAllText(path));
				var groups = Groups(path);
				
				return root?["model"]?.GetValue<string>() == "opus"
					&& root["hooks"]?["Stop"]?[0]?["hooks"]?[0]?["command"]?.GetValue<string>() == "notify.sh"
					&& groups?.Count == 2
					&& Commands(groups).SequenceEqual(["guard.sh && echo 'ok'", HookCommand])
					// Written as typed, not as && — the file is edited by hand.
					&& File.ReadAllText(path).Contains("guard.sh && echo 'ok'")
					? null
					: $"unexpected content: {root?.ToJsonString()}";
			}),
			
			// A second run must not add a second entry, must keep a hand-edited command (an
			// absolute path plus --log), and must still narrow the old catch-all matcher.
			Case("rerun keeps one entry and a customised command, and narrows the matcher", () => {
				
				const string custom = "/c/Users/someone/.dotnet/tools/madq-roslynmcp.exe hook --log";
				
				var path = Path.Combine(dir, "rerun.json");
				
				File.WriteAllText(path, $$"""
					{ "hooks": { "PreToolUse": [ { "matcher": "", "hooks": [ { "type": "command", "command": "{{custom}}" } ] } ] } }
					""");
				
				if(!Upsert(ctx, path) || !Upsert(ctx, path))
					return "UpsertHookIn returned false";
				
				var groups = Groups(path);
				
				return groups is [JsonObject group]
					&& Commands(groups).SequenceEqual([custom])
					&& group["matcher"]?.GetValue<string>() == "Read|Grep|Glob|Edit|MultiEdit|Write"
					? null
					: $"unexpected groups: {groups?.ToJsonString()}";
			}),
			
			// A profile directory with a space forces a quoted executable path. A plain whitespace
			// split would cut it at the space, miss the file name, and append a second entry
			// instead of recognising this one. An empty settings file (0 bytes) is the other
			// awkward-but-real state covered here: it must load as "no settings", not fail.
			Case("a quoted path with a space is recognised and kept; a blank file loads as empty", () => {
				
				// Built with the platform's own separator: a backslash is not a directory separator
				// outside Windows, so a hard-coded Windows path would not yield the file name there.
				var exe    = Path.Combine($"{Path.DirectorySeparatorChar}Users", "John Doe", ".dotnet", "tools", "madq-roslynmcp.exe");
				var quoted = $"\"{exe}\" hook --log";
				
				var path  = Path.Combine(dir, "quoted.json");
				var blank = Path.Combine(dir, "blank.json");
				
				File.WriteAllText(path, new JsonObject {
					
					["hooks"] = new JsonObject {
						
						["PreToolUse"] = new JsonArray {
							
							new JsonObject { ["matcher"] = "", ["hooks"] = new JsonArray { new JsonObject { ["type"] = "command", ["command"] = quoted } } }
						}
					}
				}.ToJsonString());
				
				File.WriteAllText(blank, "");
				
				if(!Upsert(ctx, path) || !Upsert(ctx, blank))
					return "UpsertHookIn returned false";
				
				var groups      = Groups(path);
				var blankGroups = Groups(blank);
				
				return groups is not null && Commands(groups).SequenceEqual([quoted])
					&& blankGroups is not null && Commands(blankGroups).SequenceEqual([HookCommand])
					? null
					: $"unexpected groups: {groups?.ToJsonString()} / {blankGroups?.ToJsonString()}";
			}),
			
			// Single quotes are how a POSIX shell user quotes the same path. The second file guards
			// the rule that makes that safe: a single quote opens a run only at the start of a
			// token, so an unquoted path with an apostrophe in a directory name (no spaces) is
			// still read as one token ending in the tool's file name. Both must be recognised as
			// the existing hook and kept as written — one entry each, not two.
			Case("a single-quoted path is recognised and kept; an apostrophe inside a path is not a quote", () => {
				
				var spaced     = Path.Combine($"{Path.DirectorySeparatorChar}Users", "John Doe", ".dotnet", "tools", "madq-roslynmcp");
				var apostrophe = Path.Combine($"{Path.DirectorySeparatorChar}Users", "O'Brien", ".dotnet", "tools", "madq-roslynmcp");
				
				var singleQuoted = $"'{spaced}' hook --log";
				var unquoted     = $"{apostrophe} hook";
				
				var quotedPath     = Path.Combine(dir, "single-quoted.json");
				var apostrophePath = Path.Combine(dir, "apostrophe.json");
				
				WriteHookEntry(quotedPath,     singleQuoted);
				WriteHookEntry(apostrophePath, unquoted);
				
				if(!Upsert(ctx, quotedPath) || !Upsert(ctx, apostrophePath))
					return "UpsertHookIn returned false";
				
				var quotedGroups     = Groups(quotedPath);
				var apostropheGroups = Groups(apostrophePath);
				
				return quotedGroups is not null && Commands(quotedGroups).SequenceEqual([singleQuoted])
					&& apostropheGroups is not null && Commands(apostropheGroups).SequenceEqual([unquoted])
					? null
					: $"unexpected groups: {quotedGroups?.ToJsonString()} / {apostropheGroups?.ToJsonString()}";
			}),
			
			// Only the hook subcommand is our advisor entry. Some other invocation of this tool in
			// a hook slot must be left alone — and above all must not be carried over as the
			// advisor command, which would report a working hook that cannot run.
			Case("a non-hook invocation of this tool is neither removed nor reused", () => {
				
				var path = Path.Combine(dir, "non-hook.json");
				
				File.WriteAllText(path, """
					{ "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "madq-roslynmcp verify" } ] } ] } }
					""");
				
				if(!Remove(ctx, path) || !Upsert(ctx, path))
					return "a method returned false";
				
				var groups = Groups(path);
				
				return groups is not null && Commands(groups).SequenceEqual(["madq-roslynmcp verify", HookCommand])
					? null
					: $"unexpected groups: {groups?.ToJsonString()}";
			}),
			
			// A settings file kept in a dotfiles repository is a symlink. The install must write
			// through the link: the link survives and the file it points at receives the hook.
			// Creating a symlink needs a privilege Windows grants only to administrators or in
			// developer mode; where it is refused the case cannot be set up and is passed over.
			Case("a symlinked settings file is written through, not replaced", () => {
				
				var real = Path.Combine(dir, "dotfiles-settings.json");
				var link = Path.Combine(dir, "linked-settings.json");
				
				File.WriteAllText(real, "{ \"model\": \"opus\" }");
				
				try { File.CreateSymbolicLink(link, real); }
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { return null; }
				
				if(!Upsert(ctx, link))
					return "UpsertHookIn returned false";
				
				var groups = Groups(real);
				
				return new FileInfo(link).LinkTarget is not null
					&& groups is not null && Commands(groups).SequenceEqual([HookCommand])
					? null
					: $"link kept: {new FileInfo(link).LinkTarget is not null}; target groups: {groups?.ToJsonString() ?? "none"}";
			}),
			
			// A matcher group someone else left with an empty hooks array is not ours to delete,
			// in either the install or the cleanup.
			Case("a foreign group with no hooks is left in place", () => {
				
				var path = Path.Combine(dir, "empty-group.json");
				
				File.WriteAllText(path, """
					{ "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [] } ] } }
					""");
				
				if(!Remove(ctx, path) || !Upsert(ctx, path))
					return "a method returned false";
				
				var groups = Groups(path);
				
				return groups?.Count == 2 && groups[0]?["matcher"]?.GetValue<string>() == "Bash"
					? null
					: $"unexpected groups: {groups?.ToJsonString()}";
			}),
			
			// "dotnet roslynmcp hook" was the command before the package was renamed; that
			// command no longer exists, so it is replaced rather than kept.
			Case("an entry under a legacy command name is replaced", () => {
				
				var path = Path.Combine(dir, "legacy-name.json");
				
				File.WriteAllText(path, """
					{ "hooks": { "PreToolUse": [ { "matcher": "", "hooks": [ { "type": "command", "command": "dotnet roslynmcp hook" } ] } ] } }
					""");
				
				if(!Upsert(ctx, path))
					return "UpsertHookIn returned false";
				
				var groups = Groups(path);
				
				return groups is not null && Commands(groups).SequenceEqual([HookCommand])
					? null
					: $"unexpected groups: {groups?.ToJsonString()}";
			}),
			
			// The stale entry in ~/.claude.json: the hook goes, the now-empty "hooks" object goes
			// with it, and the MCP server registration in the same file is untouched.
			Case("cleanup removes our hook from the old location and keeps the rest of the file", () => {
				
				var path = Path.Combine(dir, "old-location.json");
				
				File.WriteAllText(path, """
					{
					  "mcpServers": { "MadQ.RoslynMcp": { "command": "madq-roslynmcp", "args": ["."] } },
					  "hooks": { "PreToolUse": [ { "matcher": "", "hooks": [ { "type": "command", "command": "madq-roslynmcp hook" } ] } ] }
					}
					""");
				
				if(!Remove(ctx, path))
					return "RemoveHookFrom returned false";
				
				var root = JsonNode.Parse(File.ReadAllText(path));
				
				return root?["hooks"] is null
					&& root?["mcpServers"]?["MadQ.RoslynMcp"]?["command"]?.GetValue<string>() == "madq-roslynmcp"
					? null
					: $"unexpected content: {root?.ToJsonString()}";
			}),
			
			// A foreign hook sharing our matcher group must stay, and so must its containers.
			Case("cleanup leaves a foreign hook in the same group alone", () => {
				
				var path = Path.Combine(dir, "shared-group.json");
				
				File.WriteAllText(path, """
					{ "hooks": { "PreToolUse": [ { "matcher": "", "hooks": [
					    { "type": "command", "command": "guard.sh" },
					    { "type": "command", "command": "madq-roslynmcp hook" } ] } ] } }
					""");
				
				if(!Remove(ctx, path))
					return "RemoveHookFrom returned false";
				
				var groups = Groups(path);
				
				return groups is not null && Commands(groups).SequenceEqual(["guard.sh"])
					? null
					: $"unexpected groups: {groups?.ToJsonString() ?? "none"}";
			}),
			
			// The install and the cleanup are separate outcomes. When the old file cannot be
			// cleaned (here: not valid JSON), the hook is still installed, but the result must say
			// the stale entry remains — setup prints a different message for it — and the old
			// file must be left as it was. A clean old file gives the plain "Installed".
			Case("a cleanup that fails is reported without undoing the install", () => {
				
				const string broken = "{ \"hooks\": oops";
				
				var settings  = Path.Combine(dir, "report-settings.json");
				var badLegacy = Path.Combine(dir, "report-bad-legacy.json");
				var noLegacy  = Path.Combine(dir, "report-no-legacy.json");
				
				File.WriteAllText(badLegacy, broken);
				
				var install     = Method(ctx, "InstallHook");
				var withStale   = install.Invoke(null, [settings, badLegacy, HookCommand])!.ToString();
				var withNothing = install.Invoke(null, [settings, noLegacy,  HookCommand])!.ToString();
				var groups      = Groups(settings);
				
				return withStale == "InstalledStaleEntryRemains"
					&& withNothing == "Installed"
					&& File.ReadAllText(badLegacy) == broken
					&& groups is not null && Commands(groups).SequenceEqual([HookCommand])
					? null
					: $"results: {withStale} / {withNothing}; groups: {groups?.ToJsonString()}";
			}),
			
			// A file that cannot be parsed is reported as a failure and left byte-for-byte as it
			// was — rewriting it would destroy the user's settings.
			Case("an unparseable settings file is left untouched", () => {
				
				const string broken = "{ \"model\": \"opus\", oops";
				
				var path = Path.Combine(dir, "broken.json");
				
				File.WriteAllText(path, broken);
				
				if(Upsert(ctx, path))
					return "UpsertHookIn reported success on invalid JSON";
				
				return File.ReadAllText(path) == broken ? null : "the file was modified";
			}),
			
			// A duplicate key is well-formed enough to parse lazily, and then throws when the
			// object is first indexed. It must be a plain "could not load" for both methods — no
			// exception (the Case wrapper would report one), and the file left as it was. The
			// duplicate sits in a nested object because that is where a lazy load would miss it.
			Case("a settings file with a duplicate key is rejected, not crashed on", () => {
				
				const string duplicated = "{ \"hooks\": { \"PreToolUse\": [], \"PreToolUse\": [] } }";
				
				var path = Path.Combine(dir, "duplicate.json");
				
				File.WriteAllText(path, duplicated);
				
				if(Upsert(ctx, path) || Remove(ctx, path))
					return "a method reported success on a duplicate key";
				
				return File.ReadAllText(path) == duplicated ? null : "the file was modified";
			}),
		};
		
		return new TestGroup($"Claude Code Hook Setup ({tests.Count} tests)", tests, Teardown: () => {
			
			try { Directory.Delete(dir, recursive: true); }
			catch(IOException) { }
			
			return Task.CompletedTask;
		});
	}
	
	// A test body returns null on success or a description of what was wrong.
	static TestCase Case(string name, Func<string?> body)
		=> new($"claude hook setup: {name}", () => {
			
			string? failure;
			
			try { failure = body(); }
			catch(Exception ex) { failure = $"{ex.GetType().Name}: {ex.Message}"; }
			
			return Task.FromResult(failure is null ? (true, "PASS") : (false, $"FAIL  ({failure})"));
		})
	;
	
	static bool Upsert(TestContext ctx, string path) => (bool) Method(ctx, "UpsertHookIn").Invoke(null, [path, HookCommand])!;
	
	static bool Remove(TestContext ctx, string path) => (bool) Method(ctx, "RemoveHookFrom").Invoke(null, [path])!;
	
	static MethodInfo Method(TestContext ctx, string name)
		=> LoadServerAssembly(ctx)
			.GetType("RoslynMcp.Cli.ClaudeCodeClient", throwOnError: true)!
			.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!
	;
	
	// A settings file holding one catch-all PreToolUse group with one command hook. Built as a
	// JsonObject so quotes and backslashes in the command are escaped correctly.
	static void WriteHookEntry(string path, string command)
		=> File.WriteAllText(path, new JsonObject {
			
			["hooks"] = new JsonObject {
				
				["PreToolUse"] = new JsonArray {
					
					new JsonObject { ["matcher"] = "", ["hooks"] = new JsonArray { new JsonObject { ["type"] = "command", ["command"] = command } } }
				}
			}
		}.ToJsonString())
	;
	
	static JsonArray? Groups(string path) => JsonNode.Parse(File.ReadAllText(path))?["hooks"]?["PreToolUse"] as JsonArray;
	
	// Every hook command in the PreToolUse groups, in file order.
	static IEnumerable<string> Commands(JsonArray groups)
		=> groups
			.SelectMany(group => group?["hooks"] as JsonArray ?? [])
			.Select(hook => hook?["command"]?.GetValue<string>() ?? "")
	;
	
	// Same load as VsVersionPinTests: the server assembly the harness just built.
	static Assembly LoadServerAssembly(TestContext ctx)
	{
		if(serverAssembly is not null)
			return serverAssembly;
		
		var projectDir   = Path.GetDirectoryName(ctx.ServerProj)!;
		var assemblyPath = Path.Combine(projectDir, "bin", "Debug", "net10.0", "RoslynMcp.dll");
		
		serverAssembly = Assembly.LoadFrom(assemblyPath);
		
		return serverAssembly;
	}
}
