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
		string projectPath)
	{
		await applyGate.WaitAsync();
		
		try {
			var executionError = plan.ValidateCurrentState();
			
			if(executionError is null) {
				
				try {
					
					await ApplyWritesAsync(plan, projectPath);
				}
				catch(Exception ex) when(ex is not OutOfMemoryException) {
					
					executionError = ex.Message;
				}
			}
			
			if(executionError is null) {
				
				try {
					
					ApplyDeletes(plan, projectPath);
				}
				catch(Exception ex) when(ex is not OutOfMemoryException) {
					
					executionError = ex.Message;
				}
			}
			
			return new PhysicalApplyReport(executionError, plan.Verify());
		}
		finally {
			
			applyGate.Release();
		}
	}
	
	async Task ApplyWritesAsync(PhysicalSolutionApplyPlan plan, string projectPath)
	{
		foreach(var file in plan.Files) {
			
			if(file.Operation == PhysicalFileOperation.Delete)
				continue;
			
			var directory = Path.GetDirectoryName(file.Path)
				?? throw new IOException($"File '{file.Path}' has no parent directory.");
			var temporaryPath = Path.Combine(
				directory,
				$".{Path.GetFileName(file.Path)}.{Guid.NewGuid():N}.tmp");
			
			try {
				
				await FileWriter.WriteAllBytesAsync(temporaryPath, file.IntendedBytes!);
				
				if(!OperatingSystem.IsWindows() && file.Operation == PhysicalFileOperation.Write)
					File.SetUnixFileMode(temporaryPath, File.GetUnixFileMode(file.Path));
				
				if(PhysicalSolutionApplyPlan.ValidateCurrentState(file) is { } staleError)
					throw new IOException(staleError);
				
				await workspace.WriteAndInvalidate(projectPath, file.Path, () => {
					
					if(file.Operation == PhysicalFileOperation.Create)
						File.Move(temporaryPath, file.Path);
					else
						File.Replace(temporaryPath, file.Path, null);
					
					return Task.CompletedTask;
				});
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
