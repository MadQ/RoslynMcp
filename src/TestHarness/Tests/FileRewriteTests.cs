using System.Text.Json.Nodes;

/// <summary>
///     Covers what a file keeps when the server rewrites it (#322). The tools that replace a
///     whole file do it through a temp file and a rename, so that a reader never sees a
///     half-written file. A rename, however, puts the temp file in the target's place with the
///     temp file's own attributes, and two things were lost that way:
///     <list type="bullet">
///         <item>
///             <b>Unix permissions.</b> The target came back with the default mode: an executable
///             script stopped being executable, a private file became readable by others. Backup
///             snapshots had the same problem from the start — a snapshot of a private file was
///             written with the default mode.
///         </item>
///         <item>
///             <b>Symbolic links.</b> A target that was a link was replaced by a regular file,
///             which silently cut it off from the file it pointed at.
///         </item>
///     </list>
///     <para>
///         The fixture is a throwaway directory (#274) with one file per situation: an executable
///         script (<c>0755</c>), a private file (<c>0600</c>), and a link <c>linked.txt</c> to
///         <c>shared/real.txt</c> — a link that stays inside the workspace, which the boundary
///         allows. Each test rewrites one of them through the real server with
///         <c>roslyn_write_file</c> (or restores it with <c>roslyn_local_history</c>) and then
///         looks at the file system directly. Nothing is mocked.
///     </para>
///     <para>
///         Permission bits do not exist on Windows, so those tests are reported as skipped there
///         and are covered by the Linux and macOS runs. The link test needs permission to create
///         a symbolic link; where the machine refuses, it is a skip locally and a failure on a
///         CI runner (see <see cref="Skip"/>).
///     </para>
/// </summary>
static class FileRewriteTests
{
	const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
		| UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
	
	const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
	
	const string NoModes = "Unix file modes do not exist on Windows";
	
