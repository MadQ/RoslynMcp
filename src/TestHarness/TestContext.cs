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
	
	// The ids of the requests sent whose answer has not been read, and of those a read gave up on
	// (#455). Both under the lock of the first: a send and a read may overlap.
	private readonly HashSet<string> _owed      = [];
	private readonly HashSet<string> _abandoned = [];
	
	public string TargetPath { get; }
	public string RepoRoot   { get; }
	public string ServerProj { get; }
	
	// Where a failure to read a response is reported. Stderr unless the owner says otherwise; the
	// harness run supplies its own, so that quiet mode can hold the message for its report.
	internal Action<string> Diagnostic { get; init; } = Console.Error.WriteLine;
	
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
		
		// A request, as opposed to a notification or an answer to the server's own request, is owed
		// an answer: noted before it is written, since the answer can be read before this returns.
		if(JsonNode.Parse(line) is JsonObject message && message["method"] is not null && message["id"] is { } id)
			lock(_owed)
				_owed.Add(id.ToJsonString());
		
		await _writer.WriteLineAsync(line);
		await _writer.FlushAsync();
	}
	
	/// <summary>
	///     The next message from the server, or null after <paramref name="timeoutMs"/>.
	///     <para>
	///         Answers are matched to requests by id (#455). The callers take what this returns as the
	///         answer to the call they just made, and when a read gave up on a call, its answer was
	///         still to come: the next call then read it as its own, and every call after that was
	///         one answer behind. One call that waited 76 seconds failed 13 of a group's 19 tests
	///         that way, each with the previous call's response in its message (#445). So a read that
	///         times out abandons the requests it was owed answers to, and an answer to an abandoned
	///         request is dropped when it comes, with a line saying so.
	///     </para>
	///     <para>
	///         Everything else is returned as it arrives: notifications, the server's own requests,
	///         and the answers to several requests sent before any read, in whatever order they come.
	///     </para>
	/// </summary>
	internal async Task<JsonNode?> ReceiveAsync(int timeoutMs = 60_000)
	{
		using var cts = new CancellationTokenSource(timeoutMs);
		
		try {
			
			while(true) {
				
				var line = await _reader.ReadLineAsync(cts.Token);
				
				if(line is null)
					
					return null;
				
				var message = JsonNode.Parse(line);
				
				if(!IsLateAnswer(message, out var id))
					
					return message;
				
				Diagnostic($"[late answer] to request {id}, which a read had given up on — dropped");
			}
		}
		catch(OperationCanceledException) {
			
			// Timeout. Whatever is still owed is owed to a caller that has stopped waiting.
			lock(_owed) {
				
				_abandoned.UnionWith(_owed);
				_owed.Clear();
			}
			
			return null;
		}
		catch(Exception ex) {
			
			Diagnostic($"[recv error] {ex.Message}");
			
			return null; // Pipe closed or other I/O error.
		}
	}
	
	// An answer — a message with an id and no method — to a request a read gave up on. An answer
	// that is still owed is taken off the list here, on its way to the caller.
	bool IsLateAnswer(JsonNode? message, out string id)
	{
		id = "";
		
		if(message is not JsonObject answer || answer["method"] is not null || answer["id"] is not { } node)
			
			return false;
		
		id = node.ToJsonString();
		
		lock(_owed) {
			
			if(_abandoned.Remove(id))
				
				return true;
			
			_owed.Remove(id);
			
			return false;
		}
	}
	
	/// <summary>
	///     Calls a tool and returns its first content block parsed as JSON: a tool's result, or the header of a
	///     source response (#328). Null when the call failed outright (a JSON-RPC error has no result), timed
	///     out, or answered with a block that is not JSON. The one copy of what nine test groups each wrote as
	///     a local <c>Call</c> (#294 review).
	/// </summary>
	internal async Task<JsonNode?> CallToolJsonAsync(string tool, object args)
	{
		await SendAsync(new { jsonrpc = "2.0", id = NextId(), method = "tools/call", @params = new { name = tool, arguments = args } });
		
		var text = (await ReceiveAsync())?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
		
		if(string.IsNullOrEmpty(text))
			
			return null;
		
		try {
			return JsonNode.Parse(text);
		}
		catch(System.Text.Json.JsonException) {
			return null;
		}
	}
	
	// The common case: a tool whose whole response is its first content block.
	internal Task<(bool pass, string message)> RunTestAsync(
		string toolName,
		object arguments,
		Func<JsonNode?, bool> validate,
		bool expectJson = true)
		=> RunTestAsync(toolName, arguments, (data, _) => validate(data), expectJson)
	;
	
	// The validator also receives the text of every content block after the first: the
	// source-returning tools answer with a JSON header block followed by raw source blocks (#328).
	// Sends a tool call and validates the response. The test name is owned by
	// the calling TestCase — this method only concerns itself with the wire protocol.
	internal async Task<(bool pass, string message)> RunTestAsync(
		string toolName,
		object arguments,
		Func<JsonNode?, string[], bool> validate,
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
		
		var texts = result["content"]?.AsArray().Select(block => block?["text"]?.GetValue<string>()).ToArray() ?? [];
		
		if(texts is not [{ } content, ..])
			
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
		
		var pass = validate(data, [..texts.Skip(1).Select(text => text ?? "")]);
		
		return pass
			? (true,  $"PASS  [{sw.ElapsedMilliseconds}ms]")
			: (false, $"FAIL  (validation failed) [{sw.ElapsedMilliseconds}ms]");
	}
}
