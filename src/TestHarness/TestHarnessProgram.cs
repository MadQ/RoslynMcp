using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
///     Comprehensive test harness for RoslynMcp. Tests all tools against RoslynMcp itself (dogfooding).
///     Usage: dotnet run --project TestHarness/TestHarness.csproj
/// </summary>
class Program
{
	static async Task<int> Main(string[] args)
	{

var repoRoot   = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var serverProj = Path.Combine(repoRoot, "src", "RoslynMcp", "RoslynMcp.csproj");
var targetPath = Path.Combine(repoRoot, "src", "RoslynMcp"); // Dogfood: analyze ourselves

var version = typeof(Program).Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine($"  RoslynMcp Test Harness v{version}");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine($"Server:  {serverProj}");
Console.WriteLine($"Target:  {targetPath}");
Console.WriteLine();

// Build the server first — dotnet run's build output goes to stdout and breaks the MCP stdio protocol.
Console.Write("Building server... ")
;
var buildProc = Process.Start(new ProcessStartInfo("dotnet") {
	
	Arguments       = $"build \"{serverProj}\" -f net10.0 --nologo -v q",
	UseShellExecute = false,
})!;
buildProc.WaitForExit();

if(buildProc.ExitCode != 0) {
	
	Console.Error.WriteLine($"Server build failed (exit code {buildProc.ExitCode}).");
	
	return 1;
}

Console.WriteLine("done.");

var psi = new ProcessStartInfo("dotnet") {
	
	Arguments              = $"run --no-build --project \"{serverProj}\" -f net10.0",
	RedirectStandardInput  = true,
	RedirectStandardOutput = true,
	RedirectStandardError  = true,
	UseShellExecute        = false,
};

using var proc = Process.Start(psi)!;

proc.ErrorDataReceived += (_, e) => {
	
	if(e.Data is not null)
		Console.Error.WriteLine($"[stderr] {e.Data}");
};
proc.BeginErrorReadLine();

var writer = proc.StandardInput;
var reader = proc.StandardOutput;
var reqId  = 1;

// ── Test Framework ──────────────────────────────────────────────────────────

async Task SendAsync(object payload)
{
	var line = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
	await writer.WriteLineAsync(line);
	await writer.FlushAsync();
}

async Task<JsonNode?> ReceiveAsync(int timeoutMs = 60_000)
{
	using var cts = new CancellationTokenSource(timeoutMs);
	
	try {
		
		var line = await reader.ReadLineAsync(cts.Token);
		
		return line is not null ? JsonNode.Parse(line) : null;
	}
	catch(OperationCanceledException) {
		return null; // Timeout.
	}
	catch(Exception ex) {
		
		Console.Error.WriteLine($"[recv error] {ex.Message}");
		
		return null; // Pipe closed or other I/O error.
	}
}

async Task<(bool pass, string message)> RunTestAsync(string testName, string toolName, object arguments, Func<JsonNode?, bool> validate, bool expectJson = true)
{
	Console.Write($"  {testName,-50} ");
	var sw = Stopwatch.StartNew();
	
	await SendAsync(new {
		
		jsonrpc = "2.0",
		id      = reqId++,
		method  = "tools/call",
		@params = new { name = toolName, arguments }
	});
	
	var response = await ReceiveAsync();
	sw.Stop();
	
	if(response is null)
		return (false, $"FAIL  (timeout) [{sw.ElapsedMilliseconds}ms]");
	
	var error = response["error"];
	
	if(error is not null)
		return (false, $"FAIL  (error: {error["message"]}) [{sw.ElapsedMilliseconds}ms]");
	
	var result = response["result"];
	
	if(result is null)
		return (false, $"FAIL  (no result) [{sw.ElapsedMilliseconds}ms]");
	
	var content = result["content"]?[0]?["text"]?.GetValue<string>();
	
	if(content is null)
		return (false, $"FAIL  (no content) [{sw.ElapsedMilliseconds}ms]");
	
	JsonNode? data;
	
	if(expectJson) {
		
		try {
			data = JsonNode.Parse(content);
		}
		catch {
			return (false, $"FAIL  (invalid JSON) [{sw.ElapsedMilliseconds}ms]");
		}
	}
	else {
		// Wrap plain string content as JSON for validation
		data = JsonValue.Create(content);
	}
	
	var pass = validate(data);
	
	return pass
		? (true, $"PASS  [{sw.ElapsedMilliseconds}ms]")
		: (false, $"FAIL  (validation failed: {testName}) [{sw.ElapsedMilliseconds}ms]");
}

// ── MCP Session Initialization ──────────────────────────────────────────────

await SendAsync(new {
	
	jsonrpc = "2.0",
	id      = reqId++,
	method  = "initialize",
	@params = new {
		
		protocolVersion = "2024-11-05",
		capabilities    = new { },
		clientInfo      = new { name = "TestHarness", version = "1.0" }
	}
});

var initResponse = await ReceiveAsync();

if(initResponse is null) {
	
	await Task.Delay(200); // Allow stderr to flush.
	Console.Error.WriteLine("\n[FATAL] Server did not respond to initialize — check stderr above for crash details.")
	;
	proc.Kill(entireProcessTree: true);
	
	return 1;
}

await SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });

