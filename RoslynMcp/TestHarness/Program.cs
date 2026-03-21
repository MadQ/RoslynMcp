/// <summary>
///     Smoke-test harness for RoslynMcp. Starts the server as a child process,
///     sends a minimal MCP session, and prints the response.
///     Usage: dotnet run --project RoslynMcp/TestHarness/TestHarness.csproj
/// </summary>

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var repoRoot   = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var serverProj = Path.Combine(repoRoot, "RoslynMcp", "RoslynMcp.csproj");
var targetPath = Path.Combine(repoRoot, "ScreenMon");

Console.WriteLine($"Server:  {serverProj}");
Console.WriteLine($"Target:  {targetPath}");
Console.WriteLine();

var psi = new ProcessStartInfo("dotnet") {
    Arguments              = $"run --project \"{serverProj}\" --no-build -- \"{targetPath}\"",
    RedirectStandardInput  = true,
    RedirectStandardOutput = true,
    RedirectStandardError  = true,
    UseShellExecute        = false,
};

using var proc = Process.Start(psi)!;

// Drain stderr on a background thread so the process doesn't block.
proc.ErrorDataReceived += (_, e) => { if(e.Data is not null) Console.Error.WriteLine($"[stderr] {e.Data}"); };
proc.BeginErrorReadLine();

var writer = proc.StandardInput;
var reader = proc.StandardOutput;

// ── Helpers ──────────────────────────────────────────────────────────────────

async Task SendAsync(object payload)
{
    var line = JsonSerializer.Serialize(payload);
    Console.WriteLine($"→ {line}");
    await writer.WriteLineAsync(line);
    await writer.FlushAsync();
}

async Task<JsonNode?> ReceiveAsync(int timeoutMs = 10_000)
{
    using var cts  = new CancellationTokenSource(timeoutMs);
    var       line = await reader.ReadLineAsync(cts.Token);

    if(line is null) return null;

    Console.WriteLine($"← {line}");
    return JsonNode.Parse(line);
}

// ── MCP session ──────────────────────────────────────────────────────────────

// 1. initialize
await SendAsync(new {
    jsonrpc = "2.0", id = 1, method = "initialize",
    @params = new {
        protocolVersion = "2024-11-05",
        capabilities    = new { },
        clientInfo      = new { name = "TestHarness", version = "1.0" }
    }
});

await ReceiveAsync(); // initialize result

// 2. initialized notification
await SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });

// 2b. List tools to discover actual registered names
await SendAsync(new {
    jsonrpc = "2.0", id = 3, method = "tools/list",
    @params = new { }
});

var toolList = await ReceiveAsync();
Console.WriteLine();
Console.WriteLine("═══ Registered tools ═══");
var tools = toolList?["result"]?["tools"]?.AsArray();
if(tools is not null)
    foreach(var tool in tools)
        Console.WriteLine($"  {tool?["name"]}");
Console.WriteLine();

// 3. Call get_type_members — the key smoke test
await SendAsync(new {
    jsonrpc = "2.0", id = 2, method = "tools/call",
    @params = new {
        name      = "get_type_members",
        arguments = new { typeName = "ShowWindowCommand", memberKind = "enum" }
    }
});

var result = await ReceiveAsync();

Console.WriteLine();
Console.WriteLine("═══ Result ═══");

var content = result?["result"]?["content"]?[0]?["text"]?.GetValue<string>();

if(content is not null)
    Console.WriteLine(content);
else
    Console.WriteLine("(no content — see raw response above)");

writer.Close();
await proc.WaitForExitAsync(new CancellationTokenSource(5_000).Token).ConfigureAwait(false);
if(!proc.HasExited) proc.Kill();
