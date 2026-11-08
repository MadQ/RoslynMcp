/// <summary>
///     What a group's factory may need: the harness context, the server's log location, and whether this is
///     a full run — a check that audits everything the run did means nothing over a filtered one.
/// </summary>
record GroupInputs(TestContext Ctx, string LogDir, string LogGlob, bool FullRun);

/// <summary>
///     One test group in the registry. <see cref="Name"/> is what <c>--only</c> matches and <c>--list</c>
///     prints. <see cref="Build"/> creates the group — some groups start servers or create projects while
///     being built, so a group that is not selected must never be built. <see cref="FullRunOnly"/> marks a
///     group whose checks are about the whole run (Workspace Hygiene reads everything the run logged): it
///     runs in an unfiltered run, or when <c>--only</c> names it.
/// </summary>
record GroupEntry(string Name, Func<GroupInputs, Task<TestGroup>> Build, bool FullRunOnly);

/// <summary>
///     What a run executes: the groups to build, an optional test-name filter, the note a filtered run reports, and
///     whether <see cref="TestCase.Slow"/> tests run. Leaving slow tests out does not make a run filtered: they are
///     counted as skips and named on the summary line instead.
/// </summary>
record HarnessSelection(GroupEntry[] Groups, string? TestFilter, string? FilterNote, bool RunSlow);

/// <summary>The test groups in run order, and the command-line selection over them (#367).</summary>
static class HarnessGroups
{
	internal static readonly GroupEntry[] All = [
		Group("Discovery Tools",                           g => DiscoveryTests.Build(g.Ctx)),
		Group("VsVersionPin",                              g => VsVersionPinTests.Build(g.Ctx)),
		Group("ProjectStyleDetection",                     g => ProjectStyleDetectionTests.Build(g.Ctx)),
		Group("String Literal Search",                     g => FindStringLiteralTests.Build(g.Ctx)),
		Group("Unused Symbol Analysis",                    g => FindUnusedTests.Build(g.Ctx)),
		Group("Member Body Tools",                         g => MemberBodyTests.Build(g.Ctx)),
		Group("Type Understanding Tools",                  g => TypeTests.Build(g.Ctx)),
		Group("Navigation Tools",                          g => NavigationTests.Build(g.Ctx)),
		Group("Call Graph Tools",                          g => CallGraphTests.Build(g.Ctx)),
		Group("Analyze Region",                            g => AnalyzeRegionTests.Build(g.Ctx)),
		Group("Symbol Info",                               g => SymbolInfoTests.Build(g.Ctx)),
		Group("Code Generation Tools",                     g => CodeGenerationTests.Build(g.Ctx)),
		Group("File Content Tools",                        g => FileContentTests.Build(g.Ctx)),
		Group("Bounded Reads",                             g => BoundedReadTests.Build(g.Ctx, g.LogDir, g.LogGlob)),
		Group("Code Fix Tools",                            g => CodeFixTests.Build(g.Ctx, g.LogDir, g.LogGlob)),
		Group("Validation Tools",                          g => ValidationTests.Build(g.Ctx)),
		Group("Source Drift",                              g => DriftTests.Build(g.Ctx)),
		Group("Refactoring Tools",                         g => RefactoringTests.Build(g.Ctx)),
		Group("File Editing Tools",                        g => EditingTests.Build(g.Ctx)),
		AsyncGroup("Reload Flagging",                      g => ReloadFlaggingTests.BuildAsync(g.Ctx)),
		AsyncGroup("Load Health",                          g => LoadHealthTests.BuildAsync(g.Ctx)),
		AsyncGroup("Unrestored Projects",                  g => UnrestoredProjectTests.BuildAsync(g.Ctx)),
		Group("Pagination Cursor",                         g => PaginationTests.Build(g.Ctx)),
		Group("Linked Workspace",                          g => LinkedWorkspaceTests.Build(g.Ctx)),
		Group("File Rewrite",                              g => FileRewriteTests.Build(g.Ctx)),
		Group("Apply Through Links",                       g => ApplyLinkTests.Build(g.Ctx)),
		Group("Link Write Sync",                           g => LinkWriteSyncTests.Build(g.Ctx)),
		Group("Linked Files",                              g => LinkedFileTests.Build(g.Ctx, g.LogDir, g.LogGlob)),
		Group("Watcher Start-up",                          g => WatcherStartupTests.Build(g.Ctx)),
		Group("Watcher Through Writes",                    g => WatcherThroughWritesTests.Build(g.Ctx, g.LogDir, g.LogGlob)),
		Group("Moved Directory",                           g => MovedDirectoryTests.Build(g.Ctx)),
		Group("Hook Hint",                                 g => HookHintTests.Build(g.Ctx)),
		Group("Claude Code Hook Setup",                    g => ClaudeHookSetupTests.Build(g.Ctx)),
		AsyncGroup("Ignored Directories",                  g => IgnoredDirectoryTests.BuildAsync(g.Ctx)),
		Group("Brace Globs",                               g => BraceGlobTests.Build(g.Ctx)),
		AsyncGroup("Incremental Additional/AnalyzerConfig Doc", g => IncrementalAdditionalDocTests.BuildAsync(g.Ctx)),
		AsyncGroup("Local History Tools",                  g => LocalHistoryTests.BuildAsync(g.Ctx)),
		AsyncGroup("DefaultWorkspace",                     g => DefaultWorkspaceTests.BuildAsync(g.Ctx)),
		Group("Parallel Load",                             g => ParallelLoadTests.Build(g.Ctx)),
		Group("Dotnet Runner",                             g => DotnetRunnerTests.Build(g.Ctx, g.LogDir, g.LogGlob)),
		
		// Last on purpose: it reads the server log written by everything above.
		new("Workspace Hygiene", g => Task.FromResult(WorkspaceHygieneTests.Build(g.Ctx, g.LogDir, g.LogGlob, g.FullRun)), FullRunOnly: true),
	];
	
