using System.ComponentModel;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynMcp.Tools.SignatureChange;

namespace RoslynMcp.Tools;

/// <summary>
///     Thin MCP entry point for signature changes. Validates input, delegates to
///     <see cref="SignatureChangePlanner"/>, and manages the preview/apply token workflow.
///     See docs/plans/change-signature-tool.md for full design.
/// </summary>
[McpServerToolType]
internal sealed class ChangeSignatureTool : RoslynMcpTool
{
	readonly ApprovalStore approvals;
	
	public ChangeSignatureTool(WorkspaceResolver workspace, ApprovalStore approvals, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, logger, paginationCache)
	{
		this.approvals = approvals;
	}
	
	[McpServerTool(Name = "roslyn_change_signature", ReadOnly = true, Title = "Change Signature", OpenWorld = false, Idempotent = true)]
	[Description(
		"Previews adding parameters to a method signature — step 1 of a two-step workflow; no files are written until roslyn_apply_signature_change is called. " +
		"Creates a non-breaking forwarding overload of the original method, marked [Obsolete], so all existing call sites continue to compile unchanged. " +
		"Returns a unified diff and a confirmation token — review the diff, then pass the token to roslyn_apply_signature_change to commit or cancel. " +
		"Provide addParameters as a JSON array with name, type, and defaultValue for each new parameter. " +
		"Provide containingType when multiple methods share the same name to avoid ambiguous matches. " +
		"Ambiguous names never modify a first match: the call fails with a structured candidates list — " +
		"re-call with a candidate's containingType. (If the server was started with --elicit and the client " +
		"supports MCP elicitation, the user is asked to pick instead.) " +
		"For direct file edits without a review step, use roslyn_replace_in_code instead.")]
	public async Task<object> ChangeSignature(
		[Description("Method name to change, e.g. 'ProcessOrder'. Use roslyn_get_type_members to verify the exact name before proceeding.")] string methodName,
		[Description(ProjectPathDescription)] string projectPath,
		CancellationToken cancellationToken,
		McpServer server,
		[Description("Optional containing type to disambiguate when multiple methods share the same name, e.g. 'OrderService'.")] string? containingType = null,
		[Description("Parameters to add as a JSON array: [{\"name\":\"x\",\"type\":\"string\",\"defaultValue\":\"\\\"default\\\"\"}]. Each entry requires name, type, and defaultValue. Omit or pass null to preview the overload structure without adding parameters.")] string? addParameters = null)
	{
		using var scope = BeginTool("roslyn_change_signature", containingType is not null ? $"{containingType}.{methodName}" : methodName, new { containingType, addParameters });
		
		if(!TryStripChatSymbolRef(ref methodName, out var refError) || !TryStripChatSymbolRefOptional(ref containingType, out refError))
			
			return scope.Error(refError!);
		
		if(!TryGetCompilation(projectPath, out var compilation, out var error))
			
			return scope.Error(error!);
		
		// Same ambiguity policy as roslyn_preview_rename: never modify a first match — fail with
		// a structured candidate list the agent retries from; the interactive picker is a
		// server-level opt-in (--elicit / ROSLYNMCP_ELICIT / project-local .madq_roslynmcp.json).
		var candidates = FindSymbols(compilation, methodName, containingType);
		
		var symbol = candidates.Length == 1 ? candidates[0] : null;
		
		if(candidates.Length > 1) {
			
			var rootPath = workspace.GetRootPath(projectPath);
			
			if(ProjectConfig.EffectiveElicit(rootPath, logger))
				symbol = await TryElicitSymbolChoice(server, candidates, methodName, rootPath, cancellationToken);
			
			if(symbol is null)
				
				return scope.Failed("ambiguous symbol", AmbiguousSymbolError(candidates, methodName, rootPath));
		}
		
		if(symbol is not IMethodSymbol method)
			
			return scope.Failed("not a method", symbol is null
				? new ErrorResult($"Symbol '{methodName}' not found.", "Use get_type_members or find_references to verify the name.")
				: new ErrorResult($"'{methodName}' is a {symbol.Kind}, not a method."));
		
		// Parse the parameters to add.
		NewParameter[] paramsToAdd
		;
		
		try {
			
			paramsToAdd = string.IsNullOrWhiteSpace(addParameters)
				? []
				: JsonSerializer.Deserialize<NewParameter[]>(addParameters, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []
			;
		}
		catch(JsonException ex) {
			
			return scope.Failed("invalid parameters", new ErrorResult(
				$"Failed to parse addParameters JSON: {ex.Message}",
				"Expected: [{\"name\":\"x\",\"type\":\"string\",\"defaultValue\":\"\\\"default\\\"\"}]")
			);
		}
		
		var planner  = new SignatureChangePlanner();
		var solution = workspace.GetSolution(projectPath);
		
		var result = await planner.PrepareAsync(method, new SignatureChangeRequest {
			AddParameters = paramsToAdd
		}, solution, compilation, cancellationToken);
		
		if(!result.Success)
			
			return scope.Failed("change failed", new ErrorResult(result.Error!));
		
		var token = approvals.Register(result.BaseSolution, result.NewSolution, result.Diff!,
			$"{method.ContainingType?.ToDisplayString()}::{method.Name}({string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString()))})",
			ApprovalWorkflow.SignatureChange
		)
		;
		
		return scope.Outcome($"+{result.ParametersAdded.Length} param ({string.Join(", ", result.ParametersAdded)})", new ChangeSignatureResult(
			Diff:               result.Diff!,
			Token:              token,
			Message:            $"Review the diff, then call apply_signature_change with token '{token}' and approval 'y' or 'session'.",
			ParametersAdded:    result.ParametersAdded,
			DeprecationMessage: result.DeprecationMessage,
			FilesAffected:      result.FilesAffected)
		{
			Caution = AdhocCaution(projectPath)
		});
	}
}
