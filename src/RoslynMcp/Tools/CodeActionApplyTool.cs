using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace RoslynMcp.Tools;

/// <summary>
///     The apply half of a two-step code-action workflow, shared by <c>roslyn_apply_code_fix</c> and
///     <c>roslyn_apply_refactoring</c> (#389): token and approval handling, the workspace binding check,
///     the physical plan with its stale checks around the backups, and the result. The two tools differ
///     only in their workflow and names, so one body serves both. It returns an outcome rather than
///     calling the scope itself, because every return of a tool method goes through that method's own
///     scope (RMCP004). Not a tool: no <c>[McpServerToolType]</c>, and the tools deriving from it are sealed.
/// </summary>
internal abstract class CodeActionApplyTool : RoslynMcpTool
{
	protected readonly ApprovalStore           approvals;
	protected readonly BackupStore             backups;
	protected readonly PhysicalSolutionApplier physicalApplier;
	
	protected CodeActionApplyTool(WorkspaceResolver workspace, ApprovalStore approvals, BackupStore backups, FileLogger logger, PaginationCache paginationCache)
		: base(workspace, logger, paginationCache)
	{
		this.approvals  = approvals;
		this.backups    = backups;
		physicalApplier = new PhysicalSolutionApplier(workspace, backups);
	}
	
	/// <summary>
	///     What distinguishes one apply tool from the other. <see cref="Noun"/> starts a sentence
	///     ("Code fix"); <see cref="ChangeNoun"/> is the word the per-file recovery guidance uses ("fix").
	/// </summary>
	protected sealed record WorkflowNames(
		ApprovalWorkflow Workflow,
		string           SymbolKeyPrefix,
		string           PreviewTool,
		string           ApplyTool,
		string           Noun,
		string           ChangeNoun);
	
	protected sealed record ApplyOutcome(bool Success, string Detail, ApplyCodeActionResult Result);
	
