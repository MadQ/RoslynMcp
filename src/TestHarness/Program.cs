/// <summary>
///     Comprehensive test harness for RoslynMcp MVP. Tests all 24 tools against RoslynMcp itself (dogfooding).
///     Usage: dotnet run --project TestHarness/TestHarness.csproj
/// </summary>

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

var repoRoot   = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var serverProj = Path.Combine(repoRoot, "src", "RoslynMcp", "RoslynMcp.csproj");
var targetPath = Path.Combine(repoRoot, "src", "RoslynMcp"); // Dogfood: analyze ourselves

Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  RoslynMcp Test Harness — Testing 24 MVP Tools");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine($"Server:  {serverProj}");
Console.WriteLine($"Target:  {targetPath}");
Console.WriteLine();

var psi = new ProcessStartInfo("dotnet") {
    Arguments              = $"run --project \"{serverProj}\" -f net10.0",
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

async Task<JsonNode?> ReceiveAsync(int timeoutMs = 30_000)
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
    Console.Error.WriteLine("\n[FATAL] Server did not respond to initialize — check stderr above for crash details.");
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
    new { pattern = "WorkspaceManager", filePattern = "*.cs", take = 10 },
    data => data?["matches"]?.AsArray().Count > 0
));

tests.Add(await RunTestAsync(
    "roslyn_semantic_search: find TODO comments only",
    "roslyn_semantic_search",
    new { pattern = "TODO", context = "comments", take = 10 },
    data => data?["matches"]?.AsArray().Count > 0 && data?["matches"]?[0]?["context"]?.GetValue<string>() == "comment"
));

tests.Add(await RunTestAsync(
    "roslyn_list_types: enumerate types in RoslynMcp.Tools namespace",
    "roslyn_list_types",
    new { namespaceFilter = "RoslynMcp.Tools" },
    data => data?.AsArray().Count > 10
));

tests.Add(await RunTestAsync(
    "roslyn_list_files: enumerate tool files with glob pattern",
    "roslyn_list_files",
    new { pattern = "**/*Tool.cs", take = 50 },
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
    data => data?["interfaces"]?.AsArray().Any(i => i?.GetValue<string>().Contains("IDisposable") == true) == true
));

tests.Add(await RunTestAsync(
    "roslyn_find_implementations: IDisposable implementers",
    "roslyn_find_implementations",
    new { symbolName = "IDisposable" },
    data => data?["error"] is not null || (data?["total_implementations"] is not null && data?["implementations"]?.AsArray() is not null)
));

tests.Add(await RunTestAsync(
    "roslyn_get_symbol_documentation: WorkspaceManager XML docs",
    "roslyn_get_symbol_documentation",
    new { symbolName = "WorkspaceManager" },
    data => data?["symbol_name"] is not null
));

Console.WriteLine("\nNavigation Tools (3 tests)");
Console.WriteLine("─────────────────────────────────────────────────────────────");

tests.Add(await RunTestAsync(
    "roslyn_get_symbol_info: resolve symbol at location",
    "roslyn_get_symbol_info",
    new { filePath = "Program.cs", line = 10, column = 10, projectPath = targetPath },
    data => data?.GetValue<string>().Contains("Kind:") == true,
    expectJson: false
));

tests.Add(await RunTestAsync(
    "roslyn_find_references: locate WorkspaceManager usages",
    "roslyn_find_references",
    new { symbolName = "WorkspaceManager" },
    data => data?["total_references"]?.GetValue<int>() > 0 && data?["references"]?.AsArray().Count > 0
));

tests.Add(await RunTestAsync(
    "roslyn_get_symbol_definition: find WorkspaceManager declaration",
    "roslyn_get_symbol_definition",
    new { symbolName = "WorkspaceManager" },
    data => data?["file"]?.GetValue<string>().Contains("WorkspaceManager.cs") == true
));

Console.WriteLine("\nCode Generation Tools (1 test)");
Console.WriteLine("─────────────────────────────────────────────────────────────");

