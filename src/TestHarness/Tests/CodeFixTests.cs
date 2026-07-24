using System.Diagnostics;
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
		
		static string NewAdhocWorkspace()
		{
			var path = Path.Combine(Path.GetTempPath(), $"RoslynMcp_CodeFix_{Guid.NewGuid():N}");
			Directory.CreateDirectory(path);
			
			return path;
		}
		
		static void DeleteWorkspace(string path)
		{
			if(Directory.Exists(path))
				Directory.Delete(path, recursive: true);
		}
		
		async Task<(JsonNode? Data, string Text)> PreviewAdhoc(
			string workspacePath,
			string marker,
			int? actionIndex = null)
		{
			var fixturePath = Path.Combine(workspacePath, "_CodeFixFixture_.cs");
			
			if(!File.Exists(fixturePath))
				await File.WriteAllTextAsync(fixturePath, Fixture(marker));
			
			return await Call("roslyn_preview_code_fix", new {
				projectPath = workspacePath,
				filePath = "_CodeFixFixture_.cs",
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
		
		async Task<(bool pass, string msg)> RunMixedCreateWriteDelete()
		{
			var workspacePath = NewAdhocWorkspace();
			var removedPath = Path.Combine(workspacePath, "_CodeFixRemoved_.cs");
			var createdPath = Path.Combine(workspacePath, "_CodeFixCreated_.cs");
			var sw = Stopwatch.StartNew();
			
			try {
				
				await File.WriteAllTextAsync(removedPath, "class RemovedByCodeFix { }\n");
				var (preview, previewText) = await PreviewAdhoc(workspacePath, "TestMixed");
				var token = preview?["token"]?.GetValue<string>();
				
				if(token is null)
					return (false, $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
				
				var (apply, applyText) = await Apply(token, workspacePath);
				var pass = apply?["error"] is null
					&& apply?["filesWritten"]?.GetValue<int>() == 2
					&& apply?["filesDeleted"]?.GetValue<int>() == 1
					&& HasFileState(apply, "_CodeFixFixture_.cs", "written")
					&& HasFileState(apply, "_CodeFixCreated_.cs", "written")
					&& HasFileState(apply, "_CodeFixRemoved_.cs", "deleted")
					&& EveryFileHasRecovery(apply)
					&& File.Exists(createdPath)
					&& !File.Exists(removedPath);
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (apply: {applyText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		async Task<(bool pass, string msg)> RunAddedFileCollision()
		{
			var workspacePath = NewAdhocWorkspace();
			var createdPath = Path.Combine(workspacePath, "_CodeFixCreated_.cs");
			var sw = Stopwatch.StartNew();
			
			try {
				
				await File.WriteAllTextAsync(
					Path.Combine(workspacePath, "_CodeFixRemoved_.cs"),
					"class RemovedByCodeFix { }\n");
				var (preview, previewText) = await PreviewAdhoc(workspacePath, "TestMixed");
				var token = preview?["token"]?.GetValue<string>();
				
				if(token is null)
					return (false, $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
				
				const string collision = "// concurrent created file\n";
				await File.WriteAllTextAsync(createdPath, collision);
				var (apply, _) = await Apply(token, workspacePath);
				var current = await File.ReadAllTextAsync(createdPath);
				var pass = apply?["error"]?.GetValue<string>() == "stale preview"
					&& current == collision;
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (collision was overwritten) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		async Task<(bool pass, string msg)> RunPartialApply()
		{
			var workspacePath = NewAdhocWorkspace();
			var sw = Stopwatch.StartNew();
			
			try {
				
				var (preview, previewText) = await PreviewAdhoc(workspacePath, "TestPartial");
				var token = preview?["token"]?.GetValue<string>();
				
				if(token is null)
					return (false, $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
				
				var (apply, applyText) = await Apply(token, workspacePath);
				var retry = await Apply(token, workspacePath);
				var pass = apply?["error"]?.GetValue<string>() == "partial apply"
					&& HasFileState(apply, "_CodeFixFixture_.cs", "written")
					&& apply?["files"]?.AsArray().Any(file => file?["state"]?.GetValue<string>() == "untouched") == true
					&& EveryFileHasRecovery(apply)
					&& retry.Data?["error"]?.GetValue<string>() == "token unavailable";
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (apply: {applyText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		async Task<(bool pass, string msg)> RunDivergentLinkedPlan()
		{
			var workspacePath = NewAdhocWorkspace();
			var sw = Stopwatch.StartNew();
			
			try {
				
				var (preview, previewText) = await PreviewAdhoc(workspacePath, "TestLinkedDivergent");
				var token = preview?["token"]?.GetValue<string>();
				
				if(token is null)
					return (false, $"FAIL  (preview: {previewText}) [{sw.ElapsedMilliseconds}ms]");
				
				var original = await File.ReadAllTextAsync(Path.Combine(workspacePath, "_CodeFixFixture_.cs"));
				var (apply, applyText) = await Apply(token, workspacePath);
				var current = await File.ReadAllTextAsync(Path.Combine(workspacePath, "_CodeFixFixture_.cs"));
				var pass = apply?["error"]?.GetValue<string>() == "invalid physical plan"
					&& current == original
					&& !File.Exists(Path.Combine(workspacePath, "_CodeFixLinked_.cs"));
				
				return (pass, pass
					? $"PASS  [{sw.ElapsedMilliseconds}ms]"
					: $"FAIL  (apply: {applyText}) [{sw.ElapsedMilliseconds}ms]");
			}
			finally {
				DeleteWorkspace(workspacePath);
			}
		}
		
		async Task<(bool pass, string msg)> RunUnbundledDiagnosticRejected()
		{
			var workspacePath = NewAdhocWorkspace();
			var sw = Stopwatch.StartNew();
			
			try {
				
				var (preview, previewText) = await PreviewAdhoc(workspacePath, "NotSupportedByBundledProvider");
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
			new("multiple actions require and honor actionIndex", RunMultipleActionSelection),
			new("unsupported and throwing operations are structured", RunOperationShapeFailures),
			new("invalid approval leaves token pending", RunInvalidApprovalRetainsToken),
			new("rejection consumes token without changing files", RunRejectConsumesToken),
			new("workspace mismatch leaves token pending", RunWorkspaceMismatchRetainsToken),
			new("concurrent apply claims token exactly once", RunConcurrentTokenClaim),
			new("modified stale file survives and token retries", RunStaleModifiedRetainsToken),
			new("deleted stale file can be recreated and retried", RunStaleDeletedRetainsToken),
			new("multi-file write reports both verified files", RunMultiWrite),
			new("mixed create/write/delete reports exact states", RunMixedCreateWriteDelete),
			new("added-file collision is rejected as stale", RunAddedFileCollision),
			new("partial apply reports states and consumes token", RunPartialApply),
			new("divergent linked contents reject physical plan", RunDivergentLinkedPlan)
		]);
	}
}
