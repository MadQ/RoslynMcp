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
		var states = new Dictionary<string, PreviewFileState>(StringComparer.OrdinalIgnoreCase);
		
		foreach(var projectChange in newSolution.GetChanges(baseSolution).GetProjectChanges()) {
			
			foreach(var docId in projectChange.GetChangedDocuments()) {
				
				var oldDocument = baseSolution.GetDocument(docId);
				var newDocument = newSolution.GetDocument(docId);
				
				if(oldDocument?.FilePath is null || newDocument is null)
					continue;
				
				if(!File.Exists(oldDocument.FilePath))
					throw new IOException($"Affected file '{oldDocument.FilePath}' no longer exists while creating the preview.");
				
				var originalBytes = await File.ReadAllBytesAsync(oldDocument.FilePath, cancellationToken);
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
			
			foreach(var docId in projectChange.GetAddedDocuments()) {
				
				var doc = newSolution.GetDocument(docId);
				
				if(doc?.FilePath is null)
					continue;
				
				if(!baseSolution.GetDocumentIdsWithFilePath(doc.FilePath).IsEmpty) {
					
					if(!File.Exists(doc.FilePath))
						throw new IOException($"Affected linked file '{doc.FilePath}' no longer exists while creating the preview.");
					
					var originalBytes = await File.ReadAllBytesAsync(doc.FilePath, cancellationToken);
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
				
				var originalBytes = await File.ReadAllBytesAsync(doc.FilePath, cancellationToken);
				states[doc.FilePath] = new PreviewFileState(
					ExpectedFileState.Exists,
					ComputeHash(originalBytes),
					null);
			}
		}
		
		return states;
	}
	
	static async Task<byte[]> EncodeChangedDocumentAsync(
		Document oldDocument,
		Document newDocument,
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
		Document document,
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
