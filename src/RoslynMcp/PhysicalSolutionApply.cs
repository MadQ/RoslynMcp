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
	IReadOnlyList<PhysicalFileResult> Files)
{
	public int FilesWritten => Files.Count(file => file.State == PhysicalApplyState.Written);
	public int FilesDeleted => Files.Count(file => file.State == PhysicalApplyState.Deleted);
	
	public bool Succeeded =>
		ExecutionError is null
		&& Files.All(file => file.State is PhysicalApplyState.Written or PhysicalApplyState.Deleted);
	
	public bool IsPartial =>
		!Succeeded
		&& Files.Any(file => file.State is PhysicalApplyState.Written or PhysicalApplyState.Deleted);
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
				
				var text = await document.GetTextAsync(cancellationToken);
				var bytes = FileWriter.Utf8NoBom.GetBytes(text.ToString());
				AddCandidate(candidates, document.FilePath, new PhysicalFileCandidate(bytes));
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
				.ToArray()
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
			
			var intendedContents = new List<byte[]>();
			
			foreach(var documentId in newSolution.GetDocumentIdsWithFilePath(path)) {
				
				var linkedDocument = newSolution.GetDocument(documentId);
				
				if(linkedDocument is null)
					continue;
				
				var linkedText = await linkedDocument.GetTextAsync(cancellationToken);
				intendedContents.Add(FileWriter.Utf8NoBom.GetBytes(linkedText.ToString()));
			}
			
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
			
			try {
				
				if(file.OriginalState == ExpectedFileState.Absent) {
					
					if(File.Exists(file.Path))
						return $"Apply aborted — '{file.Path}' was created after preview.";
					
					continue;
				}
				
				if(!File.Exists(file.Path))
					return $"Apply aborted — '{file.Path}' no longer exists.";
				
				var currentHash = ComputeFileHash(file.Path);
				
				if(!string.Equals(currentHash, file.OriginalHash, StringComparison.OrdinalIgnoreCase))
					return $"Apply aborted — '{file.Path}' changed after preview.";
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				
				return $"Apply aborted — the current state of '{file.Path}' could not be verified: {ex.Message}";
			}
		}
		
		return null;
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
		Solution newSolution,
		string projectPath)
	{
		string? executionError = null;
		
		try {
			
			if(workspace.IsAdhoc(projectPath))
				await ApplyAdhocWritesAsync(plan, projectPath);
			else if(!workspace.ApplyChanges(projectPath, newSolution))
				executionError = "Workspace refused to apply the previewed solution.";
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
			
			executionError = ex.Message;
		}
		
		if(executionError is null) {
			
			try {
				
				ApplyDeletes(plan, projectPath);
			}
			catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
				
				executionError = ex.Message;
			}
		}
		
		return new PhysicalApplyReport(executionError, plan.Verify());
	}
	
	async Task ApplyAdhocWritesAsync(PhysicalSolutionApplyPlan plan, string projectPath)
	{
		foreach(var file in plan.Files) {
			
			if(file.Operation == PhysicalFileOperation.Delete)
				continue;
			
			await workspace.WriteAndInvalidate(projectPath, file.Path,
				() => FileWriter.WriteAllBytesAsync(file.Path, file.IntendedBytes!));
		}
	}
	
	void ApplyDeletes(PhysicalSolutionApplyPlan plan, string projectPath)
	{
		foreach(var file in plan.Files) {
			
			if(file.Operation != PhysicalFileOperation.Delete)
				continue;
			
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
