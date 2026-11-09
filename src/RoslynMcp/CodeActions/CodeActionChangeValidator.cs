using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcp;

/// <summary>What a validated change does to the disk: for the preview to show and the apply to report.</summary>
internal sealed record ValidatedChanges(ImmutableArray<string> CreatedFiles, ImmutableArray<string> DeletedFiles, int ChangedFiles);

/// <summary>
///     Checks that the physical apply path can carry every change a code action proposes, before a
///     token is minted (#389). Shared by the code-fix and refactoring previews. Allowed: text changes
///     to existing source, additional and analyzer-config documents the workspace tracks, and the
///     creation and deletion of <c>.cs</c> source files inside the workspace. Refused: everything the
///     apply has no plan entry for (added or removed additional and analyzer-config documents,
///     project, reference and option changes), a document rename or move, a generated, linked,
///     external or read-only file. Every refusal is an <see cref="UnsupportedCodeActionChangeException"/>
///     whose message says which file and why.
/// </summary>
internal static class CodeActionChangeValidator
{
	public static async Task<ValidatedChanges> ValidateAsync(
		Solution baseSolution,
		Solution newSolution,
		string workspaceRoot,
		SecurityBoundary boundary,
		CancellationToken cancellationToken)
	{
		var changes = newSolution.GetChanges(baseSolution);
		
		if(changes.GetAddedProjects().Any() || changes.GetRemovedProjects().Any())
			throw new UnsupportedCodeActionChangeException("Code actions cannot add or remove projects.");
		
		var canonicalRoot = ResolveCanonicalPath(workspaceRoot);
		var changed       = 0;
		var created       = ImmutableArray.CreateBuilder<string>();
		var deleted       = ImmutableArray.CreateBuilder<string>();
		
		foreach(var projectChange in changes.GetProjectChanges()) {
			
			var oldProject = projectChange.OldProject;
			var newProject = projectChange.NewProject;
			
			if(projectChange.GetAddedAdditionalDocuments().Any()
				|| projectChange.GetRemovedAdditionalDocuments().Any()
				|| projectChange.GetAddedAnalyzerConfigDocuments().Any()
				|| projectChange.GetRemovedAnalyzerConfigDocuments().Any())
				throw new UnsupportedCodeActionChangeException(
					"Code actions can create and delete source files only; creating or deleting additional or analyzer configuration files is not supported.");
			
			if(projectChange.GetAddedProjectReferences().Any()
				|| projectChange.GetRemovedProjectReferences().Any()
				|| projectChange.GetAddedMetadataReferences().Any()
				|| projectChange.GetRemovedMetadataReferences().Any()
				|| projectChange.GetAddedAnalyzerReferences().Any()
				|| projectChange.GetRemovedAnalyzerReferences().Any())
				throw new UnsupportedCodeActionChangeException(
					"Code actions cannot change project, metadata, or analyzer references.");
			
			// An analyzer-config edit changes CompilationOptions only through the SyntaxTreeOptionsProvider
			// derived from it; stripping the provider keeps every explicit option change rejectable. A code
			// action cannot inject its own provider: ProjectState replaces it with a workspace-derived one on
			// every CompilationOptions update, so a provider difference can only come from analyzer config.
			if(!string.Equals(oldProject.Name, newProject.Name, StringComparison.Ordinal)
				|| !string.Equals(oldProject.AssemblyName, newProject.AssemblyName, StringComparison.Ordinal)
				|| !SecurityBoundary.PathComparer.Equals(oldProject.FilePath, newProject.FilePath)
				|| !Equals(
					oldProject.CompilationOptions?.WithSyntaxTreeOptionsProvider(null),
					newProject.CompilationOptions?.WithSyntaxTreeOptionsProvider(null))
				|| !Equals(oldProject.ParseOptions, newProject.ParseOptions))
				throw new UnsupportedCodeActionChangeException(
					"Code actions cannot change project identity, compilation options, or parse options.");
			
			foreach(var documentId in projectChange.GetChangedDocuments()) {
				
				var oldDocument = baseSolution.GetDocument(documentId);
				var newDocument = newSolution.GetDocument(documentId);
				
				if(oldDocument?.FilePath is null || newDocument?.FilePath is null)
					throw new UnsupportedCodeActionChangeException(
						"Code actions require every changed document to have a physical file path.");
				
				var canonicalFile = ValidateChangedPhysicalTextDocument(baseSolution, oldDocument, newDocument, canonicalRoot, "source file");
				var oldText       = await oldDocument.GetTextAsync(cancellationToken);
				
				if(IsGeneratedFile(canonicalFile, Path.GetRelativePath(canonicalRoot, canonicalFile), oldText))
					throw new UnsupportedCodeActionChangeException(
						$"Code actions cannot modify generated source file '{oldDocument.FilePath}'.");
				
				changed++;
			}
			
			foreach(var documentId in projectChange.GetChangedAdditionalDocuments()) {
				
				var oldDocument = baseSolution.GetAdditionalDocument(documentId);
				var newDocument = newSolution.GetAdditionalDocument(documentId);
				
				if(oldDocument?.FilePath is null || newDocument?.FilePath is null)
					throw new UnsupportedCodeActionChangeException(
						"Code actions require every changed additional document to have a physical file path.");
				
				ValidateChangedPhysicalTextDocument(baseSolution, oldDocument, newDocument, canonicalRoot, "additional document");
				changed++;
			}
			
			foreach(var documentId in projectChange.GetChangedAnalyzerConfigDocuments()) {
				
				var oldDocument = baseSolution.GetAnalyzerConfigDocument(documentId);
				var newDocument = newSolution.GetAnalyzerConfigDocument(documentId);
				
				if(oldDocument?.FilePath is null || newDocument?.FilePath is null)
					throw new UnsupportedCodeActionChangeException(
						"Code actions require every changed analyzer config document to have a physical file path.");
				
				ValidateChangedPhysicalTextDocument(baseSolution, oldDocument, newDocument, canonicalRoot, "analyzer config document");
				changed++;
			}
			
			foreach(var documentId in projectChange.GetAddedDocuments()) {
				
				var document = newSolution.GetDocument(documentId);
				
				if(document?.FilePath is null)
					throw new UnsupportedCodeActionChangeException(
						"Code actions require every created document to have a physical file path.");
				
				var path = document.FilePath;
				
				// A document added at a path the solution already tracks is another name for a file
				// that exists (a link); the preview state treats it as a change to that file.
				if(!baseSolution.GetDocumentIdsWithFilePath(path).IsEmpty) {
					
					changed++;
					
					continue;
				}
				
				var canonicalFile = ResolveCanonicalPath(path);
				var relativePath  = Path.GetRelativePath(canonicalRoot, canonicalFile);
				
				if(!IsContained(canonicalFile, canonicalRoot, relativePath) || !boundary.IsPathAllowed(path))
					throw new UnsupportedCodeActionChangeException(
						$"Code actions cannot create a file outside the workspace: '{path}'.");
				
				if(IsGeneratedFile(canonicalFile, relativePath, null))
					throw new UnsupportedCodeActionChangeException(
						$"Code actions cannot create generated source file '{path}'.");
				
				if(File.Exists(path) || Directory.Exists(path))
					throw new UnsupportedCodeActionChangeException(
						$"Code actions cannot create '{path}': something already exists at that path.");
				
				// The apply swaps a temp file written beside the target into place; it does not make directories.
				if(!Directory.Exists(Path.GetDirectoryName(path)))
					throw new UnsupportedCodeActionChangeException(
						$"Code actions cannot create '{path}': its directory does not exist.");
				
				created.Add(path);
			}
			
			foreach(var documentId in projectChange.GetRemovedDocuments()) {
				
				var document = baseSolution.GetDocument(documentId);
				
				if(document?.FilePath is null)
					throw new UnsupportedCodeActionChangeException(
						"Code actions require every deleted document to have a physical file path.");
				
				var path = document.FilePath;
				
				// Another document still has the file: the file stays, as the apply plan knows.
				if(!newSolution.GetDocumentIdsWithFilePath(path).IsEmpty)
					continue;
				
				if(!File.Exists(path))
					throw new UnsupportedCodeActionChangeException(
						$"Code actions can delete existing files only; '{path}' does not exist.");
				
				if(baseSolution.GetDocumentIdsWithFilePath(path).Skip(1).Any())
					throw new UnsupportedCodeActionChangeException(
						$"Code actions cannot delete linked source file '{path}'.");
				
				var canonicalFile = ResolveCanonicalPath(path);
				var relativePath  = Path.GetRelativePath(canonicalRoot, canonicalFile);
				
				if(!IsContained(canonicalFile, canonicalRoot, relativePath))
					throw new UnsupportedCodeActionChangeException(
						$"Code actions cannot delete external or linked file '{path}'.");
				
				if(IsGeneratedFile(canonicalFile, relativePath, null))
					throw new UnsupportedCodeActionChangeException(
						$"Code actions cannot delete generated source file '{path}'.");
				
				EnsureFileWritable(path);
				deleted.Add(path);
			}
		}
		
		if(changed + created.Count + deleted.Count == 0)
			throw new UnsupportedCodeActionChangeException("The code action contains no supported file changes.");
		
		return new ValidatedChanges(created.ToImmutable(), deleted.ToImmutable(), changed);
	}
	
