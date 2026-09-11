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
			var text = response?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
			JsonNode? data = null;
			
			try { data = JsonNode.Parse(text); }
			catch { }
			
			return (data, text);
		}
		
		static string Fixture(string marker) => $"class CodeFixFixture {{ {marker} value; }}\n";
		
		// Under TestFixtures.TempRoot, so a killed run's leftovers are swept by the next run (#274).
		static string NewAdhocWorkspace() => TestFixtures.NewAdhocDir("CodeFix").Dir;
		
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
				// ("token not found") without being consumed, so the code-fix apply still completes.
				var (rename, renameText) = await Call("roslyn_apply_rename", new { token, approval = "y", projectPath = workspacePath });
				var afterReject = await File.ReadAllTextAsync(fixturePath);
				var rejected = rename?["error"]?.GetValue<string>() == "token not found"
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
		
		async Task<(bool pass, string msg)> RunCreateDeleteRejectedAtPreview()
		{
			var workspacePath = NewAdhocWorkspace();
			var removedPath = Path.Combine(workspacePath, "_CodeFixRemoved_.cs");
			var createdPath = Path.Combine(workspacePath, "_CodeFixCreated_.cs");
			var sw = Stopwatch.StartNew();
			
			try {
				
				const string removedContents = "class RemovedByCodeFix { }\n";
				await File.WriteAllTextAsync(removedPath, removedContents);
				var (preview, previewText) = await PreviewAdhoc(workspacePath, "TestMixed");
				var currentRemovedContents = await File.ReadAllTextAsync(removedPath);
				var pass = preview?["error"]?.GetValue<string>() == "unsupported code-fix change"
					&& preview?["token"] is null
					&& !File.Exists(createdPath)
					&& currentRemovedContents == removedContents;
				
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
					&& message.Contains("bundled with RoslynMcp", StringComparison.Ordinal);
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		return new TestGroup("Code Fix Tools", [
			new("unbundled diagnostic is rejected without a token", RunUnbundledDiagnosticRejected),
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
			new("unsupported solution change shapes are rejected", RunUnsupportedSolutionShapesRejected),
			new("existing file encoding and BOM are preserved", RunEncodingPreserved),
			new("external file changes are rejected during preview", RunExternalPathRejectedAtPreview),
			new("create and delete changes are rejected during preview", RunCreateDeleteRejectedAtPreview)
		]);
	}
}
