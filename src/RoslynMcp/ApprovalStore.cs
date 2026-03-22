using Microsoft.CodeAnalysis;

namespace RoslynMcp;

/// <summary>
///     Holds pending and session-approved write operations for the lifetime of the server process.
///     All state is in-memory and lost on restart — intentional, no persistence needed.
/// </summary>
internal sealed class ApprovalStore
{
    // Pending previews: token → (solution with edits applied, human-readable diff, symbol key)
    private readonly Dictionary<string, PendingOperation> pending = new();

    // Session-approved symbol keys: once a rename of a given symbol is approved with "session",
    // future previews of the same symbol are auto-confirmed without a new token exchange.
    private readonly HashSet<string> sessionApproved = new(StringComparer.Ordinal);

    // Lock object for thread-safe access to pending and sessionApproved.
    // Note: System.Threading.Lock (introduced .NET 9) would be preferable for performance and safety,
    // but we target .NET 8/10/11 and need compatibility with .NET 8. Once .NET 8 support is dropped,
    // consider upgrading to Lock for better lock semantics and reduced allocations.
    private readonly object syncRoot = new();

    /// <summary>
    ///     Registers a pending operation and returns its confirmation token.
    ///     If the symbol key is already session-approved, marks the token as pre-confirmed.
    /// </summary>
    public string Register(Solution newSolution, string diff, string symbolKey)
    {
        var token = Guid.NewGuid().ToString("N")[..12];

        lock(syncRoot) {
			var preConfirmed = sessionApproved.Contains(symbolKey);
            pending[token]   = new PendingOperation(newSolution, diff, symbolKey, preConfirmed);
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
    ///     Consumes a token and returns the operation.
    ///     Optionally marks the symbol key as session-approved so future previews skip confirmation.
    /// </summary>
    public PendingOperation? Consume(string token, bool approveForSession)
    {
        lock(syncRoot) {
			if(!pending.Remove(token, out var op))
                return null;

            if(approveForSession)
                sessionApproved.Add(op.SymbolKey);

            return op;
		}
	}

	/// <summary>Discards a token without applying anything.</summary>
	public bool Reject(string token)
    {
        lock(syncRoot)
            return pending.Remove(token);
    }

    public bool IsSessionApproved(string symbolKey)
    {
        lock(syncRoot)
            return sessionApproved.Contains(symbolKey);
    }
}

internal sealed record PendingOperation(
    Solution NewSolution,
    string   Diff,
    string   SymbolKey,
    bool     PreConfirmed
);
