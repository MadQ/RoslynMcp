using System.Diagnostics;

namespace RoslynMcp;

/// <summary>
///     One MSBuild run at a time, process-wide (#379): a workspace load or reload (an MSBuild design-time
///     build) or a <c>dotnet</c> build, clean or restore (<see cref="Tools.DotnetRunner"/>). Each of them writes
///     generated files under the projects' <c>obj\</c> — <c>GlobalUsings.g.cs</c>, assembly info, state files —
///     and two at once over the same projects collide ("Cannot create a file when that file already exists"),
///     which can leave a project loaded without metadata references (#300). Two loads of one cache key already
///     share one load (#377); this gate covers the rest: loads under different keys, the reload-once retries of
///     different workspaces, and a load beside a <c>dotnet</c> command.
///     <para>
///         Global rather than per directory, as <see cref="Tools.DotnetRunner"/>'s gate was: which projects two
///         runs share is not known before they run, concurrent runs over unrelated solutions are rare, and for
///         operations that write to disk correctness beats throughput.
///     </para>
///     <para>
///         Never taken while holding <c>cacheLock</c> (#277): workspace loads run outside it. A reload takes its
///         instance's <c>reloadGate</c> first and this gate second; nothing takes them the other way round.
///     </para>
/// </summary>
internal static class MSBuildGate
{
	static readonly SemaphoreSlim gate = new(1, 1);
	
	// What holds the gate, for the note of whoever waits behind it. Set after taking the gate and cleared
	// before releasing it, so a waiter can read null in between; it then says "another MSBuild run".
	static string? holder;
	
	/// <summary>
	///     What holds the gate right now, as a phrase for a wait note. A best guess, not part of the locking: a
	///     waiter reads it once before waiting, so its note names the holder it found then, not any that took
	///     the gate in between.
	/// </summary>
	internal static string Holder => Volatile.Read(ref holder) ?? "another MSBuild run";
	
	/// <summary>Takes the gate if it is free, without waiting.</summary>
	internal static bool TryEnter(string what)
	{
		if(!gate.Wait(0))
			
			return false;
		
		Volatile.Write(ref holder, what);
		
		return true;
	}
	
	/// <summary>Waits for the gate. A cancelled wait throws and does not hold it.</summary>
	internal static async Task EnterAsync(string what, CancellationToken cancellationToken)
	{
		await gate.WaitAsync(cancellationToken);
		Volatile.Write(ref holder, what);
	}
	
	/// <summary>
	///     Takes the gate for a synchronous caller, waiting at most <paramref name="limit"/>
	///     (<see cref="Timeout.InfiniteTimeSpan"/>: as long as it takes). A wait is reported to
	///     <paramref name="record"/>. When the limit passes, returns <see langword="false"/> without the gate,
	///     with <paramref name="behind"/> naming what held it: the caller must then fail rather than run beside
	///     it — the collision on obj\ is what the gate exists to prevent, and a long build is not a stuck one.
	///     <paramref name="cancellationToken"/> abandons the wait (an <see cref="OperationCanceledException"/>,
	///     the gate not held).
	/// </summary>
	internal static bool Enter(string what, TimeSpan limit, Action<string>? record, CancellationToken cancellationToken, out string behind)
	{
		behind = "";
		
		if(TryEnter(what))
			
			return true;
		
		behind = Holder;
		
		var waited = Stopwatch.StartNew();
		
		if(!gate.Wait(limit, cancellationToken)) {
			
			record?.Invoke($"gave up after {waited.ElapsedMilliseconds} ms queued behind {behind}");
			
			return false;
		}
		
		Volatile.Write(ref holder, what);
		record?.Invoke($"queued {waited.ElapsedMilliseconds} ms behind {behind}");
		
		return true;
	}
	
	/// <summary>Releases the gate taken by <see cref="TryEnter"/>, <see cref="EnterAsync"/> or <see cref="Enter"/>.</summary>
	internal static void Release()
	{
		Volatile.Write(ref holder, null);
		gate.Release();
	}
}