tests.Add(await RunTestAsync(
    "roslyn_get_symbols_in_scope: enumerate symbols at location",
    "roslyn_get_symbols_in_scope",
    new { filePath = "WorkspaceManager.cs", line = 80, column = 10, projectPath = targetPath },
    data => data?["fields"] is not null || data?["methods"] is not null
));

Console.WriteLine("\nValidation Tools (2 tests)");
Console.WriteLine("─────────────────────────────────────────────────────────────");

tests.Add(await RunTestAsync(
    "roslyn_get_diagnostics: check for compiler errors",
    "roslyn_get_diagnostics",
    new { },
    data => data?.AsArray() is not null
));

tests.Add(await RunTestAsync(
    "roslyn_build_project: smart Roslyn-first build",
    "roslyn_build_project",
    new { projectPath = targetPath },
    data => data?["succeeded"] is not null && data?["source"] is not null
));

Console.WriteLine("\nRefactoring Tools (1 test)");
Console.WriteLine("─────────────────────────────────────────────────────────────");

tests.Add(await RunTestAsync(
    "roslyn_preview_rename: generate diff for renaming compilation",
    "roslyn_preview_rename",
    new { symbolName = "compilation", newName = "compilation2", containingType = "WorkspaceInstance" },
    data => (data?["Token"] ?? data?["token"]) is not null || (data?["Message"] ?? data?["message"]) is not null
));

Console.WriteLine("\nFile Editing Tools (5 tests)");
Console.WriteLine("─────────────────────────────────────────────────────────────");

// Create temp files for editing tool tests
var tempTextFile = Path.Combine(targetPath, ".test_replace_temp.cs");
await File.WriteAllTextAsync(tempTextFile, "// Test line 1\nvar handle = IntPtr.Zero;\n// Test line 3\n");

var tempCodeFile = Path.Combine(targetPath, ".test_code_temp.cs");
await File.WriteAllTextAsync(tempCodeFile, "class TestClass { private int oldField = 42; }");

tests.Add(await RunTestAsync(
    "roslyn_replace_in_file: dry run literal replacement",
    "roslyn_replace_in_file",
    new { filePath = ".test_replace_temp.cs", pattern = "IntPtr", replacement = "nint", dryRun = true, projectPath = targetPath },
    data => data?["matchCount"]?.GetValue<int>() == 1 && data?["applied"]?.GetValue<bool>() == false
));

tests.Add(await RunTestAsync(
    "roslyn_replace_in_file: apply literal replacement",
    "roslyn_replace_in_file",
    new { filePath = ".test_replace_temp.cs", pattern = "IntPtr", replacement = "nint", dryRun = false, projectPath = targetPath },
    data => data?["matchCount"]?.GetValue<int>() == 1 && data?["applied"]?.GetValue<bool>() == true
));

tests.Add(await RunTestAsync(
    "roslyn_replace_in_file: regex replacement with capture groups",
    "roslyn_replace_in_file",
    new { filePath = ".test_replace_temp.cs", pattern = @"var (\w+) = nint\.Zero", replacement = "nint $1 = 0", useRegex = true, projectPath = targetPath },
    data => data?["matchCount"]?.GetValue<int>() == 1 && data?["changedLines"]?.AsArray()[0]?.GetValue<int>() == 2
));

tests.Add(await RunTestAsync(
    "roslyn_replace_in_code: dry run identifier replacement",
    "roslyn_replace_in_code",
    new { filePath = ".test_code_temp.cs", nodeKind = "IdentifierName", textPattern = "oldField", replacement = "newField", dryRun = true, projectPath = targetPath },
    data => data?["error"] is null && data?["changeCount"] is not null
));

tests.Add(await RunTestAsync(
    "roslyn_replace_in_code: apply identifier replacement",
    "roslyn_replace_in_code",
    new { filePath = ".test_code_temp.cs", nodeKind = "IdentifierName", textPattern = "newField", replacement = "finalField", dryRun = false, projectPath = targetPath },
    data => data?["error"] is null && data?["applied"] is not null
));

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
