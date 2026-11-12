using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Analyzers;
using RoslynMcp.Tools;

namespace RoslynMcp;

/// <summary>
///     Every code refactoring and code fix provider the server may run, and the one way to run them
///     (#389). Providers come from four kinds of source: Roslyn's own, exported from the Features
///     assemblies through <see cref="MefComposition"/>; the server's bundled fixers; a project's
///     analyzer references, reflected from the shadow-copied assemblies the diagnostics tool already
///     loads; and the assemblies or packages the user's and the project's config files name. Each
///     source is loaded once and reports what it yielded and what went wrong, so a tool can say where
///     an action came from and why a source contributed nothing. A provider that throws is a
///     <see cref="ProviderFailure"/>, never a failed call: the others still run.
/// </summary>
internal sealed class CodeActionHost
{
	// Roslyn's tag on an action that changes more than documents (CodeAction.RequiresNonDocumentChange,
	// internal). The LSP host skips those too: the apply path writes documents and nothing else.
	const string RequiresNonDocumentChangeTag = "RequiresNonDocumentChange";
	
	// Built-ins the server never runs, by the stem of their type name (see IsDenied): the server has
	// its own signature change; rename tracking is an editor feature; the rest open a dialog, so every
	// action of theirs would be filtered out anyway, and not running them saves the work.
	static readonly ImmutableHashSet<string> builtInDeny = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
		"ChangeSignature", "RenameTracking", "ExtractInterface", "ExtractClass", "PullMemberUp", "MoveStaticMembers", "MoveToNamespace");
	
	readonly FileLogger        logger;
	readonly Lazy<ProviderSet> builtIn;
	readonly ProviderSet       bundled;
	
	// Providers from a project's analyzer references, keyed by (path, mtime) as AnalyzerLoading keys its analyzers.
	// A set during whose load a dependency was found nowhere is kept with the number of dependency
	// locations the loader knew then: it may have lost a type that a later project's sibling reference
	// supplies, so it is loaded again once the loader knows more (#421). ShortAt is null for a set that
	// had every dependency it asked for, whatever other problem it has. One value, so that a reader
	// never finds a set beside another load's count.
	readonly ConcurrentDictionary<(string Path, long MtimeTicks), (ProviderSet Set, int? ShortAt)> fromReferences = new();
	
	// For each project file and each of its targets, the analyzer references whose file was not there
	// when that target's providers were last asked for (#424), so that roslyn_info can name them. A
	// target's list is replaced whole at every such call, so a reference it no longer lists is
	// forgotten with it, and one whose file has been found is gone. By target, since the targets of a
	// multi-target project are projects of their own with one file between them and need not miss the
	// same references: under the file alone, the target asked last would speak for all.
	readonly ConcurrentDictionary<string, ImmutableDictionary<string, ImmutableArray<string>>> missingByProject = new(SecurityBoundary.PathComparer);
	
	// Providers the config files name, keyed by the declaring file and the entry. The stamp beside each set
	// is what the entry resolved to — the assemblies with their modification times, or the problem — so a
	// provider assembly rebuilt in place, or a package restored after the server started, is loaded again
	// at the next call without a watcher (#406). The load context of the set it replaces stays in memory,
	// as a rebuilt project analyzer's does: the contexts are not collectible.
	readonly ConcurrentDictionary<string, ConfiguredSet> configured      = new(StringComparer.OrdinalIgnoreCase);
	readonly object                                      configuredLoads = new();
	
	// How often an entry has failed to load on the file system under the stamp it resolves to now; read
	// and written under configuredLoads. Past the limit the failed set is kept like any other, until
	// the stamp changes. One count per entry, so a rebuilt assembly leaves none behind.
	readonly Dictionary<string, (string Stamp, int Count)> transientLoads    = new(StringComparer.OrdinalIgnoreCase);
	const int                                              MaxTransientLoads = 5;
	
	public CodeActionHost(FileLogger logger)
	{
		this.logger = logger;
		builtIn     = new(LoadBuiltIn, LazyThreadSafetyMode.ExecutionAndPublication);
		bundled     = LoadBundled();
	}
	
	/// <summary>
	///     Every source loaded so far, for <c>roslyn_info</c>: the bundled fixers, the built-in
	///     composition once something has asked for it (reporting never forces it), and the project
	///     references and configured entries that have been loaded.
	/// </summary>
	public IEnumerable<ProviderSourceReport> Reports
	{
		get
		{
			yield return bundled.Report;
			
			if(builtIn.IsValueCreated)
				yield return builtIn.Value.Report;
			
			// A set whose file has since gone is not what a project gets any more: the file is reported
			// below as missing, and listing both would say it is loaded and not found at once.
			foreach(var (key, cached) in fromReferences)
				if(File.Exists(key.Path))
					yield return cached.Set.Report;
			
			// A reference that was missing when its project was last asked for, and still is; once,
			// however many projects and targets list it. A project whose file is gone is never asked
			// again, so what it was missing is not said any more. Between a file going away and the
			// next call for a project that lists it, the file is in neither list: this report is what
			// the calls so far found, not a look at the projects.
			var stillMissing = missingByProject
				.Where(project => File.Exists(project.Key))
				.SelectMany(project => project.Value.Values)
				.SelectMany(paths => paths)
				.Distinct(SecurityBoundary.PathComparer)
			;
			
			foreach(var path in stillMissing)
				if(!File.Exists(path))
					yield return MissingReference(path).Report;
			
			foreach(var cached in configured.Values)
				yield return cached.Set.Report;
		}
	}
	
	/// <summary>
	///     The providers that apply to <paramref name="document"/> under the effective settings: the
	///     bundled fixers always, the rest only while code actions are enabled, minus the deny-list.
	///     A provider found by two sources (the bundled analyzers are also a project reference of this
	///     repo) is kept once, from the first source that had it.
	/// </summary>
	public ResolvedProviders ProvidersFor(Document document)
	{
		var settings = CodeActionSettings.Resolve(document.Project.FilePath ?? document.FilePath ?? string.Empty, logger);
		var sets     = ImmutableArray.CreateBuilder<ProviderSet>();
		
		sets.Add(bundled);
		
		if(settings.Enabled) {
			
			if(settings.BuiltIn)
				sets.Add(builtIn.Value);
			
			if(settings.ProjectProviders)
				AddProjectSets(document.Project, sets);
			
			foreach(var entry in settings.Providers)
				sets.Add(ConfiguredSetFor(entry, settings.TrustProjectProviders));
		}
		
		var deny = builtInDeny.Union(settings.Deny);
		
		return new ResolvedProviders(
			settings,
			Select(sets.SelectMany(set => set.Refactorings), document, deny),
			Select(sets.SelectMany(set => set.Fixes), document, deny),
			[..sets.Select(set => set.Report)]);
	}
	
	/// <summary>
	///     The refactorings every applicable provider offers at <paramref name="span"/>, flattened to
	///     their leaves and sorted by provider and title, so an index into the list is stable for one
	///     input. Actions a headless host cannot run are listed apart, with the reason.
	/// </summary>
	public async Task<RefactoringQuery> GetRefactoringsAsync(Document document, TextSpan span, CancellationToken cancellationToken)
	{
		var providers   = ProvidersFor(document);
		var actions     = ImmutableArray.CreateBuilder<RefactoringAction>();
		var unavailable = ImmutableArray.CreateBuilder<UnavailableAction>();
		var failures    = ImmutableArray.CreateBuilder<ProviderFailure>();
		
		foreach(var entry in providers.Refactorings) {
			
			cancellationToken.ThrowIfCancellationRequested();
			
			// A provider may register from any thread.
			var registered = new List<CodeAction>();
			var context    = new CodeRefactoringContext(document, span, action => { lock(registered) registered.Add(action); }, cancellationToken);
			
			try {
				
				await (entry.Provider.ComputeRefactoringsAsync(context) ?? Task.CompletedTask).ConfigureAwait(false);
			}
			catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
				throw;
			}
			catch(Exception ex) {
				
				failures.Add(new ProviderFailure(entry.Name, Describe(ex)));
				
				continue;
			}
			
			foreach(var action in registered)
				Flatten(action, null, entry.Name, unavailable,
					(leaf, title) => actions.Add(new RefactoringAction(leaf, title, entry.Name, entry.ExportName, entry.Source, leaf.EquivalenceKey)));
		}
		
		actions.Sort((left, right) => string.CompareOrdinal(left.Provider, right.Provider) is var byProvider && byProvider != 0
			? byProvider
			: string.CompareOrdinal(left.Title, right.Title));
		
		return new RefactoringQuery(providers.Settings, actions.ToImmutable(), unavailable.ToImmutable(), providers.Sources, failures.ToImmutable());
	}
	
	/// <summary>
	///     Which of Roslyn's own providers on the server's deny-list would have had an action at the
	///     span, by type-name stem (ExtractInterface). For the log only, never for an answer: it is how
	///     the demand for the dialog-driven refactorings becomes visible (#443, #449). They are asked
	///     only whether they register anything: nothing they register is kept, flattened or offered.
	///     One that throws has nothing to say, which is no failure of the call. A provider the user's
	///     configuration denies is not run at all.
	///     <para>
	///         A query of its own, and not part of <see cref="GetRefactoringsAsync"/>: the preview asks
	///         for the same list and would pay for an answer nobody reads.
	///     </para>
	/// </summary>
	public async Task<ImmutableArray<string>> DeniedWithActionsAsync(Document document, TextSpan span, CancellationToken cancellationToken)
	{
		var settings = CodeActionSettings.Resolve(document.Project.FilePath ?? document.FilePath ?? string.Empty, logger);
		
		if(!settings.Enabled || !settings.BuiltIn)
			
			return [];
		
		var userDeny    = ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, settings.Deny);
		var withActions = ImmutableArray.CreateBuilder<string>();
		
		foreach(var entry in builtIn.Value.Refactorings) {
			
			if(!entry.AppliesTo(document) || !IsDenied(entry, builtInDeny) || IsDenied(entry, userDeny))
				continue;
			
			
			cancellationToken.ThrowIfCancellationRequested();
			
			var registered = 0;
			var context    = new CodeRefactoringContext(document, span, _ => Interlocked.Increment(ref registered), cancellationToken);
			
			try {
				
				await (entry.Provider.ComputeRefactoringsAsync(context) ?? Task.CompletedTask).ConfigureAwait(false);
			}
			catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
				throw;
			}
			catch(Exception) {
				continue;
			}
			
			if(Volatile.Read(ref registered) > 0)
				withActions.Add(Stem(entry.Name[(entry.Name.LastIndexOf('.') + 1)..]));
		}
		
		return withActions.ToImmutable();
	}
	
	/// <summary>
	///     The fixes every applicable provider offers for <paramref name="diagnostic"/>, in provider
	///     and registration order (the order the code-fix tools have always shown).
	/// </summary>
	public async Task<CodeFixQuery> GetFixesAsync(Document document, Diagnostic diagnostic, CancellationToken cancellationToken)
	{
		var providers   = ProvidersFor(document);
		var fixes       = ImmutableArray.CreateBuilder<AvailableCodeFix>();
		var unavailable = ImmutableArray.CreateBuilder<UnavailableAction>();
		var failures    = ImmutableArray.CreateBuilder<ProviderFailure>();
		
		foreach(var entry in providers.Fixes) {
			
			cancellationToken.ThrowIfCancellationRequested();
			
			ImmutableArray<string> fixable;
			
			try {
				fixable = entry.Provider.FixableDiagnosticIds;
			}
			catch(Exception ex) {
				
				failures.Add(new ProviderFailure(entry.Name, Describe(ex)));
				
				continue;
			}
			
			if(fixable.IsDefaultOrEmpty || !fixable.Contains(diagnostic.Id))
				continue;
			
			var registered = new List<CodeAction>();
			var context    = new CodeFixContext(document, diagnostic, (action, _) => { lock(registered) registered.Add(action); }, cancellationToken);
			
			try {
				
				await (entry.Provider.RegisterCodeFixesAsync(context) ?? Task.CompletedTask).ConfigureAwait(false);
			}
			catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) {
				throw;
			}
			catch(Exception ex) {
				
				failures.Add(new ProviderFailure(entry.Name, Describe(ex)));
				
				continue;
			}
			
			foreach(var action in registered)
				Flatten(action, null, entry.Name, unavailable,
					(leaf, title) => fixes.Add(new AvailableCodeFix(diagnostic.Id, entry.Name, title, leaf.EquivalenceKey, entry.Provider, leaf)));
		}
		
		return new CodeFixQuery(providers.Settings, fixes.ToImmutable(), unavailable.ToImmutable(), providers.Sources, failures.ToImmutable());
	}
	
	/// <summary>
	///     The analyzers that run for <paramref name="project"/> when a tool asks for analyzers: its
	///     analyzer references (through the shadow copies, as always), and Roslyn's IDE analyzers while
	///     built-in code actions are on for it. The IDE analyzers are what produce the IDE0xxx
	///     diagnostics the built-in fixers fix — without them `roslyn_preview_code_fix` could offer
	///     "remove unnecessary using" and never find the diagnostic to apply it to.
	/// </summary>
	public AnalyzerSet AnalyzersFor(Project project)
	{
		var fromProject = AnalyzerLoading.GetShadowedAnalyzers(project);
		var missing     = AnalyzerLoading.MissingReferences(project);
		
		// An AdhocWorkspace project has no file; its directory is where a project config would be.
		var settings = CodeActionSettings.Resolve(project.FilePath ?? project.Documents.FirstOrDefault()?.FilePath ?? string.Empty, logger);
		
		if(!settings.Enabled || !settings.BuiltIn || project.Language != LanguageNames.CSharp)
			return new AnalyzerSet(fromProject, fromProject.Length, 0) { MissingReferences = missing };
		
		var ide = builtIn.Value.Analyzers;
		
		return new AnalyzerSet([..fromProject, ..ide], fromProject.Length, ide.Length) { MissingReferences = missing };
	}
	
	// ── Sources ──────────────────────────────────────────────────────────────
	
	ProviderSet LoadBuiltIn()
	{
		var refactorings = ImmutableArray.CreateBuilder<ProviderEntry<CodeRefactoringProvider>>();
		var fixes        = ImmutableArray.CreateBuilder<ProviderEntry<CodeFixProvider>>();
		var analyzers    = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
		var problems     = ImmutableArray.CreateBuilder<string>();
		
		try {
			
			var container = MefComposition.Container;
			
			foreach(var export in container.GetExports<Lazy<CodeRefactoringProvider, ExportMetadata>>())
				AddExport(export, refactorings, problems);
			
			foreach(var export in container.GetExports<Lazy<CodeFixProvider, ExportMetadata>>())
				AddExport(export, fixes, problems);
			
			// The IDE analyzers are not MEF parts: Roslyn's hosts find them by their attribute, as here.
			foreach(var assembly in MefComposition.Assemblies)
				DiscoverAnalyzers(assembly, analyzers, problems);
		}
		catch(Exception ex) {
			problems.Add($"composition failed: {Describe(ex)}");
		}
		
		logger.LogInfo("CodeActions", $"built-in providers: {refactorings.Count} refactoring, {fixes.Count} fix, {analyzers.Count} analyzer(s)" + (problems.Count == 0 ? string.Empty : $", {problems.Count} problem(s): {string.Join("; ", problems)}"));
		
		return new ProviderSet(
			new ProviderSourceReport("built-in", ProviderSourceKind.BuiltIn, null, refactorings.Count, fixes.Count, problems.ToImmutable()) { Analyzers = analyzers.Count },
			refactorings.ToImmutable(),
			fixes.ToImmutable(),
			analyzers.ToImmutable());
	}
	
	// The C# diagnostic analyzers of an assembly: every non-abstract [DiagnosticAnalyzer] type for
	// C# with a parameterless constructor, as AnalyzerFileReference would find them in a file — but
	// these assemblies are already loaded (and have no file of their own in a single-file publish).
	static void DiscoverAnalyzers(Assembly assembly, ImmutableArray<DiagnosticAnalyzer>.Builder into, ImmutableArray<string>.Builder problems)
	{
		foreach(var type in SafeGetTypes(assembly, problems)) {
			
			if(type.IsAbstract || type.IsInterface || !typeof(DiagnosticAnalyzer).IsAssignableFrom(type))
				continue;
			
			try {
				
				if(type.GetCustomAttribute<DiagnosticAnalyzerAttribute>() is not { } attribute || !attribute.Languages.Contains(LanguageNames.CSharp))
					continue;
				
				if(type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) is not { } constructor)
					continue;
				
				into.Add((DiagnosticAnalyzer) constructor.Invoke(null));
			}
			catch(Exception ex) {
				problems.Add($"analyzer {type.FullName}: {Describe(ex)}");
			}
		}
	}
	
	// The types of an assembly, with the ones that cannot be loaded left out and counted, not thrown.
	static Type[] SafeGetTypes(Assembly assembly, ImmutableArray<string>.Builder problems)
	{
		try {
			return assembly.GetTypes();
		}
		catch(ReflectionTypeLoadException ex) {
			
			problems.Add($"{assembly.GetName().Name}: {ex.LoaderExceptions.Length} type(s) could not be loaded: {ex.LoaderExceptions.FirstOrDefault()?.Message ?? "no detail"}");
			
			return [..ex.Types.Where(type => type is not null)!];
		}
	}
	
	// A part whose imports cannot be satisfied throws when its value is first asked for; that is one
	// provider lost, not the source.
	static void AddExport<TProvider>(Lazy<TProvider, ExportMetadata> export, ImmutableArray<ProviderEntry<TProvider>>.Builder into, ImmutableArray<string>.Builder problems) where TProvider : class
	{
		TProvider provider;
		
		try {
			provider = export.Value;
		}
		catch(Exception ex) {
			
			problems.Add($"{export.Metadata.Name ?? "(unnamed export)"}: {Describe(ex)}");
			
			return;
		}
		
		var type = provider.GetType();
		
		into.Add(new ProviderEntry<TProvider>(
			provider,
			type.FullName ?? type.Name,
			export.Metadata.Name,
			"built-in",
			[..export.Metadata.Languages ?? []],
			[..export.Metadata.DocumentKinds ?? []],
			[..export.Metadata.DocumentExtensions ?? []]));
	}
	
	static ProviderSet LoadBundled()
	{
		var fixes = ImmutableArray.CreateBuilder<ProviderEntry<CodeFixProvider>>();
		
		fixes.Add(Describe<CodeFixProvider>(new PreferNintOverIntPtrCodeFixProvider(), "bundled"));
		fixes.Add(Describe<CodeFixProvider>(new ToolScopeCodeFixProvider(), "bundled"));
		
#if DEBUG
		// The harness provider is an explicit test seam, never a discovered project provider.
		if(Environment.GetEnvironmentVariable("ROSLYNMCP_TEST_CODE_FIXES") == "1")
			fixes.Add(Describe<CodeFixProvider>(new CodeFixTestProvider(), "bundled"));
#endif
		
		return new ProviderSet(
			new ProviderSourceReport("bundled", ProviderSourceKind.Bundled, null, 0, fixes.Count, []),
			[],
			fixes.ToImmutable());
	}
	
	void AddProjectSets(Project project, ImmutableArray<ProviderSet>.Builder sets)
	{
		if(project.FilePath is null) {
			
			sets.Add(ProviderSet.Empty("project references", ProviderSourceKind.Project, null,
				"not available in an AdhocWorkspace: without a project file there are no analyzer references to load providers from"));
			
			return;
		}
		
		// A reference may depend on one listed after it (#421).
		AnalyzerLoading.RegisterDependencies(project);
		
		var locations = ShadowCopyAnalyzerLoader.Instance.DependencyLocationCount;
		var missing   = ImmutableArray.CreateBuilder<string>();
		
		foreach(var reference in project.AnalyzerReferences) {
			
			// A reference without a path is an analyzer handed over in memory. There is no file to read
			// provider exports from, so it is no provider source; its analyzers still run.
			if(reference.FullPath is not { } path)
				continue;
			
			// A file that is not there is a source with a problem, never a silent skip (#424): its
			// fixers and refactorings are missing, and there is a remedy to name. Nothing is cached
			// for it, since there is no file: it is looked for again at every call.
			if(!File.Exists(path)) {
				
				sets.Add(MissingReference(path));
				missing.Add(path);
				
				continue;
			}
			
			var key = (path, File.GetLastWriteTimeUtc(path).Ticks);
			
			if(fromReferences.TryGetValue(key, out var cached) && (cached.ShortAt is null || cached.ShortAt == locations)) {
				
				sets.Add(cached.Set);
				
				continue;
			}
			
			// A set from a load that failed on the file system — another server holding the shadow
			// copy, say — is used for this call and loaded again at the next, not kept (#412).
			var loader     = ShadowCopyAnalyzerLoader.Instance;
			var failures   = loader.TransientFailures;
			var unresolved = loader.UnresolvedDependencies;
			var set        = LoadFromReference(path);
			
			if(loader.TransientFailures == failures) {
				
				fromReferences[key] = (set, loader.UnresolvedDependencies == unresolved ? null : locations);
				
				// One set per file: the one loaded from an earlier build of it is what no call gets
				// any more, and roslyn_info would list it beside this one under the same name (#425).
				foreach(var earlier in fromReferences.Keys)
					if(earlier.MtimeTicks != key.Ticks && SecurityBoundary.PathComparer.Equals(earlier.Path, path))
						fromReferences.TryRemove(earlier, out _);
			}
			
			sets.Add(set);
		}
		
		// What this target is missing now, in place of what it was missing at the last call. A target
		// the project file no longer has (a framework taken out of it) is never asked again, so its
		// list is let go here, by the targets this call's solution has for the file. That is the
		// caller's snapshot: a call that still holds an older one can let go of a target added
		// since, whose list is back with that target's next call. Only looked up when something is
		// stored for the file, which is seldom.
		var found  = missing.ToImmutable();
		var target = project.Name;
		
		missingByProject.AddOrUpdate(
			project.FilePath,
			_ => found.IsEmpty ? ImmutableDictionary<string, ImmutableArray<string>>.Empty : ImmutableDictionary<string, ImmutableArray<string>>.Empty.SetItem(target, found),
			(_, targets) => {
				
				if(!targets.IsEmpty) {
					
					var current = project.Solution.Projects
						.Where(other => SecurityBoundary.PathComparer.Equals(other.FilePath, project.FilePath))
						.Select(other => other.Name)
						.ToImmutableHashSet(StringComparer.Ordinal)
					;
					
					targets = targets.RemoveRange(targets.Keys.Where(name => !current.Contains(name)));
				}
				
				return found.IsEmpty ? targets.Remove(target) : targets.SetItem(target, found);
			});
	}
	
	// A source that yields nothing because its file is not there, with the remedy that fits where
	// the file should be.
	static ProviderSet MissingReference(string path)
		=> ProviderSet.Empty($"project: {Path.GetFileName(path)}", ProviderSourceKind.Project, path,
			$"analyzer reference not found: '{path}'. {AnalyzerLoading.MissingReferenceRemedy([path])}")
	;
	
	// The same shadow-copied assembly the project's analyzers come from (AnalyzerLoading), so the
	// original stays unlocked and a provider and its analyzer are one load.
	ProviderSet LoadFromReference(string path)
	{
		var source = $"project: {Path.GetFileName(path)}";
		
		try {
			
			var assembly = new AnalyzerFileReference(path, ShadowCopyAnalyzerLoader.Instance).GetAssembly();
			var set      = Discover(assembly, source, ProviderSourceKind.Project, path);
			
			if(set.Report.Refactorings + set.Report.Fixes > 0 || !set.Report.Problems.IsEmpty)
				logger.LogInfo("CodeActions", $"{source}: {set.Report.Refactorings} refactoring, {set.Report.Fixes} fix provider(s)" + (set.Report.Problems.IsEmpty ? string.Empty : $", {set.Report.Problems.Length} problem(s)"));
			
			return set;
		}
		catch(Exception ex) when(ex is not OperationCanceledException) {
			
			logger.LogInfo("CodeActions", $"{source}: could not load: {Describe(ex)}");
			
			return ProviderSet.Empty(source, ProviderSourceKind.Project, path, $"could not load: {Describe(ex)}");
		}
	}
	
	/// <summary>
	///     Loads the assemblies a configured entry resolved to (<see cref="ResolveConfigured"/>). Each
	///     load gets a context of its own, with the assemblies beside it as dependency locations — all
	///     but Roslyn and the framework, which must stay the server's.
	/// </summary>
	ProviderSet LoadConfigured(ConfiguredProvider entry, string[] assemblies, out bool transient)
	{
		var kind   = KindOf(entry);
		var source = SourceOf(entry);
		var loader = new ShadowCopyAnalyzerLoader($"RoslynMcp.Providers.{entry.Describe()}");
		
		foreach(var directory in assemblies.Select(Path.GetDirectoryName).Where(directory => directory is not null).Distinct(SecurityBoundary.PathComparer))
			foreach(var dependency in Directory.EnumerateFiles(directory!, "*.dll"))
				if(!IsHostAssembly(Path.GetFileNameWithoutExtension(dependency)))
					loader.AddDependencyLocation(dependency);
		
		var refactorings = ImmutableArray.CreateBuilder<ProviderEntry<CodeRefactoringProvider>>();
		var fixes        = ImmutableArray.CreateBuilder<ProviderEntry<CodeFixProvider>>();
		var problems     = ImmutableArray.CreateBuilder<string>();
		
		foreach(var assemblyPath in assemblies) {
			
			try {
				
				var set = Discover(loader.LoadFromPath(assemblyPath), source, kind, assemblyPath);
				
				refactorings.AddRange(set.Refactorings);
				fixes.AddRange(set.Fixes);
				problems.AddRange(set.Report.Problems);
			}
			catch(Exception ex) when(ex is not OperationCanceledException) {
				problems.Add($"{Path.GetFileName(assemblyPath)}: could not load: {Describe(ex)}");
			}
		}
		
		logger.LogInfo("CodeActions", $"{source}: {refactorings.Count} refactoring, {fixes.Count} fix provider(s) from {assemblies.Length} assembl{(assemblies.Length == 1 ? "y" : "ies")}" + (problems.Count == 0 ? string.Empty : $", {problems.Count} problem(s): {string.Join("; ", problems)}"));
		
		transient = loader.TransientFailures > 0;
		
		return new ProviderSet(
			new ProviderSourceReport(source, kind, string.Join(Path.PathSeparator, assemblies), refactorings.Count, fixes.Count, problems.ToImmutable()),
			refactorings.ToImmutable(),
			fixes.ToImmutable());
	}
	
	/// <summary>
	///     The providers a configured entry yields now: the cached set while the entry still resolves to
	///     the same assemblies at the same modification times, a fresh load when it does not (#406).
	///     Resolving is a trust check, a <c>File.Exists</c> or a listing of the package's cache directory —
	///     cheap enough for every call, which is what lets a rebuilt assembly be picked up without a watcher.
	/// </summary>
	ProviderSet ConfiguredSetFor(ConfiguredProvider entry, bool trusted)
	{
		var key      = $"{entry.DeclaredIn}|{entry.Describe()}";
		var resolved = ResolveConfigured(entry, trusted);
		
		if(configured.TryGetValue(key, out var cached) && cached.Stamp == resolved.Stamp)
			return cached.Set;
		
		// One load per entry at a time: two callers finding the same stale set must not each load it.
		lock(configuredLoads) {
			
			if(configured.TryGetValue(key, out cached) && cached.Stamp == resolved.Stamp)
				return cached.Set;
			
			if(resolved.Failure is not null) {
				
				logger.LogInfo("CodeActions", $"{SourceOf(entry)}: {resolved.Stamp}");
				
				configured[key] = new ConfiguredSet(resolved.Stamp, resolved.Failure);
				
				return resolved.Failure;
			}
			
			var set = LoadConfigured(entry, resolved.Assemblies, out var transient);
			
			// A load that failed on the file system is not what the stamp stands for: the same
			// assemblies load at the next call, once the server that held a shadow copy is done (#412).
			// Only so many times, though. Each attempt is a load context of its own that never
			// unloads, and a file that stays unreadable must not add one per call for good.
			var failed = transientLoads.TryGetValue(key, out var earlier) && earlier.Stamp == resolved.Stamp ? earlier.Count : 0;
			
			if(transient && failed < MaxTransientLoads - 1) {
				
				transientLoads[key] = (resolved.Stamp, failed + 1);
				
				return set;
			}
			
			transientLoads.Remove(key);
			configured[key] = new ConfiguredSet(resolved.Stamp, set);
			
			return set;
		}
	}
	
	/// <summary>
	///     What an entry resolves to today: the assemblies to load, stamped with their modification
	///     times, or a set that says why nothing loads, stamped with that reason. A path from the committed
	///     project file is third-party code from a checkout and loads only with the per-user trust opt-in;
	///     a package entry resolves from the cache the user filled, so it needs none.
	/// </summary>
	static ResolvedEntry ResolveConfigured(ConfiguredProvider entry, bool trusted)
	{
		var kind   = KindOf(entry);
		var source = SourceOf(entry);
		
		if(entry.Committed && entry.Path is not null && !trusted)
			return ResolvedEntry.Failed(ProviderSet.Empty(source, kind, entry.DeclaredIn,
				"skipped: untrusted — an assembly path in a committed .madq_roslynmcp.json runs only with --trust-project-providers, " +
				"ROSLYNMCP_TRUST_PROJECT_PROVIDERS=true, or \"trustProjectProviders\": true in the user's config.json"));
		
		string[] assemblies;
		
		if(entry.Path is not null) {
			
			if(!File.Exists(entry.Path))
				return ResolvedEntry.Failed(ProviderSet.Empty(source, kind, entry.DeclaredIn, $"not found: '{entry.Path}' (declared in '{entry.DeclaredIn}')"));
			
			assemblies = [entry.Path];
		}
		else if(!NuGetPackageLocator.TryLocate(entry.Package!, entry.Version, out assemblies, out var problem))
			return ResolvedEntry.Failed(ProviderSet.Empty(source, kind, entry.DeclaredIn, $"{problem} (declared in '{entry.DeclaredIn}')"));
		
		return new ResolvedEntry(string.Join('|', assemblies.Select(path => $"{path}@{File.GetLastWriteTimeUtc(path).Ticks}")), assemblies, null);
	}
	
	static ProviderSourceKind KindOf(ConfiguredProvider entry)
		=> entry.Committed ? ProviderSourceKind.ProjectConfig : ProviderSourceKind.GlobalConfig;
	
	static string SourceOf(ConfiguredProvider entry)
		=> $"config: {entry.Describe()}";
	
	/// <summary>A configured entry's loaded set and the stamp it was loaded under (see <see cref="ConfiguredSetFor"/>).</summary>
	sealed record ConfiguredSet(string Stamp, ProviderSet Set);
	
	/// <summary>A configured entry resolved: assemblies to load, or the set that says why there are none.</summary>
	sealed record ResolvedEntry(string Stamp, string[] Assemblies, ProviderSet? Failure)
	{
		public static ResolvedEntry Failed(ProviderSet set)
			=> new(set.Report.Problems[0], [], set);
	}
	
	// A package that ships Roslyn or framework assemblies beside its providers must not have them
	// loaded a second time: the provider types would then derive from another Roslyn's base types.
	static bool IsHostAssembly(string simpleName)
		=> simpleName.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase)
			|| simpleName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
			|| simpleName.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
			|| simpleName.Equals("mscorlib", StringComparison.OrdinalIgnoreCase);
	
	/// <summary>
	///     The providers an assembly exports, found as Roslyn's own project-provider loader finds them:
	///     the types carrying an export attribute. Outside a composition a provider needs a parameterless
	///     constructor; one whose constructor imports services is reported and skipped.
	/// </summary>
	internal static ProviderSet Discover(Assembly assembly, string source, ProviderSourceKind kind, string origin)
	{
		var refactorings = ImmutableArray.CreateBuilder<ProviderEntry<CodeRefactoringProvider>>();
		var fixes        = ImmutableArray.CreateBuilder<ProviderEntry<CodeFixProvider>>();
		var problems     = ImmutableArray.CreateBuilder<string>();
		
		foreach(var type in ProviderCandidates(assembly, problems)) {
			
			if(type.IsAbstract || type.IsInterface)
				continue;
			
			try {
				
				if(type.GetCustomAttribute<ExportCodeRefactoringProviderAttribute>() is { } refactoringExport)
					Instantiate(type, refactoringExport.Name, refactoringExport.Languages, refactoringExport.DocumentKinds, refactoringExport.DocumentExtensions, source, refactorings, problems);
				
				if(type.GetCustomAttribute<ExportCodeFixProviderAttribute>() is { } fixExport)
					Instantiate(type, fixExport.Name, fixExport.Languages, fixExport.DocumentKinds, fixExport.DocumentExtensions, source, fixes, problems);
			}
			catch(Exception ex) {
				problems.Add($"{type.FullName}: {Describe(ex)}");
			}
		}
		
		return new ProviderSet(
			new ProviderSourceReport(source, kind, origin, refactorings.Count, fixes.Count, problems.ToImmutable()),
			refactorings.ToImmutable(),
			fixes.ToImmutable());
	}
	
	// The types of a provider assembly to look for exports on. When every type loads, all of
	// them. When some do not, reflection gives the ones that did and a count of the rest, and the
	// rest are a problem only where a provider or an analyzer is among them (#421): those types are
	// read from the metadata and each missing one is loaded by name, so the answer names the provider
	// or analyzer that was lost and why, and says nothing of a helper type that neither needed. An
	// analyzer is no candidate for an export, but Roslyn drops one that does not load without a word,
	// and this report is where a project's analyzers running short can be seen. The language an
	// analyzer is for is not looked at: an assembly that also holds Visual Basic analyzers, whose
	// base types need an assembly the server does not have, names each of them on a C# project too.
	static IReadOnlyList<Type> ProviderCandidates(Assembly assembly, ImmutableArray<string>.Builder problems)
	{
		try {
			return assembly.GetTypes();
		}
		catch(ReflectionTypeLoadException ex) {
			
			var loaded = new List<Type>(ex.Types.Where(type => type is not null)!);
			
			ImmutableArray<string> exported;
			
			try {
				exported = ExportedProviderTypes.Read(assembly);
			}
			catch(Exception) {
				
				// Without the names there is no telling whether a provider is among the failed types.
				problems.Add($"{assembly.GetName().Name}: {ex.LoaderExceptions.Length} type(s) could not be loaded: {ex.LoaderExceptions.FirstOrDefault()?.Message ?? "no detail"}");
				
				return loaded;
			}
			
			var known = loaded.Select(type => type.FullName).ToHashSet(StringComparer.Ordinal);
			
			foreach(var name in exported.Where(name => !known.Contains(name))) {
				
				try {
					loaded.Add(assembly.GetType(name, throwOnError: true)!);
				}
				catch(Exception failure) {
					problems.Add($"{name}: could not be loaded: {Describe(failure)}");
				}
			}
			
			return loaded;
		}
	}
	
	static void Instantiate<TProvider>(
		Type type,
		string? exportName,
		string[] languages,
		string[]? documentKinds,
		string[]? documentExtensions,
		string source,
		ImmutableArray<ProviderEntry<TProvider>>.Builder into,
		ImmutableArray<string>.Builder problems) where TProvider : class
	{
		if(!typeof(TProvider).IsAssignableFrom(type)) {
			
			problems.Add($"{type.FullName} is exported as a {typeof(TProvider).Name} but does not derive from it");
			
			return;
		}
		
		var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);
		
		if(constructor is null) {
			
			problems.Add($"{type.FullName}: no parameterless constructor (its MEF imports cannot be satisfied outside a composition) — skipped");
			
			return;
		}
		
		TProvider provider;
		
		try {
			provider = (TProvider) constructor.Invoke(null);
		}
		catch(Exception ex) {
			
			problems.Add($"{type.FullName}: {Describe(ex)}");
			
			return;
		}
		
		into.Add(new ProviderEntry<TProvider>(
			provider,
			type.FullName ?? type.Name,
			exportName,
			source,
			[..languages],
			[..documentKinds ?? []],
			[..documentExtensions ?? []]));
	}
	
	// A provider the server constructs itself: its export attribute still says what it is for; one
	// without an attribute (the harness provider) is a C# source-document provider.
	static ProviderEntry<TProvider> Describe<TProvider>(TProvider provider, string source) where TProvider : class
	{
		var     type       = provider.GetType();
		string? exportName = null;
		var     languages  = new[] { LanguageNames.CSharp };
		var     kinds      = (string[]?) null;
		var     extensions = (string[]?) null;
		
		if(type.GetCustomAttribute<ExportCodeFixProviderAttribute>() is { } fixExport) {
			
			exportName = fixExport.Name;
			languages  = fixExport.Languages;
			kinds      = fixExport.DocumentKinds;
			extensions = fixExport.DocumentExtensions;
		}
		else if(type.GetCustomAttribute<ExportCodeRefactoringProviderAttribute>() is { } refactoringExport) {
			
			exportName = refactoringExport.Name;
			languages  = refactoringExport.Languages;
			kinds      = refactoringExport.DocumentKinds;
			extensions = refactoringExport.DocumentExtensions;
		}
		
		return new ProviderEntry<TProvider>(provider, type.FullName ?? type.Name, exportName, source, [..languages], [..kinds ?? []], [..extensions ?? []]);
	}
	
	// ── Selection ────────────────────────────────────────────────────────────
	
	static ImmutableArray<ProviderEntry<TProvider>> Select<TProvider>(IEnumerable<ProviderEntry<TProvider>> entries, Document document, ImmutableHashSet<string> deny) where TProvider : class
	{
		var seen     = new HashSet<string>(StringComparer.Ordinal);
		var selected = ImmutableArray.CreateBuilder<ProviderEntry<TProvider>>();
		
		foreach(var entry in entries)
			if(entry.AppliesTo(document) && !IsDenied(entry, deny) && seen.Add(entry.Name))
				selected.Add(entry);
		
		return selected.ToImmutable();
	}
	
	// A deny entry names a provider by its export name, its full or short type name, or the stem of
	// the type name: InvertIf for CSharpInvertIfCodeRefactoringProvider. The stem is what the
	// built-in deny-list and users write; export names are whatever the author typed (Roslyn's
	// invert-if exports as "Invert If Code Action Provider"), so they cannot be the only key.
	static bool IsDenied<TProvider>(ProviderEntry<TProvider> entry, ImmutableHashSet<string> deny) where TProvider : class
	{
		if(entry.ExportName is not null && deny.Contains(entry.ExportName))
			return true;
		
		var shortName = entry.Name[(entry.Name.LastIndexOf('.') + 1)..];
		
		return deny.Contains(entry.Name) || deny.Contains(shortName) || deny.Contains(Stem(shortName));
	}
	
	static string Stem(string shortName)
	{
		var stem = shortName.StartsWith("CSharp", StringComparison.Ordinal) ? shortName["CSharp".Length..] : shortName;
		
		foreach(var suffix in (ReadOnlySpan<string>) ["CodeRefactoringProvider", "CodeFixProvider", "RefactoringProvider", "Provider"])
			if(stem.EndsWith(suffix, StringComparison.Ordinal))
				return stem[..^suffix.Length];
		
		return stem;
	}
	
	/// <summary>
	///     Walks an action to its leaves: a group action has no operations of its own, only its nested
	///     actions do. A leaf that needs a dialog (<see cref="CodeActionWithOptions"/>) or changes more
	///     than documents is set aside with the reason, as Roslyn's LSP host does.
	/// </summary>
	static void Flatten(CodeAction action, string? parentTitle, string provider, ImmutableArray<UnavailableAction>.Builder unavailable, Action<CodeAction, string> leaf)
	{
		var title  = parentTitle is null ? action.Title : $"{parentTitle} › {action.Title}";
		var nested = action.NestedActions;
		
		if(!nested.IsDefaultOrEmpty) {
			
			foreach(var child in nested)
				Flatten(child, title, provider, unavailable, leaf);
			
			return;
		}
		
		if(action is CodeActionWithOptions) {
			
			unavailable.Add(new UnavailableAction(title, provider, "needs a dialog for its options, which a headless host cannot show"));
			
			return;
		}
		
		if(action.Tags.Contains(RequiresNonDocumentChangeTag)) {
			
			unavailable.Add(new UnavailableAction(title, provider, "requires changes outside the documents, which the apply path does not make"));
			
			return;
		}
		
		leaf(action, title);
	}
	
	static string Describe(Exception ex)
	{
		var root = ex;
		
		while(root is TargetInvocationException or TypeInitializationException or AggregateException && root.InnerException is not null)
			root = root.InnerException;
		
		return $"{root.GetType().Name}: {root.Message}";
	}
	
	/// <summary>
	///     The metadata an export attribute carries, as System.Composition fills a metadata view: a
	///     class with a settable property per metadata key. Both export attributes publish these four.
	/// </summary>
	public sealed class ExportMetadata
	{
		public string?   Name               { get; set; }
		public string[]? Languages          { get; set; }
		public string[]? DocumentKinds      { get; set; }
		public string[]? DocumentExtensions { get; set; }
	}
}
