/// <summary>
///     A named test case with an async runner returning a pass/fail result. A <see cref="Slow"/> test runs only
///     when asked for — <c>--slow</c>, a <c>--test</c> filter that matches it, or a CI runner — and is a skip
///     otherwise (see <see cref="Skip.SlowNotRequested"/>).
/// </summary>
record TestCase(string Name, Func<Task<(bool pass, string message)>> Run)
{
	/// <summary>
	///     The test costs enough time (tens of seconds) that a local run leaves it out unless asked. Only for a
	///     test whose cost cannot be cut without weakening what it checks.
	/// </summary>
	public bool Slow { get; init; }
}

/// <summary>
///     A group of related test cases with a shared header and optional async teardown.
///     Teardown runs after all tests in the group complete, even on failure.
/// </summary>
record TestGroup(string Header, IReadOnlyList<TestCase> Tests, Func<Task>? Teardown = null);
