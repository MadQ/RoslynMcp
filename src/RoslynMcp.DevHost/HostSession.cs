using System.Diagnostics;
using System.Text.Json;

namespace RoslynMcp.DevHost;

/// <summary>
///     One client session: forwards every message between the client and the server the pointer file
///     names, and puts another server in the first one's place when the pointer changes (#426).
///     <para>
///     A swap happens only while nobody waits for an answer, in either direction, so no call is ever cut
///     off; the host does not hold requests back to get there. The new server is started and has answered
///     the client's own <c>initialize</c>, replayed, before the old one is touched, so a build that does
///     not run costs the session nothing. What the old server knew is gone with it: preview tokens,
///     session approvals, the loaded workspace.
///     </para>
/// </summary>
sealed class HostSession(DevHome home, HostLog log, IReadOnlyList<string> arguments)
{
	static readonly TimeSpan PollInterval     = TimeSpan.FromMilliseconds(500);
	static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);
	static readonly TimeSpan OutputGrace      = TimeSpan.FromSeconds(2);
	static readonly TimeSpan OutputLimit      = TimeSpan.FromSeconds(30);
	static readonly byte[]   ToolsChanged     = [.."""{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}"""u8, (byte) '\n'];
	
	readonly Stream                    clientOutput  = Console.OpenStandardOutput();
	readonly object                    sync          = new();
	readonly HashSet<string>           clientWaiting = [];
	readonly HashSet<string>           serverWaiting = [];
	readonly TaskCompletionSource<int> ended         = new(TaskCreationOptions.RunContinuationsAsynchronously);
	
	// Held for every write to the server, and for the whole of a swap: a message of the client's waits
	// in its pipe meanwhile and goes to whichever server is current afterwards.
	readonly SemaphoreSlim toServer   = new(1, 1);
	readonly SemaphoreSlim toClient   = new(1, 1);
	readonly SemaphoreSlim idleSignal = new(0, 1);
	
	// Guarded by sync.
	ServerProcess? server;
	BuildPointer   running;
	BuildPointer?  refused;
	BuildPointer?  announced;
	byte[]?        initialize;
	string?        initializeId;
	byte[]?        initialized;
	string?        protocolVersion;
	bool           closing;
	bool           fellBack;
	
	public async Task<int> RunAsync()
	{
		if(home.ReadPointer() is not { } pointer) {
			
			log.Problem($"No build to run: {home.PointerPath} is missing or empty. Publish one with scripts/Publish-DevServer.ps1.");
			
			return 2;
		}
		
		running = pointer;
		
		var first = ServerProcess.Start(home.Resolve(pointer.Value), pointer.Value, arguments, out var failure)
			?? StartLastGood(pointer.Value, failure)
		;
		
		if(first is null)
			
			return 2;
		
		server = first;
		log.Write($"Host started: build {first.Build} (pid {first.Id}).");
		
		_ = Guard(RunServerAsync(first));
		_ = Guard(PumpClientAsync());
		_ = Guard(WatchPointerAsync());
		
		return await ended.Task;
	}
	
	// -- Client to server ---------------------------------------------------------------------------
	
	async Task PumpClientAsync()
	{
		var reader = new LineReader(Console.OpenStandardInput());
		
		while(true) {
			
			var line = await reader.ReadLineAsync();
			
			if(line.IsEmpty)
				
				break;
			
			var head = MessageHead.Read(line.Span);
			
			await toServer.WaitAsync();
			
			try {
				
				ServerProcess target;
				
				// A request counts as waiting before the server can answer it.
				lock(sync) {
					
					target = server!;
					
					if(head.IsRequest) {
						
						clientWaiting.Add(head.Id!);
						
						if(head.Method == MessageHead.Initialize && initialize is null) {
							
							initialize   = line.ToArray();
							initializeId = head.Id;
						}
					}
					else if(head.Method == MessageHead.Initialized)
						initialized ??= line.ToArray();
				}
				
				await WriteAsync(target.Input, line);
			}
			catch(Exception failure) when(failure is IOException or ObjectDisposedException) {
				// The server is gone. Its own pump ends the session, or starts the last good build.
			}
			finally {
				toServer.Release();
			}
			
			// An answer stops counting only once it has been passed on.
			if(head.IsResponse)
				Settle(serverWaiting, head.Id);
			else if(head.Method == MessageHead.Cancelled)
				Settle(clientWaiting, MessageHead.CancelledId(line.Span));
		}
		
		// The client closed its end: the session is over, and the server is told the same way.
		ServerProcess last;
		
		await toServer.WaitAsync();
		
		try {
			
			lock(sync) {
				
				closing = true;
				last    = server!;
			}
		}
		finally {
			toServer.Release();
		}
		
		await last.StopAsync();
		log.Write("The client closed the session.");
		ended.TrySetResult(0);
	}
	
	// -- Server to client ---------------------------------------------------------------------------
	
	async Task PumpServerAsync(ServerProcess source)
	{
		while(true) {
			
			ReadOnlyMemory<byte> line;
			
			// A pipe that breaks is a server that is gone, as the end of its output is.
			try {
				line = await source.Output.ReadLineAsync();
			}
			catch(IOException) {
				break;
			}
			
			if(line.IsEmpty)
				
				break;
			
			// From here until it is passed on or dropped, the line is at work (RunServerAsync).
			source.MarkForward();
			
			var head    = MessageHead.Read(line.Span);
			var retired = false;
			
			lock(sync) {
				
				// A server that was swapped out has nobody waiting for it, so nothing it still says is an answer.
				if(source != server)
					retired = true;
				else if(head.IsRequest)
					serverWaiting.Add(head.Id!);
			}
			
			if(retired) {
				
				source.MarkForward();
				
				continue;
			}
			
			await WriteClientAsync(line);
			source.MarkForward();
			
			if(head.IsResponse) {
				
				NoteFirstHandshake(source, head.Id!, line);
				Settle(clientWaiting, head.Id);
			}
			else if(head.Method == MessageHead.Cancelled)
				Settle(serverWaiting, MessageHead.CancelledId(line.Span));
		}
	}
	
	/// <summary>
	///     Forwards a server's output until the server is gone. The end of its output is not enough to
	///     know that: a process the server started (an MSBuild node, a compiler server) inherits its
	///     stdout and can hold it open long after the server exited, and a read from that pipe cannot be
	///     cancelled. So the process's exit counts as well, and what it left in the pipe is still passed
	///     on: the forwarder is given up only once it has been quiet for a while, never in the middle of
	///     a line or while lines keep coming, however slowly the client takes them.
	/// </summary>
	async Task RunServerAsync(ServerProcess source)
	{
		var pump = PumpServerAsync(source);
		
		await Task.WhenAny(pump, source.WaitForExitAsync());
		
		// Bounded: whatever holds the pipe may also write to it now and then, and a server that is gone
		// must end the session all the same.
		var waited = Stopwatch.StartNew();
		
		while(!pump.IsCompleted && waited.Elapsed < OutputLimit) {
			
			var seen = source.Forwards;
			
			await Task.WhenAny(pump, Task.Delay(OutputGrace));
			
			if(source.Forwards == seen && seen % 2 == 0)
				
				break;
		}
		
		// A failed pump is the host's failure; one still reading has nothing left that anyone waits for.
		if(pump.IsCompleted)
			await pump;
		
		await ServerEndedAsync(source);
	}
	
	// The answer to the client's own initialize: the protocol version every later server must agree to.
	void NoteFirstHandshake(ServerProcess source, string id, ReadOnlyMemory<byte> line)
	{
		bool good;
		
		lock(sync) {
			
			if(protocolVersion is not null || id != initializeId || ProtocolVersion(line) is not { } version)
				
				return;
			
			protocolVersion = version;
			good            = !fellBack;
		}
		
		log.Write($"Build {source.Build} (pid {source.Id}) answered initialize: protocol {protocolVersion}.");
		
		if(good)
			home.WriteLastGood(source.Build);
	}
	
	/// <summary>
	///     The current server exited by itself: <c>roslyn_respawn</c>, or a crash. The host exits with the
	///     same code, so the client restarts what it started, as it does without a host. The one exception
	///     is a server that never answered <c>initialize</c>: a build that does not run would fail every
	///     new session, so the last good build takes its place once.
	/// </summary>
	async Task ServerEndedAsync(ServerProcess source)
	{
		var code = await source.WaitForExitAsync();
		
		await toServer.WaitAsync();
		
		try {
			
			bool neverAnswered;
			
			lock(sync) {
				
				if(closing || source != server)
					
					return;
				
				neverAnswered = protocolVersion is null && !fellBack;
			}
			
			if(neverAnswered && StartLastGood(source.Build, $"it exited with code {code} before answering initialize") is { } replacement) {
				
				byte[]? replay;
				
				lock(sync) {
					
					server = replacement;
					replay = initialize;
				}
				
				if(replay is not null)
					await WriteAsync(replacement.Input, replay);
				
				_ = Guard(RunServerAsync(replacement));
				
				return;
			}
			
			log.Write($"Build {source.Build} (pid {source.Id}) exited with code {code}; the host exits with it.");
			ended.TrySetResult(code);
		}
		finally {
			toServer.Release();
		}
	}
	
	ServerProcess? StartLastGood(string build, string failure)
	{
		log.Problem($"Build {build} does not run: {failure}.");
		
		if(home.ReadLastGood() is not { } lastGood || lastGood == build)
			
			return null;
		
		var replacement = ServerProcess.Start(home.Resolve(lastGood), lastGood, arguments, out var lastGoodFailure);
		
		if(replacement is null) {
			
			log.Problem($"The last good build {lastGood} does not run either: {lastGoodFailure}.");
			
			return null;
		}
		
		lock(sync)
			fellBack = true;
		
		log.Problem($"Running the last good build {lastGood} (pid {replacement.Id}) instead.");
		
		return replacement;
	}
	
	// -- Swap ---------------------------------------------------------------------------------------
	
	async Task WatchPointerAsync()
	{
		while(true) {
			
			// Woken when the last waiting request is answered, and otherwise to look at the pointer.
			await idleSignal.WaitAsync(PollInterval);
			
			if(home.ReadPointer() is not { } wanted)
				
				continue;
			
			lock(sync) {
				
				if(wanted == running || wanted == refused)
					
					continue;
			}
			
			if(!IsIdle(wanted))
				
				continue;
			
			await toServer.WaitAsync();
			
			try {
				
				lock(sync) {
					
					if(closing)
						
						return;
				}
				
				// Looked at again now that the client's next request cannot get in between.
				if(IsIdle(wanted))
					await SwapAsync(wanted);
			}
			finally {
				toServer.Release();
			}
		}
	}
	
	bool IsIdle(BuildPointer wanted)
	{
		string waiting;
		
		lock(sync) {
			
			var handshaken = initialized is not null && protocolVersion is not null;
			
			if(handshaken && clientWaiting.Count == 0 && serverWaiting.Count == 0)
				
				return true;
			
			if(announced == wanted)
				
				return false;
			
			announced = wanted;
			waiting   = handshaken
				? $"{clientWaiting.Count + serverWaiting.Count} request(s) to be answered"
				: "the client's handshake"
			;
		}
		
		log.Write($"Build {wanted.Value} is wanted; waiting for {waiting}.");
		
		return false;
	}
	
	// Called with toServer held, so the client's messages wait until the swap is over either way.
	async Task SwapAsync(BuildPointer wanted)
	{
		var watch = Stopwatch.StartNew();
		var old   = server!;
		var next  = ServerProcess.Start(home.Resolve(wanted.Value), wanted.Value, arguments, out var failure);
		
		if(next is null) {
			
			Refuse(wanted, failure);
			
			return;
		}
		
		// A read from the server's pipe cannot be cancelled, so a server that says nothing is killed,
		// which ends the read.
		var handshake = HandshakeAsync(next);
		var timedOut  = await Task.WhenAny(handshake, Task.Delay(HandshakeTimeout)) != handshake;
		
		if(timedOut)
			next.Kill();
		
		var problem = await handshake;
		
		// A server that answered in the moment it was killed for being late is still a killed server.
		if(timedOut || problem is not null) {
			
			next.Kill();
			Refuse(wanted, timedOut ? $"it did not answer initialize within {HandshakeTimeout.TotalSeconds:0} s" : problem!);
			
			return;
		}
		
		bool asked;
		
		lock(sync) {
			
			// The old server may have asked the client something while the new one started. The answer
			// must reach the server that asked, so this swap is given up and tried at the next idle moment.
			asked = serverWaiting.Count > 0;
			
			if(!asked) {
				
				server   = next;
				running  = wanted;
				refused  = null;
				fellBack = false;
			}
		}
		
		if(asked) {
			
			next.Kill();
			log.Write($"Build {wanted.Value} was ready, but build {old.Build} asked the client something meanwhile; the swap is tried again.");
			
			return;
		}
		
		_ = Guard(RunServerAsync(next));
		_ = Guard(old.StopAsync());
		
		home.WriteLastGood(wanted.Value);
		
		// The new build may have other tools, or other descriptions of the same ones.
		await WriteClientAsync(ToolsChanged);
		
		log.Write($"Swapped to build {next.Build} (pid {next.Id}) in {watch.ElapsedMilliseconds} ms; build {old.Build} (pid {old.Id}) is stopped.");
	}
	
	// Null when the server answered as the first one did and is ready for requests; else what went wrong.
	async Task<string?> HandshakeAsync(ServerProcess next)
	{
		try {
			
			await WriteAsync(next.Input, initialize!);
			
			while(true) {
				
				var line = await next.Output.ReadLineAsync();
				
				if(line.IsEmpty)
					
					return "it exited before answering initialize";
				
				var head = MessageHead.Read(line.Span);
				
				if(!head.IsResponse || head.Id != initializeId)
					
					continue;
				
				var version = ProtocolVersion(line);
				
				if(version != protocolVersion)
					
					return $"it answered initialize with protocol version {version ?? "none"}, and the session runs on {protocolVersion}";
				
				await WriteAsync(next.Input, initialized!);
				
				return null;
			}
		}
		catch(Exception failure) when(failure is IOException or ObjectDisposedException) {
			
			// The same thing as the empty read above, seen from the other side: a build that
			// has exited is found out by the write to it when that comes before the read, which
			// on Linux it does now and then. One wording for both, so that the log, and whoever
			// looks for the reason in it, does not depend on which came first.
			return $"it exited before answering initialize (the pipe to it broke: {failure.Message})";
		}
	}
	
	// A pointer that was refused is not tried again until it is written again.
	void Refuse(BuildPointer wanted, string reason)
	{
		string current;
		
		lock(sync) {
			
			refused = wanted;
			current = server!.Build;
		}
		
		log.Problem($"Build {wanted.Value} was not swapped in: {reason}. Still running build {current}.");
	}
	
	// -- Shared -------------------------------------------------------------------------------------
	
	void Settle(HashSet<string> waiting, string? id)
	{
		if(id is null)
			
			return;
		
		lock(sync) {
			
			waiting.Remove(id);
			
			if(clientWaiting.Count > 0 || serverWaiting.Count > 0)
				
				return;
		}
		
		try {
			idleSignal.Release();
		}
		catch(SemaphoreFullException) {
			// Already signalled.
		}
	}
	
	static string? ProtocolVersion(ReadOnlyMemory<byte> line)
	{
		try {
			
			using var message = JsonDocument.Parse(line);
			
			return message.RootElement.TryGetProperty("result", out var result)
				&& result.ValueKind == JsonValueKind.Object
				&& result.TryGetProperty("protocolVersion", out var version)
				&& version.ValueKind == JsonValueKind.String
					? version.GetString()
					: null
			;
		}
		catch(JsonException) {
			return null;
		}
	}
	
	async Task WriteClientAsync(ReadOnlyMemory<byte> line)
	{
		await toClient.WaitAsync();
		
		try {
			await WriteAsync(clientOutput, line);
		}
		finally {
			toClient.Release();
		}
	}
	
	static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> line)
	{
		await stream.WriteAsync(line);
		await stream.FlushAsync();
	}
	
	// A failure nobody expected ends the session, loudly, and never leaves a host that forwards nothing.
	async Task Guard(Task work)
	{
		try {
			await work;
		}
		catch(Exception failure) {
			
			log.Problem($"The host failed: {failure}");
			
			lock(sync)
				server?.Kill();
			
			ended.TrySetResult(1);
		}
	}
}