Console.WriteLine("✓ MCP session initialized\n");

// ── Test Suite ───────────────────────────────────────────────────────────────

var tests = new List<(bool pass, string message)>();

Console.WriteLine("Discovery Tools (7 tests)");
Console.WriteLine("─────────────────────────────────────────────────────────────");

tests.Add(await RunTestAsync(
	"roslyn_search_files: find 'WorkspaceManager' in .cs files",
	"roslyn_search_files",
	new { pattern = "WorkspaceManager", filePattern = "*.cs", take = 10, projectPath = targetPath },
	data => data?["matches"]?.AsArray().Count > 0
));

tests.Add(await RunTestAsync(
	"roslyn_semantic_search: find TODO comments only",
	"roslyn_semantic_search",
	new { pattern = "TODO", context = "comments", take = 10, projectPath = targetPath },
	data => data?["matches"]?.AsArray().Count > 0 && data?["matches"]?[0]?["context"]?.GetValue<string>() == "comment"
));

tests.Add(await RunTestAsync(
	"roslyn_list_types: enumerate types in RoslynMcp.Tools namespace",
	"roslyn_list_types",
	new { namespaceFilter = "RoslynMcp.Tools", projectPath = targetPath },
	data => data?["types"]?.AsArray().Count > 10 && data?["page_token"] is not null
));

tests.Add(await RunTestAsync(
	"roslyn_list_files: enumerate tool files with glob pattern",
	"roslyn_list_files",
	new { pattern = "**/*Tool.cs", take = 50, projectPath = targetPath },
	data => data?["count"]?.GetValue<int>() > 20 && data?["files"]?.AsArray().Any(f => f?.GetValue<string>().Contains("Tool.cs") == true) == true
));

tests.Add(await RunTestAsync(
	"roslyn_get_file_outline: WorkspaceManager structure",
	"roslyn_get_file_outline",
	new { filePath = "WorkspaceManager.cs", projectPath = targetPath },
	data => data?["types"]?.AsArray().Count > 0
));

tests.Add(await RunTestAsync(
	"roslyn_get_project_info: verify TFM and packages",
	"roslyn_get_project_info",
	new { projectPath = targetPath },
	data => data?["target_framework"]?.GetValue<string>()?.StartsWith("net") == true
));

tests.Add(await RunTestAsync(
	"roslyn_get_usings: extract using directives from Program.cs",
	"roslyn_get_usings",
	new { filePath = "Program.cs", projectPath = targetPath },
	data => data?["usings"]?.AsArray().Count > 0
));

Console.WriteLine("\nMember Body Tools (2 tests)");
Console.WriteLine("─────────────────────────────────────────────────────────────");

