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
			foreach(var docId in projectChange.GetChangedDocuments())
				await AppendChangedAsync(sb, before.GetDocument(docId)!, after.GetDocument(docId)!, cancellationToken);
			
			// Changed AdditionalFiles and analyzer-config documents are written by
			// PhysicalSolutionApplier exactly like C# documents, so the preview must show them too.
			foreach(var docId in projectChange.GetChangedAdditionalDocuments())
				await AppendChangedAsync(sb, before.GetAdditionalDocument(docId)!, after.GetAdditionalDocument(docId)!, cancellationToken);
			
			foreach(var docId in projectChange.GetChangedAnalyzerConfigDocuments())
				await AppendChangedAsync(sb, before.GetAnalyzerConfigDocument(docId)!, after.GetAnalyzerConfigDocument(docId)!, cancellationToken);
			
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
	
	private static async Task AppendChangedAsync(System.Text.StringBuilder sb, TextDocument oldDoc, TextDocument newDoc, CancellationToken cancellationToken)
	{
		var oldText = (await oldDoc.GetTextAsync(cancellationToken)).ToString();
		var newText = (await newDoc.GetTextAsync(cancellationToken)).ToString();
		
		if(oldText == newText)
			return;
		
		var path = oldDoc.FilePath ?? oldDoc.Name;
		sb.AppendLine($"--- {path}");
		sb.AppendLine($"+++ {path}");
		sb.Append(BuildHunks(oldText, newText));
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
		
		// Pair up the lines the two files have in common, in order, then emit the gaps between
		// pairs as hunks with 3-line context.
		var hunks = BuildHunkList(oldLines, newLines, MatchLines(oldLines, newLines), context: 3);
		
		foreach(var hunk in hunks) {
			
			sb.AppendLine($"@@ -{hunk.OldStart + 1},{hunk.OldLines} +{hunk.NewStart + 1},{hunk.NewLines} @@");
			
			foreach(var line in hunk.Lines)
				sb.AppendLine(line);
		}
		
		return sb.ToString();
	}
	
	/// <summary>
	///     The longest common subsequence of the two files' lines, as (old, new) index pairs in order
	///     (#411). The lines both files start and end with are paired directly; what lies between goes
	///     to Myers' O(ND) algorithm, whose cost grows with the size of the change, not of the file.
	///     The matcher this replaces paired each new line with the earliest unused old line of the
	///     same text: one line that is common but not unique — the empty line a refactoring inserts —
	///     paired with an occurrence at the far end of the file, and nothing between could match any
	///     more, so a ten-line change came out as a whole-file diff too large for a client to show.
	/// </summary>
	private static List<(int OldIdx, int NewIdx)> MatchLines(string[] oldLines, string[] newLines)
	{
		var pairs  = new List<(int OldIdx, int NewIdx)>();
		var prefix = 0;
		
		while(prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix])
			prefix++;
		
		var suffix = 0;
		
		while(suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix
			&& oldLines[oldLines.Length - 1 - suffix] == newLines[newLines.Length - 1 - suffix])
			suffix++;
		
		for(var i = 0; i < prefix; i++)
			pairs.Add((i, i));
		
		// Lines as numbers, equal for equal text: the search compares lines far more often than there are lines.
		var ids       = new Dictionary<string, int>(StringComparer.Ordinal);
		var oldMiddle = Intern(oldLines.AsSpan(prefix, oldLines.Length - prefix - suffix), ids);
		var newMiddle = Intern(newLines.AsSpan(prefix, newLines.Length - prefix - suffix), ids);
		
		// Past the edit bound the middle stays unmatched: reported as removed and added, which is
		// true of a change that large and costs nothing to compute.
		foreach(var (oldIndex, newIndex) in CommonSubsequence(oldMiddle, newMiddle) ?? [])
			pairs.Add((prefix + oldIndex, prefix + newIndex));
		
		for(var i = suffix; i > 0; i--)
			pairs.Add((oldLines.Length - i, newLines.Length - i));
		
		return pairs;
	}
	
	static int[] Intern(ReadOnlySpan<string> lines, Dictionary<string, int> ids)
	{
		var result = new int[lines.Length];
		
		for(var i = 0; i < lines.Length; i++) {
			
			if(!ids.TryGetValue(lines[i], out var id))
				ids[lines[i]] = id = ids.Count;
			
			result[i] = id;
		}
		
		return result;
	}
	
	// The most line insertions plus deletions the search follows for one file. The search keeps one
	// row of furthest-reaching positions per edit, so memory grows with the square of this: about
	// 9 MB at the bound. A diff with more edits than this is not one anyone reads line by line.
	const int MaxEdits = 1500;
	
	/// <summary>
	///     Myers' greedy shortest-edit-script search over two sequences, returning the matched index
	///     pairs in order, or null when the sequences differ by more than <see cref="MaxEdits"/>
	///     edits. For each edit count d and diagonal k (x − y), <c>furthest[k]</c> is the furthest x
	///     reachable; a row is kept per d so the path can be walked back from the end.
	/// </summary>
	static List<(int OldIdx, int NewIdx)>? CommonSubsequence(int[] oldIds, int[] newIds)
	{
		var oldCount = oldIds.Length;
		var newCount = newIds.Length;
		
		if(oldCount == 0 || newCount == 0)
			return [];
		
		var limit    = Math.Min(oldCount + newCount, MaxEdits);
		var offset   = limit + 1;
		var furthest = new int[2 * limit + 3];
		var rows     = new List<int[]>();
		
		for(var d = 0; d <= limit; d++) {
			
			// The row as it stood before this round, diagonals −d..d: what the walk back reads.
			rows.Add(furthest.AsSpan(offset - d, 2 * d + 1).ToArray());
			
			for(var k = -d; k <= d; k += 2) {
				
				var down = k == -d || (k != d && furthest[offset + k - 1] < furthest[offset + k + 1]);
				var x    = down ? furthest[offset + k + 1] : furthest[offset + k - 1] + 1;
				var y    = x - k;
				
				while(x < oldCount && y < newCount && oldIds[x] == newIds[y]) {
					
					x++;
					y++;
				}
				
				furthest[offset + k] = x;
				
				if(x >= oldCount && y >= newCount)
					return WalkBack(rows, d, oldCount, newCount);
			}
		}
		
		return null;
	}
	
	// From the end of both sequences back to the start: each round's row says which diagonal the path
	// came from, and the diagonal run between the two points is a run of matched lines.
	static List<(int OldIdx, int NewIdx)> WalkBack(List<int[]> rows, int edits, int oldCount, int newCount)
	{
		var pairs = new List<(int OldIdx, int NewIdx)>();
		var x     = oldCount;
		var y     = newCount;
		
		for(var d = edits; d > 0; d--) {
			
			var row       = rows[d];
			var k         = x - y;
			var down      = k == -d || (k != d && row[d + k - 1] < row[d + k + 1]);
			var previousK = down ? k + 1 : k - 1;
			var previousX = row[d + previousK];
			var previousY = previousX - previousK;
			
			while(x > previousX && y > previousY)
				pairs.Add((--x, --y));
			
			x = previousX;
			y = previousY;
		}
		
		while(x > 0 && y > 0)
			pairs.Add((--x, --y));
		
		pairs.Reverse();
		
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
