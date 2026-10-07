using System.Security.Cryptography;
using Microsoft.CodeAnalysis;

namespace RoslynMcp;

internal enum PhysicalFileOperation
{
	Write,
	Create,
	Delete
}

internal enum PhysicalApplyState
{
	Written,
	Deleted,
	Untouched,
	Truncated,
	Uncertain
}

internal sealed record PhysicalFilePlan(
	string                Path,
	PhysicalFileOperation Operation,
	ExpectedFileState     OriginalState,
	string?               OriginalHash,
	byte[]?               IntendedBytes,
	string?               IntendedHash
);

internal sealed record PhysicalFileResult(
	string             Path,
	PhysicalApplyState State,
	string?            Error
);

internal sealed record PhysicalApplyReport(
	string?                           ExecutionError,
	IReadOnlyList<PhysicalFileResult> Files,
	IReadOnlyList<string>             ReplacedLinks)
{
	public int FilesWritten => Files.Count(file => file.State == PhysicalApplyState.Written);
	public int FilesDeleted => Files.Count(file => file.State == PhysicalApplyState.Deleted);
	
	public bool AllFilesReachedIntendedState =>
		Files.All(file => file.State is PhysicalApplyState.Written or PhysicalApplyState.Deleted);
	
	public bool Succeeded =>
		ExecutionError is null
		&& AllFilesReachedIntendedState;
	
	public bool IsPartial =>
		Files.Any(file => file.State is PhysicalApplyState.Written or PhysicalApplyState.Deleted)
		&& Files.Any(file => file.State is not (PhysicalApplyState.Written or PhysicalApplyState.Deleted));
}

internal sealed class PhysicalSolutionApplyPlan
{
	PhysicalSolutionApplyPlan(PhysicalFilePlan[] files)
	{
		Files = files;
	}
	
	public PhysicalFilePlan[] Files { get; }
	
	public static async Task<PhysicalSolutionApplyPlan> BuildAsync(
		Solution baseSolution,
		Solution newSolution,
		IReadOnlyDictionary<string, PreviewFileState>? previewFileStates,
		CancellationToken cancellationToken = default)
	{
		if(previewFileStates is null)
			throw new PhysicalApplyPlanException("The preview does not contain physical file states.");
		
		var candidates = new Dictionary<string, List<PhysicalFileCandidate>>(StringComparer.OrdinalIgnoreCase);
		
		foreach(var projectChange in newSolution.GetChanges(baseSolution).GetProjectChanges()) {
			
			foreach(var documentId in projectChange.GetChangedDocuments().Concat(projectChange.GetAddedDocuments())) {
				
				var document = newSolution.GetDocument(documentId);
				
				if(document?.FilePath is null)
					continue;
				
				if(!previewFileStates.TryGetValue(document.FilePath, out var previewState)
					|| previewState.IntendedBytes is null)
					throw new PhysicalApplyPlanException(
						$"The preview does not contain intended bytes for '{document.FilePath}'.");
				
				AddCandidate(candidates, document.FilePath, new PhysicalFileCandidate(previewState.IntendedBytes));
			}
			
			foreach(var documentId in projectChange.GetChangedAdditionalDocuments()) {
				
				var document = newSolution.GetAdditionalDocument(documentId);
				
				if(document?.FilePath is null)
					continue;
				
				if(!previewFileStates.TryGetValue(document.FilePath, out var previewState)
					|| previewState.IntendedBytes is null)
					throw new PhysicalApplyPlanException(
						$"The preview does not contain intended bytes for '{document.FilePath}'.");
				
				AddCandidate(candidates, document.FilePath, new PhysicalFileCandidate(previewState.IntendedBytes));
			}
			
			foreach(var documentId in projectChange.GetChangedAnalyzerConfigDocuments()) {
				
				var document = newSolution.GetAnalyzerConfigDocument(documentId);
				
				if(document?.FilePath is null)
					continue;
				
				if(!previewFileStates.TryGetValue(document.FilePath, out var previewState)
					|| previewState.IntendedBytes is null)
					throw new PhysicalApplyPlanException(
						$"The preview does not contain intended bytes for '{document.FilePath}'.");
				
				AddCandidate(candidates, document.FilePath, new PhysicalFileCandidate(previewState.IntendedBytes));
			}
			
			foreach(var documentId in projectChange.GetRemovedDocuments()) {
				
				var document = baseSolution.GetDocument(documentId);
				
				if(document?.FilePath is null || !newSolution.GetDocumentIdsWithFilePath(document.FilePath).IsEmpty)
					continue;
				
				AddCandidate(candidates, document.FilePath, new PhysicalFileCandidate(null));
			}
		}
		
		var files = new List<PhysicalFilePlan>(candidates.Count);
		
		foreach(var (path, pathCandidates) in candidates) {
			
			if(!previewFileStates.TryGetValue(path, out var original))
				throw new PhysicalApplyPlanException($"The preview does not contain an original state for '{path}'.");
			
			var writeCandidates = pathCandidates
				.Where(candidate => candidate.IntendedBytes is not null)
			;
			var hasDelete = pathCandidates.Any(candidate => candidate.IntendedBytes is null);
			
			if(hasDelete && writeCandidates.Any())
				throw new PhysicalApplyPlanException($"The solution both writes and deletes the physical path '{path}'.");
			
			if(hasDelete) {
				
				files.Add(new PhysicalFilePlan(
					path,
					PhysicalFileOperation.Delete,
					original.ExpectedState,
					original.ContentHash,
					null,
					null));
				
				continue;
			}
			
			var intendedContents = writeCandidates
				.Select(candidate => candidate.IntendedBytes!)
				.ToArray()
			;
			var intendedHashes = intendedContents
				.Select(ComputeHash)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray()
			;
			
			if(intendedHashes.Length != 1)
				throw new PhysicalApplyPlanException(
					$"Linked documents propose divergent contents for the physical path '{path}'.");
			
			var intendedBytes = intendedContents[0];
			var operation = original.ExpectedState == ExpectedFileState.Absent
				? PhysicalFileOperation.Create
				: PhysicalFileOperation.Write
			;
			
			files.Add(new PhysicalFilePlan(
				path,
				operation,
				original.ExpectedState,
				original.ContentHash,
				intendedBytes,
				intendedHashes[0]));
		}
		
		if(files.Count == 0)
			throw new PhysicalApplyPlanException("The previewed solution contains no physical file changes.");
		
		return new PhysicalSolutionApplyPlan(files.ToArray());
	}
	