tests.Add(await RunTestAsync(
	"roslyn_get_member_body: single method",
	"roslyn_get_member_body",
	new { symbolName = "GetCompilation", containingType = "WorkspaceManager", projectPath = targetPath },
	data => data?["body"]?.GetValue<string>().Contains("GetCompilation") == true
		 && data?["start_line"]?.GetValue<int>() > 0
		 && data?["symbol_kind"]?.GetValue<string>() == "method"
));

tests.Add(await RunTestAsync(
	"roslyn_get_member_body: partial class (multiple parts)",
	"roslyn_get_member_body",
	new { symbolName = "WorkspaceManager", projectPath = targetPath },
	data => data?["parts"]?.AsArray().Count > 1
		 && data?["note"]?.GetValue<string>().Contains("Partial") == true
));

Console.WriteLine("\nType Understanding Tools (4 tests)");
Console.WriteLine("─────────────────────────────────────────────────────────────");

tests.Add(await RunTestAsync(
	"roslyn_get_type_members: WorkspaceManager members with signatures",
	"roslyn_get_type_members",
	new { typeName = "WorkspaceManager", projectPath = targetPath },
	data => data?["members"]?.AsArray().Count > 0 && data["members"]?[0]?["signature"] is not null
));

tests.Add(await RunTestAsync(
	"roslyn_get_type_hierarchy: WorkspaceManager inheritance",
	"roslyn_get_type_hierarchy",
	new { typeName = "WorkspaceManager", projectPath = targetPath },
	data => data?["interfaces_and_derived"]?.AsArray().Any(i => i?.GetValue<string>().Contains("IDisposable") == true) == true
));

tests.Add(await RunTestAsync(
	"roslyn_find_implementations: IDisposable implementers",
	"roslyn_find_implementations",
	new { symbolName = "IDisposable", projectPath = targetPath },
	data => data?["error"] is not null || (data?["total_implementations"] is not null && data?["implementations"]?.AsArray() is not null)
));

tests.Add(await RunTestAsync(
	"roslyn_get_symbol_documentation: WorkspaceManager XML docs",
	"roslyn_get_symbol_documentation",
	new { symbolName = "WorkspaceManager", projectPath = targetPath },
	data => data?["symbol_name"] is not null
));

Console.WriteLine("\nNavigation Tools (3 tests)");
Console.WriteLine("─────────────────────────────────────────────────────────────");

tests.Add(await RunTestAsync(
	"roslyn_get_symbol_info: resolve symbol at location",
	"roslyn_get_symbol_info",
	new { filePath = "Program.cs", line = 10, column = 10, projectPath = targetPath },
	data => data?["kind"] is not null && data?["name"] is not null
));

tests.Add(await RunTestAsync(
	"roslyn_find_references: locate WorkspaceManager usages",
	"roslyn_find_references",
	new { symbolName = "WorkspaceManager", projectPath = targetPath },
	data => data?["total_references"]?.GetValue<int>() > 0 && data?["references"]?.AsArray().Count > 0
));

