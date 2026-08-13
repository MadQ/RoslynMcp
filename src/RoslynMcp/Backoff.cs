namespace RoslynMcp;

/// <summary>
///     Shared exponential-backoff schedule: <c>seedMs × 2^attempt</c>, capped at <c>maxMs</c>.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately just the delay sequence — no retry loop, no exception classification, no
///         logging. The two callers need the same arithmetic but nothing else in common:
///         <see cref="FileWriter"/> retries within a loop driven by a caught
///         <see cref="IOException"/>, while the workspace reload deferral is driven by a
///         "project loaded with zero metadata references" signal and carries its attempt count
///         across tool calls. Sharing anything more would force one shape onto the other.
///     </para>
///     <para>
///         The seed is a parameter rather than a constant because it carries meaning at one call
///         site: the reload deferral seeds it with the measured duration of the load that just
///         failed, so a retry never runs more often than the attempt costs. That bounds retry work
///         to roughly a 50% duty cycle whether a load takes 1 second or 20.
///     </para>
/// </remarks>
internal static class Backoff
{
	/// <summary>Seed used by file-write retries — short, because contention there is momentary.</summary>
	public const int DefaultSeedMs = 50;

	/// <summary>
	///     Delay before the given attempt. Shifting rather than <see cref="Math.Pow"/> keeps this
	///     integral; the shift is bounded so a large attempt count cannot wrap the result negative,
	///     and the arithmetic runs in <see cref="long"/> so a multi-second seed cannot overflow
	///     before the cap is applied.
	/// </summary>
	/// <param name="attempt">Zero-based attempt index — 0 yields <paramref name="seedMs"/>.</param>
	/// <param name="seedMs">Delay for attempt 0. Also the effective floor.</param>
	/// <param name="maxMs">Upper bound. Values below <paramref name="seedMs"/> clamp to the seed.</param>
	public static int DelayMs(int attempt, int seedMs, int maxMs)
	{
		var shift = Math.Clamp(attempt, 0, 20)
		;
		var delay = (long) Math.Max(0, seedMs) << shift;

		return (int) Math.Clamp(delay, seedMs, Math.Max(seedMs, maxMs));
	}
}
