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
	
	// Export names (PredefinedCodeRefactoringProviderNames) of built-ins the server never runs: the
	// server has its own signature change; rename tracking is an editor feature; the rest open a
	// dialog, so every action of theirs would be filtered out anyway, and not running them saves the work.
	static readonly ImmutableHashSet<string> builtInDeny = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
		"ChangeSignature", "RenameTracking", "ExtractInterface", "ExtractClass", "PullMemberUp", "MoveStaticMembers", "MoveToNamespace");
	
	readonly FileLogger        logger;
	readonly Lazy<ProviderSet> builtIn;
	readonly ProviderSet       bundled;
	
	// Providers from a project's analyzer references, keyed by (path, mtime) as AnalyzerLoading keys its analyzers.
	readonly ConcurrentDictionary<(string Path, long MtimeTicks), ProviderSet> fromReferences = new();
	
	public CodeActionHost(FileLogger logger)
	{
		this.logger = logger;
		builtIn     = new(LoadBuiltIn, LazyThreadSafetyMode.ExecutionAndPublication);
		bundled     = LoadBundled();
	}
	
	/// <summary>The built-in source's report, or null while nothing has asked for it yet; reporting never forces the composition.</summary>
	public ProviderSourceReport? BuiltInReport => builtIn.IsValueCreated ? builtIn.Value.Report : null;
	
	public ProviderSourceReport BundledReport => bundled.Report;
	
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
					(leaf, title) => actions.Add(new RefactoringAction(leaf, title, entry.Name, entry.Source, leaf.EquivalenceKey)));
		}
		
		actions.Sort((left, right) => string.CompareOrdinal(left.Provider, right.Provider) is var byProvider && byProvider != 0
			? byProvider
			: string.CompareOrdinal(left.Title, right.Title));
		
		return new RefactoringQuery(actions.ToImmutable(), unavailable.ToImmutable(), providers.Sources, failures.ToImmutable());
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
		
		return new CodeFixQuery(fixes.ToImmutable(), unavailable.ToImmutable(), providers.Sources, failures.ToImmutable());
	}
	
	// ── Sources ──────────────────────────────────────────────────────────────
	
	ProviderSet LoadBuiltIn()
	{
		var refactorings = ImmutableArray.CreateBuilder<ProviderEntry<CodeRefactoringProvider>>();
		var fixes        = ImmutableArray.CreateBuilder<ProviderEntry<CodeFixProvider>>();
		var problems     = ImmutableArray.CreateBuilder<string>();
		
		try {
			
			var container = MefComposition.Container;
			
			foreach(var export in container.GetExports<Lazy<CodeRefactoringProvider, ExportMetadata>>())
				AddExport(export, refactorings, problems);
			
			foreach(var export in container.GetExports<Lazy<CodeFixProvider, ExportMetadata>>())
				AddExport(export, fixes, problems);
		}
		catch(Exception ex) {
			problems.Add($"composition failed: {Describe(ex)}");
		}
		
		logger.LogInfo("CodeActions", $"built-in providers: {refactorings.Count} refactoring, {fixes.Count} fix" + (problems.Count == 0 ? string.Empty : $", {problems.Count} problem(s): {string.Join("; ", problems)}"));
		
		return new ProviderSet(
			new ProviderSourceReport("built-in", ProviderSourceKind.BuiltIn, null, refactorings.Count, fixes.Count, problems.ToImmutable()),
			refactorings.ToImmutable(),
			fixes.ToImmutable());
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
		
		foreach(var reference in project.AnalyzerReferences) {
			
			if(reference.FullPath is not { } path || !File.Exists(path))
				continue;
			
			sets.Add(fromReferences.GetOrAdd((path, File.GetLastWriteTimeUtc(path).Ticks), key => LoadFromReference(key.Path)));
		}
	}
	
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
	///     The providers an assembly exports, found as Roslyn's own project-provider loader finds them:
	///     the types carrying an export attribute. Outside a composition a provider needs a parameterless
	///     constructor; one whose constructor imports services is reported and skipped.
	/// </summary>
	internal static ProviderSet Discover(Assembly assembly, string source, ProviderSourceKind kind, string origin)
	{
		var refactorings = ImmutableArray.CreateBuilder<ProviderEntry<CodeRefactoringProvider>>();
		var fixes        = ImmutableArray.CreateBuilder<ProviderEntry<CodeFixProvider>>();
		var problems     = ImmutableArray.CreateBuilder<string>();
		Type[] types;
		
		try {
			types = assembly.GetTypes();
		}
		catch(ReflectionTypeLoadException ex) {
			
			types = [..ex.Types.Where(type => type is not null)!];
			problems.Add($"{ex.LoaderExceptions.Length} type(s) could not be loaded: {ex.LoaderExceptions.FirstOrDefault()?.Message ?? "no detail"}");
		}
		
		foreach(var type in types) {
			
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
	
	// A deny entry names a provider by its export name, its type name or its full type name.
	static bool IsDenied<TProvider>(ProviderEntry<TProvider> entry, ImmutableHashSet<string> deny) where TProvider : class
		=> (entry.ExportName is not null && deny.Contains(entry.ExportName))
			|| deny.Contains(entry.Name)
			|| deny.Contains(entry.Name[(entry.Name.LastIndexOf('.') + 1)..]);
	
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