	// The checks every modified document gets; returns the canonical path for the caller's own checks.
	static string ValidateChangedPhysicalTextDocument(
		Solution baseSolution,
		TextDocument oldDocument,
		TextDocument newDocument,
		string canonicalRoot,
		string documentKind)
	{
		// A rename changes the document's name, or its path, or both: Move type's "Rename file to
		// match type" sets only the name, which the diff would show as nothing at all.
		if(!SecurityBoundary.PathComparer.Equals(oldDocument.FilePath, newDocument.FilePath)
			|| !string.Equals(oldDocument.Name, newDocument.Name, StringComparison.Ordinal))
			throw new UnsupportedCodeActionChangeException(
				$"Code actions cannot move or rename {documentKind} '{oldDocument.FilePath}'" +
				(string.Equals(oldDocument.Name, newDocument.Name, StringComparison.Ordinal) ? "." : $" to '{newDocument.Name}'."));
		
		if(!File.Exists(oldDocument.FilePath))
			throw new UnsupportedCodeActionChangeException(
				$"Code actions can modify existing text files only; '{oldDocument.FilePath}' does not exist.");
		
		if(baseSolution.GetDocumentIdsWithFilePath(oldDocument.FilePath).Skip(1).Any())
			throw new UnsupportedCodeActionChangeException(
				$"Code actions cannot modify linked {documentKind} '{oldDocument.FilePath}'.");
		
		var canonicalFile = ResolveCanonicalPath(oldDocument.FilePath!);
		var relativePath  = Path.GetRelativePath(canonicalRoot, canonicalFile);
		
		if(!IsContained(canonicalFile, canonicalRoot, relativePath))
			throw new UnsupportedCodeActionChangeException(
				$"Code actions cannot modify external or linked file '{oldDocument.FilePath}'.");
		
		EnsureFileWritable(oldDocument.FilePath!);
		
		return canonicalFile;
	}
	