	public string? ValidateCurrentState()
	{
		foreach(var file in Files) {
			
			if(ValidateCurrentState(file) is { } error)
				return error;
		}
		
		return null;
	}
	
	public static string? ValidateCurrentState(PhysicalFilePlan file)
	{
		try {
			
			if(file.OriginalState == ExpectedFileState.Absent) {
				
				return File.Exists(file.Path)
					? $"Apply aborted — '{file.Path}' was created after preview."
					: null;
			}
			
			if(!File.Exists(file.Path))
				return $"Apply aborted — '{file.Path}' no longer exists.";
			
			var currentHash = ComputeFileHash(file.Path);
			
			return string.Equals(currentHash, file.OriginalHash, StringComparison.OrdinalIgnoreCase)
				? null
				: $"Apply aborted — '{file.Path}' changed after preview.";
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			return $"Apply aborted — the current state of '{file.Path}' could not be verified: {ex.Message}";
		}
	}
	
	public PhysicalFileResult[] Verify()
	{
		var results = new PhysicalFileResult[Files.Length];
		
		for(var index = 0; index < Files.Length; index++)
			results[index] = Verify(Files[index]);
		
		return results;
	}
	
	static PhysicalFileResult Verify(PhysicalFilePlan file)
	{
		try {
			
			if(file.Operation == PhysicalFileOperation.Delete) {
				
				if(!File.Exists(file.Path))
					return new PhysicalFileResult(file.Path, PhysicalApplyState.Deleted, null);
				
				var deleteActualHash = ComputeFileHash(file.Path);
				
				return string.Equals(deleteActualHash, file.OriginalHash, StringComparison.OrdinalIgnoreCase)
					? new PhysicalFileResult(file.Path, PhysicalApplyState.Untouched, null)
					: new PhysicalFileResult(
						file.Path,
						PhysicalApplyState.Uncertain,
						"File still exists and matches neither the intended deleted state nor its original content.");
			}
			
			if(!File.Exists(file.Path)) {
				
				return file.Operation == PhysicalFileOperation.Create
					? new PhysicalFileResult(file.Path, PhysicalApplyState.Untouched, null)
					: new PhysicalFileResult(
						file.Path,
						PhysicalApplyState.Truncated,
						"Existing file is missing after application.");
			}
			
			var actualBytes = File.ReadAllBytes(file.Path);
			var actualHash  = ComputeHash(actualBytes);
			
			if(string.Equals(actualHash, file.IntendedHash, StringComparison.OrdinalIgnoreCase))
				return new PhysicalFileResult(file.Path, PhysicalApplyState.Written, null);
			
			if(file.OriginalState == ExpectedFileState.Exists
				&& string.Equals(actualHash, file.OriginalHash, StringComparison.OrdinalIgnoreCase))
				return new PhysicalFileResult(file.Path, PhysicalApplyState.Untouched, null);
			
			if(file.IntendedBytes!.Length > 0 && actualBytes.Length <= 4)
				return new PhysicalFileResult(
					file.Path,
					PhysicalApplyState.Truncated,
					"File is empty or nearly empty after application.");
			
			return new PhysicalFileResult(
				file.Path,
				PhysicalApplyState.Uncertain,
				"File content matches neither the original nor intended hash.");
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			return new PhysicalFileResult(
				file.Path,
				PhysicalApplyState.Uncertain,
				$"Physical file state could not be verified: {ex.Message}");
		}
	}
	