// ── Pagination token test: page 1 with small take, then page 2 via token ──
{
	Console.Write($"  {"roslyn_find_references: pagination token (page 1)",-50} ");
	var sw1 = System.Diagnostics.Stopwatch.StartNew();
	
	await SendAsync(new {
		
		jsonrpc = "2.0",
		id      = reqId++,
		method  = "tools/call",
		@params = new { name = "roslyn_find_references", arguments = new { symbolName = "WorkspaceManager", projectPath = targetPath, take = 2 } }
	});
	
	var resp1 = await ReceiveAsync();
	sw1.Stop();
	var content1 = resp1?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
	var page1 = content1 is not null ? System.Text.Json.Nodes.JsonNode.Parse(content1) : null;
	var token = page1?["page_token"]?.GetValue<string>();
	var hasMore = page1?["has_more"]?.GetValue<bool>() == true;
	var page1Refs = page1?["references"]?.AsArray();
	
	if(token is not null && hasMore && page1Refs?.Count == 2) {
		
		tests.Add((true, $"PASS  [{sw1.ElapsedMilliseconds}ms]"));
		Console.WriteLine($"PASS  [{sw1.ElapsedMilliseconds}ms]");
	}
	else {
		
		tests.Add((false, $"FAIL  (no page_token or has_more) [{sw1.ElapsedMilliseconds}ms]"));
		Console.WriteLine($"FAIL  (no page_token or has_more) [{sw1.ElapsedMilliseconds}ms]");
	}
	
	Console.Write($"  {"roslyn_find_references: pagination token (page 2)",-50} ");
	var sw2 = System.Diagnostics.Stopwatch.StartNew();
	
	await SendAsync(new {
		
		jsonrpc = "2.0",
		id      = reqId++,
		method  = "tools/call",
		@params = new { name = "roslyn_find_references", arguments = new { symbolName = "WorkspaceManager", projectPath = targetPath, skip = 2, take = 2, page_token = token ?? "" } }
	});
	
	var resp2 = await ReceiveAsync();
	sw2.Stop();
	var content2 = resp2?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
	var page2 = content2 is not null ? System.Text.Json.Nodes.JsonNode.Parse(content2) : null;
	var page2Items = page2?["items"]?.AsArray();
	var page2Token = page2?["page_token"]?.GetValue<string>();
	
	if(page2Items?.Count > 0 && page2Token == token) {
		
		tests.Add((true, $"PASS  [{sw2.ElapsedMilliseconds}ms]"));
		Console.WriteLine($"PASS  [{sw2.ElapsedMilliseconds}ms]");
	}
	else {
		
		tests.Add((false, $"FAIL  (page 2 missing items or wrong token) [{sw2.ElapsedMilliseconds}ms]"));
		Console.WriteLine($"FAIL  (page 2 missing items or wrong token) [{sw2.ElapsedMilliseconds}ms]");
	}
}

tests.Add(await RunTestAsync(
	"roslyn_get_symbol_definition: find WorkspaceManager declaration",
	"roslyn_get_symbol_definition",
	new { symbolName = "WorkspaceManager", projectPath = targetPath },
	data => data?["file"]?.GetValue<string>().Contains("WorkspaceManager.cs") == true));

Console.WriteLine("\nCall Graph Tools (2 tests)");
Console.WriteLine("─────────────────────────────────────────────────────────────");

tests.Add(await RunTestAsync(
	"roslyn_find_callers: find callers of GetCompilation",
	"roslyn_find_callers",
	new { symbolName = "GetCompilation", containingType = "WorkspaceManager", projectPath = targetPath },
	data => data?["total_callers"]?.GetValue<int>() > 0
	     && data?["callers"]?.AsArray().Count > 0
	     && data?["callers"]?[0]?["caller"] is not null
	     && data?["callers"]?[0]?["file"] is not null
));

tests.Add(await RunTestAsync(
	"roslyn_get_call_graph: outgoing calls from GetCompilation",
	"roslyn_get_call_graph",
	new { symbolName = "GetCompilation", containingType = "WorkspaceManager", projectPath = targetPath },
	data => data?["method"]?.GetValue<string>().Contains("GetCompilation") == true
	     && data?["total_calls"]?.GetValue<int>() > 0
	     && data?["calls"]?.AsArray().Count > 0
	     && data?["calls"]?[0]?["callee"] is not null
));


Console.WriteLine("\nCode Generation Tools (1 test)");
Console.WriteLine("─────────────────────────────────────────────────────────────");

tests.Add(await RunTestAsync(
	"roslyn_get_symbols_in_scope: enumerate symbols at location",
	"roslyn_get_symbols_in_scope",
	new { filePath = "WorkspaceManager.cs", line = 80, column = 10, projectPath = targetPath },
	data => data?["fields"] is not null || data?["methods"] is not null
));

Console.WriteLine("\nFile Content Tools (4 tests)");
Console.WriteLine("─────────────────────────────────────────────────────────────");

