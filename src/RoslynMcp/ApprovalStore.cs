using Microsoft.CodeAnalysis;

namespace RoslynMcp;

/// <summary>
///     Holds pending and session-approved write operations for the lifetime of the server process.
///     All state is in-memory and lost on restart — intentional, no persistence needed.
/// </summary>
internal sealed class ApprovalStore
{
	private readonly Dictionary<string, PendingOperation> pending        = new();
	private readonly Dictionary<string, PendingOperation> applying       = new();
	private readonly LinkedList<string>                   insertionOrder  = new();
	private readonly HashSet<string>                      sessionApproved = new(StringComparer.Ordinal);
	private readonly object                               syncRoot        = new();
	
	// Each PendingOperation holds two Solution snapshots — cap to prevent unbounded memory growth.
	private const int MaxPending = 10
	;
	
	public string Register(Solution baseSolution, Solution newSolution, string diff, string symbolKey)
		=> Register(baseSolution, newSolution, diff, symbolKey, null);
	
	public string Register(Solution baseSolution, Solution newSolution, string diff, string symbolKey, (string OldPath, string NewPath)? fileRename)
		=> Register(baseSolution, newSolution, diff, symbolKey, fileRename, null);
	
	public string Register(
		Solution baseSolution,
		Solution newSolution,
		string diff,
		string symbolKey,
		(string OldPath, string NewPath)? fileRename,
		IReadOnlyDictionary<string, PreviewFileState>? fileStates)
	{
		var token = Guid.NewGuid().ToString("N")[..12];
		
		lock(syncRoot) {
			
			// Evict oldest if at capacity.
			while(pending.Count >= MaxPending && insertionOrder.First is not null) {
				
				var oldest = insertionOrder.First.Value;
				insertionOrder.RemoveFirst();
				pending.Remove(oldest);
			}
			
			var preConfirmed = sessionApproved.Contains(symbolKey);
			pending[token] = new PendingOperation(baseSolution, newSolution, diff, symbolKey, preConfirmed, fileRename, fileStates);
			insertionOrder.AddLast(token);
		}
		
		return token;
	}
	
	/// <summary>
	///     Retrieves a pending operation by token without consuming it.
	///     Returns null if the token is unknown or already consumed.
	/// </summary>
	public PendingOperation? Peek(string token)
	{
		lock(syncRoot)
			
			return pending.GetValueOrDefault(token);
	}
	
	/// <summary>
	///     Atomically moves a pending operation into the applying state and returns it.
	///     Returns null when the token is unknown, consumed, or already being applied.
	/// </summary>
	public PendingOperation? TryBeginApply(string token)
	{
		lock(syncRoot) {
			
			if(!pending.Remove(token, out var operation))
				
				return null;
			
			insertionOrder.Remove(token);
			applying.Add(token, operation);
			
			return operation;
		}
	}
	
	/// <summary>
	///     Completes an applying operation and permanently consumes its token.
	/// </summary>
	public bool CompleteApply(string token, bool approveForSession)
	{
		lock(syncRoot) {
			
			if(!applying.Remove(token, out var operation))
				
				return false;
			
			if(approveForSession)
				sessionApproved.Add(operation.SymbolKey);
			
			return true;
		}
	}
	
	/// <summary>
	///     Returns an applying operation to pending when physical mutation never began.
	/// </summary>
	public bool ReturnToPending(string token)
	{
		lock(syncRoot) {
			
			if(!applying.Remove(token, out var operation))
				
				return false;
			
			while(pending.Count >= MaxPending && insertionOrder.First is not null) {
				
				var oldest = insertionOrder.First.Value;
				insertionOrder.RemoveFirst();
				pending.Remove(oldest);
			}
			
			pending.Add(token, operation);
			insertionOrder.AddLast(token);
			
			return true;
		}
	}
	
	/// <summary>
	///     Consumes a token and returns the operation.
	///     Optionally marks the symbol key as session-approved so future previews skip confirmation.
	/// </summary>
	public PendingOperation? Consume(string token, bool approveForSession)
	{
		lock(syncRoot) {
			
			if(!pending.Remove(token, out var op))
				
				return null;
			
			insertionOrder.Remove(token);
			
			if(approveForSession)
				sessionApproved.Add(op.SymbolKey);
			
			return op;
		}
	}
	
	public bool Reject(string token)
	{
		lock(syncRoot) {
			
			insertionOrder.Remove(token);
			
			return pending.Remove(token);
		}
	}
	
	public bool IsSessionApproved(string symbolKey)
	{
		lock(syncRoot)
			
			return sessionApproved.Contains(symbolKey);
	}
}

internal sealed record PendingOperation(
	Solution                          BaseSolution,
	Solution                          NewSolution,
	string                            Diff,
	string                            SymbolKey,
	bool                              PreConfirmed,
	(string OldPath, string NewPath)? FileRename,
	IReadOnlyDictionary<string, PreviewFileState>? FileStates
);

internal enum ExpectedFileState
{
	Exists,
	Absent
}

internal sealed record PreviewFileState(ExpectedFileState ExpectedState, string? ContentHash);
