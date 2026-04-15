using Microsoft.CodeAnalysis;
using System.Text;

namespace RoslynMcp;

/// <summary>
///     Produces unified diffs between two Solution states and applies approved changes to disk.
/// </summary>
internal static class SolutionDiff
{
	static readonly string[] LineSeparators = ["\r\n", "\n"];
	
	/// <summary>
	///     Returns a unified diff string comparing the changed documents between
	///     <paramref name="before"/> and <paramref name="after"/>.
	/// </summary>
	public static async Task<string> BuildAsync(Solution before, Solution after, CancellationToken cancellationToken)
	{
		var sb = new System.Text.StringBuilder();
		
		foreach(var projectChange in after.GetChanges(before).GetProjectChanges()) {
			
			// Changed documents: both files exist, content differs.
			foreach(var docId in projectChange.GetChangedDocuments()) {
				
				var oldDoc = before.GetDocument(docId)!;
				var newDoc = after.GetDocument(docId)!;
				
				var oldText = (await oldDoc.GetTextAsync(cancellationToken)).ToString();
				var newText = (await newDoc.GetTextAsync(cancellationToken)).ToString();
				
				if(oldText == newText)
					continue;
				
				var path = oldDoc.FilePath ?? oldDoc.Name;
				sb.AppendLine($"--- {path}");
				sb.AppendLine($"+++ {path}");
				sb.Append(BuildHunks(oldText, newText));
			}
			
			// Added documents: new file, e.g., a type renamed to Bar.cs.
			foreach(var docId in projectChange.GetAddedDocuments()) {
				
				var newDoc = after.GetDocument(docId)!;
				
				if(newDoc.FilePath is null)
					continue;
				
				var newText = (await newDoc.GetTextAsync(cancellationToken)).ToString();
				
				sb.AppendLine($"--- /dev/null");
				sb.AppendLine($"+++ {newDoc.FilePath}");
				sb.Append(BuildAllAdded(newText));
			}
			
			// Removed documents: deleted file, e.g., old Foo.cs after rename to Bar.cs.
			// Skip paths still referenced in the new solution (linked/shared files can
			// appear as removed in one project while still present in another).
			foreach(var docId in projectChange.GetRemovedDocuments()) {
				
				var oldDoc = before.GetDocument(docId)!;
				
				if(oldDoc.FilePath is null || !after.GetDocumentIdsWithFilePath(oldDoc.FilePath).IsEmpty)
					continue;
				
				var oldText = (await oldDoc.GetTextAsync(cancellationToken)).ToString();
				
				sb.AppendLine($"--- {oldDoc.FilePath}");
				sb.AppendLine($"+++ /dev/null");
				sb.Append(BuildAllRemoved(oldText));
			}
		}
		
		return sb.Length > 0 ? sb.ToString() : "(no changes)";
	}
	
	/// <summary>
	///     Writes all changed documents from <paramref name="newSolution"/> to disk.
	///     Only files with a non-null <see cref="Document.FilePath"/> are written.
	/// </summary>
	public static async Task ApplyToDiskAsync(Solution oldSolution, Solution newSolution, Func<string, string, Task> writeFile)
	{
		foreach(var projectChange in newSolution.GetChanges(oldSolution).GetProjectChanges()) {
			
			// Write changed and added documents. Added documents are new files (e.g., the
			// renamed file at its new path); they need the same disk write as changed documents.
			foreach(var docId in projectChange.GetChangedDocuments().Concat(projectChange.GetAddedDocuments())) {
				
				var newDoc = newSolution.GetDocument(docId)!;
				
				if(newDoc.FilePath is null)
					continue;
				
				var sourceText = await newDoc.GetTextAsync();
				
				try {
					// SourceText.Encoding is unreliable — StreamReader.CurrentEncoding returns a
					// BOM-emitting instance regardless of whether the file had a BOM. RM's policy
					// is always UTF-8 without BOM, so we never use sourceText.Encoding here.
					await writeFile(newDoc.FilePath, sourceText.ToString())
					;
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
					throw new InvalidOperationException($"Failed to write '{newDoc.FilePath}': {ex.Message}", ex);
				}
			}
		}
	}
	
	
	// ── File-level add / remove diffs ────────────────────────────────────────
	
	// All lines shown as additions (for a newly created file).
	private static string BuildAllAdded(string text)
	{
		var lines = text.Split(LineSeparators, StringSplitOptions.None);
		var sb    = new StringBuilder();
		
		sb.AppendLine($"@@ -0,0 +1,{lines.Length} @@");
		
		foreach(var line in lines)
			sb.AppendLine("+" + line);
		
		return sb.ToString();
	}
	
	// All lines shown as removals (for a deleted file).
	private static string BuildAllRemoved(string text)
	{
		var lines = text.Split(LineSeparators, StringSplitOptions.None);
		var sb    = new StringBuilder();
		
		sb.AppendLine($"@@ -1,{lines.Length} +0,0 @@");
		
		foreach(var line in lines)
			sb.AppendLine("-" + line);
		
		return sb.ToString();
	}
	
	
	// ── Minimal line-level unified diff ──────────────────────────────────────
	
