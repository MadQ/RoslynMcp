using System.ComponentModel;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;
using ModelContextProtocol.Server;

namespace RoslynMcp.Tools;

[McpServerToolType]
internal sealed class PreviewRenameTool : RoslynMcpTool
{
	readonly ApprovalStore approvals;
	
	public PreviewRenameTool(WorkspaceResolver workspace, ApprovalStore approvals, FileLogger logger, PaginationCache paginationCache) : base(workspace, logger, paginationCache)
	{
		this.approvals = approvals;
	}
	
	[McpServerTool(Name = "roslyn_preview_rename", ReadOnly = true, Title = "Preview Rename", OpenWorld = false, Idempotent = true)]
	[Description(
		"Use this to rename a symbol (type, method, field, property, parameter, or local) across all files before committing. " +
		"Returns a unified diff of every affected file plus a confirmation token — review the diff, then pass the token " +
		"to roslyn_apply_rename to commit or cancel. " +
		"This is step 1 of a two-step rename workflow; no files are written until roslyn_apply_rename is called. " +
		"Renames can affect dozens or hundreds of files — always preview before applying. " +
		"If the symbol was previously approved for the session, the token is pre-confirmed and the response message will say so. " +
		"Provide containingType when multiple symbols share the same name to avoid ambiguous matches. " +
		"For direct text replacement without a review step, use roslyn_replace_in_code instead.")]
	public async Task<PreviewRenameResult> PreviewRename(
		[Description("Current symbol name to rename, e.g. 'WindowKey'. Use roslyn_get_type_members or roslyn_find_references to verify the exact name before renaming.")] string symbolName,
		[Description("New name for the symbol, e.g. 'WindowIdentity'. Must be a valid C# identifier.")] string newName,
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken,
		[Description("Optional containing type to narrow the search when multiple symbols share the same name, e.g. 'WindowTracker'.")] string? containingType = null)
	{
		using var scope = BeginTool("roslyn_preview_rename", $"{symbolName}→{newName}");
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return new PreviewRenameResult(
				null, null,
				JsonSerializer.Serialize(error),
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
		var newSolution = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName, cancellationToken);
		var diff        = await SolutionDiff.BuildAsync(solution, newSolution, cancellationToken);
		var token       = approvals.Register(solution, newSolution, diff, symbolKey);
		var preConfirmed = approvals.IsSessionApproved(symbolKey);
		
		return new PreviewRenameResult(token, diff,
			preConfirmed
				? $"Session-approved. Call apply_rename with token '{token}' to apply, or 'n' to reject."
				: $"Review the diff, then call apply_rename with token '{token}' and approval 'y' or 'session'.",
			preConfirmed
		);
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