	static void AddCandidate(
		IDictionary<string, List<PhysicalFileCandidate>> candidates,
		string path,
		PhysicalFileCandidate candidate)
	{
		if(!candidates.TryGetValue(path, out var pathCandidates)) {
			
			pathCandidates = [];
			candidates.Add(path, pathCandidates);
		}
		
		pathCandidates.Add(candidate);
	}
	
	static string ComputeFileHash(string path)
	{
		using var stream = File.OpenRead(path);
		var hash = SHA256.HashData(stream);
		
		return Convert.ToHexString(hash);
	}
	
	static string ComputeHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
	
	sealed record PhysicalFileCandidate(byte[]? IntendedBytes);
}

internal sealed class PhysicalSolutionApplier
{
	static readonly SemaphoreSlim applyGate = new(1, 1);
	
	readonly WorkspaceResolver workspace;
	readonly BackupStore      backups;
	
	public PhysicalSolutionApplier(WorkspaceResolver workspace, BackupStore backups)
	{
		this.workspace = workspace;
		this.backups   = backups;
	}
	
	public async Task PrepareBackupsAsync(
		PhysicalSolutionApplyPlan plan,
		string projectPath,
		string operation)
	{
		foreach(var file in plan.Files) {
			
			if(file.Operation != PhysicalFileOperation.Create)
				await backups.SavePreAsync(file.Path, projectPath, operation);
			
			if(file.Operation != PhysicalFileOperation.Delete)
				await backups.SavePostAsync(file.Path, projectPath, operation, file.IntendedBytes!);
		}
	}
	
	public async Task<PhysicalApplyReport> ApplyAsync(
		PhysicalSolutionApplyPlan plan,
		string projectPath,
		CancellationToken cancellationToken = default)
	{
		await applyGate.WaitAsync(cancellationToken);
		
		try {
			
			// First of the layered stale checks: guard the whole plan up front, before touching any
			// file, so an apply against a preview that no longer matches disk fails fast and clean.
			var executionError = plan.ValidateCurrentState();
			
			WriteTarget?[] targets = [];
			
			if(executionError is null) {
				
				try {
					
					// Worked out for the whole plan before the first write: two entries that
					// disagree about one file must fail the apply while nothing has changed yet.
					targets = ResolveWriteTargets(plan, projectPath);
					
					await ApplyWritesAsync(plan, targets, projectPath, cancellationToken);
				}
				catch(Exception ex) when(ex is not OutOfMemoryException and not OperationCanceledException) {
					
					executionError = ex.Message;
				}
			}
			
			if(executionError is null) {
				
				try {
					
					ApplyDeletes(plan, projectPath, cancellationToken);
				}
				catch(Exception ex) when(ex is not OutOfMemoryException and not OperationCanceledException) {
					
					executionError = ex.Message;
				}
			}
			
			var results = plan.Verify();
			
			return new PhysicalApplyReport(executionError, results, ReplacedLinks(plan, targets, results));
		}
		finally {
			
			applyGate.Release();
		}
	}
	
