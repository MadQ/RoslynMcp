using System.Text.Json.Nodes;

internal static class RefactoringTests
{
	internal static async Task RunAsync(TestContext ctx, List<(bool pass, string message)> tests)
	{
		Console.WriteLine("\nRefactoring Tools (11 tests)");
		Console.WriteLine("─────────────────────────────────────────────────────────────");
		
		// ── preview_rename ──────────────────────────────────────────────────────
		
		tests.Add(await ctx.RunTestAsync(
			"roslyn_preview_rename: generate diff for renaming a symbol",
			"roslyn_preview_rename",
			new { symbolName = "NormalizePath", newName = "NormalizePath2", containingType = "RoslynMcpTool", projectPath = ctx.TargetPath },
			data => data?["token"] is not null
		));
		
		// ── apply_rename tests ──────────────────────────────────────────────────
		
		// Calls a tool and returns (no-protocol-error, parsed JSON data, raw content text).
		async Task<(bool ok, JsonNode? data, string text)> Call(string tool, object args) {
			
			await ctx.SendAsync(new { jsonrpc = "2.0", id = ctx.NextId(), method = "tools/call",
				@params = new { name = tool, arguments = args } });
			
			var resp  = await ctx.ReceiveAsync();
			var text  = resp?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
			
			JsonNode? data = null;
			
			try { data = JsonNode.Parse(text); }
			catch { }
			
			return (resp?["error"] is null && text.Length > 0, data, text);
		}
		
		// 1. Full apply: write fixture → preview → apply → read file → verify class renamed → cleanup.
		{
			const string TestName   = "roslyn_apply_rename: rename class, verify file updated";
			// File name != class name intentionally — keeps the fixture path stable
			// (Roslyn's RenameFile only triggers when the file name matches the symbol name).
			const string FixtureRel = "src/RoslynMcp/RenameApplyFixture.cs";
			const string RenamedRel = "src/RoslynMcp/RenameApplyRenamed.cs";
			
			Console.Write($"  {TestName,-55}");
			
			async Task<(bool pass, string msg)> RunApplySuccess() {
				
				var fixturePath = Path.Combine(ctx.RepoRoot, FixtureRel.Replace('/', Path.DirectorySeparatorChar));
				var renamedPath = Path.Combine(ctx.RepoRoot, RenamedRel.Replace('/', Path.DirectorySeparatorChar));
				var sw          = System.Diagnostics.Stopwatch.StartNew();
				
				// Pre-test cleanup — handles leftovers from a previous incomplete run.
				if(File.Exists(fixturePath)) File.Delete(fixturePath);
				if(File.Exists(renamedPath)) File.Delete(renamedPath);
				
				try {
					var (wOk, _, _) = await Call("roslyn_write_file", new {
						filePath    = FixtureRel,
						projectPath = ctx.TargetPath,
						createNew   = true,
						content     = "namespace RoslynMcp;\n\npublic class RenameApplyFixtureClass\\n{\n}\n"
					});
					
					if(!wOk)
						return (false, $"FAIL  (write fixture failed) [{sw.ElapsedMilliseconds}ms]");
					
					var (pvOk, pvData, _) = await Call("roslyn_preview_rename", new {
						symbolName  = "RenameApplyFixtureClass",
						newName     = "RenameApplyRenamedClass",
						projectPath = ctx.TargetPath
					});
					
					var token = pvData?["token"]?.GetValue<string>();
					
					if(!pvOk || token is null)
						return (false, $"FAIL  (preview failed) [{sw.ElapsedMilliseconds}ms]");
					
					var (_, apData, _) = await Call("roslyn_apply_rename", new {
						token, approval = "y", projectPath = ctx.TargetPath
					});
					
					if(apData?["error"] is not null || apData?["filesWritten"]?.GetValue<int>() is null or < 1)
						return (false, $"FAIL  (apply: {apData?["message"]}) [{sw.ElapsedMilliseconds}ms]");
					
					var (rdOk, rdData, _) = await Call("roslyn_read_file", new {
						filePath    = FixtureRel,
						projectPath = ctx.TargetPath
					});
					
					var fileText = string.Join("\n",
						rdData?["lines"]?.AsArray().Select(l => l?.GetValue<string>() ?? "") ?? []);
					
					var pass = rdOk
						&& fileText.Contains("class RenameApplyRenamedClass")
						&& !fileText.Contains("class RenameApplyFixtureClass");
					
					return pass
						? (true,  $"PASS  [{sw.ElapsedMilliseconds}ms]")
						: (false, $"FAIL  (class name not updated in file) [{sw.ElapsedMilliseconds}ms]");
				}
				catch(Exception ex) {
					return (false, $"FAIL  ({ex.Message}) [{sw.ElapsedMilliseconds}ms]");
				}
				finally {
					if(File.Exists(fixturePath)) File.Delete(fixturePath);
					if(File.Exists(renamedPath)) File.Delete(renamedPath);
				}
			}
			
			var (p1, m1) = await RunApplySuccess();
			Console.WriteLine(m1);
			tests.Add((p1, m1));
		}
		
		// 2. Cancel with 'n': returns "rejected", no files written.
		{
			const string TestName = "roslyn_apply_rename: cancel with 'n' returns rejected";
			Console.Write($"  {TestName,-55}");
			var sw = System.Diagnostics.Stopwatch.StartNew();
			
			var (_, pvData, _) = await Call("roslyn_preview_rename", new {
				symbolName = "NormalizePath", newName = "NormalizePath2",
				containingType = "RoslynMcpTool", projectPath = ctx.TargetPath
			});
			
			var token2 = pvData?["token"]?.GetValue<string>();
			
			bool pass2;
			string msg2;
			
			if(token2 is null) {
				pass2 = false;
				msg2  = $"FAIL  (no preview token) [{sw.ElapsedMilliseconds}ms]";
			}
			else {
				var (_, apData2, _) = await Call("roslyn_apply_rename", new {
					token = token2, approval = "n", projectPath = ctx.TargetPath
				});
				pass2 = apData2?["error"]?.GetValue<string>() == "rejected";
				msg2  = pass2 ? $"PASS  [{sw.ElapsedMilliseconds}ms]" : $"FAIL  (error: {apData2?["error"]}) [{sw.ElapsedMilliseconds}ms]";
			}
			
			Console.WriteLine(msg2);
			tests.Add((pass2, msg2));
		}
		
		// 3. Invalid approval value returns structured error.
		{
			const string TestName = "roslyn_apply_rename: invalid approval returns error";
			Console.Write($"  {TestName,-55}");
			var sw = System.Diagnostics.Stopwatch.StartNew();
			
			var (_, pvData, _) = await Call("roslyn_preview_rename", new {
				symbolName = "NormalizePath", newName = "NormalizePath2",
				containingType = "RoslynMcpTool", projectPath = ctx.TargetPath
			});
			
			var token3 = pvData?["token"]?.GetValue<string>();
			
			bool pass3;
			string msg3;
			
			if(token3 is null) {
				pass3 = false;
				msg3  = $"FAIL  (no preview token) [{sw.ElapsedMilliseconds}ms]";
			}
			else {
				var (_, apData3, _) = await Call("roslyn_apply_rename", new {
					token = token3, approval = "xyz", projectPath = ctx.TargetPath
				});
				pass3 = apData3?["error"]?.GetValue<string>() == "invalid approval";
				msg3  = pass3 ? $"PASS  [{sw.ElapsedMilliseconds}ms]" : $"FAIL  (error: {apData3?["error"]}) [{sw.ElapsedMilliseconds}ms]";
			}
			
			Console.WriteLine(msg3);
			tests.Add((pass3, msg3));
		}
		
		// 4. Stale/consumed token cannot be reused after rejection.
		{
			const string TestName = "roslyn_apply_rename: consumed token returns not found";
			Console.Write($"  {TestName,-55}");
			var sw = System.Diagnostics.Stopwatch.StartNew();
			
			var (_, pvData, _) = await Call("roslyn_preview_rename", new {
				symbolName = "NormalizePath", newName = "NormalizePath2",
				containingType = "RoslynMcpTool", projectPath = ctx.TargetPath
			});
			
			var token4 = pvData?["token"]?.GetValue<string>();
			
			bool pass4;
			string msg4;
			
			if(token4 is null) {
				pass4 = false;
				msg4  = $"FAIL  (no preview token) [{sw.ElapsedMilliseconds}ms]";
			}
			else {
				// Reject token so it is consumed.
				await Call("roslyn_apply_rename", new { token = token4, approval = "n", projectPath = ctx.TargetPath });
				
				// Same token must now return "not found".
				var (_, apData4, _) = await Call("roslyn_apply_rename", new {
					token = token4, approval = "y", projectPath = ctx.TargetPath
				});
				pass4 = apData4?["error"]?.GetValue<string>() == "token not found";
				msg4  = pass4 ? $"PASS  [{sw.ElapsedMilliseconds}ms]" : $"FAIL  (error: {apData4?["error"]}) [{sw.ElapsedMilliseconds}ms]";
			}
			
			Console.WriteLine(msg4);
			tests.Add((pass4, msg4));
		}
		
		// 5. Completely unknown token.
		tests.Add(await ctx.RunTestAsync(
			"roslyn_apply_rename: unknown token returns error",
			"roslyn_apply_rename",
			new { token = "doesnotexist999", approval = "y", projectPath = ctx.TargetPath },
			data => data?["error"]?.GetValue<string>() == "token not found"
		));
		
		// ── change_signature tests ──────────────────────────────────────────────
		
		// 1. Basic: add a parameter, verify diff has [Obsolete] and forwarding overload.
		tests.Add(await ctx.RunTestAsync(
			"roslyn_change_signature: add parameter with default",
			"roslyn_change_signature",
			new {
				
				methodName     = "NormalizePath",
				containingType = "RoslynMcpTool",
				addParameters  = """[{"name":"toLower","type":"bool","defaultValue":"false"}]""",
				projectPath    = ctx.TargetPath
			},
			data => data?["token"] is not null
				 && data?["diff"]?.GetValue<string>().Contains("Obsolete") == true
				 && data?["parameters_added"]?.AsArray().Count == 1
				 && data?["deprecation_message"]?.GetValue<string>().Contains("NormalizePath") == true
		));
		
		// 2. Error: non-method symbol.
		tests.Add(await ctx.RunTestAsync(
			"roslyn_change_signature: reject non-method symbol",
			"roslyn_change_signature",
			new {
				
				methodName    = "WorkspaceManager",
				addParameters = """[{"name":"x","type":"int"}]""",
				projectPath   = ctx.TargetPath
			},
			data => data?["error"]?.GetValue<string>().Contains("not a method") == true
		));
		
		// 3. Error: no parameters provided.
		tests.Add(await ctx.RunTestAsync(
			"roslyn_change_signature: reject empty addParameters",
			"roslyn_change_signature",
			new {
				
				methodName     = "NormalizePath",
				containingType = "RoslynMcpTool",
				projectPath    = ctx.TargetPath
			},
			data => data?["error"]?.GetValue<string>().Contains("No parameters") == true
		));
		
		// 4. Error: invalid JSON for addParameters.
		tests.Add(await ctx.RunTestAsync(
			"roslyn_change_signature: reject invalid JSON",
			"roslyn_change_signature",
			new {
				
				methodName     = "NormalizePath",
				containingType = "RoslynMcpTool",
				addParameters  = "not valid json",
				projectPath    = ctx.TargetPath
			},
			data => data?["error"]?.GetValue<string>().Contains("parse") == true
		));
		
		// 5. Error: duplicate parameter name.
		tests.Add(await ctx.RunTestAsync(
			"roslyn_change_signature: reject duplicate parameter name",
			"roslyn_change_signature",
			new {
				
				methodName     = "NormalizePath",
				containingType = "RoslynMcpTool",
				addParameters  = """[{"name":"filePath","type":"string"}]""",
				projectPath    = ctx.TargetPath
			},
			data => data?["error"]?.GetValue<string>().Contains("already exists") == true
		));
	}
}