tests.Add(await RunTestAsync(
	"roslyn_read_file: read .cs file from Roslyn in-memory",
	"roslyn_read_file",
	new { filePath = "WorkspaceManager.cs", projectPath = targetPath },
	data => data?["source"]?.GetValue<string>() == "roslyn"
		 && data?["total_lines"]?.GetValue<int>() > 100
		 && data?["lines"]?.AsArray().Count > 0
));

tests.Add(await RunTestAsync(
	"roslyn_read_file: read with line range",
	"roslyn_read_file",
	new { filePath = "WorkspaceManager.cs", startLine = 1, endLine = 10, projectPath = targetPath },
	data => data?["lines"]?.AsArray().Count == 10
		 && data?["start_line"]?.GetValue<int>() == 1
		 && data?["end_line"]?.GetValue<int>() == 10
));

tests.Add(await RunTestAsync(
	"roslyn_read_file: read non-.cs file from disk",
	"roslyn_read_file",
	new { filePath = "RoslynMcp.csproj", projectPath = targetPath },
	data => data?["source"]?.GetValue<string>() == "disk"
		 && data?["total_lines"]?.GetValue<int>() > 0
));

tests.Add(await RunTestAsync(
	"roslyn_get_line_count: single and multi-file",
	"roslyn_get_line_count",
	new { filePaths = "WorkspaceManager.cs,RoslynMcp.csproj", projectPath = targetPath },
	data => data?["files"]?.AsArray().Count == 2
		 && data?["files"]?[0]?["line_count"]?.GetValue<int>() > 100
));

Console.WriteLine("\nValidation Tools (2 tests)");
Console.WriteLine("─────────────────────────────────────────────────────────────");

tests.Add(await RunTestAsync(
	"roslyn_get_diagnostics: check for compiler errors",
	"roslyn_get_diagnostics",
	new { projectPath = targetPath },
	data => data?["errors"] is not null && data?["summary"] is not null
));

tests.Add(await RunTestAsync(
	"roslyn_build_project: smart Roslyn-first build",
	"roslyn_build_project",
	new { projectPath = targetPath },
	data => data?["succeeded"] is not null && data?["source"] is not null
));

Console.WriteLine("\nRefactoring Tools (6 tests)");
Console.WriteLine("─────────────────────────────────────────────────────────────");

tests.Add(await RunTestAsync(
	"roslyn_preview_rename: generate diff for renaming compilation",
	"roslyn_preview_rename",
	new { symbolName = "compilation", newName = "compilation2", containingType = "WorkspaceInstance", projectPath = targetPath },
	data => (data?["Token"] ?? data?["token"]) is not null || (data?["Message"] ?? data?["message"]) is not null
));

// ── change_signature tests ──────────────────────────────────────────────

// 1. Basic: add a parameter, verify diff has [Obsolete] and forwarding overload.
tests.Add(await RunTestAsync(
	"roslyn_change_signature: add parameter with default",
	"roslyn_change_signature",
	new {
		
		methodName     = "NormalizePath",
		containingType = "RoslynMcpTool",
		addParameters  = "[{\"name\":\"toLower\",\"type\":\"bool\",\"defaultValue\":\"false\"}]",
		projectPath    = targetPath
	},
	data => data?["token"] is not null
		 && data?["diff"]?.GetValue<string>().Contains("Obsolete") == true
		 && data?["parameters_added"]?.AsArray().Count == 1
		 && data?["deprecation_message"]?.GetValue<string>().Contains("NormalizePath") == true
));

// 2. Error: non-method symbol.
tests.Add(await RunTestAsync(
	"roslyn_change_signature: reject non-method symbol",
	"roslyn_change_signature",
	new {
		
		methodName     = "WorkspaceManager",
		addParameters  = "[{\"name\":\"x\",\"type\":\"int\"}]",
		projectPath    = targetPath
	},
	data => data?["error"]?.GetValue<string>().Contains("not a method") == true
));