	/// <summary>
	///     Where one plan entry is written. <paramref name="Path"/> is the file the swap happens at.
	/// </summary>
	/// <param name="FollowsLink">The document is a link and <paramref name="Path"/> is where it leads.</param>
	/// <param name="ReplacesLink">The document is a link that leads out of the workspace, and is replaced where it sits.</param>
	/// <param name="SharedWithEarlier">An earlier entry writes this same file with the same content; this one writes nothing.</param>
	sealed record WriteTarget(string Path, bool FollowsLink, bool ReplacesLink, bool SharedWithEarlier);
	
	/// <summary>
	///     Where each entry of <paramref name="plan"/> is written, in plan order; null for a delete.
	///     <para>
	///         A document that is a symbolic link is written <b>through</b> the link when the
	///         workspace boundary allows where it leads, so the link stays and the file behind it
	///         changes — the same as every other write tool (#322, #334). A link that leads out
	///         of the workspace is not followed: it is replaced where it sits by a regular file,
	///         and the file it pointed at is left alone.
	///     </para>
	///     <para>
	///         Two entries can then name one file: a link and its target, or one file reached
	///         through a linked directory. The first one writes it. A later one with the same
	///         intended content is marked as shared and writes nothing — its own stale check
	///         would otherwise see the first write and report a partial apply that did not
	///         happen. A later one with different content cannot be applied at all.
	///     </para>
	/// </summary>
	/// <exception cref="PhysicalApplyPlanException">Two entries propose different contents for one file.</exception>
	WriteTarget?[] ResolveWriteTargets(PhysicalSolutionApplyPlan plan, string projectPath)
	{
		var boundary = workspace.GetSecurityBoundary(projectPath);
		var targets  = new WriteTarget?[plan.Files.Length];
		var writers  = new Dictionary<string, PhysicalFilePlan>(SecurityBoundary.PathComparer);
		
		for(var index = 0; index < plan.Files.Length; index++) {
			
			var file = plan.Files[index];
			
			if(file.Operation == PhysicalFileOperation.Delete)
				continue;
			
			var path     = Path.GetFullPath(file.Path);
			var follows  = false;
			var replaces = false;
			
			if(file.Operation == PhysicalFileOperation.Write && FileWriter.IsLink(path)) {
				
				if(boundary.IsPathAllowed(path)) {
					
					path    = FileWriter.FollowLink(path);
					follows = true;
				}
				else
					replaces = true;
			}
			
			// A replaced link is its own file from here on. Everything else is known by where it
			// really is, with links in the directories above it followed too.
			var identity = replaces ? path : SecurityBoundary.ResolveLinks(path) ?? path;
			var shared   = false;
			
			if(writers.TryGetValue(identity, out var writer)) {
				
				if(!string.Equals(writer.IntendedHash, file.IntendedHash, StringComparison.OrdinalIgnoreCase))
					throw new PhysicalApplyPlanException(
						$"Apply aborted — '{writer.Path}' and '{file.Path}' are one file on disk, reached through a symbolic link, " +
						"and the change proposes different contents for them. No files were modified.");
				
				shared = true;
			}
			else
				writers.Add(identity, file);
			
			targets[index] = new WriteTarget(path, follows, replaces, shared);
		}
		
		return targets;
	}
	
	/// <summary>
	///     The links that were replaced by a regular file: planned that way, and written.
	/// </summary>
	static string[] ReplacedLinks(PhysicalSolutionApplyPlan plan, WriteTarget?[] targets, PhysicalFileResult[] results)
	{
		var replaced = new List<string>();
		
		for(var index = 0; index < targets.Length; index++)
			if(targets[index] is { ReplacesLink: true } && results[index].State == PhysicalApplyState.Written)
				replaced.Add(plan.Files[index].Path);
		
		return [..replaced];
	}
	
