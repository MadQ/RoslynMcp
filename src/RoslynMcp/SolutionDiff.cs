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
		
		// Simple greedy diff: pair up identical lines in order, then emit the gaps between
		// pairs as hunks with 3-line context. Not a full Myers diff — sufficient for readable
		// output on typical refactoring changes.
		var hunks = BuildHunkList(oldLines, newLines, MatchLines(oldLines, newLines), context: 3);
		
		foreach(var hunk in hunks) {
			
			sb.AppendLine($"@@ -{hunk.OldStart + 1},{hunk.OldLines} +{hunk.NewStart + 1},{hunk.NewLines} @@");
			
			foreach(var line in hunk.Lines)
				sb.AppendLine(line);
		}
		
		return sb.ToString();
	}
	
	/// <summary>
	///     Greedy forward matching of identical lines: pairs each new line with the earliest
	///     unused old position past the previous match. O(n+m) space vs O(n*m) for full LCS DP.
	///     Produces identical results for the common case (few scattered changes).
	///     Slightly noisier hunks when many identical lines exist in different positions.
	/// </summary>
	private static List<(int OldIdx, int NewIdx)> MatchLines(string[] oldLines, string[] newLines)
	{
		var pairs = new List<(int OldIdx, int NewIdx)>();
		
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
		
		for(var ni = 0; ni < newLines.Length; ni++) {
			
			if(!oldPositions.TryGetValue(newLines[ni], out var positions))
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
			
			pairs.Add((oldPos, ni));
			lastMatchedOld = oldPos;
		}
		
		return pairs;
	}
	
	private sealed record Hunk(int OldStart, int OldLines, int NewStart, int NewLines, List<string> Lines);
	
	/// <summary>
	///     Builds hunks from the matched-pair alignment. Changed regions are the gaps between
	///     consecutive pairs; regions separated by at most 2×context matched lines merge into
	///     one hunk. Emitting strictly from the alignment keeps context lines truthful — they
	///     are matched lines by construction, never positions assumed to still be in sync.
	/// </summary>
	private static List<Hunk> BuildHunkList(string[] oldLines, string[] newLines, List<(int OldIdx, int NewIdx)> matches, int context)
	{
		// Changed regions between consecutive pairs (ends exclusive). The sentinel pair closes
		// the final region when either file has trailing unmatched lines.
		var regions = new List<(int OldStart, int OldEnd, int NewStart, int NewEnd)>()
		;
		var oi = 0;
		var ni = 0;
		
		foreach(var (mo, mn) in matches.Append((oldLines.Length, newLines.Length))) {
			
			if(oi < mo || ni < mn)
				regions.Add((oi, mo, ni, mn));
			
			oi = mo + 1;
			ni = mn + 1;
		}
		
		var hunks = new List<Hunk>();
		
		for(var r = 0; r < regions.Count; ) {
			
			// Merge regions whose surrounding context would touch or overlap.
			var group = r + 1
			;
			
			while(group < regions.Count && regions[group].OldStart - regions[group - 1].OldEnd <= context * 2)
				group++;
			
			var first        = regions[r];
			var last         = regions[group - 1];
			var hunkOldStart = Math.Max(0, first.OldStart - context);
			var hunkOldEnd   = Math.Min(oldLines.Length, last.OldEnd + context);
			var hunkNewStart = first.NewStart - (first.OldStart - hunkOldStart);
			var lines        = new List<string>();
			var newCount     = 0;
			
			for(var i = r; i < group; i++) {
				
				var (regOldStart, regOldEnd, regNewStart, regNewEnd) = regions[i]
				;
				var contextFrom = i == r ? hunkOldStart : regions[i - 1].OldEnd;
				
				// Context between the previous region (or hunk start) and this region —
				// matched lines, identical in both files.
				for(var c = contextFrom; c < regOldStart; c++, newCount++)
					lines.Add(" " + oldLines[c]);
				
				for(var c = regOldStart; c < regOldEnd; c++)
					lines.Add("-" + oldLines[c]);
				
				for(var c = regNewStart; c < regNewEnd; c++, newCount++)
					lines.Add("+" + newLines[c]);
			}
			
			// Trailing context.
			for(var c = last.OldEnd; c < hunkOldEnd; c++, newCount++)
				lines.Add(" " + oldLines[c]);
			
			hunks.Add(new Hunk(hunkOldStart, hunkOldEnd - hunkOldStart, hunkNewStart, newCount, lines));
			r = group;
		}
		
		return hunks;
	}
}