// 3. Error: no parameters provided.
tests.Add(await RunTestAsync(
	"roslyn_change_signature: reject empty addParameters",
	"roslyn_change_signature",
	new {
		
		methodName     = "NormalizePath",
		containingType = "RoslynMcpTool",
		projectPath    = targetPath
	},
	data => data?["error"]?.GetValue<string>().Contains("No parameters") == true
));

// 4. Error: invalid JSON for addParameters.
tests.Add(await RunTestAsync(
	"roslyn_change_signature: reject invalid JSON",
	"roslyn_change_signature",
	new {
		
		methodName     = "NormalizePath",
		containingType = "RoslynMcpTool",
		addParameters  = "not valid json",
		projectPath    = targetPath
	},
	data => data?["error"]?.GetValue<string>().Contains("parse") == true
));

// 5. Error: duplicate parameter name.
tests.Add(await RunTestAsync(
	"roslyn_change_signature: reject duplicate parameter name",
	"roslyn_change_signature",
	new {
		
		methodName     = "NormalizePath",
		containingType = "RoslynMcpTool",
		addParameters  = "[{\"name\":\"filePath\",\"type\":\"string\"}]",
		projectPath    = targetPath
	},
	data => data?["error"]?.GetValue<string>().Contains("already exists") == true
));

Console.WriteLine("\nFile Editing Tools (10 tests)");
Console.WriteLine("─────────────────────────────────────────────────────────────");

// NOTE: The empty-file-after-write detection added to all four editing tools
// (roslyn_write_file, roslyn_replace_in_file, roslyn_replace_in_code, roslyn_insert_lines)
// cannot be exercised from here. The condition it guards against — the OS or antivirus
// silently truncating a file after an apparently successful write — requires environmental
// interference that cannot be provoked via the MCP API. The guard condition itself
// (writeBytes.Length > 4 && new FileInfo(fullPath).Length <= 4) is covered implicitly:
// every successful write test below confirms the check does not false-positive on real files.

// Create temp files for editing tool tests
var tempTextFile = Path.Combine(targetPath, ".test_replace_temp.cs");
await File.WriteAllTextAsync(tempTextFile, "// Test line 1\nvar handle = IntPtr.Zero;\n// Test line 3\n");

var tempCodeFile = Path.Combine(targetPath, ".test_code_temp.cs");
await File.WriteAllTextAsync(tempCodeFile, "class TestClass { private int oldField = 42; }");

tests.Add(await RunTestAsync(
	"roslyn_replace_in_file: dry run literal replacement",
	"roslyn_replace_in_file",
	new { filePath = ".test_replace_temp.cs", pattern = "IntPtr", replacement = "nint", dryRun = true, projectPath = targetPath },
	data => data?["match_count"]?.GetValue<int>() == 1 && data?["applied"]?.GetValue<bool>() == false
));

tests.Add(await RunTestAsync(
	"roslyn_replace_in_file: apply literal replacement",
	"roslyn_replace_in_file",
	new { filePath = ".test_replace_temp.cs", pattern = "IntPtr", replacement = "nint", dryRun = false, projectPath = targetPath },
	data => data?["match_count"]?.GetValue<int>() == 1 && data?["applied"]?.GetValue<bool>() == true
));

tests.Add(await RunTestAsync(
	"roslyn_replace_in_file: regex replacement with capture groups",
	"roslyn_replace_in_file",
	new { filePath = ".test_replace_temp.cs", pattern = @"var (\w+) = nint\.Zero", replacement = "nint $1 = 0", useRegex = true, projectPath = targetPath },
	data => data?["match_count"]?.GetValue<int>() == 1 && data?["changed_lines"]?.AsArray()[0]?.GetValue<int>() == 2
));

tests.Add(await RunTestAsync(
	"roslyn_replace_in_code: dry run identifier replacement",
	"roslyn_replace_in_code",
	new { filePath = ".test_code_temp.cs", nodeKind = "IdentifierName", textPattern = "oldField", replacement = "newField", dryRun = true, projectPath = targetPath },
	data => data?["error"] is null && data?["change_count"] is not null
));

