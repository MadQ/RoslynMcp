using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;

// The harness's own reading of the server's answers (#455). Every test takes the next message as
// the answer to the call it just made, which holds only while no answer is late. When a read gave
// up on a call after its timeout, the answer came later and the next call took it for its own;
// from there every call of the group was one answer behind. One call that waited 76 seconds
// failed 13 of 19 tests that way, none of them broken (#445).
//
// No server here: a TestContext over streams the test feeds, so that the late answer arrives
// exactly when the test says. What is checked is which message a read returns.
static class HarnessProtocolTests
{
	internal static TestGroup Build(TestContext ctx)
	{
		// A context whose "server" is the returned feed: each line given to it is a message from the
		// server. What the context sends goes nowhere, and what it would print is collected.
		(TestContext Context, Func<string, Task> Feed, List<string> Said) Open()
		{
			var pipe = new Pipe();
			var said = new List<string>();
			
			var context = new TestContext(
				new StreamWriter(Stream.Null),
				new StreamReader(pipe.Reader.AsStream(), Encoding.UTF8),
				ctx.TargetPath, ctx.RepoRoot, ctx.ServerProj) { Diagnostic = said.Add };
			
			async Task Feed(string line) => await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
			
			return (context, Feed, said);
		}
		
		static object Request(int id) => new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name = "any" } };
		
		static string Answer(int id, string text) => $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{{\"text\":\"{text}\"}}}}";
		
		static string? Text(JsonNode? message) => message?["result"]?["text"]?.GetValue<string>();
		
		var tests = new List<TestCase> {
			
			// The case that failed whole groups. Request 1 is read with a short timeout and given up
			// on. Request 2 is sent, and then both answers arrive, the late one first, as they did.
			// The read for request 2 must return request 2's answer, and say that it dropped the
			// other: before, it returned "first" and the group went on one answer behind.
			new("harness protocol: an answer that comes after its read gave up is not the next call's answer", async () => {
				
				var (context, feed, said) = Open();
				
				await context.SendAsync(Request(1));
				
				if(await context.ReceiveAsync(100) is not null)
					return (false, "FAIL  (a read with nothing to read returned a message)");
				
				await context.SendAsync(Request(2));
				await feed(Answer(1, "first"));
				await feed(Answer(2, "second"));
				
				var text = Text(await context.ReceiveAsync(5_000));
				
				if(text != "second")
					return (false, $"FAIL  (the read for request 2 returned '{text ?? "nothing"}')");
				
				return said.Any(line => line.Contains("request 1", StringComparison.Ordinal))
					? (true, "PASS  (request 2 got its own answer; the late one was dropped and reported)")
					: (false, $"FAIL  (the dropped answer was not reported: {string.Join(" | ", said)})");
			}),
			
			// Matching must not turn into "only the last request's answer counts": a test may send
			// several requests before it reads (Parallel Load, the cancellation check of Dotnet
			// Runner), and their answers come in the order the server finishes them. Both are
			// returned, in arrival order, and nothing is dropped.
			new("harness protocol: several requests sent before a read all get their answers", async () => {
				
				var (context, feed, said) = Open();
				
				await context.SendAsync(Request(1));
				await context.SendAsync(Request(2));
				await feed(Answer(2, "second"));
				await feed(Answer(1, "first"));
				
				var first  = Text(await context.ReceiveAsync(5_000));
				var second = Text(await context.ReceiveAsync(5_000));
				
				return first == "second" && second == "first" && said.Count == 0
					? (true, "PASS  (both answers returned in arrival order)")
					: (false, $"FAIL  (read '{first ?? "nothing"}' then '{second ?? "nothing"}'; said: {string.Join(" | ", said)})");
			}),
			
			// Only an answer can be late. After request 7 is given up on, the server sends a
			// notification and a request of its own that happens to carry the id 7 (the two sides
			// number their requests independently): both are the caller's to see. The answer to
			// request 7 that follows is still dropped, and the read after it ends on its timeout
			// with nothing, not with that answer.
			new("harness protocol: notifications and the server's own requests are never taken for a late answer", async () => {
				
				var (context, feed, said) = Open();
				
				await context.SendAsync(Request(7));
				await context.ReceiveAsync(100);
				
				await feed("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\",\"params\":{}}");
				await feed("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"elicitation/create\",\"params\":{}}");
				await feed(Answer(7, "late"));
				
				var notification = await context.ReceiveAsync(5_000);
				var request      = await context.ReceiveAsync(5_000);
				var nothing      = await context.ReceiveAsync(300);
				
				var pass = notification?["method"]?.GetValue<string>() == "notifications/message"
					&& request?["method"]?.GetValue<string>() == "elicitation/create"
					&& nothing is null
					&& said.Count == 1;
				
				return pass
					? (true, "PASS  (both passed through; the late answer was dropped)")
					: (false, $"FAIL  (notification: {notification?.ToJsonString() ?? "none"} | request: {request?.ToJsonString() ?? "none"} | then: {nothing?.ToJsonString() ?? "nothing"} | said: {string.Join(" | ", said)})");
			}),
		};
		
		return new TestGroup($"Harness Protocol ({tests.Count} tests)", tests);
	}
}
