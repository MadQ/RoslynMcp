/// <summary>
///     The third test outcome: a test that could not check anything on this machine. Returning a
///     plain pass for that hides it — in quiet mode a passing test prints nothing, so a run could
///     report "all passed" while a case was never exercised (#330). A skip is printed in both
///     output modes and counted separately in the summary.
///     <para>
///         There are two reasons a test cannot run, and they are not equally acceptable:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Not applicable</b> — the thing under test does not exist on this operating
///             system (Unix file modes on Windows). Always a skip; another platform's run covers it.
///         </item>
///         <item>
///             <b>Setup unavailable</b> — the test applies here, but this machine will not let the
///             fixture be built (no permission to create symbolic links). A skip on a developer's
///             machine, where it says nothing about the change being tested. On a CI runner it is a
///             failure: the release gate must not pass with a case nobody checked.
///         </item>
///     </list>
/// </summary>
static class Skip
{
	const string Prefix = "SKIP  ";
	
	// Set by GitHub Actions on every runner.
	static bool OnCiRunner => Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
	
	/// <summary>The test has nothing to check on this operating system.</summary>
	public static (bool pass, string message) NotApplicable(string reason) => (true, $"{Prefix}({reason})");
	
	/// <summary>
	///     The test applies, but its fixture cannot be built on this machine. A skip locally; a
	///     failure on a CI runner.
	/// </summary>
	public static (bool pass, string message) SetupUnavailable(string reason)
		=> OnCiRunner
			? (false, $"FAIL  (not checked on a CI runner, which the release gate does not allow: {reason})")
			: (true,  $"{Prefix}({reason})")
	;
	
	/// <summary>Whether a test result is a skip rather than a pass.</summary>
	public static bool IsSkip(bool pass, string message) => pass && message.StartsWith(Prefix, StringComparison.Ordinal);
	
	/// <summary>The reason of a skip message, without the leading marker.</summary>
	public static string Reason(string message) => message.StartsWith(Prefix, StringComparison.Ordinal) ? message[Prefix.Length..] : message;
}

/// <summary>
///     Carries a skip (or, on a CI runner, the failure that replaces it) out of a test body whose
///     return type has no room for it — the bodies in <c>ClaudeHookSetupTests</c> return a failure
///     description or null. Built from <see cref="Skip.NotApplicable"/> or
///     <see cref="Skip.SetupUnavailable"/>.
/// </summary>
sealed class SkipTest((bool pass, string message) outcome) : Exception(outcome.message)
{
	public (bool pass, string message) Outcome { get; } = outcome;
}
