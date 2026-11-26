using System.Text.Json.Nodes;

static class LocalHistoryTests
{
	internal static async Task<TestGroup> BuildAsync(TestContext ctx)
	{
		
		// The fixture lives in a temp project (#274): original content on disk, then overwritten
		// via the server to produce a backup token before tests run.
		var fx              = TestFixtures.NewMsBuildProject("LocalHistory");
		var tempHistoryFile = "history.txt"
		;
		
		fx.Write(tempHistoryFile, "// original content\n");
		
		// For the two replace tools (#513): a file of its own for each case, so that one case
		// failing halfway leaves the other's file as it expects it, and what each held before.
		const string editedByText   = "EditedByText.cs";
		const string editedByNode   = "EditedByNode.cs";
		const string writeProtected = "WriteProtected.cs";
		const string editedOriginal = "class Edited\n{\n\tint One() => 1;\n}\n";
		
		fx.Write(editedByText, editedOriginal);
		fx.Write(editedByNode, editedOriginal.Replace("Edited", "EditedToo"));
		fx.Write(writeProtected, editedOriginal.Replace("Edited", "Protected"));
		
		// One edit through a replace tool, then an undo with nothing but the token the edit
		// answered. Both tools backed the file up and answered no token, so undoing took a
		// listing of the snapshots first; the other edit tools answered one. The file is read
		// from disk before and after: that the edit was written, and that the undo put back
		// what was there. A dry run writes nothing and has no token.
		async Task<(bool, string)> EditThenUndoAsync(string tool, string file, object dryRun, object edit, string before, string expectedAfterEdit)
		{
			var preview = await ctx.CallToolJsonAsync(tool, dryRun);
			var written = await ctx.CallToolJsonAsync(tool, edit);
			var token   = written?["backup_token"]?.GetValue<string>();
			var edited  = File.ReadAllText(fx.PathOf(file)).ReplaceLineEndings("\n");
			
			var undone   = token is null ? null : await ctx.CallToolJsonAsync("roslyn_local_history", new { action = "apply", token, projectPath = fx.Csproj });
			var restored = File.ReadAllText(fx.PathOf(file)).ReplaceLineEndings("\n");
			
			return preview?["applied"]?.GetValue<bool>() == false && preview["backup_token"] is null
				&& written?["applied"]?.GetValue<bool>() == true && token is { Length: > 0 }
				&& edited == expectedAfterEdit
				&& undone?["restored"]?.GetValue<bool>() == true
				&& restored == before
				? (true, "PASS")
				: (false, $"FAIL  (dry run: {preview?.ToJsonString() ?? "null"}; edit: {written?.ToJsonString() ?? "null"}; undo: {undone?.ToJsonString() ?? "null"}; after edit: {edited}; after undo: {restored})");
		}
		
		// Write new content via the server — this triggers BackupStore.Save and returns backup_token.
		string? historyToken = null
		;
		
		await ctx.SendAsync(new {
			
			jsonrpc = "2.0",
			id      = ctx.NextId(),
			method  = "tools/call",
			@params = new {
				
				name      = "roslyn_write_file",
				arguments = new {
					
					filePath    = tempHistoryFile,
					projectPath = fx.Csproj,
					content     = "// modified content\n"
				}
			}
		});
		
		var resp       = await ctx.ReceiveAsync();
		var content    = resp?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
		var setupData  = content is not null ? JsonNode.Parse(content) : null;
		historyToken   = setupData?["backupToken"]?.GetValue<string>();
		
		var tests = new List<TestCase> {
			
			new("roslyn_local_history: list backups for temp file",
				() => ctx.RunTestAsync(
					"roslyn_local_history",
					new { action = "list", filePath = tempHistoryFile, projectPath = fx.Csproj },
					data => data?["items"]?.AsArray().Count > 0 && data?["count"]?.GetValue<int>() > 0)),
			
			new("roslyn_local_history: preview backup token",
				() => ctx.RunTestAsync(
					"roslyn_local_history",
					new { action = "preview", token = historyToken ?? "invalid", projectPath = fx.Csproj },
					data => data?["token"] is not null && data?["absolutePath"] is not null)),
			
			new("roslyn_local_history: apply restores original content",
				() => ctx.RunTestAsync(
					"roslyn_local_history",
					new { action = "apply", token = historyToken ?? "invalid", projectPath = fx.Csproj },
					data => data?["restored"]?.GetValue<bool>() == true && data?["absolutePath"] is not null)),
			
			new("roslyn_replace_in_file: a write answers a backup token that undoes it, a dry run answers none",
				() => EditThenUndoAsync(
					"roslyn_replace_in_file",
					editedByText,
					new { filePath = editedByText, pattern = "=> 1;", replacement = "=> 2;", dryRun = true, projectPath = fx.Csproj },
					new { filePath = editedByText, pattern = "=> 1;", replacement = "=> 2;", projectPath = fx.Csproj },
					editedOriginal,
					editedOriginal.Replace("=> 1;", "=> 2;"))),
			
			new("roslyn_replace_in_code: a write answers a backup token that undoes it, a dry run answers none",
				() => EditThenUndoAsync(
					"roslyn_replace_in_code",
					editedByNode,
					new { filePath = editedByNode, nodeKind = "method", textPattern = "One", replacement = "int One() => 3;", dryRun = true, projectPath = fx.Csproj },
					new { filePath = editedByNode, nodeKind = "method", textPattern = "One", replacement = "int One() => 3;", projectPath = fx.Csproj },
					editedOriginal.Replace("Edited", "EditedToo"),
					editedOriginal.Replace("Edited", "EditedToo").Replace("=> 1;", "=> 3;"))),
			
			// A write that fails after the backup was saved: the file can be read and backed up,
			// and not written. The error is when the way back is wanted most, and its hint sent
			// the agent to list the snapshots; it now names the token, and the token is a real
			// one. Only where a read-only file refuses the write the tool makes: elsewhere the
			// write goes through, and there is no failure to look at.
			new("roslyn_replace_in_file: a write that fails after the backup names the backup's token in its hint", async () => {
				
				var path = fx.PathOf(writeProtected);
				
				File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
				
				try {
					
					var failed = await ctx.CallToolJsonAsync("roslyn_replace_in_file", new { filePath = writeProtected, pattern = "=> 1;", replacement = "=> 2;", projectPath = fx.Csproj });
					
					if(failed?["applied"]?.GetValue<bool>() == true)
						
						return Skip.NotApplicable("a read-only file does not refuse this write here");
					
					var hint  = failed?["hint"]?.GetValue<string>() ?? "";
					var token = System.Text.RegularExpressions.Regex.Match(hint, "has the token '([^']+)'").Groups[1].Value;
					
					var preview = token.Length == 0 ? null : await ctx.CallToolJsonAsync("roslyn_local_history", new { action = "preview", token, projectPath = fx.Csproj });
					
					return (failed?["error"]?.GetValue<string>() ?? "").StartsWith("Write failed")
						&& token.Length > 0
						&& preview?["token"]?.GetValue<string>() == token
						? (true, "PASS")
						: (false, $"FAIL  (edit: {failed?.ToJsonString() ?? "null"}; preview: {preview?.ToJsonString() ?? "null"})");
				}
				finally {
					
					// Or the fixture cannot be deleted at teardown.
					File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
				}
			}),
		};
		
		return new TestGroup($"Local History Tools ({tests.Count} tests)", tests, Teardown: () =>
		{
			
			fx.Dispose();
			
			return Task.CompletedTask;
		});
	}
}