	async Task ApplyWritesAsync(
		PhysicalSolutionApplyPlan plan,
		WriteTarget?[] targets,
		string projectPath,
		CancellationToken cancellationToken)
	{
		for(var index = 0; index < plan.Files.Length; index++) {
			
			var file = plan.Files[index];
			
			if(targets[index] is not { } target)
				continue;
			
			// Only safe cancellation boundary: between whole files, before this file's temp write and
			// atomic swap. Never mid-swap — a half-replaced file is worse than an honest partial apply.
			// Already-swapped files remain; not-yet-started files stay untouched (same as any per-file
			// failure, which the report already models as partial).
			cancellationToken.ThrowIfCancellationRequested();
			
			if(target.SharedWithEarlier) {
				
				// Already on disk through the entry that shares the file. The document at this
				// path still has to learn about it.
				workspace.InvalidateFile(projectPath, file.Path);
				
				continue;
			}
			
			// The swap is at target.Path: the document's own path, or the file its link leads to.
			var directory = Path.GetDirectoryName(target.Path)
				?? throw new IOException($"File '{target.Path}' has no parent directory.");
			var temporaryPath = Path.Combine(
				directory,
				$".{Path.GetFileName(target.Path)}.{Guid.NewGuid():N}.tmp");
			
			try {
				
				// Created with the permissions of the file it replaces: the swap puts the temp
				// file in the target's place with the temp file's own mode.
				var mode = file.Operation == PhysicalFileOperation.Write ? FileWriter.UnixModeOf(target.Path) : null;
				
				await FileWriter.WriteAllBytesAsync(temporaryPath, file.IntendedBytes!, mode);
				
				await workspace.WriteAndInvalidate(projectPath, file.Path, () => {
					
					// The swap is retried, like every other disk write. On Windows a virus scanner
					// or indexer that has the target open for a moment makes File.Replace fail with
					// "Unable to remove the file to be replaced"; the target is left exactly as it
					// was and the temp file is still there, so trying again is safe. Without the
					// retry the whole apply failed on such a blip (#330). Five attempts wait up to
					// about 750 ms in total — a scan of a source file is over well before that.
					FileWriter.WriteWithRetry(() => {
						
						// Innermost stale check, before every attempt: re-verify this one file
						// immediately before the swap. Backups, the temp write and a retry's wait
						// take real wall-clock time during which the target could change on disk;
						// this is the last TOCTOU gate that still lets us abort without corrupting
						// the file.
						if(PhysicalSolutionApplyPlan.ValidateCurrentState(file) is { } staleError)
							throw new IOException(staleError);
						
						// The boundary approved one place. A link that has been pointed somewhere
						// else since then has not been checked, even when the content still matches.
						if(target.FollowsLink
							&& !string.Equals(FileWriter.FollowLink(file.Path), target.Path, StringComparison.Ordinal))
							throw new IOException($"Apply aborted — the symbolic link '{file.Path}' was changed after preview.");
						
						// A link that is replaced where it sits needs a move: on Windows File.Replace
						// refuses a target that is a symbolic link, and the apply failed there (#334).
						// The move swaps the link itself for the new file, on every platform.
						if(file.Operation == PhysicalFileOperation.Create)
							File.Move(temporaryPath, target.Path);
						else if(target.ReplacesLink)
							File.Move(temporaryPath, target.Path, overwrite: true);
						else
							File.Replace(temporaryPath, target.Path, null);
					}, 5, file.Path);
					
					return Task.CompletedTask;
				});
				
				// The file behind the link can be a document of its own, at its own path.
				if(target.FollowsLink)
					workspace.InvalidateFile(projectPath, target.Path);
			}
			finally {
				
				try {
					
					if(File.Exists(temporaryPath))
						File.Delete(temporaryPath);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
			}
		}
	}
	
	void ApplyDeletes(PhysicalSolutionApplyPlan plan, string projectPath, CancellationToken cancellationToken)
	{
		foreach(var file in plan.Files) {
			
			if(file.Operation != PhysicalFileOperation.Delete)
				continue;
			
			cancellationToken.ThrowIfCancellationRequested();
			
			if(File.Exists(file.Path))
				File.Delete(file.Path);
			
			workspace.InvalidateFile(projectPath, file.Path);
		}
	}
}

internal sealed class PhysicalApplyPlanException : Exception
{
	public PhysicalApplyPlanException(string message)
		: base(message)
	{ }
}

/// <summary>
///     Per-file outcome of a physical apply, shared across every apply tool built on
///     <see cref="PhysicalSolutionApplier"/> (code fix, rename, signature change).
/// </summary>
internal sealed record ApplyFileResult(
	string  Path,
	string  State,
	string? Error,
	string  Recovery
);

/// <summary>
///     Maps a <see cref="PhysicalApplyReport"/> to the shared <see cref="ApplyFileResult"/> shape
///     and produces the local-history recovery guidance shown per file — one implementation reused
///     by every apply tool built on <see cref="PhysicalSolutionApplier"/>, instead of triplicating
///     this projection logic.
/// </summary>
internal static class PhysicalApplyResultMapper
{
	public static ApplyFileResult[] MapFiles(
		PhysicalApplyReport report,
		PhysicalSolutionApplyPlan plan,
		Func<string, string> toRelativePath,
		bool backupsEnabled,
		string changeNoun)
	{
		var plannedFiles = plan.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
		
		return report.Files
			.Select(file => {
				
				var relativePath = toRelativePath(file.Path);
				var plannedFile  = plannedFiles[file.Path];
				
				return new ApplyFileResult(
					relativePath,
					file.State.ToString().ToLowerInvariant(),
					file.Error,
					RecoveryGuidance(file.State, plannedFile.Operation, relativePath, backupsEnabled, changeNoun));
			})
			.ToArray()
		;
	}
	
