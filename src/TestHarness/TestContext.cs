using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
///     Shared state and helper methods for all test sections. Wraps the MCP stdio communication
///     channel and provides <see cref="RunTestAsync"/> for structured test execution.
/// </summary>
class TestContext
{
	private int                   _reqId = 1;
	private readonly StreamWriter _writer;
	private readonly StreamReader _reader;
	
	public string TargetPath { get; }
	public string RepoRoot   { get; }
	public string ServerProj { get; }
	
	internal TestContext(StreamWriter writer, StreamReader reader, string targetPath, string repoRoot, string serverProj)
	{
		_writer    = writer;
		_reader    = reader;
		TargetPath = targetPath;
		RepoRoot   = repoRoot;
		ServerProj = serverProj;
	}
	
	// Closes the stdin pipe to signal EOF to the server.
	internal void CloseInput() => _writer.Close()
	;
	
	// Returns the next JSON-RPC request ID and increments the counter.
	internal int NextId() => _reqId++
	;
	
	internal async Task SendAsync(object payload)
	{
		var line = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
		await _writer.WriteLineAsync(line);
		await _writer.FlushAsync();
	}
	
	internal async Task<JsonNode?> ReceiveAsync(int timeoutMs = 60_000)
	{
		using var cts = new CancellationTokenSource(timeoutMs);
		
		try {
			
			var line = await _reader.ReadLineAsync(cts.Token);
			
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
	
	// Sends a tool call and validates the response. The test name is owned by
	// the calling TestCase — this method only concerns itself with the wire protocol.
	internal async Task<(bool pass, string message)> RunTestAsync(
		string toolName,
		object arguments,
		Func<JsonNode?, bool> validate,
		bool expectJson = true)
	{
		var sw = Stopwatch.StartNew();
		
		await SendAsync(new {
			
			jsonrpc = "2.0",
			id      = NextId(),
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
			// Wrap plain string content as JSON for validation.
			data = JsonValue.Create(content)
			;
		}
		
		var pass = validate(data);
		
		return pass
			? (true,  $"PASS  [{sw.ElapsedMilliseconds}ms]")
			: (false, $"FAIL  (validation failed) [{sw.ElapsedMilliseconds}ms]");
	}
}
