using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;

namespace RoslynMcp;

/// <summary>
///     Builds <see cref="PreviewFileState"/> snapshots from a Solution diff — the physical-file
///     hashes and intended bytes that <see cref="PhysicalSolutionApplyPlan"/> needs to safely apply
///     a previewed change later. Shared across every two-step preview/apply workflow (code fix,
///     rename, signature change) since the underlying Solution-diff-to-bytes logic is generic;
///     workflow-specific restrictions (e.g. code-fix's Phase 1 .cs-only guard) live in the
///     individual preview tools, not here.
/// </summary>
internal static class PreviewFileStateBuilder
{
	public static async Task<IReadOnlyDictionary<string, PreviewFileState>> BuildAsync(
		Solution baseSolution,
		Solution newSolution,
		CancellationToken cancellationToken)
	{
		var states = new Dictionary<string, PreviewFileState>(SecurityBoundary.PathComparer);
		
		foreach(var projectChange in newSolution.GetChanges(baseSolution).GetProjectChanges()) {
			
			// The three kinds of text document a change can touch. They differ only in how a
			// solution hands them out.
			foreach(var docId in projectChange.GetChangedDocuments())
				await AddChangedAsync(states, baseSolution.GetDocument(docId), newSolution.GetDocument(docId), cancellationToken);
			
			foreach(var docId in projectChange.GetChangedAdditionalDocuments())
				await AddChangedAsync(states, baseSolution.GetAdditionalDocument(docId), newSolution.GetAdditionalDocument(docId), cancellationToken);
			
			foreach(var docId in projectChange.GetChangedAnalyzerConfigDocuments())
				await AddChangedAsync(states, baseSolution.GetAnalyzerConfigDocument(docId), newSolution.GetAnalyzerConfigDocument(docId), cancellationToken);
			
			foreach(var docId in projectChange.GetAddedDocuments()) {
				
				var doc = newSolution.GetDocument(docId);
				
				if(doc?.FilePath is null)
					continue;
				
				if(!baseSolution.GetDocumentIdsWithFilePath(doc.FilePath).IsEmpty) {
					
					if(!File.Exists(doc.FilePath))
						throw new IOException($"Affected linked file '{doc.FilePath}' no longer exists while creating the preview.");
					
					var originalBytes = await FileReader.ReadAllBytesAsync(doc.FilePath, FileReader.DocumentLimit, cancellationToken);
					states[doc.FilePath] = new PreviewFileState(
						ExpectedFileState.Exists,
						ComputeHash(originalBytes),
						await EncodeNewDocumentAsync(doc, cancellationToken));
					
					continue;
				}
				
				if(File.Exists(doc.FilePath))
					throw new IOException($"New file '{doc.FilePath}' already exists while creating the preview.");
				
				states[doc.FilePath] = new PreviewFileState(
					ExpectedFileState.Absent,
					null,
					await EncodeNewDocumentAsync(doc, cancellationToken));
			}
			
			foreach(var docId in projectChange.GetRemovedDocuments()) {
				
				var doc = baseSolution.GetDocument(docId);
				
				if(doc?.FilePath is null || !newSolution.GetDocumentIdsWithFilePath(doc.FilePath).IsEmpty)
					continue;
				
				if(!File.Exists(doc.FilePath))
					throw new IOException($"Affected file '{doc.FilePath}' no longer exists while creating the preview.");
				
				var originalBytes = await FileReader.ReadAllBytesAsync(doc.FilePath, FileReader.DocumentLimit, cancellationToken);
				states[doc.FilePath] = new PreviewFileState(
					ExpectedFileState.Exists,
					ComputeHash(originalBytes),
					null);
			}
		}
		
		return states;
	}
	
	/// <summary>
	///     Records the state of one changed text document: the hash of the file as it is now, and the
	///     bytes the change intends for it. One method for source, additional and analyzer-config
	///     documents, which all are a <see cref="TextDocument"/>, so that the three cannot come to
	///     differ in how they are checked, read, encoded or hashed (#324). A document without a
	///     file path, or one the new solution no longer has, is no file to write and is passed over.
	/// </summary>
	static async Task AddChangedAsync(
		Dictionary<string, PreviewFileState> states,
		TextDocument? oldDocument,
		TextDocument? newDocument,
		CancellationToken cancellationToken)
	{
		if(oldDocument?.FilePath is null || newDocument is null)
			
			return;
		
		if(!File.Exists(oldDocument.FilePath))
			throw new IOException($"Affected file '{oldDocument.FilePath}' no longer exists while creating the preview.");
		
		var originalBytes = await FileReader.ReadAllBytesAsync(oldDocument.FilePath, FileReader.DocumentLimit, cancellationToken);
		var intendedBytes = await EncodeChangedDocumentAsync(
			oldDocument,
			newDocument,
			originalBytes,
			cancellationToken);
		
		states[oldDocument.FilePath] = new PreviewFileState(
			ExpectedFileState.Exists,
			ComputeHash(originalBytes),
			intendedBytes);
	}
	
	static async Task<byte[]> EncodeChangedDocumentAsync(
		TextDocument oldDocument,
		TextDocument newDocument,
		byte[] originalBytes,
		CancellationToken cancellationToken)
	{
		var oldText = await oldDocument.GetTextAsync(cancellationToken);
		var newText = await newDocument.GetTextAsync(cancellationToken);
		var encoding = CreateStrictEncoding(oldText.Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		var preamble = FindOriginalPreamble(originalBytes, encoding);
		var decoded = encoding.GetString(originalBytes, preamble.Length, originalBytes.Length - preamble.Length);
		
		if(!string.Equals(decoded, oldText.ToString(), StringComparison.Ordinal))
			throw new UnsupportedFileEncodingException(
				$"Encoding for '{oldDocument.FilePath}' could not be preserved exactly.");
		
		var content = encoding.GetBytes(newText.ToString());
		
		if(preamble.Length == 0)
			return content;
		
		var result = new byte[preamble.Length + content.Length];
		preamble.CopyTo(result, 0);
		content.CopyTo(result, preamble.Length);
		
		return result;
	}
	
	static async Task<byte[]> EncodeNewDocumentAsync(
		TextDocument document,
		CancellationToken cancellationToken)
	{
		var text = await document.GetTextAsync(cancellationToken);
		var encoding = CreateStrictEncoding(text.Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		
		return encoding.GetBytes(text.ToString());
	}
	
	static Encoding CreateStrictEncoding(Encoding encoding)
	{
		var strict = (Encoding) encoding.Clone();
		strict.DecoderFallback = DecoderFallback.ExceptionFallback;
		strict.EncoderFallback = EncoderFallback.ExceptionFallback;
		
		return strict;
	}
	
	static byte[] FindOriginalPreamble(byte[] bytes, Encoding encoding)
	{
		var preamble = encoding.CodePage switch {
			12000 => new byte[] { 0xFF, 0xFE, 0x00, 0x00 },
			12001 => new byte[] { 0x00, 0x00, 0xFE, 0xFF },
			1200  => new byte[] { 0xFF, 0xFE },
			1201  => new byte[] { 0xFE, 0xFF },
			65001 => new byte[] { 0xEF, 0xBB, 0xBF },
			_     => encoding.GetPreamble()
		};
		
		return preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble)
			? preamble
			: [];
	}
	
	public static string ComputeHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

internal sealed class UnsupportedFileEncodingException : Exception
{
	public UnsupportedFileEncodingException(string message)
		: base(message)
	{ }
}
