using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class PreviewRenameTool : RoslynMcpTool
{
	readonly ApprovalStore approvals;
	
	public PreviewRenameTool(WorkspaceResolver workspace, ApprovalStore approvals, FileLogger logger) : base(workspace, logger)
	{
		this.approvals = approvals;
	}
	
	[McpServerTool(Name = "roslyn_preview_rename", ReadOnly = true)]
	[Description(
		"Previews renaming a symbol across all files. Returns a unified diff and a confirmation token. " +
		"Pass the token to apply_rename to commit the change, or discard it to cancel. " +
		"If the symbol was previously approved for this session, the token is pre-confirmed.")]
	public async Task<PreviewRenameResult> PreviewRename(
		[Description("Current symbol name, e.g. 'WindowKey'.")] string symbolName,
		[Description("New name, e.g. 'WindowIdentity'.")] string newName,
		[Description(ProjectPathDescription)] string projectPath,
		[Description("Optional containing type to disambiguate, e.g. 'WindowTracker'.")] string? containingType = null)
	{
		using var scope = BeginTool("roslyn_preview_rename", $"{symbolName}→{newName}");
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			return new PreviewRenameResult(
				null, null,
				error.ToString()!,
				false
			);
		
		var symbol      = FindSymbol(compilation, symbolName, containingType);
		
		if(symbol is null)
			return scope.Failed("symbol not found", new PreviewRenameResult(
				null, null,
				$"Symbol '{symbolName}' not found. Use get_type_members or find_references to verify the name.",
				false
			));
		
		var solution    = workspace.GetSolution(projectPath);
		var symbolKey   = SymbolKey(symbol);
		var newSolution = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName);
		var diff        = await SolutionDiff.BuildAsync(solution, newSolution);
		var token       = approvals.Register(solution, newSolution, diff, symbolKey);
		var preConfirmed = approvals.IsSessionApproved(symbolKey);
		
		return new PreviewRenameResult(token, diff,
			preConfirmed
				? $"Session-approved. Call apply_rename with token '{token}' to apply, or 'n' to reject."
				: $"Review the diff, then call apply_rename with token '{token}' and approval 'y' or 'session'.",
			preConfirmed
		);
	}
	
	private static ISymbol? FindSymbol(Compilation compilation, string name, string? inType)
	{
		if(inType is not null) {
			var type = compilation.GetTypeByMetadataName(inType)
				?? compilation.GlobalNamespace.Accept(new SimpleNameFinder<INamedTypeSymbol>(inType));
			
			return type?.GetMembers(name).FirstOrDefault();
		}
		
		return compilation.GlobalNamespace.Accept(new AnySymbolFinder(name));
	}
	
	private static string SymbolKey(ISymbol symbol)
	{
		var container = symbol.ContainingType?.ToDisplayString() ?? symbol.ContainingNamespace?.ToDisplayString();

		if(symbol is IMethodSymbol method) {

			var parameters = string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString()));
			return $"{container}::{method.Name}({parameters})";
		}

		return $"{container}::{symbol.Name}";
	}
}

internal sealed record PreviewRenameResult(
	string? Token,
	string? Diff,
	string  Message,
	bool    PreConfirmed
);