	/// <summary>
	///     The caution for an apply that replaced symbolic links by regular files, or null when it
	///     replaced none. Without it the change is silent: the file has the new content and looks
	///     fine, and nothing says that it is no longer the link it was.
	/// </summary>
	public static string? LinkCaution(PhysicalApplyReport report, Func<string, string> toRelativePath)
	{
		if(!report.ReplacedLinks.Any())
			
			return null;
		
		var links = string.Join(", ", report.ReplacedLinks.Select(path => $"'{toRelativePath(path)}'"));
		
		return $"Symbolic links that lead out of the workspace were replaced by regular files holding the new content: {links}. " +
			"The files they pointed at were not changed, and restoring a backup does not bring the links back.";
	}
	
	public static string RecoveryGuidance(
		PhysicalApplyState state,
		PhysicalFileOperation operation,
		string filePath,
		bool backupsEnabled,
		string changeNoun)
	{
		if(state == PhysicalApplyState.Untouched)
			return "No recovery is needed; the file still matches its preview baseline.";
		
		if(!backupsEnabled)
			return "Local history is disabled. Inspect the file and use source control or another backup to recover it.";
		
		var listStep = $"Use roslyn_local_history with action 'list' and filePath '{filePath}', " +
			"then call action 'preview' with the selected backup token before applying it.";
		
		return (state, operation) switch {
			
			(PhysicalApplyState.Written, PhysicalFileOperation.Write) =>
				$"{listStep} Apply the 'pre' snapshot to restore the original file.",
			
			(PhysicalApplyState.Written, PhysicalFileOperation.Create) =>
				$"This file did not exist before the {changeNoun}, so there is no 'pre' snapshot. " +
				$"Delete it to roll back. To restore the intended content, {listStep} Apply the 'post' snapshot.",
			
			(PhysicalApplyState.Deleted, _) =>
				$"{listStep} Apply the 'pre' snapshot to recreate the deleted original file.",
			
			(PhysicalApplyState.Truncated or PhysicalApplyState.Uncertain, PhysicalFileOperation.Create) =>
				$"Inspect the file first. It had no original version, so delete it to roll back. " +
				$"To complete the {changeNoun}, {listStep} Apply the 'post' snapshot.",
			
			(PhysicalApplyState.Truncated or PhysicalApplyState.Uncertain, PhysicalFileOperation.Delete) =>
				$"Inspect the file first. {listStep} Apply the 'pre' snapshot to restore the original content.",
			
			_ =>
				$"Inspect the file first. {listStep} Apply the 'pre' snapshot to restore the original content, " +
				$"or the 'post' snapshot to complete the intended {changeNoun}."
		};
	}
}