	private static string BuildHunks(string oldText, string newText)
	{
		var oldLines = oldText.Split(LineSeparators, StringSplitOptions.None);
		var newLines = newText.Split(LineSeparators, StringSplitOptions.None);
		var sb       = new System.Text.StringBuilder();
		
		// Simple greedy diff: find changed regions with 3-line context.
		// Not a full Myers diff — sufficient for readable output on typical refactoring changes.
		var lcs    = LongestCommonSubsequence(oldLines, newLines)
		;
		var hunks  = BuildHunkList(oldLines, newLines, lcs, context: 3);
		
		foreach(var hunk in hunks) {
			
			sb.AppendLine($"@@ -{hunk.OldStart + 1},{hunk.OldLines} +{hunk.NewStart + 1},{hunk.NewLines} @@");
			
			foreach(var line in hunk.Lines)
				sb.AppendLine(line);
		}
		
		return sb.ToString();
	}
	
	/// <summary>
	///     Greedy forward-matching of old lines into new lines. Returns a bool[] where
	///     true = old line was matched (unchanged). O(n+m) space vs O(n*m) for full LCS DP.
	///     Produces identical results for the common case (few scattered changes).
	///     Slightly noisier hunks when many identical lines exist in different positions.
	/// </summary>
	private static bool[] LongestCommonSubsequence(string[] oldLines, string[] newLines)
	{
		var inLcs = new bool[oldLines.Length];
		
		// Map each line to its positions in the old file.
		var oldPositions = new Dictionary<string, List<int>>()
		;
		
		for(var i = 0; i < oldLines.Length; i++) {
			
			if(!oldPositions.TryGetValue(oldLines[i], out var list))
				oldPositions[oldLines[i]] = list = [];
			
			list.Add(i);
		}
		
		// Walk the new file, greedily matching each line to the earliest
		// unused position in the old file (preserving order).
		var lastMatchedOld = -1
		;
		
		foreach(var line in newLines) {
			
			if(!oldPositions.TryGetValue(line, out var positions))
				continue;
			
			// Binary search for first position > lastMatchedOld.
			var lo	 = 0
			;
			var hi	 = positions.Count - 1;
			var best = -1;
			
			while(lo <= hi) {
				
				var mid = lo + (hi - lo) / 2;
				
				if(positions[mid] > lastMatchedOld) {
					
					best = mid;
					hi   = mid - 1;
				}
				else
					lo = mid + 1;
			}
			
			if(best < 0)
				continue;
			
			var oldPos = positions[best];
			
			inLcs[oldPos]  = true;
			lastMatchedOld = oldPos;
		}
		
		return inLcs;
	}
	
	private sealed record Hunk(int OldStart, int OldLines, int NewStart, int NewLines, List<string> Lines);
	
	private static List<Hunk> BuildHunkList(string[] oldLines, string[] newLines, bool[] inLcs, int context)
	{
		// Map LCS positions to new-file positions.
		var hunks   = new List<Hunk>()
		;
		var oi      = 0; // old index
		var ni      = 0; // new index
		var lcsIdx  = 0;
		var oldLen  = oldLines.Length;
		var newLen  = newLines.Length;
		var lcsLen  = inLcs.Length;
		
		while(oi < oldLen || ni < newLen) {
			
			if(lcsIdx < lcsLen && inLcs[lcsIdx] && oi < oldLen && ni < newLen && oldLines[oi] == newLines[ni]) {
				
				oi++;
				ni++;
				lcsIdx++;
				continue;
			}
			
			// Start of a changed region.
			var hunkOldStart = Math.Max(0, oi - context)
			;
			var hunkNewStart = Math.Max(0, ni - context);
			var lines        = new List<string>();
			
			// Leading context.
			for(var c = hunkOldStart; c < oi; c++)
				lines.Add(" " + oldLines[c]);
			
			// Changed lines.
			var hunkOi = oi
			;
			var hunkNi = ni;
			
			while(oi < oldLen || ni < newLen) {
				
				var atLcs = lcsIdx < lcsLen && inLcs[lcsIdx]
					&& oi < oldLen && ni < newLen
					&& oldLines[oi] == newLines[ni];
				
				if(atLcs)
					break;
				
				// If ni is exhausted, the LCS match can never be reached — treat as deletion.
				if(oi < oldLen && (lcsIdx >= lcsLen || !inLcs[lcsIdx] || ni >= newLen)) {
					
					lines.Add("-" + oldLines[oi++]);
					lcsIdx++;
				}
				else if(ni < newLen)
					lines.Add("+" + newLines[ni++]);
			}
			
			// Trailing context.
			for(var c = 0; c < context && oi < oldLen; c++, oi++, ni++, lcsIdx++)
				lines.Add(" " + oldLines[oi]);
			
			hunks.Add(new Hunk(hunkOldStart, oi - hunkOldStart, hunkNewStart, ni - hunkNewStart, lines));
		}
		
		return hunks;
	}
}