	protected async Task<ApplyOutcome> ApplyAsync(string token, string approval, string? projectPath, WorkflowNames names, CancellationToken cancellationToken)
	{
		var noun = names.Noun;
		
		if(approval.Equals("n", StringComparison.OrdinalIgnoreCase)) {
			
			if(!approvals.Reject(token, names.Workflow))
				return Fail("token unavailable", $"Token '{token}' was not found, was consumed, or is already being applied.");
			
			return Fail("rejected", $"{noun} rejected. No files were changed.");
		}
		
		if(!approval.Equals("y", StringComparison.OrdinalIgnoreCase))
			return Fail("invalid approval", "Invalid approval value. Use 'y' or 'n'.");
		
		var operation = approvals.TryBeginApply(token, names.Workflow);
		
		if(operation is null)
			return Fail("token unavailable", $"Token '{token}' was not found, was consumed, or is already being applied. Run {names.PreviewTool} again if the operation is not currently in progress.");
		
		var physicalApplyStarted = false;
		
		try {
			
			if(!operation.SymbolKey.StartsWith(names.SymbolKeyPrefix, StringComparison.Ordinal))
				return Fail("token type mismatch", $"The approval token was not created by {names.PreviewTool}. Use the matching apply tool.");
			
			if(operation.WorkspaceBinding is null)
				return Fail("workspace binding missing", $"The preview token does not contain an originating workspace. Re-run {names.PreviewTool}.");
			
			// The mismatch check guards against applying a token to a workspace the caller *named*
			// differently. An omitted projectPath names nothing, so the token's own binding is the
			// workspace — resolving the session default here would compare against a workspace the
			// caller never named, and reject a valid token minted in another one.
			if(!string.IsNullOrWhiteSpace(projectPath)
				&& CheckApplyWorkspace(operation.WorkspaceBinding, projectPath, names.PreviewTool) is { } refusal)
				return Fail(refusal.Detail, refusal.Message, refusal.Kind);
			
			var boundProjectPath = operation.WorkspaceBinding.CanonicalPath;
			
			if(!TryResolveRoot(boundProjectPath, out var rootPath, out var rootError)) {
				
				var (message, kind) = DescribeResolveFailure(rootError);
				
				return Fail("workspace unavailable", message, kind);
			}
			
			PhysicalSolutionApplyPlan plan;
			
			try {
				
				plan = await PhysicalSolutionApplyPlan.BuildAsync(
					operation.BaseSolution,
					operation.NewSolution,
					operation.FileStates,
					cancellationToken);
			}
			catch(PhysicalApplyPlanException ex) {
				
				return Fail("invalid physical plan", $"{noun} cannot be applied safely: {ex.Message}");
			}
			
			// Second layer of the TOCTOU guard: the preview may be minutes old. Re-validate against
			// disk before spending any effort on backups, so a stale preview aborts early and cheap.
			if(plan.ValidateCurrentState() is { } staleError)
				return Fail("stale preview", $"{staleError} Re-run {names.PreviewTool}.");
			
			// Before any backup is written: a change that proposes different contents for two
			// documents that are one file on disk can never be applied (#338).
			if(PhysicalSolutionApplier.FindTargetConflict(plan, workspace.GetSecurityBoundary(boundProjectPath)) is { } conflict)
				return Fail("conflicting link contents", $"{noun} cannot be applied: {conflict} No files were modified.");
			
			try {
				
				await physicalApplier.PrepareBackupsAsync(plan, boundProjectPath, names.ApplyTool);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				
				return Fail("backup failed",
					$"Write aborted — backups could not be prepared: {ex.Message}. No source files were modified. " +
					"The approval token is still valid — retry after resolving the issue.");
			}
			
			// Third layer: preparing backups did real I/O and took time — re-validate so a file that
			// changed during backup is caught before the write loop (which validates a fourth time,
			// per-file, immediately before each atomic swap). Each layer narrows the TOCTOU window.
			if(plan.ValidateCurrentState() is { } postBackupStaleError)
				return Fail("stale preview",
					$"{postBackupStaleError} The file changed while backups were being prepared. " +
					"No source files were modified; the approval token is still valid.");
			
			// ApplyAsync can throw OperationCanceledException before touching any file (e.g. the
			// concurrency gate itself is canceled) — physicalApplyStarted is only latched to true once
			// the call returns a report, so a cancellation that never wrote anything returns the token
			// to pending instead of permanently consuming it.
			var report = await physicalApplier.ApplyAsync(plan, boundProjectPath, cancellationToken);
			physicalApplyStarted = true;
			
			string Relative(string path) => TryMakeRelative(path, rootPath) ?? path;
			
			var files   = PhysicalApplyResultMapper.MapFiles(report, plan, Relative, backups.IsEnabled, names.ChangeNoun);
			var created = Reached(plan, report, PhysicalFileOperation.Create, PhysicalApplyState.Written);
			var deleted = Reached(plan, report, PhysicalFileOperation.Delete, PhysicalApplyState.Deleted);
			var caution = ComposeCautions(
				PhysicalApplyResultMapper.LinkCaution(report, Relative),
				ExplicitCompileItemsCaution(operation.NewSolution, created, Relative));
			
			if(report.Succeeded)
				return new ApplyOutcome(true, $"{report.FilesWritten} file(s) written, {report.FilesDeleted} deleted", new ApplyCodeActionResult(
					$"{noun} applied. {report.FilesWritten} file(s) written and {report.FilesDeleted} deleted.",
					report.FilesWritten,
					null,
					report.FilesDeleted,
					files,
					created.Length == 0 ? null : [..created.Select(Relative)],
					deleted.Length == 0 ? null : [..deleted.Select(Relative)]) { Caution = caution });
			
			var error = report.AllFilesReachedIntendedState
				? "apply state uncertain"
				: report.IsPartial ? "partial apply" : "apply failed"
			;
			var executionNote = report.ExecutionError is null
				? string.Empty
				: $" Persistence error: {report.ExecutionError}."
			;
			var outcomeMessage = report.AllFilesReachedIntendedState
				? $"Every physical file matches the intended state of the {names.ChangeNoun}, but the workspace reported a persistence failure."
				: $"{noun} did not reach its intended state for every file."
			;
			
			return new ApplyOutcome(false, error, new ApplyCodeActionResult(
				$"{outcomeMessage}{executionNote} " +
				$"Review each files[].state and files[].recovery value before deciding whether to roll back or complete the {names.ChangeNoun}.",
				report.FilesWritten,
				error,
				report.FilesDeleted,
				files,
				created.Length == 0 ? null : [..created.Select(Relative)],
				deleted.Length == 0 ? null : [..deleted.Select(Relative)]) { Caution = caution });
		}
		finally {
			
			if(physicalApplyStarted)
				approvals.CompleteApply(token, names.Workflow, approveForSession: false);
			else
				approvals.ReturnToPending(token, names.Workflow);
		}
	}
	
