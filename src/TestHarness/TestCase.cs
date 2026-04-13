/// <summary>A named test case with an async runner returning a pass/fail result.</summary>
record TestCase(string Name, Func<Task<(bool pass, string message)>> Run);

/// <summary>
///     A group of related test cases with a shared header and optional async teardown.
///     Teardown runs after all tests in the group complete, even on failure.
/// </summary>
record TestGroup(string Header, IReadOnlyList<TestCase> Tests, Func<Task>? Teardown = null);
