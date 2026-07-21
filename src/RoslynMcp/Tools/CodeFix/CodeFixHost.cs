using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using RoslynMcp.Analyzers;

namespace RoslynMcp.Tools;

internal sealed class CodeFixHost
{
	readonly ImmutableArray<CodeFixProvider> providers =
	[
		new PreferNintOverIntPtrCodeFixProvider(),
		new ToolScopeCodeFixProvider()
	];
	
	public async Task<ImmutableArray<AvailableCodeFix>> GetFixesAsync(
		Document document,
		Diagnostic diagnostic,
		CancellationToken cancellationToken)
	{
		var fixes = ImmutableArray.CreateBuilder<AvailableCodeFix>();
		
		foreach(var provider in providers) {
			
			if(!provider.FixableDiagnosticIds.Contains(diagnostic.Id))
				continue;
			
			var providerName = provider.GetType().FullName ?? provider.GetType().Name;
			var context = new CodeFixContext(
				document,
				diagnostic,
				(action, diagnostics) => fixes.Add(new AvailableCodeFix(
					diagnostic.Id,
					providerName,
					action.Title,
					action.EquivalenceKey,
					provider,
					action)),
				cancellationToken);
			
			try {
				
				await provider.RegisterCodeFixesAsync(context)
					.ConfigureAwait(false)
				;
			}
			catch(Exception ex) {
				
				throw new CodeFixProviderException(providerName, ex);
			}
		}
		
		return fixes.ToImmutable();
	}
}

internal sealed class CodeFixProviderException : Exception
{
	public CodeFixProviderException(string providerName, Exception innerException)
		: base($"Code fix provider '{providerName}' failed: {innerException.Message}", innerException)
	{
		ProviderName = providerName;
	}
	
	public string ProviderName { get; }
}

internal sealed record AvailableCodeFix(
	string          DiagnosticId,
	string          ProviderName,
	string          Title,
	string?         EquivalenceKey,
	CodeFixProvider Provider,
	CodeAction      Action
);