	internal static TestGroup Build(TestContext ctx)
	{
		
		var fx = TestFixtures.NewAdhocDir("FileRewrite");
		
		// A name no other run uses, so the snapshots of this file can be found in the shared
		// backup store without mistaking another run's for them.
		var privateName = $"private-{Guid.NewGuid():N}.txt";
		
		var script  = fx.Write("run.sh",          "#!/bin/sh\necho one\n");
		var secret  = fx.Write(privateName,       "first\n");
		var real    = fx.Write("shared/real.txt", "behind the link\n");
		var link    = fx.PathOf("linked.txt");
		var linked  = false;
		
		if(!OperatingSystem.IsWindows()) {
			
			File.SetUnixFileMode(script, Executable);
			File.SetUnixFileMode(secret, Private);
		}
		
		try {
			
			File.CreateSymbolicLink(link, real);
			
			linked = true;
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
		
		// Set by the first test, used by the restore test.
		string? scriptBackupToken = null;
		
		// Calls a tool and returns its parsed result, or null when the call failed outright.
		async Task<JsonNode?> Call(string tool, object args)
		{
			
			await ctx.SendAsync(new { jsonrpc = "2.0", id = ctx.NextId(), method = "tools/call",
				@params = new { name = tool, arguments = args } });
			
			var resp = await ctx.ReceiveAsync();
			var text = resp?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
			
			if(text is null)
				
				return null;
			
			try { return JsonNode.Parse(text); }
			catch { return null; }
		}
		
		Task<JsonNode?> Write(string filePath, string content)
			=> Call("roslyn_write_file", new { filePath, content, projectPath = fx.Dir });
		
		var tests = new List<TestCase> {
			
			// The visible form of the bug: after an edit the script could no longer be run.
			new("file rewrite: roslyn_write_file keeps an executable file executable",
				async () => {
					
					if(OperatingSystem.IsWindows())
						
						return Skip.NotApplicable(NoModes);
					
					var result = await Write("run.sh", "#!/bin/sh\necho two\n");
					
					scriptBackupToken = result?["backupToken"]?.GetValue<string>();
					
					var mode = File.GetUnixFileMode(script);
					
					return result?["written"]?.GetValue<bool>() == true && mode == Executable && File.ReadAllText(script).Contains("echo two")
						? (true,  "PASS")
						: (false, $"FAIL  (mode after the write: {mode}; result: {result?.ToJsonString() ?? "none"})");
				}),
			
			// Restoring content must not also reset who may read or run the file.
			new("file rewrite: a local-history restore keeps the file's permissions",
				async () => {
					
					if(OperatingSystem.IsWindows())
						
						return Skip.NotApplicable(NoModes);
					
					if(scriptBackupToken is null)
						
						return (false, "FAIL  (no backup token from the write test)");
					
					var result = await Call("roslyn_local_history", new { action = "apply", token = scriptBackupToken, projectPath = fx.Dir });
					var mode   = File.GetUnixFileMode(script);
					
					return result?["restored"]?.GetValue<bool>() == true && mode == Executable && File.ReadAllText(script).Contains("echo one")
						? (true,  "PASS")
						: (false, $"FAIL  (mode after the restore: {mode}; result: {result?.ToJsonString() ?? "none"})");
				}),
			
			// A write takes two snapshots, before and after. Both hold the private file's content
			// and both must be as private as the file. The file itself is checked too.
			new("file rewrite: snapshots of a private file are not readable by others",
				async () => {
					
					if(OperatingSystem.IsWindows())
						
						return Skip.NotApplicable(NoModes);
					
					var result = await Write(privateName, "second\n");
					
					if(result?["written"]?.GetValue<bool>() != true)
						
						return (false, $"FAIL  (the write failed: {result?.ToJsonString() ?? "no result"})");
					
					// Plain loops, not LINQ: the platform analyzer does not carry the Windows check
					// above into a lambda and would flag the calls.
					var snapshots = 0;
					var exposed   = new List<string>();
					
					foreach(var path in Directory.EnumerateFiles(BackupRoot(), $"{privateName}_*.bak", SearchOption.AllDirectories)) {
						
						snapshots++;
						
						var snapshotMode = File.GetUnixFileMode(path);
						
						if(snapshotMode != Private)
							exposed.Add(snapshotMode.ToString());
					}
					
					var fileMode = File.GetUnixFileMode(secret);
					
					return snapshots >= 2 && !exposed.Any() && fileMode == Private
						? (true,  "PASS")
						: (false, $"FAIL  ({snapshots} snapshot(s), {exposed.Count} not private: {string.Join(", ", exposed)}; file: {fileMode})");
				}),
			
			// The link stays inside the workspace, so the boundary lets the write through. It
			// must then go to the file behind the link, and the link must still be a link.
			new("file rewrite: a write through a link changes the file behind it and keeps the link",
				async () => {
					
					if(!linked)
						
						return Skip.SetupUnavailable("this machine may not create symbolic links");
					
					var result = await Write("linked.txt", "written through the link\n");
					
					var stillLink = new FileInfo(link).LinkTarget is not null;
					var content   = File.ReadAllText(real);
					
					return result?["written"]?.GetValue<bool>() == true && stillLink && content.Contains("written through the link")
						? (true,  "PASS")
						: (false, $"FAIL  (link kept: {stillLink}; file behind it: '{content.Trim()}'; result: {result?.ToJsonString() ?? "none"})");
				}),
		};
		
		return new TestGroup($"File Rewrite ({tests.Count} tests)", tests, Teardown: () => {
			
			// The link first: deleting the tree must not reach through it.
			try {
				if(linked)
					File.Delete(link);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			
			fx.Dispose();
			
			return Task.CompletedTask;
		});
	}
	
	// Where the server under test keeps its snapshots: the override the server reads, else its
	// default under the local application data folder. The harness starts the server with its
	// own environment, so both see the same value.
	static string BackupRoot()
		=> Environment.GetEnvironmentVariable("ROSLYNMCP_BACKUP_PATH") is { Length: > 0 } configured
			? configured
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoslynMcp", "backups")
	;
}
