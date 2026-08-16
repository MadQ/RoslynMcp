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
	
	public string Register(Solution baseSolution, Solution newSolution, string diff, string symbolKey, ApprovalWorkflow workflow)
		=> Register(baseSolution, newSolution, diff, symbolKey, workflow, null);
	
	public string Register(Solution baseSolution, Solution newSolution, string diff, string symbolKey, ApprovalWorkflow workflow, (string OldPath, string NewPath)? fileRename)
		=> Register(baseSolution, newSolution, diff, symbolKey, workflow, fileRename, null);
	
	public string Register(
		Solution baseSolution,
		Solution newSolution,
		string diff,
		string symbolKey,
		ApprovalWorkflow workflow,
		(string OldPath, string NewPath)? fileRename,
		IReadOnlyDictionary<string, PreviewFileState>? fileStates,
		WorkspaceBinding? workspaceBinding = null)
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
			pending[token] = new PendingOperation(baseSolution, newSolution, diff, symbolKey, workflow, preConfirmed, fileRename, fileStates, workspaceBinding);
			insertionOrder.AddLast(token);
		}
		
		return token;
	}
	
	/// <summary>
	///     Retrieves a pending operation by token without consuming it.
	///     Returns null if the token is unknown, already consumed, or belongs to a different
	///     approval workflow than <paramref name="expected"/> — a codefix token must never be
	///     applied through the rename path, and vice-versa.
	/// </summary>
	public PendingOperation? Peek(string token, ApprovalWorkflow expected)
	{
		lock(syncRoot) {
			
			var operation = pending.GetValueOrDefault(token);
			
			return operation?.Workflow == expected ? operation : null;
		}
	}
	
	/// <summary>
	///     Atomically moves a pending operation into the applying state and returns it.
	///     Returns null when the token is unknown, consumed, already being applied, or belongs to a
	///     different workflow than <paramref name="expected"/>. A workflow mismatch leaves the entry
	///     untouched so the legitimate owner can still apply it.
	/// </summary>
	public PendingOperation? TryBeginApply(string token, ApprovalWorkflow expected)
	{
		lock(syncRoot) {
			
			if(!pending.TryGetValue(token, out var operation) || operation.Workflow != expected)
				
				return null;
			
			pending.Remove(token);
			insertionOrder.Remove(token);
			applying.Add(token, operation);
			
			return operation;
		}
	}
	
	/// <summary>
	///     Completes an applying operation and permanently consumes its token.
	///     A workflow mismatch leaves the applying entry untouched and returns false.
	/// </summary>
	public bool CompleteApply(string token, ApprovalWorkflow expected, bool approveForSession)
	{
		lock(syncRoot) {
			
			if(!applying.TryGetValue(token, out var operation) || operation.Workflow != expected)
				
				return false;
			
			applying.Remove(token);
			
			if(approveForSession)
				sessionApproved.Add(operation.SymbolKey);
			
			return true;
		}
	}
	
	/// <summary>
	///     Returns an applying operation to pending when physical mutation never began.
	///     A workflow mismatch leaves the applying entry untouched and returns false.
	/// </summary>
	public bool ReturnToPending(string token, ApprovalWorkflow expected)
	{
		lock(syncRoot) {
			
			if(!applying.TryGetValue(token, out var operation) || operation.Workflow != expected)
				
				return false;
			
			applying.Remove(token);
			
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
	///     A workflow mismatch leaves the entry untouched and returns null.
	/// </summary>
	public PendingOperation? Consume(string token, ApprovalWorkflow expected, bool approveForSession)
	{
		lock(syncRoot) {
			
			if(!pending.TryGetValue(token, out var op) || op.Workflow != expected)
				
				return null;
			
			pending.Remove(token);
			insertionOrder.Remove(token);
			
			if(approveForSession)
				sessionApproved.Add(op.SymbolKey);
			
			return op;
		}
	}
	
	/// <summary>
	///     Rejects a pending token. A workflow mismatch leaves the entry untouched and returns false —
	///     critical so a stray reject on the wrong path cannot discard another workflow's pending op.
	/// </summary>
	public bool Reject(string token, ApprovalWorkflow expected)
	{
		lock(syncRoot) {
			
			if(!pending.TryGetValue(token, out var op) || op.Workflow != expected)
				
				return false;
			
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
	ApprovalWorkflow                  Workflow,
	bool                              PreConfirmed,
	(string OldPath, string NewPath)? FileRename,
	IReadOnlyDictionary<string, PreviewFileState>? FileStates,
	WorkspaceBinding?                 WorkspaceBinding
);

/// <summary>
///     Identifies which two-phase preview/apply workflow a pending token belongs to.
///     Enforced on every ApprovalStore accessor so a token minted by one workflow can never be
///     applied or rejected through another — each apply path has different safety invariants.
/// </summary>
internal enum ApprovalWorkflow
{
	Rename,
	SignatureChange,
	CodeFix
}

internal sealed record WorkspaceBinding(string CanonicalPath, bool IsMSBuild)
{
	public static WorkspaceBinding Create(string rootPath, bool isMSBuild, string? csprojPath)
	{
		var identityPath = isMSBuild && csprojPath is not null ? csprojPath : rootPath;
		var canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(identityPath));
		
		return new WorkspaceBinding(canonicalPath, isMSBuild);
	}
	
	public bool Matches(WorkspaceBinding other) =>
		IsMSBuild == other.IsMSBuild
		&& string.Equals(CanonicalPath, other.CanonicalPath, StringComparison.OrdinalIgnoreCase);
}

internal enum ExpectedFileState
{
	Exists,
	Absent
}

internal sealed record PreviewFileState(
	ExpectedFileState ExpectedState,
	string?           ContentHash,
	byte[]?           IntendedBytes);