	// Containment by the server's own case rule: GetRelativePath ignores case on Windows and macOS, and
	// would put a case-sensitive directory's ROOT/ sibling inside root/ (#391 review).
	static bool IsContained(string canonicalFile, string canonicalRoot, string relativePath)
		=> SecurityBoundary.IsUnderDirectory(canonicalFile, canonicalRoot)
			&& !Path.IsPathRooted(relativePath)
			&& relativePath != ".."
			&& !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
			&& !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
	
	// By path, and by header when the text is at hand (a file about to be created has none).
	static bool IsGeneratedFile(string canonicalPath, string relativePath, SourceText? text)
	{
		var segments = relativePath.Split(
			[Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
			StringSplitOptions.RemoveEmptyEntries);
		
		if(segments.Any(segment => segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
			|| segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
			|| segment.Equals("generated", StringComparison.OrdinalIgnoreCase)))
			return true;
		
		var fileName = Path.GetFileName(canonicalPath);
		
		if(fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
			|| fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
			|| fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
			return true;
		
		if(text is null)
			return false;
		
		var headerLength = Math.Min(text.Length, 2048);
		var header       = text.ToString(TextSpan.FromBounds(0, headerLength));
		
		return header.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase)
			|| header.Contains("<autogenerated", StringComparison.OrdinalIgnoreCase)
			|| header.Contains("auto-generated by", StringComparison.OrdinalIgnoreCase);
	}
	
	// The read-only attribute only, checked without opening the file. Opening it for writing to
	// see whether that works (as this did until #349) collides with whoever has it open at that
	// moment — the server's own reads keep writers out (FileReader.ReadSharing), and so do an
	// editor saving and an antivirus scan — and refuses the change over a handle that is gone a
	// moment later. What the attribute does not show (a lock, a denying ACL) the apply finds: it
	// writes atomically and reports a file it could not write, with recovery guidance.
	static void EnsureFileWritable(string path)
	{
		try {
			
			// The file a link leads to: on Windows the attributes of a link are its own, and a
			// link to a read-only file would pass.
			if(File.GetAttributes(FileWriter.FollowLink(path)).HasFlag(FileAttributes.ReadOnly))
				throw new UnauthorizedAccessException("The file has the read-only attribute.");
		}
		catch(Exception ex) when(ex is FileNotFoundException or DirectoryNotFoundException) {
			
			// Deleted between the caller's existence check and this one: the caller's message.
			throw new UnsupportedCodeActionChangeException(
				$"Code actions can modify existing text files only; '{path}' does not exist.");
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
			
			throw new UnsupportedCodeActionChangeException(
				$"Code actions require writable source files; '{path}' cannot be written: {ex.Message}");
		}
	}
	
	static string ResolveCanonicalPath(string path)
	{
		var fullPath = Path.GetFullPath(path);
		var pathRoot = Path.GetPathRoot(fullPath)
			?? throw new UnsupportedCodeActionChangeException($"Path '{path}' has no filesystem root.");
		var current  = pathRoot;
		var segments = fullPath[pathRoot.Length..].Split(
			[Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
			StringSplitOptions.RemoveEmptyEntries);
		
		foreach(var segment in segments) {
			
			current = Path.Combine(current, segment);
			
			// A path that does not exist yet (a file about to be created) has no link to follow, and
			// asking would throw.
			if(!Directory.Exists(current) && !File.Exists(current))
				continue;
			
			FileSystemInfo info = Directory.Exists(current)
				? new DirectoryInfo(current)
				: new FileInfo(current)
			;
			var target = info.ResolveLinkTarget(returnFinalTarget: true);
			
			if(target is not null)
				current = target.FullName;
		}
		
		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
	}
}

internal sealed class UnsupportedCodeActionChangeException(string message) : Exception(message);