	static readonly string[] KnownSwitches = ["--quiet", "--list", "--only", "--test", "--slow", "--only-build-diag"];
	
	static GroupEntry Group(string name, Func<GroupInputs, TestGroup> build)
		=> new(name, g => Task.FromResult(build(g)), FullRunOnly: false);
	
	static GroupEntry AsyncGroup(string name, Func<GroupInputs, Task<TestGroup>> build)
		=> new(name, build, FullRunOnly: false);
	
	/// <summary>
	///     Reads <c>--only</c>, <c>--only-build-diag</c> and <c>--test</c>. Without <c>--only</c> every group
	///     is selected except the full-run-only ones when <c>--test</c> narrows the run. Each comma-separated
	///     <c>--only</c> term selects every group whose name contains it (case-insensitive); a term that
	///     selects nothing is an error, so a typo cannot pass with zero tests run. Slow tests run with
	///     <c>--slow</c>, on a CI runner, or when <c>--test</c> is given — every test it keeps matches its text, so
	///     a slow one it keeps was asked for by name.
	/// </summary>
	internal static bool TrySelect(string[] args, out HarnessSelection selection, out string? error)
	{
		selection = new([], null, null, false);
		error     = null;
		
		if(!TryValue(args, "--only", out var only, out error) || !TryValue(args, "--test", out var test, out error))
			
			return false;
		
		var runSlow = args.Contains("--slow") || Skip.OnCiRunner || test is not null;
		
		// An unknown switch is an error, not ignored: '--only "a,b"' passed as one argument (as
		// Test-HarnessWsl.ps1 forwards a single string) would otherwise run the whole suite unnoticed.
		for(var i = 0; i < args.Length; i++) {
			
			if(args[i] is "--only" or "--test") {
				
				i++;
				continue;
			}
			
			if(!KnownSwitches.Contains(args[i])) {
				
				error = $"unknown argument '{args[i]}'. Switches: {string.Join(", ", KnownSwitches)}; --only and --test take a value as the next argument.";
				
				return false;
			}
		}
		
		// The original single-group switch, kept as a name for the same selection.
		if(args.Contains("--only-build-diag"))
			only = only is null ? "validation" : $"{only},validation";
		
		if(only is null) {
			
			GroupEntry[] groups = [.. All.Where(g => test is null || !g.FullRunOnly)];
			
			selection = new(groups, test, test is null ? null : $"filtered: tests matching '{test}'", runSlow);
			
			return true;
		}
		
		var terms = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		
		if(terms.Length == 0) {
			
			error = "--only needs at least one group name.";
			
			return false;
		}
		
		foreach(var term in terms)
			if(!All.Any(g => Matches(g, term))) {
				
				error = $"--only '{term}' matches no group. Groups: {string.Join(", ", All.Select(g => g.Name))}";
				
				return false;
			}
		
		GroupEntry[] selected = [.. All.Where(g => terms.Any(term => Matches(g, term)))];
		
		// Every group and no test filter is a full run, however it was asked for ('--only e'): the
		// whole-run audits must not skip a run that did everything.
		if(selected.Length == All.Length && test is null) {
			
			selection = new(selected, null, null, runSlow);
			
			return true;
		}
		
		var note = $"filtered: {string.Join(", ", selected.Select(g => g.Name))}";
		
		if(test is not null)
			note += $"; tests matching '{test}'";
		
		selection = new(selected, test, note, runSlow);
		
		return true;
	}
	
	static bool Matches(GroupEntry group, string term)
		=> group.Name.Contains(term, StringComparison.OrdinalIgnoreCase);
	
	// The value after a switch, or null when the switch is absent. A switch with nothing after it, or
	// followed by another switch, is an error rather than a silent no-op.
	static bool TryValue(string[] args, string name, out string? value, out string? error)
	{
		value = null;
		error = null;
		
		var index = Array.IndexOf(args, name);
		
		if(index < 0)
			
			return true;
		
		// Only one value is read, so a second occurrence would be dropped without a word.
		if(Array.IndexOf(args, name, index + 1) >= 0) {
			
			error = $"{name} is given more than once; list several values in one, separated by commas.";
			
			return false;
		}
		
		if(index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) {
			
			error = $"{name} needs a value.";
			
			return false;
		}
		
		value = args[index + 1];
		
		return true;
	}
}
