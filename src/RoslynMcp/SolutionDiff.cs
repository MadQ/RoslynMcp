using Microsoft.CodeAnalysis;

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
	public static async Task<string> BuildAsync(Solution before, Solution after)
	{
		var sb = new System.Text.StringBuilder();
		
		foreach(var projectChange in after.GetChanges(before).GetProjectChanges()) {
			foreach(var docId in projectChange.GetChangedDocuments()) {
			
				var oldDoc = before.GetDocument(docId)!;
				var newDoc = after.GetDocument(docId)!;
				
				var oldText = (await oldDoc.GetTextAsync()).ToString();
				var newText = (await newDoc.GetTextAsync()).ToString();
				
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
				
				var text = (await newDoc.GetTextAsync()).ToString();
				await File.WriteAllTextAsync(newDoc.FilePath, text);
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
	
	private static bool[] LongestCommonSubsequence(string[] a, string[] b)
	{
		// Returns a bool[] of length a.Length: true = line is in LCS (unchanged).
		var m   = a.Length;
		var n   = b.Length;
		var dp  = new int[m + 1, n + 1];
		
		for(var i = m - 1; i >= 0; i--)
			for(var j = n - 1; j >= 0; j--)
				dp[i, j] = a[i] == b[j]
					? dp[i + 1, j + 1] + 1
					: Math.Max(dp[i + 1, j], dp[i, j + 1])
				;
		
		var inLcs = new bool[m];
		var x = 0;
		var y = 0;
		
		while(x < m && y < n) {
			if(a[x] == b[y]) {
				inLcs[x++] = true;
				y++;
			}
			else if(dp[x + 1, y] >= dp[x, y + 1])
				x++;
			else
				y++;
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
			// Skip unchanged context lines.
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
