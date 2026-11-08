using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

internal static class CodeFixTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		async Task<(JsonNode? Data, string Text)> Call(string tool, object args)
		{
			await ctx.SendAsync(new {
				jsonrpc = "2.0",
				id      = ctx.NextId(),
				method  = "tools/call",
				@params = new { name = tool, arguments = args }
			});
			
			var response = await ctx.ReceiveAsync();
			
			// Text is the whole response; Data is its first block. They differ only for the
			// source-returning tools, which answer with a JSON header block and then raw source (#328).
			var blocks = response?["result"]?["content"]?.AsArray().Select(block => block?["text"]?.GetValue<string>() ?? string.Empty).ToArray() ?? [];
			var text   = string.Concat(blocks);
			JsonNode? data = null;
			
			try { data = JsonNode.Parse(blocks.FirstOrDefault() ?? string.Empty); }
			catch { }
			
			return (data, text);
		}
		
		static string Fixture(string marker) => $"class CodeFixFixture {{ {marker} value; }}\n";
		
		// Under TestFixtures.TempRoot, so a killed run's leftovers are swept by the next run (#274).
		// These fixtures test the bundled harness provider alone: the project file turns Roslyn's own
		// fixers off for the workspace (#389), which is also what checks that the opt-out works —
		// CS0246 would otherwise bring Roslyn's "Generate type" actions along.
		const string BuiltInOff = "{ \"codeActions\": { \"builtIn\": false } }\n";
		
		static string NewAdhocWorkspace()
		{
			var dir = TestFixtures.NewAdhocDir("CodeFix").Dir;
			
			File.WriteAllText(Path.Combine(dir, ".madq_roslynmcp.json"), BuiltInOff);
			
			return dir;
		}
		
		static void DeleteWorkspace(string path) => TestFixtures.DeleteTree(path);
		
		async Task<(JsonNode? Data, string Text)> PreviewAdhoc(
			string workspacePath,
			string marker,
			int? actionIndex = null,
			string fileName = "_CodeFixFixture_.cs")
		{
			var fixturePath = Path.Combine(workspacePath, fileName);
			
			if(!File.Exists(fixturePath))
				await File.WriteAllTextAsync(fixturePath, Fixture(marker));
			
			return await Call("roslyn_preview_code_fix", new {
				projectPath = workspacePath,
				filePath = fileName,
				line = 1,
				column = 24,
				diagnosticId = "CS0246",
				actionIndex
			});
		}
		
		async Task<(JsonNode? Data, string Text)> Apply(string token, string projectPath, string approval = "y") =>
			await Call("roslyn_apply_code_fix", new { token, approval, projectPath });
		
		static bool HasFileState(JsonNode? data, string pathSuffix, string state)
		{
			var files = data?["files"]?.AsArray();
			
			return files is not null && files.Any(file =>
				file?["path"]?.GetValue<string>().EndsWith(pathSuffix, StringComparison.OrdinalIgnoreCase) == true
				&& file?["state"]?.GetValue<string>() == state);
		}
		
		static bool EveryFileHasRecovery(JsonNode? data)
		{
			var files = data?["files"]?.AsArray();
			
			return files is not null
				&& files.Count > 0
				&& files.All(file => !string.IsNullOrWhiteSpace(file?["recovery"]?.GetValue<string>()));
		}
		
		async Task<(bool pass, string msg)> RunSingleWrite()
		{
			var workspacePath = NewAdhocWorkspace();
			var sw = Stopwatch.StartNew();
			
			try {
				
				var (preview, previewText) = await PreviewAdhoc(workspacePath, "TestSingleWrite");
				var token = preview?["token"]?.GetValue<string>();
				
				if(token is null)
					return (false, $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
				
				var (apply, applyText) = await Apply(token, workspacePath);
				var updated = await File.ReadAllTextAsync(Path.Combine(workspacePath, "_CodeFixFixture_.cs"));
				var pass = apply?["error"] is null
					&& apply?["filesWritten"]?.GetValue<int>() == 1
					&& HasFileState(apply, "_CodeFixFixture_.cs", "written")
					&& EveryFileHasRecovery(apply)
					&& updated.Contains("object value", StringComparison.Ordinal);
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (apply: {applyText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		async Task<(bool pass, string msg)> RunMultipleActionSelection()
		{
			var workspacePath = NewAdhocWorkspace();
			var sw = Stopwatch.StartNew();
			
			try {
				
				var (choices, choicesText) = await PreviewAdhoc(workspacePath, "TestMultipleActions");
				var actions = choices?["actions"]?.AsArray();
				
				if(choices?["token"] is not null || actions?.Count != 3)
					return (false, $"FAIL  (choices: {choicesText}) [{sw.ElapsedMilliseconds}ms]");
				
				var (invalid, _) = await PreviewAdhoc(workspacePath, "TestMultipleActions", 9);
				var (selected, selectedText) = await PreviewAdhoc(workspacePath, "TestMultipleActions", 1);
				var token = selected?["token"]?.GetValue<string>();
				var diff = selected?["diff"]?.GetValue<string>() ?? string.Empty;
				var pass = invalid?["error"]?.GetValue<string>() == "invalid action index"
					&& token is not null
					&& diff.Contains("string", StringComparison.Ordinal)
					&& selected?["actions"]?[0]?["index"]?.GetValue<int>() == 1;
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (selected: {selectedText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		async Task<(bool pass, string msg)> RunOperationShapeFailures()
		{
			var sw = Stopwatch.StartNew();
			var cases = new Dictionary<string, string> {
				["TestNoOperations"] = "unsupported operation shape",
				["TestCustomOperation"] = "unsupported operation shape",
				["TestMultipleOperations"] = "unsupported operation shape",
				["TestThrowOperations"] = "code action calculation failed"
			};
			
			foreach(var (marker, expectedError) in cases) {
				
				var workspacePath = NewAdhocWorkspace();
				
				try {
					
					var (data, text) = await PreviewAdhoc(workspacePath, marker);
					
					if(data?["error"]?.GetValue<string>() != expectedError)
						return (false, $"FAIL  ({marker}: {text}) [{sw.ElapsedMilliseconds}ms]");
				}
				finally {
					DeleteWorkspace(workspacePath);
				}
			}
			
			return (true, $"PASS  [{sw.ElapsedMilliseconds}ms]");
		}
		
		async Task<(bool pass, string msg)> RunInvalidApprovalRetainsToken()
		{
			var workspacePath = NewAdhocWorkspace();
			var sw = Stopwatch.StartNew();
			
			try {
				
				var (preview, previewText) = await PreviewAdhoc(workspacePath, "TestSingleWrite");
				var token = preview?["token"]?.GetValue<string>();
				
				if(token is null)
					return (false, $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
				
				var (invalid, _) = await Apply(token, workspacePath, "session");
				var (applied, applyText) = await Apply(token, workspacePath);
				var pass = invalid?["error"]?.GetValue<string>() == "invalid approval"
					&& applied?["error"] is null
					&& HasFileState(applied, "_CodeFixFixture_.cs", "written");
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (retry: {applyText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		async Task<(bool pass, string msg)> RunRejectConsumesToken()
		{
			var workspacePath = NewAdhocWorkspace();
			var sw = Stopwatch.StartNew();
			
			try {
				
				var (preview, previewText) = await PreviewAdhoc(workspacePath, "TestSingleWrite");
				var token = preview?["token"]?.GetValue<string>();
				
				if(token is null)
					return (false, $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
				
				var original = await File.ReadAllTextAsync(Path.Combine(workspacePath, "_CodeFixFixture_.cs"));
				var (rejected, _) = await Apply(token, workspacePath, "n");
				var (retry, _) = await Apply(token, workspacePath);
				var current = await File.ReadAllTextAsync(Path.Combine(workspacePath, "_CodeFixFixture_.cs"));
				var pass = rejected?["error"]?.GetValue<string>() == "rejected"
					&& retry?["error"]?.GetValue<string>() == "token unavailable"
					&& current == original;
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (token was reusable or file changed) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		async Task<(bool pass, string msg)> RunWorkspaceMismatchRetainsToken()
		{
			var workspacePath = NewAdhocWorkspace();
			var wrongWorkspace = NewAdhocWorkspace();
			var sw = Stopwatch.StartNew();
			
			try {
				
				await File.WriteAllTextAsync(
					Path.Combine(wrongWorkspace, "_Other_.cs"),
					"class OtherWorkspace { }\n");
				var (preview, previewText) = await PreviewAdhoc(workspacePath, "TestSingleWrite");
				var token = preview?["token"]?.GetValue<string>();
				
				if(token is null)
					return (false, $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
				
				var (mismatch, _) = await Apply(token, wrongWorkspace);
				var (applied, applyText) = await Apply(token, workspacePath);
				var pass = mismatch?["error"]?.GetValue<string>() == "workspace mismatch"
					&& applied?["error"] is null
					&& HasFileState(applied, "_CodeFixFixture_.cs", "written");
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (retry: {applyText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
				DeleteWorkspace(wrongWorkspace);
			}
		}
		
		async Task<(bool pass, string msg)> RunConcurrentTokenClaim()
		{
			var workspacePath = NewAdhocWorkspace();
			var sw = Stopwatch.StartNew();
			
			try {
				
				var (preview, previewText) = await PreviewAdhoc(workspacePath, "TestSingleWrite");
				var token = preview?["token"]?.GetValue<string>();
				
				if(token is null)
					return (false, $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
				
				await ctx.SendAsync(new {
					jsonrpc = "2.0",
					id = ctx.NextId(),
					method = "tools/call",
					@params = new {
						name = "roslyn_apply_code_fix",
						arguments = new { token, approval = "y", projectPath = workspacePath }
					}
				});
				await ctx.SendAsync(new {
					jsonrpc = "2.0",
					id = ctx.NextId(),
					method = "tools/call",
					@params = new {
						name = "roslyn_apply_code_fix",
						arguments = new { token, approval = "y", projectPath = workspacePath }
					}
				});
				
				var firstText = (await ctx.ReceiveAsync())?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
				var secondText = (await ctx.ReceiveAsync())?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
				var first = JsonNode.Parse(firstText);
				var second = JsonNode.Parse(secondText);
				var errors = new[] {
					first?["error"]?.GetValue<string>(),
					second?["error"]?.GetValue<string>()
				};
				var pass = errors.Count(error => error is null) == 1
					&& errors.Count(error => error == "token unavailable") == 1;
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  ({firstText} | {secondText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		async Task<(bool pass, string msg)> RunStaleModifiedRetainsToken()
		{
			var workspacePath = NewAdhocWorkspace();
			var fixturePath = Path.Combine(workspacePath, "_CodeFixFixture_.cs");
			var sw = Stopwatch.StartNew();
			
			try {
				
				var original = Fixture("TestSingleWrite");
				var (preview, previewText) = await PreviewAdhoc(workspacePath, "TestSingleWrite");
				var token = preview?["token"]?.GetValue<string>();
				
				if(token is null)
					return (false, $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
				
				const string concurrentEdit = "// concurrent edit\n";
				await File.WriteAllTextAsync(fixturePath, concurrentEdit);
				var (stale, _) = await Apply(token, workspacePath);
				await File.WriteAllTextAsync(fixturePath, original);
				var (retry, retryText) = await Apply(token, workspacePath);
				var pass = stale?["error"]?.GetValue<string>() == "stale preview"
					&& retry?["error"] is null
					&& HasFileState(retry, "_CodeFixFixture_.cs", "written");
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (retry: {retryText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		async Task<(bool pass, string msg)> RunStaleDeletedRetainsToken()
		{
			var workspacePath = NewAdhocWorkspace();
			var fixturePath = Path.Combine(workspacePath, "_CodeFixFixture_.cs");
			var sw = Stopwatch.StartNew();
			
			try {
				
				var original = Fixture("TestSingleWrite");
				var (preview, previewText) = await PreviewAdhoc(workspacePath, "TestSingleWrite");
				var token = preview?["token"]?.GetValue<string>();
				
				if(token is null)
					return (false, $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
				
				File.Delete(fixturePath);
				var (stale, _) = await Apply(token, workspacePath);
				await File.WriteAllTextAsync(fixturePath, original);
				var (retry, retryText) = await Apply(token, workspacePath);
				var pass = stale?["error"]?.GetValue<string>() == "stale preview"
					&& retry?["error"] is null;
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (retry: {retryText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		async Task<(bool pass, string msg)> RunMultiWrite()
		{
			var workspacePath = NewAdhocWorkspace();
			var sw = Stopwatch.StartNew();
			
			try {
				
				await File.WriteAllTextAsync(
					Path.Combine(workspacePath, "_CodeFixSecond_.cs"),
					"class SecondFile { string state = \"before\"; }\n");
				var (preview, previewText) = await PreviewAdhoc(workspacePath, "TestMultiWrite");
				var token = preview?["token"]?.GetValue<string>();
				
				if(token is null)
					return (false, $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
				
				var (apply, applyText) = await Apply(token, workspacePath);
				var second = await File.ReadAllTextAsync(Path.Combine(workspacePath, "_CodeFixSecond_.cs"));
				var pass = apply?["error"] is null
					&& apply?["filesWritten"]?.GetValue<int>() == 2
					&& HasFileState(apply, "_CodeFixFixture_.cs", "written")
					&& HasFileState(apply, "_CodeFixSecond_.cs", "written")
					&& EveryFileHasRecovery(apply)
					&& second.Contains("after", StringComparison.Ordinal);
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (apply: {applyText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		async Task<(bool pass, string msg)> RunWrongWorkflowTokenRejected()
		{
			var workspacePath = NewAdhocWorkspace();
			var fixturePath = Path.Combine(workspacePath, "_CodeFixFixture_.cs");
			var sw = Stopwatch.StartNew();
			
			try {
				
				await File.WriteAllTextAsync(fixturePath, "class CodeFixFixture { }\n");
				var (renamePreview, renameText) = await Call("roslyn_preview_rename", new {
					projectPath = workspacePath,
					symbolName = "CodeFixFixture",
					newName = "RenamedCodeFixFixture"
				});
				var token = renamePreview?["token"]?.GetValue<string>();
				
				if(token is null)
					return (false, $"FAIL  (rename preview: {renameText}) [{sw.ElapsedMilliseconds}ms]");
				
				// A rename token must be refused by the code-fix apply path — the workflow discriminator
				// rejects it at TryBeginApply ("token unavailable") — and, critically, must NOT be
				// consumed, so its rightful owner (apply_rename) can still apply it afterward.
				var (apply, applyText) = await Apply(token, workspacePath);
				var afterReject = await File.ReadAllTextAsync(fixturePath);
				var rejected = apply?["error"]?.GetValue<string>() == "token unavailable"
					&& afterReject == "class CodeFixFixture { }\n";
				
				var (rename, renameApplyText) = await Call("roslyn_apply_rename", new { token, approval = "y", projectPath = workspacePath });
				var afterRename = await File.ReadAllTextAsync(fixturePath);
				var stillValid = rename?["error"] is null
					&& afterRename.Contains("RenamedCodeFixFixture", StringComparison.Ordinal);
				
				var pass = rejected && stillValid;
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (code-fix apply: {applyText}; rename apply: {renameApplyText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		async Task<(bool pass, string msg)> RunCodeFixTokenRejectedByRename()
		{
			var workspacePath = NewAdhocWorkspace();
			var fixturePath = Path.Combine(workspacePath, "_CodeFixFixture_.cs");
			var sw = Stopwatch.StartNew();
			
			try {
				
				var (preview, previewText) = await PreviewAdhoc(workspacePath, "TestSingleWrite");
				var token = preview?["token"]?.GetValue<string>();
				
				if(token is null)
					return (false, $"FAIL  (code-fix preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
				
				// The opposite direction: a code-fix token must be refused by the rename apply path
				// ("token unavailable" — #264 moved rename apply onto the same 3-state approval
				// lifecycle as code fix) without being consumed, so the code-fix apply still completes.
				var (rename, renameText) = await Call("roslyn_apply_rename", new { token, approval = "y", projectPath = workspacePath });
				var afterReject = await File.ReadAllTextAsync(fixturePath);
				var rejected = rename?["error"]?.GetValue<string>() == "token unavailable"
					&& afterReject == Fixture("TestSingleWrite");
				
				var (apply, applyText) = await Apply(token, workspacePath);
				var afterApply = await File.ReadAllTextAsync(fixturePath);
				var stillValid = apply?["error"] is null
					&& apply?["filesWritten"]?.GetValue<int>() == 1
					&& afterApply.Contains("object value", StringComparison.Ordinal);
				
				var pass = rejected && stillValid;
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (rename apply: {renameText}; code-fix apply: {applyText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		async Task<(bool pass, string msg)> RunGeneratedFileRejectedAtPreview()
		{
			var workspacePath = NewAdhocWorkspace();
			var sw = Stopwatch.StartNew();
			
			try {
				
				var (preview, previewText) = await PreviewAdhoc(
					workspacePath,
					"TestSingleWrite",
					fileName: "_CodeFixFixture_.g.cs");
				var pass = preview?["error"]?.GetValue<string>() == "unsupported code-fix change"
					&& preview?["token"] is null
					&& preview?["message"]?.GetValue<string>().Contains("generated source file", StringComparison.Ordinal) == true;
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		async Task<(bool pass, string msg)> RunReadOnlyFileRejectedAtPreview()
		{
			var workspacePath = NewAdhocWorkspace();
			var fixturePath = Path.Combine(workspacePath, "_CodeFixFixture_.cs");
			var sw = Stopwatch.StartNew();
			
			try {
				
				await File.WriteAllTextAsync(fixturePath, Fixture("TestSingleWrite"));
				File.SetAttributes(fixturePath, File.GetAttributes(fixturePath) | FileAttributes.ReadOnly);
				var (preview, previewText) = await PreviewAdhoc(workspacePath, "TestSingleWrite");
				var pass = preview?["error"]?.GetValue<string>() == "unsupported code-fix change"
					&& preview?["token"] is null
					&& preview?["message"]?.GetValue<string>().Contains("writable source files", StringComparison.Ordinal) == true;
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				if(File.Exists(fixturePath))
					File.SetAttributes(fixturePath, File.GetAttributes(fixturePath) & ~FileAttributes.ReadOnly);
				
				DeleteWorkspace(workspacePath);
			}
		}

		// #349: the preview no longer opens a file for writing to see whether that works — such
		// an open collided with whoever had the file open at that moment, the server's own reads
		// included, and refused the fix over a handle gone a moment later. So a file someone
		// holds without sharing it for writing or deleting (as a program that has it open
		// usually does) is previewed like any other: the preview must return a token, which is
		// the #349 failure turned around. The apply then cannot replace it: its atomic rename
		// needs the holder to share for deleting. It must list the file as untouched, with
		// recovery guidance, and leave it exactly as it was — the guarantee the dropped check
		// was standing in for. One handle, held across both calls. Windows only: elsewhere
		// sharing is advisory and the rename succeeds, so there is nothing to check.
		async Task<(bool pass, string msg)> RunHeldFileLeftAloneByApply()
		{
			if(!OperatingSystem.IsWindows())
				
				return Skip.NotApplicable("file sharing is advisory on this platform; the rename succeeds");
			
			var workspacePath = NewAdhocWorkspace();
			var fixturePath = Path.Combine(workspacePath, "_CodeFixFixture_.cs");
			var sw = Stopwatch.StartNew();
			
			try {
				
				await File.WriteAllTextAsync(fixturePath, Fixture("TestSingleWrite"));
				
				(JsonNode? Data, string Text) preview;
				(JsonNode? Data, string Text) apply = (null, "not run");
				
				using(new FileStream(fixturePath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
					
					preview = await PreviewAdhoc(workspacePath, "TestSingleWrite");
					
					if(preview.Data?["token"]?.GetValue<string>() is { } token)
						apply = await Apply(token, workspacePath);
				}
				
				var content = await File.ReadAllTextAsync(fixturePath);
				var listed  = apply.Data?["files"]?.AsArray().FirstOrDefault(file =>
					file?["path"]?.GetValue<string>().EndsWith("_CodeFixFixture_.cs", StringComparison.OrdinalIgnoreCase) == true);
				var pass = preview.Data?["token"] is not null
					&& listed is not null
					&& listed["state"]?.GetValue<string>() == "untouched"
					&& EveryFileHasRecovery(apply.Data)
					&& content == Fixture("TestSingleWrite");
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (preview: {preview.Text}; apply: {apply.Text}; file now: {content}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		// #349, the other side: a handle that shares the file for reading and deleting — the way
		// every read in the server opens a file (FileReader.ReadSharing) — does not block the
		// apply. The apply replaces the file by renaming a temp file over it, which on Windows
		// fails against any open handle that does not share for deleting; before #349 the
		// server's own reads were such handles, and the replace got through only on the write
		// retry. Held across the apply, the handle stands in for a server read in progress; the
		// test above is the same handle without Delete, which the rename cannot get past.
		async Task<(bool pass, string msg)> RunReadDeleteSharedHandleAllowsApply()
		{
			if(!OperatingSystem.IsWindows())
				
				return Skip.NotApplicable("file sharing is advisory on this platform; nothing to check");
			
			var workspacePath = NewAdhocWorkspace();
			var fixturePath = Path.Combine(workspacePath, "_CodeFixFixture_.cs");
			var sw = Stopwatch.StartNew();
			
			try {
				
				await File.WriteAllTextAsync(fixturePath, Fixture("TestSingleWrite"));
				
				var preview = await PreviewAdhoc(workspacePath, "TestSingleWrite");
				
				if(preview.Data?["token"]?.GetValue<string>() is not { } token)
					
					return (false, $"FAIL  (preview: {preview.Text}) [{sw.ElapsedMilliseconds}ms]");
				
				(JsonNode? Data, string Text) apply;
				string held;
				
				using(var reader = new StreamReader(new FileStream(fixturePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))) {
					
					apply = await Apply(token, workspacePath);
					
					// The handle still reads what it opened: a rename replaces the name, not the
					// file the handle has.
					held = await reader.ReadToEndAsync();
				}
				
				var updated = await File.ReadAllTextAsync(fixturePath);
				var pass = apply.Data?["error"] is null
					&& HasFileState(apply.Data, "_CodeFixFixture_.cs", "written")
					&& updated.Contains("object value", StringComparison.Ordinal)
					&& held.Contains("TestSingleWrite value", StringComparison.Ordinal);
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (apply: {apply.Text}; held handle read: {held}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		

		async Task<(bool pass, string msg)> RunTrackedNonCSharpDocuments()
		{
			var sw = Stopwatch.StartNew();

			async Task<(bool pass, string message)> RunCase(
				string marker,
				string documentPath,
				bool expectApply,
				string expectedText)
			{
				using var fixture = TestFixtures.NewMsBuildProject(
					$"CodeFixTracked_{marker}",
					extraProjectXml: """
						<ItemGroup>
						  <AdditionalFiles Include="Tracked.txt" />
						</ItemGroup>
						""");

				fixture.Write("Tracked.txt", "probe = before\n");
				fixture.Write(".madq_roslynmcp.json", BuiltInOff);
				fixture.Write(".editorconfig", "root = true\n# before\n");
				fixture.Write("Probe.cs", $"class CodeFixFixture {{ {marker} value; }}\n");
				await fixture.WarmAsync(ctx);

				var (preview, previewText) = await Call("roslyn_preview_code_fix", new {
					projectPath = fixture.Csproj,
					filePath = "Probe.cs",
					line = 1,
					column = 24,
					diagnosticId = "CS0246"
				});
				var token = preview?["token"]?.GetValue<string>();

				if(!expectApply) {
					var rejected = preview?["error"]?.GetValue<string>() == "unsupported code-fix change"
						&& token is null;

					return (rejected,
						rejected
							? $"PASS  ({marker}: add/delete rejected)"
							: $"FAIL  ({marker} preview: {previewText})");
				}

				if(token is null)
					return (false, $"FAIL  ({marker} preview: {previewText})");

				// The preview diff is what the user approves, so a non-C# write missing from it is a failure.
				var diff = preview?["diff"]?.GetValue<string>() ?? string.Empty;
				var expectedChangedLine = expectedText.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1];

				if(!diff.Contains(documentPath, StringComparison.OrdinalIgnoreCase)
					|| !diff.Contains($"+{expectedChangedLine}", StringComparison.Ordinal))
					return (false, $"FAIL  ({marker} preview diff omits {documentPath}: {previewText})");

				var (apply, applyText) = await Apply(token, fixture.Csproj!);
				var actual = await File.ReadAllTextAsync(fixture.PathOf(documentPath));
				var (read, readText) = await Call("roslyn_read_file", new {
					projectPath = fixture.Csproj,
					filePath = documentPath
				});
				var applied = apply?["error"] is null
					&& HasFileState(apply, documentPath, "written")
					&& actual == expectedText
					&& read?["source"]?.GetValue<string>() == "roslyn"
					&& readText.Contains(expectedChangedLine, StringComparison.Ordinal);

				return (applied,
					applied
						? $"PASS  ({marker}: preview/apply/read verified)"
						: $"FAIL  ({marker} apply: {applyText}; read: {readText})");
			}

			var cases = new[] {
				("TestModifyAdditionalDocument", "Tracked.txt", true, "probe = after\n"),
				("TestModifyAnalyzerConfig", ".editorconfig", true, "root = true\n[*.cs]\ndotnet_diagnostic.CS0168.severity = error\n"),
				("TestAnalyzerConfigWithOptions", ".editorconfig", false, "root = true\n# before\n"),
				("TestAdditionalDocument", "Tracked.txt", false, "probe = before\n"),
				("TestDeleteAdditionalDocument", "Tracked.txt", false, "probe = before\n"),
				("TestAnalyzerConfig", ".editorconfig", false, "root = true\n# before\n"),
				("TestDeleteAnalyzerConfig", ".editorconfig", false, "root = true\n# before\n")
			};

			foreach(var (marker, documentPath, expectApply, expectedText) in cases) {
				var result = await RunCase(marker, documentPath, expectApply, expectedText);

				if(!result.pass)
					return (false, $"FAIL  ({result.message}) [{sw.ElapsedMilliseconds}ms]");
			}

			return (true, $"PASS  [{sw.ElapsedMilliseconds}ms]");
		}
		
		async Task<(bool pass, string msg)> RunUnsupportedSolutionShapesRejected()
		{
			var sw = Stopwatch.StartNew();
			var markers = new[] {
				"TestAdditionalDocument",
				"TestAnalyzerConfig",
				"TestAddedProject",
				"TestMetadataReference",
				"TestProjectOptions"
			};
			
			foreach(var marker in markers) {
				
				var workspacePath = NewAdhocWorkspace();
				
				try {
					
					var (preview, previewText) = await PreviewAdhoc(workspacePath, marker);
					
					if(preview?["error"]?.GetValue<string>() != "unsupported code-fix change"
						|| preview?["token"] is not null)
						return (false, $"FAIL  ({marker}: {previewText}) [{sw.ElapsedMilliseconds}ms]");
				}
				finally {
					DeleteWorkspace(workspacePath);
				}
			}
			
			return (true, $"PASS  [{sw.ElapsedMilliseconds}ms]");
		}
		
		async Task<(bool pass, string msg)> RunEncodingPreserved()
		{
			var sw = Stopwatch.StartNew();
			var cases = new (string Name, Encoding Encoding)[] {
				("UTF-8 without BOM", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)),
				("UTF-8 with BOM", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true)),
				("UTF-16 LE with BOM", new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true))
			};
			
			foreach(var testCase in cases) {
				
				var workspacePath = NewAdhocWorkspace();
				var fixturePath = Path.Combine(workspacePath, "_CodeFixFixture_.cs");
				
				try {
					
					static byte[] Encode(string text, Encoding encoding)
					{
						var preamble = encoding.GetPreamble();
						var content = encoding.GetBytes(text);
						var bytes = new byte[preamble.Length + content.Length];
						preamble.CopyTo(bytes, 0);
						content.CopyTo(bytes, preamble.Length);
						
						return bytes;
					}
					
					await File.WriteAllBytesAsync(fixturePath, Encode(Fixture("TestSingleWrite"), testCase.Encoding));
					var (preview, previewText) = await PreviewAdhoc(workspacePath, "TestSingleWrite");
					var token = preview?["token"]?.GetValue<string>();
					
					if(token is null)
						return (false, $"FAIL  ({testCase.Name} preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
					
					var (apply, applyText) = await Apply(token, workspacePath);
					var actualBytes = await File.ReadAllBytesAsync(fixturePath);
					var expectedBytes = Encode(Fixture("object"), testCase.Encoding);
					
					if(apply?["error"] is not null || !actualBytes.AsSpan().SequenceEqual(expectedBytes))
						return (false, $"FAIL  ({testCase.Name} apply: {applyText}) [{sw.ElapsedMilliseconds}ms]");
				}
				finally {
					DeleteWorkspace(workspacePath);
				}
			}
			
			return (true, $"PASS  [{sw.ElapsedMilliseconds}ms]");
		}
		
		async Task<(bool pass, string msg)> RunExternalPathRejectedAtPreview()
		{
			var workspacePath = NewAdhocWorkspace();
			var externalPath = Path.Combine(
				Path.GetDirectoryName(workspacePath)!,
				$"_{Path.GetFileName(workspacePath)}_External.cs");
			var sw = Stopwatch.StartNew();
			
			try {
				
				const string externalContents = "// external user file\n";
				await File.WriteAllTextAsync(externalPath, externalContents);
				var (preview, previewText) = await PreviewAdhoc(workspacePath, "TestExternalPath");
				var currentExternalContents = await File.ReadAllTextAsync(externalPath);
				var pass = preview?["error"]?.GetValue<string>() == "unsupported code-fix change"
					&& preview?["token"] is null
					&& currentExternalContents == externalContents;
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				if(File.Exists(externalPath))
					File.Delete(externalPath);
				
				DeleteWorkspace(workspacePath);
			}
		}
		
		async Task<(bool pass, string msg)> RunCreateDeleteApplied()
		{
			var workspacePath = NewAdhocWorkspace();
			var removedPath = Path.Combine(workspacePath, "_CodeFixRemoved_.cs");
			var createdPath = Path.Combine(workspacePath, "_CodeFixCreated_.cs");
			var sw = Stopwatch.StartNew();
			
			try {
				
				const string removedContents = "class RemovedByCodeFix { }\n";
				await File.WriteAllTextAsync(removedPath, removedContents);
				var (preview, previewText) = await PreviewAdhoc(workspacePath, "TestMixed");
				var token = preview?["token"]?.GetValue<string>();
				var previewNamesFiles = preview?["createdFiles"]?.AsArray().Any(file => file?.GetValue<string>().EndsWith("_CodeFixCreated_.cs", StringComparison.Ordinal) == true) == true
					&& preview?["deletedFiles"]?.AsArray().Any(file => file?.GetValue<string>().EndsWith("_CodeFixRemoved_.cs", StringComparison.Ordinal) == true) == true
					&& !File.Exists(createdPath)
					&& await File.ReadAllTextAsync(removedPath) == removedContents;
				
				if(token is null || !previewNamesFiles)
					return (false, $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
				
				// The preview wrote nothing; the apply creates one file and deletes the other (#389).
				var (apply, applyText) = await Apply(token, workspacePath);
				var pass = apply?["error"] is null
					&& HasFileState(apply, "_CodeFixCreated_.cs", "written")
					&& HasFileState(apply, "_CodeFixRemoved_.cs", "deleted")
					&& apply?["createdFiles"]?.AsArray().Count == 1
					&& apply?["deletedFiles"]?.AsArray().Count == 1
					&& File.Exists(createdPath)
					&& (await File.ReadAllTextAsync(createdPath)).Contains("class CreatedByCodeFix", StringComparison.Ordinal)
					&& !File.Exists(removedPath);
				
				previewText = $"apply: {applyText}";
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		// Exercises the production MSBuildWorkspace path instead of the AdhocWorkspace used by
		// most boundary tests. The exact-byte assertion also proves that the physical apply path
		// persists the reviewed output without relying on an in-memory workspace update alone.
		async Task<(bool pass, string msg)> RunMsBuildSingleWrite()
		{
			var workspacePath = NewAdhocWorkspace();
			var projectPath = Path.Combine(workspacePath, "CodeFixFixture.csproj");
			var fixturePath = Path.Combine(workspacePath, "_CodeFixFixture_.cs");
			var sw = Stopwatch.StartNew();
			
			try {
				
				const string projectFile = """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
""";
				
				await File.WriteAllTextAsync(projectPath, projectFile);
				await File.WriteAllTextAsync(fixturePath, Fixture("TestSingleWrite"));
				
				var (preview, previewText) = await Call("roslyn_preview_code_fix", new {
					projectPath,
					filePath = fixturePath,
					line = 1,
					column = 24,
					diagnosticId = "CS0246"
				});
				var token = preview?["token"]?.GetValue<string>();
				
				if(token is null)
					return (false, $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
				
				var (apply, applyText) = await Apply(token, projectPath);
				var actualBytes = await File.ReadAllBytesAsync(fixturePath);
				var expectedBytes = Encoding.UTF8.GetBytes(Fixture("object"));
				var (read, readText) = await Call("roslyn_read_file", new {
					projectPath,
					filePath = "_CodeFixFixture_.cs"
				});
				var pass = apply?["error"] is null
					&& apply?["filesWritten"]?.GetValue<int>() == 1
					&& HasFileState(apply, "_CodeFixFixture_.cs", "written")
					&& actualBytes.AsSpan().SequenceEqual(expectedBytes)
					&& read is not null
					&& readText.Contains("object value", StringComparison.Ordinal);
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (apply: {applyText}; read: {readText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		// Builds two real MSBuild projects that both include one physical source file. A fix
		// calculated through either Roslyn document must be rejected because writing the shared
		// path would also mutate the other project without a separately reviewed change.
		async Task<(bool pass, string msg)> RunLinkedFileRejectedAtPreview()
		{
			var workspacePath = NewAdhocWorkspace();
			var projectAPath = Path.Combine(workspacePath, "ProjectA");
			var projectBPath = Path.Combine(workspacePath, "ProjectB");
			var projectFile = Path.Combine(projectAPath, "ProjectA.csproj");
			var sharedPath = Path.Combine(projectAPath, "Shared.cs");
			var originalBytes = Encoding.UTF8.GetBytes(Fixture("TestSingleWrite"));
			var sw = Stopwatch.StartNew();
			
			try {
				
				Directory.CreateDirectory(projectAPath);
				Directory.CreateDirectory(projectBPath);
				
				const string projectA = """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../ProjectB/ProjectB.csproj" />
  </ItemGroup>
</Project>
""";
				const string projectB = """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="../ProjectA/Shared.cs" Link="Shared.cs" />
  </ItemGroup>
</Project>
""";
				
				await File.WriteAllTextAsync(projectFile, projectA);
				await File.WriteAllTextAsync(Path.Combine(projectBPath, "ProjectB.csproj"), projectB);
				await File.WriteAllBytesAsync(sharedPath, originalBytes);
				
				var (preview, previewText) = await Call("roslyn_preview_code_fix", new {
					projectPath = projectFile,
					filePath = sharedPath,
					line = 1,
					column = 24,
					diagnosticId = "CS0246"
				});
				var currentBytes = await File.ReadAllBytesAsync(sharedPath);
				var message = preview?["message"]?.GetValue<string>() ?? string.Empty;
				var pass = preview?["error"]?.GetValue<string>() == "unsupported code-fix change"
					&& preview?["token"] is null
					&& message.Contains("linked source file", StringComparison.Ordinal)
					&& currentBytes.AsSpan().SequenceEqual(originalBytes);
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		

		async Task<(bool pass, string msg)> RunUnbundledDiagnosticRejected()
		{
			var workspacePath = NewAdhocWorkspace();
			var fixturePath = Path.Combine(workspacePath, "_CodeFixFixture_.cs");
			var sw = Stopwatch.StartNew();
			
			try {
				
				await File.WriteAllTextAsync(
					fixturePath,
					"class CodeFixFixture { void M() { MissingName(); } }\n");
				var (preview, previewText) = await Call("roslyn_preview_code_fix", new {
					projectPath = workspacePath,
					filePath = "_CodeFixFixture_.cs",
					line = 1,
					column = 35,
					diagnosticId = "CS0103"
				});
				var message = preview?["message"]?.GetValue<string>() ?? string.Empty;
				var pass = preview?["error"]?.GetValue<string>() == "no fixes"
					&& preview?["token"] is null
					&& message.Contains("available to the server", StringComparison.Ordinal);
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		// Without the fixture's opt-out, Roslyn's own fixers are offered beside the bundled one (#389):
		// CS0246 brings "Generate type" from the Features assemblies, through the server's composition.
		// The bundled provider stays first, so the other tests' action indices would hold either way.
		async Task<(bool pass, string msg)> RunBuiltInFixersOffered()
		{
			var workspacePath = TestFixtures.NewAdhocDir("CodeFixBuiltIn").Dir;
			var sw = Stopwatch.StartNew();
			
			try {
				
				var (preview, previewText) = await PreviewAdhoc(workspacePath, "TestSingleWrite");
				var actions = preview?["actions"]?.AsArray();
				var providers = actions?.Select(action => action?["provider"]?.GetValue<string>() ?? string.Empty).ToArray() ?? [];
				var pass = preview?["token"] is null
					&& providers.Length > 1
					&& providers[0] == "RoslynMcp.Tools.CodeFixTestProvider"
					&& providers.Any(provider => provider.EndsWith(".GenerateTypeCodeFixProvider", StringComparison.Ordinal));
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		

		List<TestCase> tests = [
			new("unbundled diagnostic is rejected without a token", RunUnbundledDiagnosticRejected),
			new("Roslyn's own fixers are offered unless the project file turns them off", RunBuiltInFixersOffered),
			new("single write reports exact state and recovery", RunSingleWrite),
			new("MSBuild workspace previews and applies an existing-file fix", RunMsBuildSingleWrite),
			new("linked source file is rejected without changing physical bytes", RunLinkedFileRejectedAtPreview),
			new("multiple actions require and honor actionIndex", RunMultipleActionSelection),
			new("unsupported and throwing operations are structured", RunOperationShapeFailures),
			new("invalid approval leaves token pending", RunInvalidApprovalRetainsToken),
			new("rejection consumes token without changing files", RunRejectConsumesToken),
			new("workspace mismatch leaves token pending", RunWorkspaceMismatchRetainsToken),
			new("concurrent apply claims token exactly once", RunConcurrentTokenClaim),
			new("modified stale file survives and token retries", RunStaleModifiedRetainsToken),
			new("deleted stale file can be recreated and retried", RunStaleDeletedRetainsToken),
			new("multi-file write reports both verified files", RunMultiWrite),
			new("rename token is rejected by code-fix apply and survives", RunWrongWorkflowTokenRejected),
			new("code-fix token is rejected by rename apply and survives", RunCodeFixTokenRejectedByRename),
			new("generated source files are rejected during preview", RunGeneratedFileRejectedAtPreview),
			new("read-only source files are rejected during preview", RunReadOnlyFileRejectedAtPreview),
			new("a held file is previewed, and left alone by the apply", RunHeldFileLeftAloneByApply),
			new("a reader that shares for deleting does not block the apply's atomic replace", RunReadDeleteSharedHandleAllowsApply),
			new("tracked additional and analyzer-config documents support safe edits only", RunTrackedNonCSharpDocuments),
			new("unsupported solution change shapes are rejected", RunUnsupportedSolutionShapesRejected),
			new("existing file encoding and BOM are preserved", RunEncodingPreserved),
			new("external file changes are rejected during preview", RunExternalPathRejectedAtPreview),
			new("create and delete changes are previewed and applied", RunCreateDeleteApplied)
		];
		
		return new TestGroup($"Code Fix Tools ({tests.Count} tests)", tests);
	}
}
