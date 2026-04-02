using Microsoft.CodeAnalysis;
using System.Text;

namespace RoslynMcp;

/// <summary>
///     Produces unified diffs between two Solution states and applies approved changes to disk.
/// </summary>
internal static class SolutionDiff
{
	/// <summary>
	///     Returns a unified diff string comparing the changed documents between
	///     <paramref name="before"/> and <paramref name="after"/>.
	/// </summary>
	public static async Task<string> BuildAsync(Solution before, Solution after, CancellationToken cancellationToken)
	{
		var sb = new System.Text.StringBuilder();
		
		foreach(var projectChange in after.GetChanges(before).GetProjectChanges()) {
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
		}
		
		return sb.Length > 0 ? sb.ToString() : "(no changes)";
	}
	
	/// <summary>
	///     Writes all changed documents from <paramref name="newSolution"/> to disk.
	///     Only files with a non-null <see cref="Document.FilePath"/> are written.
	/// </summary>
	public static async Task ApplyToDiskAsync(Solution oldSolution, Solution newSolution)
	{
		foreach(var projectChange in newSolution.GetChanges(oldSolution).GetProjectChanges()) {
			foreach(var docId in projectChange.GetChangedDocuments()) {
			
				var newDoc = newSolution.GetDocument(docId)!;
				
				if(newDoc.FilePath is null)
					continue;
				
				var sourceText = await newDoc.GetTextAsync();

				try {
					var encoding = sourceText.Encoding ?? Encoding.UTF8;
				await File.WriteAllTextAsync(newDoc.FilePath, sourceText.ToString(), encoding);
				}
				catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
					throw new InvalidOperationException($"Failed to write '{newDoc.FilePath}': {ex.Message}", ex);
				}
			}
		}
	}
	
	// ── Minimal line-level unified diff ──────────────────────────────────────
	
	private static string BuildHunks(string oldText, string newText)
	{
		var oldLines = oldText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
		var newLines = newText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
		var sb       = new System.Text.StringBuilder();
		
		// Simple greedy diff: find changed regions with 3-line context.
		// Not a full Myers diff — sufficient for readable output on typical refactoring changes.
		var lcs    = LongestCommonSubsequence(oldLines, newLines);
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
	///     Produces identical results for the common case (few scattered changes);
	///     slightly noisier hunks when many identical lines exist in different positions.
	/// </summary>
	private static bool[] LongestCommonSubsequence(string[] oldLines, string[] newLines)
	{
		var inLcs = new bool[oldLines.Length];

		// Map each line to its positions in the old file.
		var oldPositions = new Dictionary<string, List<int>>();

		for(var i = 0; i < oldLines.Length; i++) {

			if(!oldPositions.TryGetValue(oldLines[i], out var list))
				oldPositions[oldLines[i]] = list = [];

			list.Add(i);
		}

		// Walk the new file, greedily matching each line to the earliest
		// unused position in the old file (preserving order).
		var lastMatchedOld = -1;

		foreach(var line in newLines) {

			if(!oldPositions.TryGetValue(line, out var positions))
				continue;

			// Binary search for first position > lastMatchedOld.
			var lo = 0;
			var hi = positions.Count - 1;
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
		var hunks   = new List<Hunk>();
		var oi      = 0; // old index
		var ni      = 0; // new index
		var lcsIdx  = 0;
		
		while(oi < oldLines.Length || ni < newLines.Length) {
			if(lcsIdx < inLcs.Length && inLcs[lcsIdx] && oi < oldLines.Length && ni < newLines.Length && oldLines[oi] == newLines[ni]) {
				oi++;
				ni++;
				lcsIdx++;
				continue;
			}
			
			// Start of a changed region.
			var hunkOldStart = Math.Max(0, oi - context);
			var hunkNewStart = Math.Max(0, ni - context);
			var lines        = new List<string>();
			
			// Leading context.
			for(var c = hunkOldStart; c < oi; c++)
				lines.Add(" " + oldLines[c]);
			
			// Changed lines.
			var hunkOi = oi;
			var hunkNi = ni;
			
			while(oi < oldLines.Length || ni < newLines.Length) {
				var atLcs = lcsIdx < inLcs.Length && inLcs[lcsIdx]
					&& oi < oldLines.Length && ni < newLines.Length
					&& oldLines[oi] == newLines[ni];
				
				if(atLcs)
					break;
				
				if(oi < oldLines.Length && (lcsIdx >= inLcs.Length || !inLcs[lcsIdx])) {
					lines.Add("-" + oldLines[oi++]);
					lcsIdx++;
				}
				else if(ni < newLines.Length) {
					lines.Add("+" + newLines[ni++]);
				}
			}
			
			// Trailing context.
			for(var c = 0; c < context && oi < oldLines.Length; c++, oi++, ni++, lcsIdx++)
				lines.Add(" " + oldLines[oi]);
			
			hunks.Add(new Hunk(hunkOldStart, oi - hunkOldStart, hunkNewStart, ni - hunkNewStart, lines));
		}
		
		return hunks;
	}
}