tests.Add(await RunTestAsync(
	"roslyn_replace_in_code: apply identifier replacement",
	"roslyn_replace_in_code",
	new { filePath = ".test_code_temp.cs", nodeKind = "IdentifierName", textPattern = "newField", replacement = "finalField", dryRun = false, projectPath = targetPath },
	data => data?["error"] is null && data?["applied"] is not null
));


// ── insert_lines tests ──────────────────────────────────────────────────

var tempInsertFile = Path.Combine(targetPath, ".test_insert_temp.txt");
await File.WriteAllTextAsync(tempInsertFile, "line one\nline two\nline three\n");

tests.Add(await RunTestAsync(
	"roslyn_insert_lines: dry run insertAfter",
	"roslyn_insert_lines",
	new { filePath = ".test_insert_temp.txt", text = "inserted line", insertAfter = "line one", dryRun = true, projectPath = targetPath },
	data => data?["applied"]?.GetValue<bool>() == false && data?["inserted_at"]?.GetValue<int>() == 2 && data?["line_count"]?.GetValue<int>() == 1
));

tests.Add(await RunTestAsync(
	"roslyn_insert_lines: apply insertAfter",
	"roslyn_insert_lines",
	new { filePath = ".test_insert_temp.txt", text = "after one", insertAfter = "line one", projectPath = targetPath },
	data => data?["applied"]?.GetValue<bool>() == true && data?["inserted_at"]?.GetValue<int>() == 2
));

tests.Add(await RunTestAsync(
	"roslyn_insert_lines: apply insertBefore",
	"roslyn_insert_lines",
	new { filePath = ".test_insert_temp.txt", text = "before three", insertBefore = "line three", projectPath = targetPath },
	data => data?["applied"]?.GetValue<bool>() == true && data?["inserted_at"]?.GetValue<int>() == 4
));

tests.Add(await RunTestAsync(
	"roslyn_insert_lines: apply atLine",
	"roslyn_insert_lines",
	new { filePath = ".test_insert_temp.txt", text = "at line 1", atLine = 1, projectPath = targetPath },
	data => data?["applied"]?.GetValue<bool>() == true && data?["inserted_at"]?.GetValue<int>() == 1
));

tests.Add(await RunTestAsync(
	"roslyn_insert_lines: error when no location specified",
	"roslyn_insert_lines",
	new { filePath = ".test_insert_temp.txt", text = "oops", projectPath = targetPath },
	data => data?["error"]?.GetValue<string>().Contains("exactly one") == true
));

try { File.Delete(tempInsertFile); } catch { }
// Clean up temp files
try { File.Delete(tempTextFile); } catch { }
try { File.Delete(tempCodeFile); } catch { }
try { File.Delete(Path.Combine(targetPath, ".test_code_debug.cs")); } catch { }

// ── Summary ──────────────────────────────────────────────────────────────────

Console.WriteLine("\n═══════════════════════════════════════════════════════════════");
Console.WriteLine("  Test Summary");
Console.WriteLine("═══════════════════════════════════════════════════════════════\n");

var passed = tests.Count(t => t.pass);
var failed = tests.Count - passed;

foreach(var test in tests)
	Console.WriteLine(test.message);

Console.WriteLine();
Console.WriteLine($"Passed: {passed}/{tests.Count}");
Console.WriteLine($"Failed: {failed}/{tests.Count}");
Console.WriteLine();

if(failed == 0)
	Console.WriteLine("✅ All tests passed!");
else
	Console.WriteLine($"❌ {failed} test(s) failed.");

writer.Close();
await proc.WaitForExitAsync(new CancellationTokenSource(5_000).Token).ConfigureAwait(false);

if(!proc.HasExited)
	proc.Kill();

return failed == 0 ? 0 : 1;
	
	}
}
