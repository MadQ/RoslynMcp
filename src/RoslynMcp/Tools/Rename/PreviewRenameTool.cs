using System.ComponentModel;
using System.Text.Json;
using RoslynMcp;
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
		"Provide containingType when multiple symbols share the same name to avoid ambiguous matches, " +
		"or pass filePath+line (and optional column) to resolve the symbol at an exact source position — " +
		"the precise way to rename one specific overload, local, or parameter. " +
		"Ambiguous names never rename a first match: when the client supports MCP elicitation the user is asked " +
		"to pick the intended symbol; otherwise the call fails with the candidate list. " +
		"For direct text replacement without a review step, use roslyn_replace_in_code instead.")]
	public async Task<PreviewRenameResult> PreviewRename(
		[Description("Current symbol name to rename, e.g. 'WindowKey'. Use roslyn_get_type_members or roslyn_find_references to verify the exact name before renaming.")] string symbolName,
		[Description("New name for the symbol, e.g. 'WindowIdentity'. Must be a valid C# identifier.")] string newName,
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken,
		McpServer server,
		[Description("Optional containing type to narrow the search when multiple symbols share the same name, e.g. 'WindowTracker'.")] string? containingType = null,
		[Description("Optional relative file path for position-based resolution, e.g. 'Core/Foo.cs'. Required when line is specified.")] string? filePath = null,
		[Description("Optional 1-based line number. When > 0, resolves the symbol at filePath:line:column instead of by name.")] int line = 0,
		[Description("1-based column for position-based resolution. Default: 1.")] int column = 1)
	{
		using var scope = BeginTool("roslyn_preview_rename", $"{symbolName}→{newName}", new { containingType, line });
		
		// Use TryGetProject so symbol and solution both derive from the same workspace
		// snapshot — Renamer.RenameSymbolAsync requires the symbol to belong to the
		// solution it receives.
		if(!TryGetProject(projectPath, out var project, out var error))
			
			return scope.Failed("workspace error", new PreviewRenameResult(null, null, JsonSerializer.Serialize(error, RoslynMcpJson.Compact), false));
		
		var compilation = await project.GetCompilationAsync(cancellationToken);
		
		if(compilation is null)
			
			return scope.Failed("compilation unavailable", new PreviewRenameResult(
				null, null, "Compilation unavailable — the project may have unresolved references or errors.", false));
		
		if(line > 0 && filePath is null)
			
			return scope.Failed("filePath required", new PreviewRenameResult(
				null, null, "filePath is required when line is specified.", false));
		
		// Position (line > 0) pinpoints one symbol — the precise way to rename one overload,
		// local, or parameter. The compilation-based lookup keeps the symbol in this snapshot.
		ISymbol? symbol;
		
		if(line > 0)
			symbol = await FindSymbolAtPosition(compilation, filePath!, line, column, cancellationToken);
		
		else {
			
			// Renaming the wrong symbol mutates code silently — never fall back to a first match.
			// On ambiguity, ask the user via MCP elicitation when the client supports it; otherwise
			// fail with the candidate list so the agent retries with containingType or filePath+line.
			var candidates = FindSymbols(compilation, symbolName, containingType);
			
			symbol = candidates.Length == 1 ? candidates[0] : null;
			
			if(candidates.Length > 1) {
				
				var rootPath = workspace.GetRootPath(projectPath);
				
				symbol = await TryElicitSymbolChoice(server, candidates, symbolName, rootPath, cancellationToken);
				
				if(symbol is null)
					
					return scope.Failed("ambiguous symbol", new PreviewRenameResult(
						null, null,
						$"Multiple symbols match '{symbolName}' — disambiguate with containingType or filePath+line: "
							+ string.Join("; ", candidates.Select(c => FormatSymbolCandidate(c, rootPath))),
						false));
			}
		}
		
		if(symbol is null)
			
			return scope.Failed("symbol not found", new PreviewRenameResult(
				null, null,
				$"Symbol '{symbolName}' not found. Use get_type_members or find_references to verify the name.",
				false
			));
		
		// symbol and solution are from the same snapshot — no stale-ref risk.
		var solution   = project.Solution
		;
		var symbolKey  = SymbolKey(symbol);
		var fileRename = ComputeFileRename(symbol, newName);
		
		var newSolution  = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions { RenameFile = false }, newName, cancellationToken);
		var diff         = await SolutionDiff.BuildAsync(solution, newSolution, cancellationToken);
		var token        = approvals.Register(solution, newSolution, diff, symbolKey, fileRename);
		var preConfirmed = approvals.IsSessionApproved(symbolKey);
		
		return scope.Outcome("preview ready", new PreviewRenameResult(token, diff,
			preConfirmed
				? $"Pre-confirmed. Call apply_rename with token '{token}' and approval 'y' or 'session'."
				: $"Review the diff, then call apply_rename with token '{token}' and approval 'y' or 'session'.",
			preConfirmed
		));
	}
	
	/// <summary>
	///     Returns the file rename plan for a symbol if the current file stem matches the symbol's
	///     declared name. Only applies to non-nested named types in a single source file.
	/// </summary>
	private static (string OldPath, string NewPath)? ComputeFileRename(ISymbol symbol, string newName)
	{
		// Only top-level named types can trigger a file rename.
		if(symbol is not INamedTypeSymbol { ContainingType: null })
			
			return null;
		
		// Partial types span multiple files — renaming any single file is ambiguous.
		var sourceLocs = symbol.Locations.Where(l => l.IsInSource).ToArray()
		;
		
		if(sourceLocs.Length != 1)
			
			return null;
		
		var path = sourceLocs[0].SourceTree?.FilePath;
		
		if(path is null)
			
			return null;
		
		// Only rename if the file stem exactly matches the type's current name.
		var stem = Path.GetFileNameWithoutExtension(path)
		;
		
		if(!string.Equals(stem, symbol.Name, StringComparison.Ordinal))
			
			return null;
		
		var dir     = Path.GetDirectoryName(path)!;
		var ext     = Path.GetExtension(path);
		var newPath = Path.Combine(dir, newName + ext);
		
		// Guard: already identical (shouldn't happen in practice, but be safe).
		if(string.Equals(path, newPath, StringComparison.Ordinal))
			
			return null;
		
		return (path, newPath);
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
