using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoslynMcp;

/// <summary>Where a set of providers came from. The tools report the kind next to each source.</summary>
internal enum ProviderSourceKind
{
	/// <summary>Roslyn's own providers, from the Features assemblies in <see cref="MefComposition"/>.</summary>
	BuiltIn,
	
	/// <summary>The server's own fixers, from <c>RoslynMcp.Analyzers</c>; always on.</summary>
	Bundled,
	
	/// <summary>An analyzer reference of the project: a NuGet analyzer package or an <c>&lt;Analyzer Include&gt;</c> item.</summary>
	Project,
	
	/// <summary>An entry of the user's <c>config.json</c>.</summary>
	GlobalConfig,
	
	/// <summary>An entry of the committed <c>.madq_roslynmcp.json</c>.</summary>
	ProjectConfig,
}

/// <summary>
///     A provider the host may run, with the export metadata that says which documents it is for and a
///     label saying where it came from. <see cref="Name"/> is the provider's full type name, the one
///     stable identity across Roslyn versions; <see cref="ExportName"/> is the optional <c>Name</c> of
///     its export attribute (Roslyn's built-ins set it to a <c>PredefinedCodeRefactoringProviderNames</c>
///     value).
/// </summary>
internal sealed record ProviderEntry<TProvider>(
	TProvider              Provider,
	string                 Name,
	string?                ExportName,
	string                 Source,
	ImmutableArray<string> Languages,
	ImmutableArray<string> DocumentKinds,
	ImmutableArray<string> DocumentExtensions) where TProvider : class
{
	/// <summary>
	///     Whether the export metadata admits <paramref name="document"/>: its language, the document
	///     kind (an export that names none means source documents) and, when the export limits them,
	///     the file extension.
	/// </summary>
	public bool AppliesTo(Document document)
	{
		if(!Languages.Contains(document.Project.Language))
			return false;
		
		if(!DocumentKinds.IsEmpty && !DocumentKinds.Contains(nameof(TextDocumentKind.Document)))
			return false;
		
		if(DocumentExtensions.IsEmpty)
			return true;
		
		var extension = Path.GetExtension(document.FilePath ?? document.Name);
		
		return DocumentExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
	}
}

/// <summary>What one source contributed, and what went wrong loading it; reported by the tools and by <c>roslyn_info</c>.</summary>
internal sealed record ProviderSourceReport(
	string                 Source,
	ProviderSourceKind     Kind,
	string?                Origin,
	int                    Refactorings,
	int                    Fixes,
	ImmutableArray<string> Problems)
{
	/// <summary>Diagnostic analyzers the source carries beside its providers; only the built-in source has any (Roslyn's IDE analyzers).</summary>
	public int Analyzers { get; init; }
}

/// <summary>
///     The providers one source yielded, with its report. <see cref="Analyzers"/> holds the diagnostic
///     analyzers found beside them — Roslyn's IDE analyzers (IDE0xxx) live in the Features assemblies
///     with their fixers, and a fixer is only as useful as the diagnostic it fixes.
/// </summary>
internal sealed record ProviderSet(
	ProviderSourceReport                                   Report,
	ImmutableArray<ProviderEntry<CodeRefactoringProvider>> Refactorings,
	ImmutableArray<ProviderEntry<CodeFixProvider>>         Fixes,
	ImmutableArray<DiagnosticAnalyzer>                     Analyzers)
{
	public ProviderSet(
		ProviderSourceReport report,
		ImmutableArray<ProviderEntry<CodeRefactoringProvider>> refactorings,
		ImmutableArray<ProviderEntry<CodeFixProvider>> fixes)
		: this(report, refactorings, fixes, [])
	{ }
	
	/// <summary>A source that yielded nothing, for the reason given.</summary>
	public static ProviderSet Empty(string source, ProviderSourceKind kind, string? origin, string problem)
		=> new(new ProviderSourceReport(source, kind, origin, 0, 0, [problem]), [], []);
}

/// <summary>
///     The analyzers that run for a project when analyzers are asked for: the project's own analyzer
///     references, and Roslyn's IDE analyzers while built-in code actions are on for it.
/// </summary>
internal sealed record AnalyzerSet(ImmutableArray<DiagnosticAnalyzer> Analyzers, int FromProject, int BuiltIn)
{
	/// <summary>The project's analyzer references whose file is not there, by path; their analyzers did not run (#424).</summary>
	public ImmutableArray<string> MissingReferences { get; init; } = [];
}

/// <summary>The providers that apply to one document, after the settings and the deny-list.</summary>
internal sealed record ResolvedProviders(
	CodeActionSettings                                     Settings,
	ImmutableArray<ProviderEntry<CodeRefactoringProvider>> Refactorings,
	ImmutableArray<ProviderEntry<CodeFixProvider>>         Fixes,
	ImmutableArray<ProviderSourceReport>                   Sources);

/// <summary>A provider that threw while computing its actions. The others still ran.</summary>
internal sealed record ProviderFailure(string Provider, string Error);

/// <summary>
///     An action the agent can preview. <see cref="Title"/> is the flattened title (<c>Parent › Child</c>
///     for a nested action); <see cref="Action"/> is the leaf, the one with operations.
/// </summary>
internal sealed record RefactoringAction(CodeAction Action, string Title, string Provider, string? ExportName, string Source, string? EquivalenceKey);

/// <summary>An action a provider offered that a headless host cannot run, and why.</summary>
internal sealed record UnavailableAction(string Title, string Provider, string Reason);

internal sealed record RefactoringQuery(
	CodeActionSettings                   Settings,
	ImmutableArray<RefactoringAction>    Actions,
	ImmutableArray<UnavailableAction>    Unavailable,
	ImmutableArray<ProviderSourceReport> Sources,
	ImmutableArray<ProviderFailure>      Failures);

internal sealed record AvailableCodeFix(
	string          DiagnosticId,
	string          ProviderName,
	string          Title,
	string?         EquivalenceKey,
	CodeFixProvider Provider,
	CodeAction      Action);

internal sealed record CodeFixQuery(
	CodeActionSettings                   Settings,
	ImmutableArray<AvailableCodeFix>     Fixes,
	ImmutableArray<UnavailableAction>    Unavailable,
	ImmutableArray<ProviderSourceReport> Sources,
	ImmutableArray<ProviderFailure>      Failures);