	static ApplyOutcome Fail(string detail, string message)
		=> Fail(detail, message, detail);
	
	static ApplyOutcome Fail(string detail, string message, string kind)
		=> new(false, detail, new ApplyCodeActionResult(message, null, kind));
	
	// The planned files of one operation that the report says reached their state, by full path.
	static string[] Reached(PhysicalSolutionApplyPlan plan, PhysicalApplyReport report, PhysicalFileOperation operation, PhysicalApplyState state) =>
	[
		..plan.Files
			.Where(file => file.Operation == operation
				&& report.Files.Any(result => result.State == state && SecurityBoundary.PathComparer.Equals(result.Path, file.Path)))
			.Select(file => file.Path)
	];
	
	/// <summary>
	///     A created file is compiled by an SDK-style project through its default glob, and by nothing
	///     else: a project that lists its Compile items (no Sdk, or EnableDefaultCompileItems false) does
	///     not see it. Visual Studio adds the item through the project system when it applies the same
	///     action; the physical apply writes files only, so it says so. Null when nothing was created,
	///     the project is SDK-style, or the project file cannot be read (then nothing is known).
	/// </summary>
	static string? ExplicitCompileItemsCaution(Solution solution, string[] createdPaths, Func<string, string> toRelativePath)
	{
		if(createdPaths.Length == 0)
			return null;
		
		var projectFiles = createdPaths
			.SelectMany(path => solution.GetDocumentIdsWithFilePath(path))
			.Select(id => solution.GetProject(id.ProjectId)?.FilePath)
			.Where(path => path is not null)
			.Distinct(SecurityBoundary.PathComparer)
			.Where(path => ListsCompileItemsExplicitly(path!))
			.Select(path => toRelativePath(path!))
			.ToArray()
		;
		
		if(projectFiles.Length == 0)
			return null;
		
		return $"{string.Join(", ", projectFiles.Select(path => $"'{path}'"))} lists its Compile items explicitly, so the created file(s) " +
			$"{string.Join(", ", createdPaths.Select(path => $"'{toRelativePath(path)}'"))} are not compiled until added to the project. " +
			"Visual Studio adds them through the project system; this apply writes files only.";
	}
	
	static bool ListsCompileItemsExplicitly(string projectFile)
	{
		try {
			
			var root = FileReader.LoadXml(projectFile).Root;
			
			if(root is null)
				return false;
			
			var ns    = root.Name.Namespace;
			var isSdk = root.Attribute("Sdk") is not null
				|| root.Elements(ns + "Sdk").Any()
				|| root.Elements(ns + "Import").Any(import => import.Attribute("Sdk") is not null);
			
			if(!isSdk)
				return true;
			
			return root.Descendants(ns + "EnableDefaultCompileItems")
				.Any(element => string.Equals(element.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase));
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) {
			return false;
		}
	}
}

/// <summary>
///     The result of either apply tool. <see cref="FilesWritten"/>, <see cref="FilesDeleted"/> and
///     <see cref="Files"/> are null before the apply ran (a refused token, a stale preview);
///     <see cref="CreatedFiles"/> and <see cref="DeletedFiles"/> name what the apply brought into
///     being or removed, and are omitted when nothing was.
/// </summary>
internal sealed record ApplyCodeActionResult : ToolResult, IToolError
{
	public ApplyCodeActionResult(string message, int? filesWritten, string? error)
		: this(message, filesWritten, error, null, null, null, null)
	{ }
	
	public ApplyCodeActionResult(
		string message,
		int? filesWritten,
		string? error,
		int? filesDeleted,
		ApplyFileResult[]? files,
		string[]? createdFiles,
		string[]? deletedFiles)
	{
		Message      = message;
		FilesWritten = filesWritten;
		FilesDeleted = filesDeleted;
		Files        = files;
		CreatedFiles = createdFiles;
		DeletedFiles = deletedFiles;
		Error        = error;
	}
	
	public string             Message      { get; }
	public int?               FilesWritten { get; }
	public int?               FilesDeleted { get; }
	public ApplyFileResult[]? Files        { get; }
	
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string[]? CreatedFiles { get; }
	
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string[]? DeletedFiles { get; }
}
